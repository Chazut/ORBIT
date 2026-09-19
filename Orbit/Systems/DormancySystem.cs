using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using Orbit.Core;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Looting;
using Orbit.Navigation;
using Orbit.Sain;
using UnityEngine;

namespace Orbit.Systems;

/// <summary>
/// Ghost Mode, the built-in AI limiter. Sleeps the BODY of far-away squads, not their brain: all ORBIT logic
/// (dispatch, strategies, timers) lives outside the bot's GameObject, so <c>SetActive(false)</c> kills the
/// per-bot BSG/SAIN cost while the squad keeps thinking and (via MovementSystem's ghost follower) keeps
/// moving along its planned routes. Neither AILimit nor Questing Bots can do this: their own logic runs on
/// the bot's GameObject and freezes with it.
///
/// Sleep/wake recipe proven by Questing Bots: sleep = DecisionQueue.Clear + GoalEnemy=null +
/// PatrollingData.Pause + SetActive(false); wake = SetActive(true) + Unpause + PostActivate (mandatory:
/// deactivation leaves BotState=NonActive and PostActivate re-enters the activation pipeline). Awake bots
/// are kept from raycasting sleepers by <see cref="Orbit.Patches.DormantVisionPatch"/>.
///
/// ORBIT squads sleep atomically, polled at 2 Hz, with TWO kinds of hysteresis learned from the test
/// raids:
///  - spatial: sleep beyond SleepDistance from every human, wake within WakeDistance;
///  - temporal: a woken squad stays awake at least WakeCooldownSeconds (so SAIN gets a real window to
///    heal or fight — without it a bleeding sleeper looped wake/sleep every bleed tick and bled out),
///    and a freshly-slept squad ignores the awake-bot trigger for SleepGraceSeconds (kills the 2 Hz
///    ping-pong pairs without muting encounter wakes: an active bot crossing a dormant squad MUST wake
///    it, that is where scav kills come from).
///
/// Sleep POLICY is per bot TYPE, orthogonal to who drives the bot: every type except PMC and PlayerScav
/// has a "default dormant" toggle (scavs, Goons, bosses+followers, cultists, raiders/rogues, bloodhounds,
/// others — all ON by default). A toggled type sleeps from a tight ring and never takes a population-floor
/// slot; ORBIT-driven members ghost-walk their routes while supported native members follow their own paths (per
/// BSG BotsGroup, so a boss never sleeps apart from its followers). Toggle OFF = ORBIT bots of that type
/// fall back to the standard PMC-like rules, vanilla bots of that type are left untouched. The floor only
/// counts standard-policy bots and self-caps to half of them, so small-population maps still sleep. Bots
/// whose HP dropped recently never sleep (bleeds tick on inactive bodies and a sleeper cannot heal).
/// Corpses can never go dormant, and OnAgentRemoved re-activates a dormant body just in case.
/// </summary>
public class DormancySystem
{
    private const float PollIntervalSeconds = 0.5f;
    // Extract-bound squads keep ghosting toward the exfil and only wake this close to it: the
    // walk stays free, only the trigger interaction runs on the real AI.
    private const float ExtractWakeDistance = 50f;
    private const float ExtractWakeDistanceSqr = ExtractWakeDistance * ExtractWakeDistance;
    private const float WakeCooldownSeconds = 30f;
    private const float SleepGraceSeconds = 15f;
    private const float HpStableSeconds = 15f;
    // A far, out-of-combat bot whose HP keeps dropping gets a simulated patch-up at most this often.
    private const float GhostPatchUpCooldownSeconds = 30f;
    // Dormant bots' brain tick (BigBrain layer sweep + active action) runs on one frame out of this many.
    public const int DormantBrainTickDivisor = 6;

    // Ghost skirmishes: a hostile DORMANT unit detects another when it comes inside its REACH: a base
    // engagement range scaled by the best optic magnification among its members (a sniper ghost spots
    // and duels at range, a shotgun scav only brawls), with a line-of-sight check between the closest
    // members so hills and buildings block detection. Contact chance falls with distance (point-blank
    // encounters almost always fight, edge-of-reach spotting usually stays a near miss), and the fight
    // resolution weighs each side's reach against the actual distance: the scoped side dominates far
    // duels and gets swarmed up close. Casualties go through the real death pipeline (lootable corpses).
    private const float SkirmishBaseDetectRange = 60f;
    private const float SkirmishReachCap = 400f;
    private const float SkirmishPairCooldownSeconds = 180f;
    private const float SkirmishChanceClose = 0.85f;
    private const float SkirmishChanceFar = 0.25f;
    // Under this distance two hostile units ALWAYS make contact (raid 8: two ghosts crossed at arm's
    // length on a 54% roll and walked on). A failed roll beyond it only burns the short cooldown below,
    // not the full pair cooldown, so units travelling together re-roll within seconds, not minutes.
    private const float SkirmishGuaranteedContactRange = 20f;
    private const float SkirmishShadowCooldownSeconds = 30f;
    private const float GhostHealDelaySeconds = 30f;  // wounded sleepers wait this long before self-patching
    private const float GhostHealPerSecond = 1.2f;    // ~70 HP/min once patching starts

    // Scoped wake (Adaptive Bot Culling's optic-FOV insight): while the player aims through an optic,
    // the wake ring stretches forward with the optic's magnification (derived from its camera FOV, so
    // any modded scope works), inside a cone WIDER than the scope view so bots wake while the player
    // sweeps toward them, never at the reticle (dormant bodies are not rendered, so a reticle-timed
    // wake would be visible pop-in). Terrain/buildings between camera and bot keep it asleep.
    private const float ScopedWakeConeMarginDeg = 15f;

    // Profile ids of currently-dormant bots (ORBIT and vanilla), static so the vision patch can do a
    // set hit per CheckLookEnemy call. Cleared at raid start (ctor) and raid end (OrbitDisposePatch).
    private static readonly HashSet<string> DormantProfileIds = new();

    public static bool IsDormantProfile(string profileId)
        => profileId != null && DormantProfileIds.Count > 0 && DormantProfileIds.Contains(profileId);

    /// <summary>Read by MovementSystem's dormant branch: OFF = sleepers hold position instead of ghost-walking.</summary>
    public static bool GhostMovementEnabled { get; private set; }

    /// <summary>Incremented by DormantVisionPatch each time an awake bot's CheckLookEnemy was blocked on
    /// a sleeper. Read + reset by the 30s summary line — proves the vision shield actually fires.</summary>
    public static long VisionBlocks;

    /// <summary>Brain agents (AICoreAgent) of dormant bots, consulted by DormantBrainThrottlePatch on
    /// every brain tick. Static because the patch is static; there is one dormancy system per raid.</summary>
    private static readonly HashSet<object> _throttledBrainAgents = new();

    /// <summary>Brain ticks skipped on dormant bots since the last summary line.</summary>
    public static long BrainTicksSkipped;

    /// <summary>True when this brain tick belongs to a dormant bot and lands on a skipped frame. Ticks are
    /// staggered per agent so the ones that do run spread across frames instead of bunching up.</summary>
    public static bool ShouldSkipBrainTick(object agent)
    {
        if (agent == null || _throttledBrainAgents.Count == 0 || !_throttledBrainAgents.Contains(agent)) return false;
        var phase = (RuntimeHelpers.GetHashCode(agent) & 0x7fffffff) % DormantBrainTickDivisor;
        if ((Time.frameCount + phase) % DormantBrainTickDivisor == 0) return false;
        BrainTicksSkipped++;
        return true;
    }

    private static void ThrottleBrain(BotOwner bot)
    {
        var agent = bot?.Brain?.Agent;
        if (agent != null) _throttledBrainAgents.Add(agent);
    }

    private static void UnthrottleBrain(BotOwner bot)
    {
        var agent = bot?.Brain?.Agent;
        if (agent != null) _throttledBrainAgents.Remove(agent);
    }

    public static void ClearStatics()
    {
        NativeGhostSystem.Clear();
        Api.OrbitTelemetry.ClearGhostFights();
        DormantProfileIds.Clear();
        _throttledBrainAgents.Clear();
        GhostMovementEnabled = false;
        VisionBlocks = 0;
        BrainTicksSkipped = 0;
    }

    private readonly MovementSystem _movementSystem;
    private readonly DoorSystem _doorSystem;
    private readonly BotRoster _botRoster;
    private readonly NativeGhostSystem _nativeGhosts;
    private readonly GameWorld _gameWorld;
    private readonly TimePacing _pollPacing = new(PollIntervalSeconds);

    private enum GhostFightsMode { Simulated, Real, Off }

    private readonly bool _enabled;
    private readonly GhostFightsMode _fightsMode;
    private readonly float _skirmishCooldown;
    private readonly float _contactChanceMul;
    private readonly float _lethality;
    private readonly bool _scopedWakeEnabled;
    private readonly float _scopedWakeMax;
    private readonly ServerConfig.GhostModeSection _cfg;
    private readonly int _minAwakeBots;
    private readonly float _sleepDistanceSqr;
    private readonly float _scavSleepDistanceSqr;
    private readonly float _wakeDistanceSqr;
    private readonly float _hostileWakeDistanceSqr;

    private readonly List<Agent> _dormantAgents = new();
    // Poll scratch buffers, reused to stay allocation-free at 2 Hz.
    private readonly List<Vector3> _humanPositions = new();
    private readonly HashSet<string> _targetedProfileIds = new();
    private readonly List<Squad> _sleepCandidates = new();
    private readonly List<Squad> _wakeQueue = new();
    private readonly List<string> _wakeReasons = new();

    // Vanilla (non-ORBIT) sleeper state, keyed per bot. Groups are evaluated per BSG BotsGroup so a boss
    // and its followers sleep and wake together.
    private readonly HashSet<BotOwner> _vanillaDormant = new();
    private readonly List<BotOwner> _nativeMoveScratch = new();
    private readonly Dictionary<BotOwner, float> _vanillaHpBaseline = new();
    private readonly Dictionary<BotOwner, float> _vanillaLastHp = new();
    private readonly Dictionary<BotOwner, float> _vanillaHpDropAt = new();
    private readonly Dictionary<object, float> _vanillaGroupWokeAt = new();
    private readonly Dictionary<object, float> _vanillaGroupSleptAt = new();
    private readonly Dictionary<object, List<BotOwner>> _vanillaGroups = new();

    // Per-poll scoped-wake state (see the scoped-wake notes above). One source per aiming human:
    // the local camera when an optic renders, plus every remote human whose replicated state says
    // ADS with a magnified optic mounted (covers Fika clients and headless, where there is no camera).
    private struct ScopeSource
    {
        public Vector3 Pos;
        public Vector3 Fwd;
        public float DistSqr;
        public float ConeCos;
    }

    private readonly List<ScopeSource> _scopeSources = new();
    private float _unscopedFovDeg = 65f;

    // Best-optic magnification cache per bot (skirmish reach). Weapons rarely change; 60s TTL.
    private readonly Dictionary<BotOwner, (float mag, float at)> _opticCache = new();

    // Weapon effective-kill-range cache per player (skirmish casualties). Same 60s TTL rationale.
    private readonly Dictionary<Player, (float range, float at)> _killRangeCache = new();
    // Night vision cache per bot (skirmish reach and fight odds at night). Same 60s TTL rationale.
    private readonly Dictionary<BotOwner, (bool has, float at)> _nightVisionCache = new();
    // Darkness of the current skirmish poll, 0 (day / lit interior) to 1 (full night).
    private float _darkness;

    // Simulated-fight gunfire: shots queued at resolution time and played over the following seconds
    // through BetterAudio's own sources (the fighters' bodies stay inactive — we only read their
    // weapons' sound banks), so the player hears WHERE the off-screen activity is and can go look.
    private struct PendingShot
    {
        public float At;
        public Vector3 Pos;
        public WeaponSoundPlayer Sound;
        public int Rounds;            // rounds of this trigger pull (automatic weapons loop their Body clip)
        public bool IsTail;           // end of an automatic pull: cut the loop, play the Tail bank
        public BetterSource LoopSource;
    }

    private readonly List<PendingShot> _pendingShots = new();

    // Ghost-skirmish bookkeeping. Pair keys are ordered profile-id pairs (squad ids are recycled).
    private readonly Dictionary<string, float> _skirmishPairSeenAt = new();

    // A resolved skirmish plays out over a WINDOW instead of an instant: both squads hold position,
    // the gunfire spans the whole window, casualties drop mid-window and the survivors' wear lands at
    // the end. Duration scales with how contested the fight is and with distance.
    private sealed class ActiveGhostFight
    {
        public GhostUnit Winner;
        public GhostUnit Loser;
        public float EndsAt;
        public float WinnerWoundChance;
        public float LoserWoundChance;
        public float WinnerWound;
        public float LoserWound;
        public readonly List<float> WinnerKillAts = new();
        public readonly List<float> LoserKillAts = new();
    }

    private readonly List<ActiveGhostFight> _activeFights = new();
    private readonly Dictionary<string, float> _unitFightingUntil = new(); // vanilla units have no Squad field
    private readonly List<GhostUnit> _ghostUnits = new();

    private sealed class GhostUnit
    {
        public string Key;
        public string Label;
        public bool IsSavage;
        public float Reach;
        public float KillRange; // best member weapon's effective kill distance (bEffDist, buckshot capped)
        public bool NightCapable; // at least one member sees in the dark (NVG, thermal goggles, thermal / NV scope)
        public Squad Squad;          // ORBIT units
        public object VanillaKey;    // vanilla units
        public readonly List<Agent> Agents = new();        // ORBIT members (empty for vanilla units)
        public readonly List<BotOwner> VanillaBots = new(); // vanilla members (empty for ORBIT units)
        public int Count => Agents.Count + VanillaBots.Count;
    }

    // 30s summary window counters — the single-raid verification surface. "farBlocked" counts are
    // poll-squad units: a far-from-everyone squad that stays blocked racks one count per poll, so a
    // persistently-blocked squad shows up as a big number in exactly one bucket.
    private float _summaryWindowStart;
    private int _windowSleeps, _windowWakes;
    private int _wakeByHuman, _wakeByAwakeBot, _wakeByExtract, _wakeByTargeted, _wakeByDamage, _wakeByScope;
    private int _farBlockedCombat, _farBlockedLoot, _farBlockedDoor, _farBlockedExtract, _farBlockedState;
    private int _farBlockedBleeding, _farBlockedCooldown, _farBlockedHealing, _farBlockedHands;
    private int _windowPatchUps;
    private int _windowShotsDropped;
    private readonly Dictionary<BotOwner, float> _vanillaPatchUpAt = new();
    private int _blockedProximity, _blockedFloor;
    private int _windowFights;
    private int _windowShotsPlayed;
    private int _lastAwakeStandard;

    public int DormantCount => _dormantAgents.Count;

    public DormancySystem(MovementSystem movementSystem, DoorSystem doorSystem, BotRoster botRoster)
    {
        _movementSystem = movementSystem;
        _doorSystem = doorSystem;
        _botRoster = botRoster;
        _nativeGhosts = new NativeGhostSystem(doorSystem);
        _gameWorld = Singleton<GameWorld>.Instance;

        // Config is read once per raid: ServerConfig is re-fetched in OrbitInitPatch right before this
        // system is constructed, so a web-UI Save applies on the next raid, and values never move mid-raid.
        var cfg = ServerConfig.GhostMode;
        _cfg = cfg;
        _enabled = cfg.Enabled;
        _fightsMode = (cfg.GhostFightsMode ?? "simulated").ToLowerInvariant() switch
        {
            "real" => GhostFightsMode.Real,
            "off" => GhostFightsMode.Off,
            _ => GhostFightsMode.Simulated,
        };
        // Fight tuning: frequency scales the contact odds and the per-pair cooldown, lethality the
        // casualty rolls. Scoped wake can be turned off or range-capped independently of the wake ring.
        var freq = (cfg.GhostFightFrequency ?? "normal").ToLowerInvariant();
        _skirmishCooldown = SkirmishPairCooldownSeconds * (freq == "rare" ? 2f : freq == "frequent" ? 0.5f : 1f);
        _contactChanceMul = freq == "rare" ? 0.6f : freq == "frequent" ? 1.5f : 1f;
        _lethality = Mathf.Clamp(cfg.GhostFightLethality, 0.5f, 2f);
        _scopedWakeEnabled = cfg.ScopedWake;
        _hearingEnabled = cfg.Enabled && cfg.GhostHearing;
        _scopedWakeMax = Mathf.Clamp(cfg.ScopedWakeMaxDistance, 100f, 1500f);
        // FullSleep (default): no population floor — far from every human, the whole map may sleep.
        _minAwakeBots = cfg.FullSleep ? 0 : Mathf.Max(0, cfg.MinAwakeBots);
        var sleepDist = Mathf.Max(cfg.SleepDistance, cfg.WakeDistance); // sleep must be the outer ring
        _sleepDistanceSqr = sleepDist * sleepDist;
        // Bot scavs and vanilla bots are default-dormant citizens: they sleep from a much tighter ring
        // (just enough margin above the wake ring to keep the spatial hysteresis).
        var scavSleepDist = Mathf.Max(cfg.WakeDistance * 1.1f, cfg.WakeDistance + 15f);
        _scavSleepDistanceSqr = scavSleepDist * scavSleepDist;
        _wakeDistanceSqr = cfg.WakeDistance * cfg.WakeDistance;
        _hostileWakeDistanceSqr = cfg.HostileWakeDistance * cfg.HostileWakeDistance;

        ClearStatics();
        GhostMovementEnabled = cfg.GhostMovement;
        _summaryWindowStart = Time.time;

        if (_enabled)
            Log.Always($"Ghost Mode ON — sleep>{sleepDist:F0}m (default-dormant>{scavSleepDist:F0}m) wake<{cfg.WakeDistance:F0}m hostileWake<{cfg.HostileWakeDistance:F0}m minAwake={_minAwakeBots} ghost={(cfg.GhostMovement ? "on" : "off")} ghostLoot={B(cfg.GhostLooting)} fights={_fightsMode.ToString().ToLowerInvariant()}/{freq} lethality={_lethality:F1}x scopedWake={(_scopedWakeEnabled ? $"{_scopedWakeMax:F0}m" : "off")} dormantTypes=[scav={B(cfg.DormantScavs)} goon={B(cfg.DormantGoons)} boss={B(cfg.DormantBosses)} cultist={B(cfg.DormantCultists)} raider={B(cfg.DormantRaiders)} bloodhound={B(cfg.DormantBloodhounds)} other={B(cfg.DormantOthers)}]");
    }

    private static string B(bool v) => v ? "on" : "off";

    /// <summary>
    /// The per-type sleep policy. True = this bot is a default-dormant citizen (tight ring, no floor
    /// slot), whether ORBIT drives it or not. PMCs and PlayerScavs are always standard-policy.
    /// </summary>
    private bool IsDefaultDormant(BotOwner bot)
    {
        var role = bot?.Profile?.Info?.Settings?.Role;
        if (!role.HasValue) return false;
        var r = role.Value;
        if (r.IsPMC()) return false;
        if (r.IsScav())
            return bot.Profile != null && !bot.Profile.WillBeAPlayerScav() && _cfg.DormantScavs;
        // Sniper scavs never sleep: they are cheap stationary overwatch, and their entire role is
        // long-range threat — a sleeping one is just absent.
        if (r == WildSpawnType.marksman) return false;
        if (r.IsGoon()) return _cfg.DormantGoons;
        if (r.IsCultist()) return _cfg.DormantCultists;
        if (r.IsRaider()) return _cfg.DormantRaiders;
        if (r.IsBloodhound()) return _cfg.DormantBloodhounds;
        if (BotTypeUtils.IsBoss(r) || r.ToString().StartsWith("follower")) return _cfg.DormantBosses;
        return _cfg.DormantOthers; // modded factions and anything unrecognised
    }

    public void Update(List<Agent> liveAgents, List<Squad> squads)
    {
        if (!_enabled) return;
        if (_pendingShots.Count > 0) PumpGhostFightShots();
        if (_activeFights.Count > 0) PumpGhostFights();
        _nativeMoveScratch.Clear();
        _nativeMoveScratch.AddRange(_vanillaDormant);
        for (var i = 0; i < _nativeMoveScratch.Count; i++) _nativeGhosts.Move(_nativeMoveScratch[i]);
        if (!_pollPacing.Allowed()) return;

        // The whole poll is crash-proofed: raid-3 test showed a single throwing access (a despawning
        // player's disposed components) killed the poll SILENTLY for the rest of the raid — BSG swallows
        // exceptions escaping the AI tick, so nothing ever hit the log and no sleeper ever woke again.
        try
        {
            PollOnce(liveAgents, squads);
        }
        catch (System.Exception e)
        {
            _pollErrors++;
            if (_pollErrors <= 5 || _pollErrors % 200 == 0)
                Log.Error($"Ghost Mode poll failed (#{_pollErrors}) — skipping this tick, limiter stays alive: {e}");
        }
    }

    private int _pollErrors;

    private void PollOnce(List<Agent> liveAgents, List<Squad> squads)
    {
        ScanWorld();
        UpdateScopeState();

        // ── Pass 1: decide wakes, collect sleep candidates ──────────────
        _sleepCandidates.Clear();
        _wakeQueue.Clear();

        for (var i = 0; i < squads.Count; i++)
        {
            var squad = squads[i];
            if (squad == null || squad.Members.Count == 0) continue;

            if (IsSquadDormant(squad))
            {
                var reason = WakeReason(squad);
                if (reason != null)
                {
                    _wakeQueue.Add(squad);
                    _wakeReasons.Add(reason);
                }
            }
            else
            {
                UpdateHpTracking(squad);
                if (CanSleep(squad)) _sleepCandidates.Add(squad);
            }
        }

        for (var i = 0; i < _wakeQueue.Count; i++)
            WakeSquad(_wakeQueue[i], _wakeReasons[i]);
        _wakeReasons.Clear();

        // ── Pass 2: proximity guard + population floor over the candidates ──
        // The floor protects STANDARD-policy bots only (PMCs, PlayerScavs, toggled-off types), and
        // self-caps to half of them so small maps still sleep. Default-dormant types never take a slot.
        // Only bots that will REMAIN awake block a candidate's proximity guard.
        var totalStandard = 0;
        var awakeStandard = 0;
        for (var i = 0; i < liveAgents.Count; i++)
        {
            var agent = liveAgents[i];
            if (agent == null || IsDefaultDormant(agent.Bot)) continue;
            totalStandard++;
            if (!agent.IsDormant) awakeStandard++;
        }
        var floor = Mathf.Min(_minAwakeBots, (totalStandard + 1) / 2);

        for (var i = 0; i < _sleepCandidates.Count; i++)
        {
            var squad = _sleepCandidates[i];
            var defaultDormant = IsDefaultDormantSquad(squad);
            if (!defaultDormant && awakeStandard - squad.Members.Count < floor) { _blockedFloor++; continue; }
            if (AnyRemainingAwakeBotNear(squad)) { _blockedProximity++; continue; }

            SleepSquad(squad);
            if (!defaultDormant) awakeStandard -= squad.Members.Count;
        }
        _lastAwakeStandard = awakeStandard;

        UpdateVanilla();

        if (_fightsMode != GhostFightsMode.Off)
            ResolveGhostSkirmishes(squads);

        PollGhostHearing(squads);
        HealDormantWounded();

        EmitSummaryIfDue(liveAgents.Count);
    }

    /// <summary>Re-activates a dormant body when its agent leaves the roster (death or removal), so a
    /// corpse or despawned bot is never left as an invisible inactive GameObject.</summary>
    public void OnAgentRemoved(Agent agent)
    {
        if (!agent.IsDormant) return;
        agent.IsDormant = false;
        _dormantAgents.Remove(agent);
        DormantProfileIds.Remove(agent.Player?.ProfileId);
        UnthrottleBrain(agent.Bot);
        try
        {
            var go = agent.Bot?.gameObject;
            if (go != null && !go.activeSelf) go.SetActive(true);
        }
        catch
        {
            // The body was already destroyed (despawn), nothing left to re-activate.
        }
        Log.Info($"{agent} removed while dormant — body re-activated");
    }

    /// <summary>
    /// One "LIMITER:" Info line every 30s while the limiter is ON — enough to verify a whole raid from the
    /// log alone: how much slept, why squads woke, what kept far squads awake, and that the vision shield
    /// fired. Enable Debug level for the per-transition detail on top.
    /// </summary>
    private void EmitSummaryIfDue(int liveAgentCount)
    {
        if (Time.time - _summaryWindowStart < 30f) return;
        Log.Info(
            $"LIMITER: dormant={_dormantAgents.Count}/{liveAgentCount} agents +{_vanillaDormant.Count} vanilla (standardAwake={_lastAwakeStandard}) | 30s: sleeps={_windowSleeps} wakes={_windowWakes} " +
            $"[human={_wakeByHuman} awakeBot={_wakeByAwakeBot} extract={_wakeByExtract} targeted={_wakeByTargeted} damage={_wakeByDamage} scope={_wakeByScope}] " +
            $"farBlocked=[combat={_farBlockedCombat} loot={_farBlockedLoot} door={_farBlockedDoor} extract={_farBlockedExtract} state={_farBlockedState} bleeding={_farBlockedBleeding} healing={_farBlockedHealing} hands={_farBlockedHands} cooldown={_farBlockedCooldown} proximity={_blockedProximity} floor={_blockedFloor}] " +
            $"ghostFights={_windowFights} fightShotsPlayed={_windowShotsPlayed} fightShotsDropped={_windowShotsDropped} visionBlocks={VisionBlocks} brainTicksSkipped={BrainTicksSkipped} patchUps={_windowPatchUps}");
        _summaryWindowStart = Time.time;
        _windowSleeps = _windowWakes = 0;
        _wakeByHuman = _wakeByAwakeBot = _wakeByExtract = _wakeByTargeted = _wakeByDamage = _wakeByScope = 0;
        _farBlockedCombat = _farBlockedLoot = _farBlockedDoor = _farBlockedExtract = _farBlockedState = 0;
        _farBlockedBleeding = _farBlockedCooldown = _farBlockedHealing = _farBlockedHands = 0;
        _blockedProximity = _blockedFloor = 0;
        _windowFights = 0;
        _windowShotsPlayed = 0;
        _windowShotsDropped = 0;
        _windowPatchUps = 0;
        VisionBlocks = 0;
        BrainTicksSkipped = 0;
    }

    // ── Poll world scan ─────────────────────────────────────────────────

    private void ScanWorld()
    {
        _humanPositions.Clear();
        _targetedProfileIds.Clear();

        var players = _gameWorld.AllAlivePlayersList;
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player == null) continue;
            try
            {
                if (player.HealthController is not { IsAlive: true }) continue;

                if (!player.AIData.IsAI)
                {
                    _humanPositions.Add(player.Position);
                    continue;
                }

                // Build the "who is being targeted" set from every awake bot's current goal enemy, ORBIT or not.
                var goalPerson = player.AIData.BotOwner?.Memory?.GoalEnemy?.Person;
                if (goalPerson != null) _targetedProfileIds.Add(goalPerson.ProfileId);
            }
            catch
            {
                // Despawning/extracting player with disposed components — skip it, never kill the scan.
            }
        }
    }

    // ── Squad-level criteria ────────────────────────────────────────────

    private static bool IsSquadDormant(Squad squad)
    {
        // Atomic by construction (squads sleep and wake whole), so the first member's flag is the squad's.
        return squad.Members.Count > 0 && squad.Members[0].IsDormant;
    }

    private bool IsDefaultDormantSquad(Squad squad)
    {
        for (var i = 0; i < squad.Members.Count; i++)
            if (!IsDefaultDormant(squad.Members[i].Bot)) return false;
        return squad.Members.Count > 0;
    }

    /// <summary>Rolls each awake member's HP into its drop tracker. A recent drop (bleed, mine, anything)
    /// blocks sleep until HP has been stable for <see cref="HpStableSeconds"/> — a sleeper cannot heal,
    /// and re-sleeping a bleeder created a lethal wake/sleep loop in testing.</summary>
    private static void UpdateHpTracking(Squad squad)
    {
        for (var i = 0; i < squad.Members.Count; i++)
        {
            var agent = squad.Members[i];
            var hp = TotalHp(agent);
            if (hp < agent.LastPollHp - 0.5f) agent.LastHpDropTime = Time.time;
            agent.LastPollHp = hp;
        }
    }

    private bool CanSleep(Squad squad)
    {
        // Distance gate first: a squad near a human is simply "in play", not diagnostic. Everything
        // counted below answers the verification question "why does a FAR squad stay awake?".
        // Default-dormant types (per-type toggles) use the tighter ring.
        var gate = IsDefaultDormantSquad(squad) ? _scavSleepDistanceSqr : _sleepDistanceSqr;
        for (var i = 0; i < squad.Members.Count; i++)
            if (MinSqrDistanceToHumans(squad.Members[i].Position) <= gate)
                return false;

        // Temporal hysteresis: a woken squad stays awake long enough for SAIN to actually do something.
        if (Time.time - squad.DormancyWokeAt < WakeCooldownSeconds)
        {
            _farBlockedCooldown++;
            return false;
        }

        // Extract-bound squads may keep ghosting toward the exfil: they only must stay awake
        // once close enough for the actual trigger interaction.
        if (squad.ExtractRequested && ExtractProximityWake(squad) != null) { _farBlockedExtract++; return false; }

        for (var i = 0; i < squad.Members.Count; i++)
        {
            var agent = squad.Members[i];
            var bot = agent.Bot;
            if (bot == null || bot.IsDead || bot.BotState != EBotState.Active) { _farBlockedState++; return false; }
            if (!bot.gameObject.activeSelf) { _farBlockedState++; return false; } // someone else owns the GameObject — never fight over it
            if (agent.SoloExtractRequested && (agent.SoloExtractIsEmergency || NearOwnExfil(agent))) { _farBlockedExtract++; return false; }
            if (agent.Objective.Status == ObjectiveStatus.Looting) { _farBlockedLoot++; return false; } // let the loot animation finish
            if (Time.time < agent.Movement.DoorInteractHoldUntil) { _farBlockedDoor++; return false; } // mid door interaction
            if (bot.Memory != null && (bot.Memory.GoalEnemy != null || bot.Memory.IsUnderFire)) { _farBlockedCombat++; return false; }
            if (_targetedProfileIds.Contains(agent.Player.ProfileId)) { _farBlockedCombat++; return false; }
            // A body mid-heal stays awake: deactivating it freezes the meds animation, Medecine.Using
            // never clears and the bot cannot walk once it wakes.
            if (bot.Medecine is { Using: true }) { _farBlockedHealing++; return false; }
            if (InventoryOperationInFlight(agent)) { _farBlockedHands++; return false; }
            if (Time.time - agent.LastHpDropTime < HpStableSeconds)
            {
                _farBlockedBleeding++;
                TryGhostPatchUp(agent);
                return false;
            }
        }
        return true;
    }

    /// <summary>Non-null when an extract-bound member is close enough to its exfil objective that
    /// the real AI must take over for the trigger interaction. Members not yet dispatched onto an
    /// Exfil waypoint report null: they keep ghosting toward it.</summary>
    private static string ExtractProximityWake(Squad squad)
    {
        for (var i = 0; i < squad.Members.Count; i++)
        {
            var agent = squad.Members[i];
            if (NearOwnExfil(agent))
                return $"{agent} near exfil (<{ExtractWakeDistance:F0}m)";
        }
        return null;
    }

    private static bool NearOwnExfil(Agent agent)
    {
        return agent.Objective.Location is { Category: WaypointCategory.Exfil } exfil
               && (agent.Position - exfil.Position).sqrMagnitude <= ExtractWakeDistanceSqr;
    }

    /// <summary>Simulated self-care for a FAR, out-of-combat bot whose HP keeps dropping. The bleed gate
    /// kept such bots awake for minutes at a time in the release-raid logs (bleeding=59/60 polls), so
    /// strip the negative effects the way a stim would: the drop stops and the 15s gate can clear.
    /// Rate-limited per bot; a drop that survives the patch-up (custom effects, environmental damage)
    /// keeps blocking sleep exactly as before, so the old wake/sleep bleed loop stays impossible.</summary>
    private void TryGhostPatchUp(Agent agent)
    {
        if (Time.time - agent.LastGhostPatchUpAt < GhostPatchUpCooldownSeconds) return;
        agent.LastGhostPatchUpAt = Time.time;
        if (!GhostPatchUp(agent.Player)) return;
        _windowPatchUps++;
        Log.Info($"{agent} ghost patch-up: negative effects removed (HP still dropping out of combat, far from everyone)");
    }

    private void TryGhostPatchUpVanilla(BotOwner bot)
    {
        if (_vanillaPatchUpAt.TryGetValue(bot, out var at) && Time.time - at < GhostPatchUpCooldownSeconds) return;
        _vanillaPatchUpAt[bot] = Time.time;
        if (!GhostPatchUp(bot.GetPlayer)) return;
        _windowPatchUps++;
        Log.Info($"vanilla {bot.GetPlayer?.Profile?.Nickname} ghost patch-up: negative effects removed");
    }

    private static bool GhostPatchUp(Player player)
    {
        try
        {
            var hc = player?.ActiveHealthController;
            if (hc is not { IsAlive: true }) return false;
            hc.RemoveNegativeEffects(EBodyPart.Common);
            return true;
        }
        catch
        {
            // Half-despawned body: nothing to patch.
            return false;
        }
    }

    /// <summary>Why a dormant squad must wake, or null to keep sleeping. The string goes straight to the
    /// wake log line so a single raid read tells premature wakes from legit ones.</summary>
    private string WakeReason(Squad squad)
    {
        // Extract-bound squads ghost their way to the exfil and only wake shortly before its
        // radius, so the real AI handles just the trigger interaction (not the whole walk).
        if (squad.ExtractRequested)
        {
            var extractWake = ExtractProximityWake(squad);
            if (extractWake != null) { _wakeByExtract++; return extractWake; }
        }

        // The awake-bot trigger gets a short grace after sleep entry — that alone killed the 2 Hz
        // ping-pong pairs. Human proximity, damage, targeting and extracts always wake instantly.
        var awakeBotTriggerArmed = Time.time - squad.DormancySleptAt >= SleepGraceSeconds;

        for (var i = 0; i < squad.Members.Count; i++)
        {
            var agent = squad.Members[i];
            if (agent.SoloExtractRequested && (agent.SoloExtractIsEmergency || NearOwnExfil(agent)))
            {
                _wakeByExtract++;
                return agent.SoloExtractIsEmergency ? $"{agent} emergency solo extract" : $"{agent} solo extract near exfil";
            }
            if (_targetedProfileIds.Contains(agent.Player.ProfileId)) { _wakeByTargeted++; return $"{agent} targeted"; }
            // Position-based damage (border minefields at least) lands on inactive bodies, and a sleeper
            // can neither react nor heal — hand it back to SAIN immediately.
            var hp = TotalHp(agent);
            if (hp < agent.DormantHpBaseline - 1f)
            {
                _wakeByDamage++;
                return $"{agent} took {agent.DormantHpBaseline - hp:F0} damage while dormant at {agent.Position}" +
                       (DangerZones.IsInside(agent.Position) ? " (inside a border/minefield zone)" : "");
            }
            var humanSqr = MinSqrDistanceToHumans(agent.Position);
            if (humanSqr <= _wakeDistanceSqr) { _wakeByHuman++; return $"human at {Mathf.Sqrt(humanSqr):F0}m"; }
            if (InScopedView(agent.Position, out var scopeDist)) { _wakeByScope++; return $"in scoped view at {scopeDist:F0}m"; }
            if (awakeBotTriggerArmed && AnyAwakeBotNear(agent.Position, squad)) { _wakeByAwakeBot++; return $"awake bot near {agent}"; }
        }
        return null;
    }

    private float MinSqrDistanceToHumans(Vector3 position)
    {
        // No humans left (all dead/extracted, or a headless lobby between connects) = everyone is far.
        var min = float.MaxValue;
        for (var i = 0; i < _humanPositions.Count; i++)
        {
            var d = (_humanPositions[i] - position).sqrMagnitude;
            if (d < min) min = d;
        }
        return min;
    }

    /// <summary>Any awake AI within the hostile-wake ring of any member, counting only bots that will
    /// REMAIN awake (not dormant, not a fellow sleep candidate this poll).</summary>
    private bool AnyRemainingAwakeBotNear(Squad squad)
    {
        for (var i = 0; i < squad.Members.Count; i++)
            if (AnyAwakeBotNear(squad.Members[i].Position, squad, excludeCandidates: true))
                return true;
        return false;
    }

    /// <summary>
    /// Deliberately faction-agnostic (any awake non-squadmate counts): allegiance rules between modded
    /// factions are a swamp, and waking slightly too often is the safe failure mode. Every awake bot
    /// counts — including ones the floor held awake: they are fully live bots roaming the map, and an
    /// encounter with a sleeper must turn into a normal encounter (muting them cost a whole raid its
    /// scav kills). The 2 Hz ping-pong this could cause is handled by the temporal hysteresis instead.
    /// </summary>
    private bool AnyAwakeBotNear(Vector3 position, Squad ownSquad, bool excludeCandidates = false)
    {
        var players = _gameWorld.AllAlivePlayersList;
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player == null || !player.AIData.IsAI || player.HealthController is not { IsAlive: true }) continue;
            if (DormantProfileIds.Contains(player.ProfileId)) continue;
            if ((player.Position - position).sqrMagnitude > _hostileWakeDistanceSqr) continue;
            // The BTR gunner roams the whole map with its vehicle and never dismounts — counting it here
            // wakes every squad along the BTR route (a human riding the BTR is covered by the human wake).
            if (player.Profile?.Info?.Settings?.Role == WildSpawnType.shooterBTR) continue;

            var owner = player.AIData.BotOwner;
            if (owner != null && ownSquad != null && IsMemberBot(ownSquad, owner)) continue;
            if (excludeCandidates && owner != null && IsCandidateBot(owner)) continue;
            return true;
        }
        return false;
    }

    private static bool IsMemberBot(Squad squad, BotOwner owner)
    {
        for (var i = 0; i < squad.Members.Count; i++)
            if (ReferenceEquals(squad.Members[i].Bot, owner)) return true;
        return false;
    }

    private bool IsCandidateBot(BotOwner owner)
    {
        for (var i = 0; i < _sleepCandidates.Count; i++)
            if (IsMemberBot(_sleepCandidates[i], owner)) return true;
        return false;
    }

    private static float TotalHp(Agent agent)
    {
        var hc = agent.Player?.HealthController;
        if (hc == null || !hc.IsAlive) return 0f;
        return hc.GetBodyPartHealth(EBodyPart.Common, true).Current;
    }

    // ── ORBIT squad sleep / wake mechanics ──────────────────────────────

    // An operation that is still registered after this long is stuck for good, asleep or not.
    private const float InventoryBusyMaxHoldSeconds = 15f;

    /// <summary>
    /// True while the body has an inventory operation in flight (a reload, a magazine check, an item going in
    /// or out of the hands). BSG keeps such an operation registered as active until its hands animation ends,
    /// and refuses every later operation that touches the same item or the same cells ("Can not execute").
    /// Deactivating the body freezes the animation, so the operation would stay active for the whole sleep:
    /// Interchange raid, mikufilck fell asleep 9 frames after its fight and 15 of its ghost loot operations
    /// were refused. Same idea as the mid-heal gate above. Bounded, so an operation that never completes on
    /// an awake body cannot keep its squad awake for the rest of the raid.
    /// </summary>
    private static bool InventoryOperationInFlight(Agent agent)
    {
        try
        {
            var controller = agent.Player?.InventoryController;
            if (controller == null) return false;
            ItemEventArgs first = null;
            var count = 0;
            foreach (var activeEvent in controller.SelectEvents<ItemEventArgs>())
            {
                first ??= activeEvent;
                count++;
            }
            if (count == 0)
            {
                agent.InventoryBusySince = -1f;
                return false;
            }
            if (float.IsPositiveInfinity(agent.InventoryBusySince)) return false; // released as stuck, until the list empties
            if (agent.InventoryBusySince < 0f)
            {
                agent.InventoryBusySince = Time.time;
                Log.Debug($"{agent} stays awake: {count} inventory operation(s) in flight ({first.GetType().Name} on {first.Item?.LocalizedName() ?? "?"}, {first.Status})");
                return true;
            }
            if (Time.time - agent.InventoryBusySince < InventoryBusyMaxHoldSeconds) return true;
            Log.Info($"{agent} still has {count} inventory operation(s) in flight after {InventoryBusyMaxHoldSeconds:F0}s ({first.GetType().Name} on {first.Item?.LocalizedName() ?? "?"}, {first.Status}): stuck, no longer holding the body awake");
            agent.InventoryBusySince = float.PositiveInfinity;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void SleepSquad(Squad squad)
    {
        var minHumanSqr = float.MaxValue;
        for (var i = 0; i < squad.Members.Count; i++)
        {
            SleepAgent(squad.Members[i]);
            var d = MinSqrDistanceToHumans(squad.Members[i].Position);
            if (d < minHumanSqr) minHumanSqr = d;
        }
        squad.DormancySleptAt = Time.time;
        _windowSleeps++;
        var humanDist = minHumanSqr < float.MaxValue ? $"{Mathf.Sqrt(minHumanSqr):F0}m" : "none";
        Log.Info($"{squad} dormant (nearest human {humanDist}, {squad.Members.Count} bots asleep, {_dormantAgents.Count} total dormant)");
    }

    private void SleepAgent(Agent agent)
    {
        var bot = agent.Bot;

        try
        {
            // Questing Bots' proven recipe, in this order.
            bot.DecisionQueue.Clear();
            bot.Memory.GoalEnemy = null;
            bot.PatrollingData.Pause();
            bot.gameObject.SetActive(false);
            ThrottleBrain(bot);
        }
        catch (System.Exception e)
        {
            // A half-slept bot is recoverable (GameObject state wins); a thrown poll tick is not.
            Log.Error($"{agent} sleep recipe failed: {e}");
        }

        agent.Look.Target = null;
        agent.DormantHpBaseline = TotalHp(agent);
        agent.IsDormant = true;
        _dormantAgents.Add(agent);
        DormantProfileIds.Add(agent.Player.ProfileId);
    }

    private void WakeSquad(Squad squad, string reason)
    {
        for (var i = 0; i < squad.Members.Count; i++)
            WakeAgent(squad.Members[i]);
        squad.DormancyWokeAt = Time.time;
        _windowWakes++;
        Log.Info($"{squad} awake: {reason} ({_dormantAgents.Count} still dormant)");
    }

    private void WakeAgent(Agent agent)
    {
        var bot = agent.Bot;
        var player = agent.Player;

        agent.IsDormant = false;
        // Fresh tracking state so the bleed gate starts from the wake-time HP.
        agent.LastPollHp = TotalHp(agent);
        _dormantAgents.Remove(agent);
        DormantProfileIds.Remove(player.ProfileId);

        if (bot == null || bot.IsDead) return;

        try
        {
            // Questing Bots' proven recipe: PostActivate is mandatory, deactivation leaves BotState=NonActive.
            UnthrottleBrain(bot);
            bot.gameObject.SetActive(true);
            bot.PatrollingData.Unpause();
            bot.PostActivate();
            // Ground and fall bookkeeping were frozen while dormant (DormantGroundCollisionPatch): restart
            // them from the current height so a ghost that walked downhill does not "land" from its
            // sleep altitude on the first live tick.
            player.MovementContext?.ResetFlying();
            // A heal that was in flight when the body went inactive never finished its animation and
            // leaves Medecine.Using stuck; putting the main weapon back in hands clears the state.
            if (bot.Medecine is { Using: true })
            {
                Log.Info($"{agent} woke with a stale meds state, taking the main weapon back in hands");
                try { bot.WeaponManager?.Selector?.TakeMainWeapon(); } catch { }
            }

            // Gear equipped while asleep never reached the hands (no animation on an inactive body): refresh
            // the weapon list and redraw the main weapon now that the body is live again.
            if (agent.GhostHandsResync)
            {
                agent.GhostHandsResync = false;
                Orbit.Looting.WeaponSwap.WeaponSwapper.ResyncHandsAfterWake(bot, agent.ToString());
            }

            // Ghost movement can leave the body marginally off-mesh (or squarely off it when the wake
            // lands mid-segment on a slope); snap back before the mover resumes. Path corners are
            // guaranteed on-mesh, so they are the fallback when the local sample fails.
            if (UnityEngine.AI.NavMesh.SamplePosition(agent.Position, out var hit, 2f, UnityEngine.AI.NavMesh.AllAreas))
            {
                var snapDist = (hit.position - agent.Position).magnitude;
                if (snapDist > 0.05f) Log.Debug($"{agent} wake navmesh snap: {snapDist:F2}m");
                player.Teleport(hit.position);
            }
            else if (TryNearestPathCorner(agent, out var cornerPos))
            {
                Log.Warning($"{agent} woke OFF-MESH at {agent.Position} — snapping to path corner {cornerPos}");
                player.Teleport(cornerPos);
            }
            else
            {
                Log.Warning($"{agent} woke OFF-MESH at {agent.Position} with no path corner to snap to — waking in place");
            }

            // Unity forgets Physics.IgnoreCollision pairs while colliders are disabled — re-apply the per-door
            // collision verdicts or the bot walks through closed doors (or gets shoved by open ones).
            _doorSystem.ResyncBot(player.CharacterController.GetCollider(), player.POM.Collider);

            // Back to the ORBIT-driven mover state (StartMovement skipped these while dormant).
            bot.Mover.Stop();
            bot.Mover.Pause = true;

            // Re-path from the real wake position if a move was in flight: the ghost walk followed the old
            // corners, but doors it phased through are still closed and the mesh may differ locally.
            var movement = agent.Movement;
            if (movement.Status == MovementStatus.Moving && movement.Target != Movement.Infinity)
                _movementSystem.MoveToByPath(agent, movement.Target,
                    movement.Pose, movement.Speed, movement.Prone, movement.Sprint, movement.Urgency);

            Log.Debug($"{agent} wake: state={bot.BotState} pathInFlight={movement.Status == MovementStatus.Moving}");
        }
        catch (System.Exception e)
        {
            // Must not abort the squad loop: a bot left inactive here would be a permanent ghost.
            Log.Error($"{agent} wake recipe failed (bot may be stuck inactive): {e}");
        }
    }

    private static bool TryNearestPathCorner(Agent agent, out Vector3 pos)
    {
        pos = default;
        var movement = agent.Movement;
        if (!movement.HasPath) return false;
        var current = Mathf.Clamp(movement.CurrentCorner, 0, movement.Path.Length - 1);
        var prev = Mathf.Max(0, current - 1);
        var p = agent.Position;
        pos = (movement.Path[prev] - p).sqrMagnitude < (movement.Path[current] - p).sqrMagnitude
            ? movement.Path[prev]
            : movement.Path[current];
        return true;
    }


    // ── Scoped wake ─────────────────────────────────────────────────────

    /// <summary>
    /// Once per poll: if the local player is aiming through a rendering optic, derive a forward wake
    /// distance from the optic camera's FOV (magnification = tan(baseFov/2) / tan(opticFov/2), so any
    /// modded scope scales correctly) and a test cone wider than the scope view. Headless clients have
    /// no camera and the whole feature stays inert.
    /// </summary>
    private void UpdateScopeState()
    {
        _scopeSources.Clear();
        if (!_scopedWakeEnabled) return;
        var wakeBase = Mathf.Sqrt(_wakeDistanceSqr);
        var localCameraActive = false;

        // Local player: read the real optic camera (exact FOV, any modded scope).
        try
        {
            if (CameraManager.Exist)
            {
                var cameraManager = CameraManager.Instance;
                var mainCamera = cameraManager?.Camera;
                if (mainCamera != null)
                {
                    var opticManager = cameraManager.OpticCameraManager;
                    var scoped = opticManager != null && opticManager.IsAnyOpticCameraRendering;
                    if (!scoped)
                    {
                        // Cache the base FOV while unscoped: some sights zoom the MAIN camera too, so
                        // reading it mid-ADS would understate the magnification (ABC does the same).
                        if (mainCamera.fieldOfView > 0f) _unscopedFovDeg = mainCamera.fieldOfView;
                    }
                    else
                    {
                        var opticCamera = opticManager.Camera;
                        if (opticCamera != null && opticCamera.enabled && opticCamera.fieldOfView > 0f)
                        {
                            var magnification = Mathf.Tan(_unscopedFovDeg * 0.5f * Mathf.Deg2Rad)
                                                / Mathf.Max(0.001f, Mathf.Tan(opticCamera.fieldOfView * 0.5f * Mathf.Deg2Rad));
                            var dist = Mathf.Clamp(wakeBase * magnification, wakeBase, _scopedWakeMax);
                            var halfAngleDeg = opticCamera.fieldOfView * 0.5f + ScopedWakeConeMarginDeg;
                            _scopeSources.Add(new ScopeSource
                            {
                                Pos = mainCamera.transform.position,
                                Fwd = mainCamera.transform.forward,
                                DistSqr = dist * dist,
                                ConeCos = Mathf.Cos(halfAngleDeg * Mathf.Deg2Rad),
                            });
                            localCameraActive = true;
                        }
                    }
                }
            }
        }
        catch
        {
            // Camera plumbing mid-teardown (raid end) — no local source this poll.
        }

        // Remote humans (Fika clients, and every human on headless): approximate from replicated state.
        // ADS flag and look direction replicate for animation purposes; magnification comes from the
        // mounted optic's SightComponent (current zoom, max as fallback). Iron sights / low zooms are
        // skipped — the normal wake ring already covers them.
        var players = _gameWorld.AllAlivePlayersList;
        for (var i2 = 0; i2 < players.Count; i2++)
        {
            var player = players[i2];
            if (player == null) continue;
            try
            {
                if (player.AIData.IsAI) continue;
                if (player.IsYourPlayer && localCameraActive) continue; // exact camera source already added
                if (player.HealthController is not { IsAlive: true }) continue;

                var firearm = player.HandsController as Player.FirearmController;
                if (firearm == null || !firearm.IsAiming) continue;

                var magnification = 1f;
                var weapon = firearm.Item;
                if (weapon != null)
                {
                    foreach (var sight in weapon.GetItemComponentsInChildren<SightComponent>(false))
                    {
                        var zoom = sight.GetCurrentOpticZoom();
                        if (zoom <= 1f) zoom = sight.GetMaxOpticZoom();
                        if (zoom > magnification) magnification = zoom;
                    }
                }
                if (magnification < 1.5f) continue;

                var fwd = player.LookDirection;
                if (fwd.sqrMagnitude < 0.01f) continue;
                fwd.Normalize();

                var dist = Mathf.Clamp(wakeBase * magnification, wakeBase, _scopedWakeMax);
                // Cone: the optic FOV a scope of this magnification would have, plus the sweep margin.
                var opticHalfDeg = Mathf.Atan(Mathf.Tan(_unscopedFovDeg * 0.5f * Mathf.Deg2Rad) / magnification) * Mathf.Rad2Deg;
                var halfAngleDeg = opticHalfDeg + ScopedWakeConeMarginDeg;
                _scopeSources.Add(new ScopeSource
                {
                    Pos = player.Position + new Vector3(0f, 1.5f, 0f),
                    Fwd = fwd,
                    DistSqr = dist * dist,
                    ConeCos = Mathf.Cos(halfAngleDeg * Mathf.Deg2Rad),
                });
            }
            catch
            {
                // Observed-player replication quirks must never break the poll.
            }
        }
    }

    /// <summary>Cone + range + occlusion test against every active scope source. The raycast uses the
    /// same terrain mask as ORBIT's arrival LoS check, so bots behind hills/buildings sleep on.</summary>
    private bool InScopedView(Vector3 botPosition, out float distance)
    {
        distance = 0f;
        for (var i = 0; i < _scopeSources.Count; i++)
        {
            var source = _scopeSources[i];
            var to = botPosition - source.Pos;
            var distSqr = to.sqrMagnitude;
            if (distSqr > source.DistSqr || distSqr < 1f) continue;

            var d = Mathf.Sqrt(distSqr);
            if (Vector3.Dot(to / d, source.Fwd) < source.ConeCos) continue;

            var chest = botPosition + new Vector3(0f, 1.4f, 0f);
            var dir = chest - source.Pos;
            var chestDist = dir.magnitude;
            if (Physics.Raycast(source.Pos, dir / chestDist, chestDist, LayersMaskController.HighPolyWithTerrainMask)) continue;

            distance = d;
            return true;
        }
        return false;
    }

    // ── Ghost skirmishes ────────────────────────────────────────────────

    /// <summary>
    /// Simulated off-screen fights: when two hostile dormant units (ORBIT squads or vanilla groups)
    /// cross paths, roll a gear-weighted skirmish and apply the casualties through the REAL death
    /// pipeline (victim briefly re-activated, then killed), so corpses ragdoll, register as loot
    /// waypoints and can be scavenged by other ghosts. Survivors stay dormant and walk on: the world
    /// writes its own history without a single frame of real combat.
    /// </summary>
    private void ResolveGhostSkirmishes(List<Squad> squads)
    {
        BuildGhostUnits(squads);
        if (_ghostUnits.Count < 2) return;

        for (var i = 0; i < _ghostUnits.Count; i++)
        {
            for (var j = i + 1; j < _ghostUnits.Count; j++)
            {
                var a = _ghostUnits[i];
                var b = _ghostUnits[j];
                // Who fights whom is the game's own verdict, asked to both groups (see UnitsHostile).
                if (!UnitsHostile(a, b)) continue;

                // A unit mid-fight can't be pulled into a second one until its window closes.
                if (UnitInFight(a) || UnitInFight(b)) continue;

                var distSqr = MinUnitDistanceSqr(a, b, out var posA, out var posB);
                var reach = Mathf.Max(a.Reach, b.Reach);
                if (distSqr > reach * reach) continue;
                var dist = Mathf.Sqrt(distSqr);

                // Terrain/structure LoS between the closest members, THREE rays that must ALL be
                // clear (head height, chest height, laterally offset): a single torso-height ray can
                // slip between tree trunks on forest maps and start fights between units that could
                // never actually see each other. No cooldown is burned on a block.
                if (!ClearFightLos(posA, posB)) continue;

                var pairKey = string.CompareOrdinal(a.Key, b.Key) < 0 ? a.Key + "|" + b.Key : b.Key + "|" + a.Key;
                if (_skirmishPairSeenAt.TryGetValue(pairKey, out var seenAt) && Time.time - seenAt < _skirmishCooldown)
                    continue;

                // Point-blank contact is guaranteed; beyond that the chance falls with distance, shaded
                // by how eager both sides are to engage (a Cautious/Rat squad shadows, a GigaChad pushes).
                float contactChance;
                if (dist <= SkirmishGuaranteedContactRange)
                    contactChance = 1f;
                else
                {
                    var t = (dist - SkirmishGuaranteedContactRange) / Mathf.Max(1f, reach - SkirmishGuaranteedContactRange);
                    contactChance = Mathf.Min(0.95f, _contactChanceMul * ContactAggressionMul(a, b) * Mathf.Lerp(SkirmishChanceClose, SkirmishChanceFar, t));
                }
                if (Random.value > contactChance)
                {
                    // A failed roll burns only a SHORT cooldown: the pair re-rolls within seconds while
                    // still in range, instead of ghosting through each other for 3 minutes.
                    _skirmishPairSeenAt[pairKey] = Time.time - Mathf.Max(0f, _skirmishCooldown - SkirmishShadowCooldownSeconds);
                    Log.Debug($"GHOST SKIRMISH: {a.Label} and {b.Label} shadowed each other at {dist:F0}m, no contact (chance {contactChance:P0})");
                    continue;
                }
                _skirmishPairSeenAt[pairKey] = Time.time;

                if (_fightsMode == GhostFightsMode.Real)
                    WakeBothForRealFight(a, b, dist);
                else
                    ResolveFight(a, b, dist, posA, posB);
            }
        }
    }

    // Pair verdicts from the hostility settings, cached for the raid: BSG's test rolls the "chanced enemy"
    // odds on every call, so asking again at 2 Hz would turn every chanced pair hostile within seconds.
    private readonly Dictionary<string, bool> _hostilityCache = new();
    private readonly HashSet<string> _hostilityRolesLogged = new();

    /// <summary>
    /// Whether two ghost units would fight, by the game's own rules rather than a side shortcut. The old test
    /// (two Savage-side units never fight, anything involving a PMC does) was wrong for every modded faction,
    /// which all spawn on the Savage side: RUAF and Black Division are enemies of the scavs and of each other,
    /// scavs attack UNTAR, cultists are hostile to most of them, while UNTAR and RUAF only WARN each other;
    /// and PMC squads of the same faction are not always hostile either. BotsGroup.IsPlayerEnemy is what an
    /// awake bot runs when it sees someone: the bot-type enemy / friend / warn lists of the location's
    /// hostility settings (where MoreBotsAPI factions, SPT's PMC config, ABPS and RvR write theirs), then the
    /// per-side behaviour. One hostile direction is enough, the other side defends itself. Grudges picked up
    /// while awake (BotsGroup.Enemies) are read live on top of the cached verdict.
    /// </summary>
    private bool UnitsHostile(GhostUnit a, GhostUnit b)
    {
        var botA = LeadBot(a);
        var botB = LeadBot(b);
        if (botA == null || botB == null) return !(a.IsSavage && b.IsSavage);
        try
        {
            var groupA = botA.BotsGroup;
            var groupB = botB.BotsGroup;
            if (groupA == null || groupB == null) return !(a.IsSavage && b.IsSavage);
            if (ReferenceEquals(groupA, groupB)) return false;
            // IPlayer handles come from the game world's bridge: upcasting Player to IPlayer here would drag
            // the voice-chat assembly (Player implements IDissonancePlayer) into ORBIT's compile references.
            var playerA = _gameWorld.GetAlivePlayerBridgeByProfileID(botA.ProfileId)?.iPlayer;
            var playerB = _gameWorld.GetAlivePlayerBridgeByProfileID(botB.ProfileId)?.iPlayer;
            if (playerA == null || playerB == null) return !(a.IsSavage && b.IsSavage);
            if (groupA.IsEnemy(playerB) || groupB.IsEnemy(playerA))
            {
                // BSG registers known hostiles on a group's enemy list as soon as they meet or spawn, so most
                // hostile pairs end here: log them too, or the matrix in the log only shows the friendly ones.
                LogHostilityOnce(botA, botB, true, "already on each other's enemy list");
                return true;
            }

            var key = string.CompareOrdinal(a.Key, b.Key) < 0 ? a.Key + "|" + b.Key : b.Key + "|" + a.Key;
            if (_hostilityCache.TryGetValue(key, out var cached)) return cached;
            var aHatesB = groupA.IsPlayerEnemy(playerB);
            var bHatesA = groupB.IsPlayerEnemy(playerA);
            var hostile = aHatesB || bHatesA;
            _hostilityCache[key] = hostile;

            LogHostilityOnce(botA, botB, hostile, $"settings: first attacks second {aHatesB}, second attacks first {bHatesA}");
            return hostile;
        }
        catch
        {
            return !(a.IsSavage && b.IsSavage); // torn-down group: the old side rule
        }
    }

    /// <summary>The weapon a bot brings to a simulated fight: what it holds, or for a sleeper the better
    /// weapon it equipped while asleep and has not drawn yet (hands are resynchronised at wake). Sounds keep
    /// following the weapon in hands, the only one with a live sound player.</summary>
    private Item FightWeaponOf(BotOwner bot)
    {
        if (bot == null) return null;
        var agent = _botRoster.GetAgent(bot);
        if (agent != null && agent.IsDormant && agent.GhostBestWeapon != null) return agent.GhostBestWeapon;
        return bot.GetPlayer?.HandsController?.Item;
    }

    /// <summary>One Info line per pair of bot types and verdict per raid: the hostility matrix as the game
    /// sees it.</summary>
    private void LogHostilityOnce(BotOwner botA, BotOwner botB, bool hostile, string why)
    {
        var roleA = botA.Profile?.Info?.Settings?.Role.ToString() ?? "?";
        var roleB = botB.Profile?.Info?.Settings?.Role.ToString() ?? "?";
        var roleKey = string.CompareOrdinal(roleA, roleB) < 0 ? roleA + "|" + roleB : roleB + "|" + roleA;
        if (!_hostilityRolesLogged.Add(roleKey + (hostile ? "+" : "-"))) return;
        Log.Info($"GHOST HOSTILITY: {roleA} vs {roleB}: {(hostile ? "hostile" : "not hostile")} ({why})");
    }

    private static BotOwner LeadBot(GhostUnit unit)
        => unit.Agents.Count > 0 ? unit.Agents[0].Bot
            : unit.VanillaBots.Count > 0 ? unit.VanillaBots[0]
            : null;

    private void BuildGhostUnits(List<Squad> squads)
    {
        _ghostUnits.Clear();
        _darkness = CurrentDarkness();

        for (var i = 0; i < squads.Count; i++)
        {
            var squad = squads[i];
            if (squad == null || squad.Members.Count == 0 || !IsSquadDormant(squad)) continue;
            var lead = squad.Members[0];
            var unit = new GhostUnit
            {
                Key = lead.Player.ProfileId,
                Label = squad.ToString(),
                IsSavage = lead.Player.Side == EPlayerSide.Savage,
                Squad = squad,
            };
            for (var m = 0; m < squad.Members.Count; m++)
            {
                unit.Agents.Add(squad.Members[m]);
                unit.Reach = Mathf.Max(unit.Reach, UnitMemberReach(squad.Members[m].Bot));
                unit.KillRange = Mathf.Max(unit.KillRange, WeaponKillRange(squad.Members[m].Player));
                unit.NightCapable |= HasNightVision(squad.Members[m].Bot);
            }
            ApplyNightReach(unit);
            _ghostUnits.Add(unit);
        }

        foreach (var kv in _vanillaGroups)
        {
            var group = kv.Value;
            if (group.Count == 0 || !_vanillaDormant.Contains(group[0])) continue;
            var lead = group[0].GetPlayer;
            if (lead == null) continue;
            var unit = new GhostUnit
            {
                Key = lead.ProfileId,
                Label = $"vanilla group ({lead.Profile?.Nickname} +{group.Count - 1})",
                IsSavage = lead.Side == EPlayerSide.Savage,
                VanillaKey = kv.Key,
            };
            for (var m = 0; m < group.Count; m++)
            {
                unit.VanillaBots.Add(group[m]);
                unit.Reach = Mathf.Max(unit.Reach, UnitMemberReach(group[m]));
                unit.KillRange = Mathf.Max(unit.KillRange, WeaponKillRange(group[m].GetPlayer));
                unit.NightCapable |= HasNightVision(group[m]);
            }
            ApplyNightReach(unit);
            _ghostUnits.Add(unit);
        }
    }

    private static float MinUnitDistanceSqr(GhostUnit a, GhostUnit b, out Vector3 closestA, out Vector3 closestB)
    {
        var min = float.MaxValue;
        closestA = default;
        closestB = default;
        for (var i = 0; i < a.Count; i++)
        {
            var pa = i < a.Agents.Count ? a.Agents[i].Position : a.VanillaBots[i - a.Agents.Count].GetPlayer.Position;
            for (var j = 0; j < b.Count; j++)
            {
                var pb = j < b.Agents.Count ? b.Agents[j].Position : b.VanillaBots[j - b.Agents.Count].GetPlayer.Position;
                var d = (pa - pb).sqrMagnitude;
                if (d < min) { min = d; closestA = pa; closestB = pb; }
            }
        }
        return min;
    }

    /// <summary>Engagement reach of one member: base range scaled by the best optic on the weapon in
    /// hand (current zoom, max as fallback). Cached: weapons rarely change.</summary>
    private float UnitMemberReach(BotOwner bot)
    {
        if (bot == null) return SkirmishBaseDetectRange;
        if (_opticCache.TryGetValue(bot, out var cached) && Time.time - cached.at < 60f)
            return Mathf.Min(SkirmishReachCap, SkirmishBaseDetectRange * cached.mag);

        var mag = 1f;
        try
        {
            var weapon = FightWeaponOf(bot);
            if (weapon != null)
            {
                foreach (var sight in weapon.GetItemComponentsInChildren<SightComponent>(false))
                {
                    var zoom = sight.GetCurrentOpticZoom();
                    if (zoom <= 1f) zoom = sight.GetMaxOpticZoom();
                    if (zoom > mag) mag = zoom;
                }
            }
        }
        catch
        {
            // Disposed/edge-case inventory — base reach.
        }
        _opticCache[bot] = (mag, Time.time);
        return Mathf.Min(SkirmishReachCap, SkirmishBaseDetectRange * mag);
    }

    // ── Ghost hearing ──────────────────────────────────────────────────────────────────────────────
    // A sleeper has no ears: its body is inactive, SAIN is not running. Firefights therefore register here
    // as NOISE events and sleeping squads roll whether to go and look, by personality. Two sources: the
    // simulated ghost fights (their window and their weapons are known) and real gunfire, read from the
    // game's own AI sound event, the one BSG's hearing sensor subscribes to, so the player's shots and any
    // awake bot's shots count. A single stray shot is not a fight: a real cluster needs a few shots first.
    private const float NoiseRangeLoud = 350f;
    private const float NoiseRangeSuppressed = 120f;
    private const float NoiseMinDistance = 40f;          // closer than this the skirmish / wake logic owns it
    private const float NoiseClusterRadius = 60f;        // shots this close together are the same firefight
    private const float NoiseLingerSeconds = 45f;        // a fight stays "audible" this long after its last shot
    private const int NoiseMinRealShots = 4;
    private const float NoiseReactionCooldownSeconds = 150f;
    private const float NoisePollIntervalSeconds = 2f;

    private sealed class NoiseEvent
    {
        public Vector3 Position;
        public float Range;
        public float LastShotAt;
        public int Shots;
        public bool Simulated;
        public readonly HashSet<int> SourceSquadIds = new();
        public readonly HashSet<int> RolledSquadIds = new();
    }

    private readonly List<NoiseEvent> _noises = new();
    private readonly bool _hearingEnabled;
    private bool _soundHooked;
    private float _nextNoisePollAt;

    private void HookGunfire()
    {
        if (!_hearingEnabled || _soundHooked) return;
        try
        {
            var dispatcher = Singleton<GlobalEventDispatcher>.Instance;
            if (dispatcher == null) return;
            dispatcher.OnSoundPlayed += OnAiSoundPlayed;
            _soundHooked = true;
        }
        catch (System.Exception e)
        {
            Log.Debug($"Ghost hearing: could not subscribe to the AI sound event ({e.Message}), real gunfire will not be heard");
        }
    }

    public void Dispose()
    {
        NativeGhostSystem.Clear();
        if (!_soundHooked) return;
        try { Singleton<GlobalEventDispatcher>.Instance.OnSoundPlayed -= OnAiSoundPlayed; } catch { }
        _soundHooked = false;
    }

    // Fires for every AI-audible sound in the raid: keep it to a type test and a short list walk.
    private void OnAiSoundPlayed(IPlayer player, Vector3 position, float power, AISoundType type)
    {
        if (type != AISoundType.gun && type != AISoundType.silencedGun) return;
        try
        {
            var range = type == AISoundType.gun ? NoiseRangeLoud : NoiseRangeSuppressed;
            var sourceSquadId = -1;
            if (player is Player shooter && shooter.IsAI)
            {
                var agent = _botRoster.GetAgent(shooter.AIData?.BotOwner);
                // A sleeper does not fire real rounds; a dormant shooter here is a simulated-fight artefact.
                if (agent != null && agent.IsDormant) return;
                if (agent?.Squad != null) sourceSquadId = agent.Squad.Id;
            }
            RegisterNoise(position, range, Time.time, simulated: false, sourceSquadId, -1);
        }
        catch
        {
            // Never let a listener break the game's sound dispatch.
        }
    }

    private void RegisterNoise(Vector3 position, float range, float lastShotAt, bool simulated, int sourceA, int sourceB)
    {
        NoiseEvent noise = null;
        for (var i = 0; i < _noises.Count; i++)
        {
            var n = _noises[i];
            if (n.Simulated != simulated) continue;
            if ((n.Position - position).sqrMagnitude > NoiseClusterRadius * NoiseClusterRadius) continue;
            noise = n;
            break;
        }
        if (noise == null)
        {
            noise = new NoiseEvent { Position = position, Simulated = simulated };
            _noises.Add(noise);
        }
        noise.Range = Mathf.Max(noise.Range, range);
        noise.LastShotAt = Mathf.Max(noise.LastShotAt, lastShotAt);
        noise.Shots++;
        if (sourceA >= 0) noise.SourceSquadIds.Add(sourceA);
        if (sourceB >= 0) noise.SourceSquadIds.Add(sourceB);
    }

    /// <summary>A simulated fight is audible for its whole window, as loud as its loudest gun.</summary>
    private void RegisterFightNoise(GhostUnit a, Vector3 posA, GhostUnit b, Vector3 posB, float duration)
    {
        if (!_hearingEnabled) return;
        var loud = UnitHasLoudGun(a) || UnitHasLoudGun(b);
        RegisterNoise((posA + posB) * 0.5f, loud ? NoiseRangeLoud : NoiseRangeSuppressed, Time.time + duration,
            simulated: true, a.Squad?.Id ?? -1, b.Squad?.Id ?? -1);
    }

    private bool UnitHasLoudGun(GhostUnit unit)
    {
        for (var i = 0; i < unit.Agents.Count; i++)
        {
            var sound = WeaponSoundFromProfile(unit.Agents[i].Player?.ProfileId);
            if (sound == null || !sound.IsSilenced) return true; // unknown gun: assume loud
        }
        for (var i = 0; i < unit.VanillaBots.Count; i++)
        {
            var sound = WeaponSoundFromProfile(unit.VanillaBots[i].GetPlayer?.ProfileId);
            if (sound == null || !sound.IsSilenced) return true;
        }
        return false;
    }

    /// <summary>How likely a sleeping squad is to push a firefight it hears. PMCs follow their SAIN
    /// archetype; PlayerScavs are opportunists; everything else (bot scavs, bosses, factions) stays on its
    /// own business, as it does awake.</summary>
    private static float NoiseCuriosity(Squad squad)
    {
        if (squad.Personality != null)
        {
            switch (squad.Archetype)
            {
                case PersonalityArchetype.VeryAggressive: return 0.85f;
                case PersonalityArchetype.Aggressive: return 0.6f;
                case PersonalityArchetype.Cautious: return 0.08f;
                case PersonalityArchetype.Timmy: return 0.03f;
                default: return 0.3f;
            }
        }
        var lead = squad.Members.Count > 0 ? squad.Members[0].Bot : null;
        return lead?.Profile != null && lead.Profile.WillBeAPlayerScav() ? 0.2f : 0f;
    }

    private void PollGhostHearing(List<Squad> squads)
    {
        if (!_hearingEnabled) return;
        HookGunfire(); // the dispatcher may not exist yet when the system is constructed
        var now = Time.time;
        if (now < _nextNoisePollAt) return;
        _nextNoisePollAt = now + NoisePollIntervalSeconds;

        for (var i = _noises.Count - 1; i >= 0; i--)
            if (now - _noises[i].LastShotAt > NoiseLingerSeconds) _noises.RemoveAt(i);
        if (_noises.Count == 0) return;

        for (var s = 0; s < squads.Count; s++)
        {
            var squad = squads[s];
            if (squad == null || squad.Members.Count == 0 || !IsSquadDormant(squad)) continue;
            if (squad.ExtractRequested || squad.InvestigateNoisePosition.HasValue) continue;
            if (now < squad.GhostFightUntil || now - squad.LastNoiseReactionAt < NoiseReactionCooldownSeconds) continue;
            var curiosity = NoiseCuriosity(squad);
            if (curiosity <= 0f) continue;

            var listener = squad.Members[0].Position;
            for (var n = 0; n < _noises.Count; n++)
            {
                var noise = _noises[n];
                if (!noise.Simulated && noise.Shots < NoiseMinRealShots) continue;
                if (noise.SourceSquadIds.Contains(squad.Id) || noise.RolledSquadIds.Contains(squad.Id)) continue;
                var dist = Vector3.Distance(listener, noise.Position);
                if (dist < NoiseMinDistance || dist > noise.Range) continue;

                noise.RolledSquadIds.Add(squad.Id); // one roll per squad per firefight, whatever the outcome
                // A fight at the edge of earshot is less tempting than one next door.
                var chance = curiosity * Mathf.Lerp(1f, 0.5f, dist / noise.Range);
                if (Random.value > chance)
                {
                    Log.Debug($"GHOST HEARING: {squad} ({squad.Archetype}) heard {(noise.Simulated ? "a ghost fight" : "real gunfire")} {dist:F0}m away and ignored it (chance {chance:P0})");
                    continue;
                }
                squad.InvestigateNoisePosition = noise.Position;
                squad.LastNoiseReactionAt = now;
                _windowNoiseReactions++;
                Log.Info($"GHOST HEARING: {squad} ({(squad.Personality != null ? squad.Archetype.ToString() : "PlayerScav")}) heard {(noise.Simulated ? "a ghost fight" : $"real gunfire ({noise.Shots} shots)")} {dist:F0}m away (audible to {noise.Range:F0}m) and goes to look (chance {chance:P0})");
                break;
            }
        }
    }

    private int _windowNoiseReactions;

    // Night model. A unit with no night vision (NVG or thermal goggles on the head, thermal / NV scope on
    // the gun in hand) loses most of its detection reach in the dark, optics included: magnification does
    // not help an eye that sees nothing. It also fights at a handicap against a unit that can see (first
    // shots, target acquisition); two blind sides or two equipped sides stay even, they simply meet
    // closer. Darkness follows the raid clock: full from 22:30 to 04:30 with a one-hour ramp on each side.
    // Factory night is always dark; Factory day, Labs and the Labyrinth are lit interiors, never night.
    private const float NightBlindReach = 35f;
    private const float NightBlindStrengthMul = 0.7f;
    private const string SpecialScopeParentId = "55818aeb4bdc2ddc698b456a"; // thermal and NV scopes

    private static float CurrentDarkness()
    {
        try
        {
            var world = Singleton<GameWorld>.Instance;
            if (world == null) return 0f;
            var map = (world.LocationId ?? "").ToLowerInvariant();
            if (map == "factory4_night") return 1f;
            if (map == "factory4_day" || map == "laboratory" || map == "labyrinth") return 0f;
            var gameTime = world.GameDateTime;
            if (gameTime == null) return 0f;
            var hour = (float)gameTime.Calculate().TimeOfDay.TotalHours;
            if (hour >= 22.5f || hour < 4.5f) return 1f;
            if (hour >= 21.5f) return Mathf.InverseLerp(21.5f, 22.5f, hour);
            if (hour < 5.5f) return 1f - Mathf.InverseLerp(4.5f, 5.5f, hour);
            return 0f;
        }
        catch
        {
            return 0f; // no clock on this map: treat as day, nothing changes
        }
    }

    private bool HasNightVision(BotOwner bot)
    {
        if (bot == null) return false;
        if (_nightVisionCache.TryGetValue(bot, out var cached) && Time.time - cached.at < 60f)
            return cached.has;
        var has = false;
        try
        {
            // NVG on the helmet: BSG's own flag, set when the headwear carries a NightVisionComponent.
            has = bot.NightVision is { HaveNightVision: true };
            var player = bot.GetPlayer;
            if (!has && player != null)
            {
                // Thermal goggles are a separate component that BSG's flag ignores.
                if (player.Inventory?.Equipment?.GetSlot(EquipmentSlot.Headwear)?.ContainedItem is CompoundItem headwear)
                {
                    foreach (var _ in headwear.GetItemComponentsInChildren<ThermalVisionComponent>())
                    {
                        has = true;
                        break;
                    }
                }
                // Thermal / NV scope on the weapon in hand (BSG's "special scopes").
                if (!has && FightWeaponOf(bot) is Weapon weapon)
                {
                    foreach (var sight in weapon.GetItemComponentsInChildren<SightComponent>(false))
                    {
                        if (sight.Item?.Template?.ParentId?.ToString() != SpecialScopeParentId) continue;
                        has = true;
                        break;
                    }
                }
            }
        }
        catch
        {
            // Disposed/edge-case inventory: no night vision.
        }
        _nightVisionCache[bot] = (has, Time.time);
        return has;
    }

    private void ApplyNightReach(GhostUnit unit)
    {
        if (_darkness <= 0f || unit.NightCapable) return;
        unit.Reach = Mathf.Lerp(unit.Reach, Mathf.Min(unit.Reach, NightBlindReach), _darkness);
    }

    private float NightFightMul(GhostUnit self, GhostUnit other)
        => _darkness > 0f && !self.NightCapable && other.NightCapable ? Mathf.Lerp(1f, NightBlindStrengthMul, _darkness) : 1f;

    /// <summary>
    /// Effective kill distance of the weapon in a member's hands: the game's own per-weapon
    /// bEffDist (what LookSensor uses for real shooting decisions), hard-capped when the LOADED
    /// round is multi-projectile — buckshot must never credit a 300m kill no matter the shotgun.
    /// Generous fallback when the weapon or ammo can't be read, so nothing new gets blocked.
    /// </summary>
    private float WeaponKillRange(Player p)
    {
        if (p == null) return DefaultKillRange;
        if (_killRangeCache.TryGetValue(p, out var cached) && Time.time - cached.at < 60f)
            return cached.range;

        var range = DefaultKillRange;
        try
        {
            if (FightWeaponOf(p.AIData?.BotOwner) is Weapon weapon)
            {
                range = Mathf.Max(25f, weapon.Template.bEffDist);
                var ammo = weapon.CurrentAmmoTemplate;
                if (ammo != null && ammo.ProjectileCount > 1)
                    range = Mathf.Min(range, BuckshotKillRangeCap);
            }
        }
        catch
        {
            // Disposed/edge-case inventory — keep the permissive default.
        }
        _killRangeCache[p] = (range, Time.time);
        return range;
    }

    private const float DefaultKillRange = 400f;
    private const float BuckshotKillRangeCap = 50f;

    /// <summary>Strength: per member, a difficulty-scaled base point (an impossible bot fights like 1.3
    /// easy ones) plus up to 1.5 for inventory worth; the total shaded by the squad's SAIN archetype.</summary>
    private static float UnitStrength(GhostUnit unit)
    {
        var total = 0f;
        for (var i = 0; i < unit.Agents.Count; i++)
            total += (DifficultyMul(unit.Agents[i].Bot) + Mathf.Min(1.5f, ItemPriceLookup.SumInventoryWorth(unit.Agents[i].Bot) / 400_000f)) * HealthMul(unit.Agents[i].Player);
        for (var i = 0; i < unit.VanillaBots.Count; i++)
            total += (DifficultyMul(unit.VanillaBots[i]) + Mathf.Min(1.5f, ItemPriceLookup.SumInventoryWorth(unit.VanillaBots[i]) / 400_000f)) * HealthMul(unit.VanillaBots[i].GetPlayer);
        return total * ArchetypeStrengthMul(unit);
    }

    /// <summary>A wounded fighter is a worse fighter: strength scales with overall HP down to 0.35x.
    /// Feeds back with post-fight attrition so chained wins get progressively riskier.</summary>
    private static float HealthMul(Player player)
    {
        try
        {
            var hp = player.HealthController.GetBodyPartHealth(EBodyPart.Common);
            if (hp.Maximum > 0f) return Mathf.Lerp(0.35f, 1f, hp.Current / hp.Maximum);
        }
        catch
        {
            // Torn-down health controller — neutral.
        }
        return 1f;
    }

    private static float DifficultyMul(BotOwner bot)
    {
        try
        {
            switch (bot?.Profile?.Info?.Settings?.BotDifficulty)
            {
                case BotDifficulty.easy: return 0.85f;
                case BotDifficulty.hard: return 1.15f;
                case BotDifficulty.impossible: return 1.3f;
            }
        }
        catch
        {
            // Torn-down profile — neutral.
        }
        return 1f;
    }

    /// <summary>Archetype shades apply only to PMC squads with a resolved SAIN personality
    /// (Personality stays null for scavs and vanilla units — they fight at face value).</summary>
    private static float ArchetypeStrengthMul(GhostUnit unit)
    {
        if (unit.Squad?.Personality == null) return 1f;
        switch (unit.Squad.Archetype)
        {
            case PersonalityArchetype.Timmy: return 0.8f;
            case PersonalityArchetype.Cautious: return 0.9f;
            case PersonalityArchetype.Aggressive: return 1.15f;
            case PersonalityArchetype.VeryAggressive: return 1.3f;
            default: return 1f;
        }
    }

    private static float ContactAggressionMul(GhostUnit a, GhostUnit b)
        => (ArchetypeContactMul(a) + ArchetypeContactMul(b)) * 0.5f;

    private static float ArchetypeContactMul(GhostUnit unit)
    {
        if (unit.Squad?.Personality == null) return 1f;
        switch (unit.Squad.Archetype)
        {
            case PersonalityArchetype.Timmy: return 0.85f;
            case PersonalityArchetype.Cautious: return 0.7f;
            case PersonalityArchetype.Aggressive: return 1.2f;
            case PersonalityArchetype.VeryAggressive: return 1.35f;
            default: return 1f;
        }
    }

    /// <summary>Real-fights mode: wake both units on contact and let the AI fight it out for real.
    /// Costs actual off-screen combat CPU, in exchange for fully authentic outcomes. The pair cooldown
    /// plus the 30s wake cooldown keep the encounter from re-triggering while they disengage.</summary>
    private void WakeBothForRealFight(GhostUnit a, GhostUnit b, float distance)
    {
        _windowFights++;
        Log.Info($"GHOST CONTACT at {distance:F0}m: {a.Label} vs {b.Label} — waking both for a real fight");
        WakeGhostUnit(a, $"ghost contact with {b.Label} at {distance:F0}m");
        WakeGhostUnit(b, $"ghost contact with {a.Label} at {distance:F0}m");
    }

    private void WakeGhostUnit(GhostUnit unit, string reason)
    {
        if (unit.Squad != null)
        {
            WakeSquad(unit.Squad, reason);
            return;
        }
        if (unit.VanillaKey != null && _vanillaGroups.TryGetValue(unit.VanillaKey, out var group))
            WakeVanillaGroup(unit.VanillaKey, group, reason);
    }

    private void ResolveFight(GhostUnit a, GhostUnit b, float distance, Vector3 posA, Vector3 posB)
    {
        // Range fitness: how comfortable each side is at this distance. The scoped side dominates a far
        // duel (its reach covers the distance, the other side's does not) and loses its edge up close.
        var rangeA = Mathf.Clamp(a.Reach / Mathf.Max(distance, 25f), 0.25f, 1.5f);
        var rangeB = Mathf.Clamp(b.Reach / Mathf.Max(distance, 25f), 0.25f, 1.5f);
        // Weapon fitness: the loaded gun's effective kill distance (bEffDist, buckshot hard-capped)
        // vs the duel distance — a buckshot squad at 300m fights at a fraction of its strength.
        var gunA = Mathf.Lerp(0.3f, 1f, Mathf.Clamp01(a.KillRange * 1.3f / Mathf.Max(distance, 10f)));
        var gunB = Mathf.Lerp(0.3f, 1f, Mathf.Clamp01(b.KillRange * 1.3f / Mathf.Max(distance, 10f)));
        var rollA = UnitStrength(a) * rangeA * gunA * NightFightMul(a, b) * Random.Range(0.7f, 1.3f);
        var rollB = UnitStrength(b) * rangeB * gunB * NightFightMul(b, a) * Random.Range(0.7f, 1.3f);
        var winner = rollA >= rollB ? a : b;
        var loser = rollA >= rollB ? b : a;
        var ratio = Mathf.Max(rollA, rollB) / Mathf.Max(0.1f, Mathf.Min(rollA, rollB));

        // Long-range realism: past ~250m real AI trades shots without landing kills, so far contacts
        // are mostly bloodless exchanges (the sounds still play). Close fights stay deadly.
        var rangeLethality = Mathf.Lerp(0.2f, 1f, Mathf.Clamp01(1f - (distance - 50f) / 300f));

        var loserDeaths = Mathf.Min(loser.Count, ProbRound((1 + (ratio > 1.6f ? 1 : 0) + (ratio > 2.5f ? 1 : 0)) * _lethality * rangeLethality));
        var winnerDeaths = ratio < 1.2f && winner.Count > 1 && Random.value < 0.4f * _lethality * rangeLethality ? 1 : 0;

        // How contested the exchange was: 1 = coin flip, 0 = total stomp. Drives the fight's duration,
        // the wound odds and the wound sizes.
        var closeness = Mathf.Clamp01(1f - (ratio - 1f) / 2f);

        // Fight window: stomps end fast, even fights drag on, and RANGE stretches everything — a
        // 350m duel takes ranging and aiming time, nobody lands instant kills out there (the distance
        // floor alone guarantees ~30s at 350m). A quarter of contacts go PROTRACTED: neither side
        // commits and they trade sporadic pot-shots for minutes.
        var dist01 = Mathf.Clamp01(distance / 350f);
        var duration = Random.Range(8f, 20f)
                       * Mathf.Lerp(0.6f, 1.6f, closeness)
                       * Mathf.Lerp(0.85f, 1.4f, dist01);
        var shotsPerSecond = Random.Range(1.2f, 2.2f);
        if (Random.value < 0.25f)
        {
            duration *= Random.Range(2.5f, 4f);
            shotsPerSecond = Random.Range(0.35f, 0.8f);
        }
        duration = Mathf.Max(duration, distance / 12f);

        var fight = new ActiveGhostFight
        {
            Winner = winner,
            Loser = loser,
            EndsAt = Time.time + duration,
            // Not every exchange draws blood: a stomp usually ends clean for the winner (a graze at
            // most), a coin-flip fight marks almost everyone, and losers always risk more.
            WinnerWoundChance = Mathf.Lerp(0.1f, 0.85f, closeness),
            LoserWoundChance = Mathf.Lerp(0.5f, 0.95f, closeness),
            WinnerWound = Random.Range(10f, 45f) * Mathf.Lerp(0.5f, 1.5f, closeness),
            LoserWound = Random.Range(25f, 60f),
        };
        // First blood comes later at range: close ambushes kill early in the window, long duels only
        // after a good stretch of aiming and repositioning.
        var killEarliest = Mathf.Lerp(0.2f, 0.45f, dist01);
        for (var i = 0; i < loserDeaths; i++) fight.LoserKillAts.Add(Time.time + duration * Random.Range(killEarliest, 0.95f));
        for (var i = 0; i < winnerDeaths; i++) fight.WinnerKillAts.Add(Time.time + duration * Random.Range(killEarliest, 0.95f));
        _activeFights.Add(fight);

        // Telemetry (raid-review renders the fight window in the replay).
        Api.OrbitTelemetry.PushGhostFight(new Api.OrbitGhostFight
        {
            AX = posA.x, AY = posA.y, AZ = posA.z,
            BX = posB.x, BY = posB.y, BZ = posB.z,
            Duration = duration,
            Casualties = loserDeaths + winnerDeaths,
        });

        // Pin both squads for the window: no ghost walking and no second fight mid-firefight.
        _unitFightingUntil[winner.Key] = fight.EndsAt;
        _unitFightingUntil[loser.Key] = fight.EndsAt;
        foreach (var bot in winner.VanillaBots) _nativeGhosts.Pin(bot, fight.EndsAt);
        foreach (var bot in loser.VanillaBots) _nativeGhosts.Pin(bot, fight.EndsAt);
        if (winner.Squad != null) winner.Squad.GhostFightUntil = fight.EndsAt;
        if (loser.Squad != null) loser.Squad.GhostFightUntil = fight.EndsAt;

        _windowFights++;
        Log.Info($"GHOST SKIRMISH at {distance:F0}m: {a.Label} (str {rollA:F1}, reach {a.Reach:F0}m, gun {a.KillRange:F0}m) vs {b.Label} (str {rollB:F1}, reach {b.Reach:F0}m, gun {b.KillRange:F0}m), {winner.Label} wins over {duration:F0}s, {loserDeaths + winnerDeaths} killed");

        if (_darkness > 0f)
            Log.Info($"GHOST SKIRMISH night: darkness {_darkness:F2}, {a.Label} nightVision={a.NightCapable} (x{NightFightMul(a, b):F2}), {b.Label} nightVision={b.NightCapable} (x{NightFightMul(b, a):F2})");
        QueueFightSounds(a, posA, b, posB, loserDeaths + winnerDeaths, duration, shotsPerSecond);
        RegisterFightNoise(a, posA, b, posB, duration);
    }

    private bool UnitInFight(GhostUnit unit)
    {
        if (unit.Squad != null && Time.time < unit.Squad.GhostFightUntil) return true;
        foreach (var bot in unit.VanillaBots)
            if (_nativeGhosts.InFight(bot)) return true;
        return _unitFightingUntil.TryGetValue(unit.Key, out var until) && Time.time < until;
    }

    /// <summary>Advances the in-flight fight windows every frame: due casualties drop, a window whose
    /// participant woke escalates into a REAL fight, and when a window closes normally the survivors'
    /// wear lands (dormancy-silent) and the units are released.</summary>
    private void PumpGhostFights()
    {
        for (var i = _activeFights.Count - 1; i >= 0; i--)
        {
            var fight = _activeFights[i];
            try
            {
                // Escalation: if either side woke mid-window (player proximity, scope, damage), the
                // simulated fight turns REAL — wake the other side too, drop the remaining scripted
                // casualties and wear, and let the actual AI finish what the dice started. They are
                // within reach with line of sight by construction, so vision picks the fight up fast.
                if (UnitWokeMidFight(fight.Winner) || UnitWokeMidFight(fight.Loser))
                {
                    Log.Info($"GHOST SKIRMISH: {fight.Winner.Label} vs {fight.Loser.Label} escalated to a REAL fight (a side woke mid-window)");
                    WakeGhostUnit(fight.Winner, "ghost fight escalation");
                    WakeGhostUnit(fight.Loser, "ghost fight escalation");
                    ReleaseFightPins(fight);
                    _activeFights.RemoveAt(i);
                    continue;
                }

                ExecuteDueKills(fight.LoserKillAts, fight.Loser, fight.Winner);
                ExecuteDueKills(fight.WinnerKillAts, fight.Winner, fight.Loser);
                if (Time.time < fight.EndsAt) continue;
                ApplyFightAttrition(fight.Winner, fight.WinnerWoundChance, fight.WinnerWound);
                ApplyFightAttrition(fight.Loser, fight.LoserWoundChance, fight.LoserWound);
                _activeFights.RemoveAt(i);
            }
            catch (System.Exception e)
            {
                // Drop the broken fight rather than retrying it every frame (BSG swallows tick exceptions).
                Log.Error($"GHOST SKIRMISH: fight pump failed, dropping the fight: {e.Message}");
                _activeFights.RemoveAt(i);
            }
        }
    }

    private bool UnitWokeMidFight(GhostUnit unit)
    {
        for (var i = 0; i < unit.Agents.Count; i++)
        {
            var agent = unit.Agents[i];
            if (agent?.Player != null && agent.Player.HealthController is { IsAlive: true } && !agent.IsDormant)
                return true;
        }
        for (var i = 0; i < unit.VanillaBots.Count; i++)
        {
            var bot = unit.VanillaBots[i];
            if (bot != null && !bot.IsDead && !_vanillaDormant.Contains(bot))
                return true;
        }
        return false;
    }

    private void ReleaseFightPins(ActiveGhostFight fight)
    {
        _unitFightingUntil.Remove(fight.Winner.Key);
        _unitFightingUntil.Remove(fight.Loser.Key);
        foreach (var bot in fight.Winner.VanillaBots) _nativeGhosts.Pin(bot, 0f);
        foreach (var bot in fight.Loser.VanillaBots) _nativeGhosts.Pin(bot, 0f);
        if (fight.Winner.Squad != null) fight.Winner.Squad.GhostFightUntil = -999f;
        if (fight.Loser.Squad != null) fight.Loser.Squad.GhostFightUntil = -999f;
    }

    private void ExecuteDueKills(List<float> killAts, GhostUnit victims, GhostUnit killers)
    {
        for (var i = killAts.Count - 1; i >= 0; i--)
        {
            if (Time.time < killAts[i]) continue;
            killAts.RemoveAt(i);
            KillRandomMember(victims, killers);
        }
    }

    private static readonly EBodyPart[] AttritionParts = { EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg };

    private void ApplyFightAttrition(GhostUnit unit, float woundChance, float perMemberDamage)
    {
        for (var i = 0; i < unit.Agents.Count; i++)
        {
            if (Random.value > woundChance) continue;
            var agent = unit.Agents[i];
            if (!agent.IsDormant) continue; // woke mid-window — no invisible wounds on live bots
            if (!WoundSurvivor(agent.Player, perMemberDamage)) continue;
            // Rebase the damage-wake baselines so fight wear never wakes the squad (the whole point of
            // the limiter). Real enemy damage still compares against the NEW baseline and wakes as usual.
            // The drop timestamp delays both the simulated self-care and any re-sleep after a real wake.
            agent.DormantHpBaseline = TotalHp(agent);
            agent.LastPollHp = agent.DormantHpBaseline;
            agent.LastHpDropTime = Time.time;
        }
        for (var i = 0; i < unit.VanillaBots.Count; i++)
        {
            if (Random.value > woundChance) continue;
            var bot = unit.VanillaBots[i];
            if (!_vanillaDormant.Contains(bot)) continue; // woke mid-window — no invisible wounds
            if (!WoundSurvivor(bot.GetPlayer, perMemberDamage)) continue;
            _vanillaHpBaseline[bot] = VanillaHp(bot);
            _vanillaLastHp[bot] = _vanillaHpBaseline[bot];
            _vanillaHpDropAt[bot] = Time.time;
        }
    }

    private static bool WoundSurvivor(Player player, float damage)
    {
        try
        {
            var hc = player?.ActiveHealthController;
            if (hc is not { IsAlive: true }) return false;
            // Plain HP loss, no hit processing: ApplyDamage rolled real bleeds and fractures on the
            // sleeper, and a bleed ticking on the inactive body tripped the damage wake seconds after
            // the window closed. Both sides then met awake and fought for real (release-raid logs:
            // damage wake, awakeBot wake, then minutes of combat/proximity blocks).
            hc.ChangeHealth(AttritionParts[Random.Range(0, AttritionParts.Length)], -damage, default);
            return true;
        }
        catch
        {
            // Half-despawned survivor — skip the wound.
            return false;
        }
    }

    /// <summary>Simulated self-care: wounded sleepers slowly patch themselves up off-screen, the way an
    /// awake bot would between fights. Regen starts once the last HP drop is old enough, never repairs a
    /// destroyed part (that takes a real surgery kit at a real wake), and rebases the damage-wake
    /// baselines as it goes so the healing itself never wakes anyone and later REAL damage still
    /// compares against fresh HP.</summary>
    private void HealDormantWounded()
    {
        var heal = GhostHealPerSecond * PollIntervalSeconds;
        for (var i = 0; i < _dormantAgents.Count; i++)
        {
            var agent = _dormantAgents[i];
            try
            {
                if (Time.time - agent.LastHpDropTime < GhostHealDelaySeconds) continue;
                if (!HealWeakestPart(agent.Player, heal)) continue;
                agent.DormantHpBaseline = TotalHp(agent);
                agent.LastPollHp = agent.DormantHpBaseline;
            }
            catch
            {
                // Despawning body mid-iteration — never break the poll.
            }
        }
        foreach (var bot in _vanillaDormant)
        {
            try
            {
                if (_vanillaHpDropAt.TryGetValue(bot, out var dropAt) && Time.time - dropAt < GhostHealDelaySeconds) continue;
                if (!HealWeakestPart(bot.GetPlayer, heal)) continue;
                _vanillaHpBaseline[bot] = VanillaHp(bot);
                _vanillaLastHp[bot] = _vanillaHpBaseline[bot];
            }
            catch
            {
                // Despawning body mid-iteration — never break the poll.
            }
        }
    }

    private static readonly EBodyPart[] HealableParts =
        { EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach, EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg };

    private static bool HealWeakestPart(Player player, float amount)
    {
        var hc = player?.ActiveHealthController;
        if (hc is not { IsAlive: true }) return false;
        var weakest = EBodyPart.Common;
        var worstFrac = 1f;
        for (var i = 0; i < HealableParts.Length; i++)
        {
            var hp = hc.GetBodyPartHealth(HealableParts[i]);
            if (hp.Maximum <= 0f || hp.Current <= 0f || hp.AtMaximum) continue;
            var frac = hp.Current / hp.Maximum;
            if (frac < worstFrac)
            {
                worstFrac = frac;
                weakest = HealableParts[i];
            }
        }
        if (weakest == EBodyPart.Common) return false;
        hc.ChangeHealth(weakest, amount, default);
        return true;
    }

    /// <summary>Probabilistic rounding: a 1.4 casualty budget kills 1 bot 60% of the time, 2 otherwise.</summary>
    private static int ProbRound(float value)
    {
        var floor = Mathf.FloorToInt(value);
        return floor + (Random.value < value - floor ? 1 : 0);
    }

    /// <summary>
    /// Audible exchange of fire for a simulated fight: a burst of authentic shot sounds (each side's
    /// own weapon sound banks) spread over a few seconds at the fighters' positions. Skipped entirely
    /// when no human is close enough to hear.
    /// </summary>
    private void QueueFightSounds(GhostUnit a, Vector3 posA, GhostUnit b, Vector3 posB, int casualties, float duration, float shotsPerSecond)
    {
        if (!_cfg.GhostFightSounds) return;

        // Shot count follows the window at the fight's own firing rate (protracted fights trade
        // sporadic pot-shots, not a continuous mag dump), capped to bound the queue.
        var shots = Mathf.Min(90, Mathf.RoundToInt(duration * shotsPerSecond) + casualties * 3);

        // Every member fires its own gun from its own spot (a 3-man squad is heard as three weapons):
        // each side's half of the budget is split between its members with a random skew.
        _shooters.Clear();
        CollectShooters(a, shots / 2, _shooters);
        var sideBStart = _shooters.Count;
        CollectShooters(b, shots - shots / 2, _shooters);
        var profileA = sideBStart > 0 ? _shooters[0].ProfileId : null;
        var profileB = _shooters.Count > sideBStart ? _shooters[sideBStart].ProfileId : null;

        // Fika bridge (Orbit.Fika addon): raised before the local earshot gate, because audibility
        // is a per-listener judgement and each co-op client replays the burst against its own ears.
        Api.OrbitEvents.RaiseGhostFightSounds(new Api.OrbitEvents.GhostFightSounds
        {
            PosA = posA,
            PosB = posB,
            ProfileA = profileA,
            ProfileB = profileB,
            Shots = shots,
            Duration = duration,
            Shooters = new List<Api.OrbitEvents.GhostShooter>(_shooters),
        });

        const float earshotSqr = 1500f * 1500f;
        var listenerDist = Mathf.Sqrt(Mathf.Min(MinSqrDistanceToHumans(posA), MinSqrDistanceToHumans(posB)));
        if (listenerDist * listenerDist > earshotSqr)
        {
            Log.Debug($"GHOST FIGHT SOUNDS: skipped, closest human {listenerDist:F0}m is out of earshot");
            return;
        }

        if (_shooters.Count == 0)
        {
            Log.Debug("GHOST FIGHT SOUNDS: skipped, no weapon sound player resolved on either side");
            return;
        }

        Log.Info($"GHOST FIGHT SOUNDS: queueing up to {shots} shots from {_shooters.Count} shooter(s) over {duration:F1}s, closest human {listenerDist:F0}m");
        var engagementDistance = Vector3.Distance(posA, posB);
        var shootersA = sideBStart;
        var shootersB = _shooters.Count - sideBStart;
        for (var i = 0; i < _shooters.Count; i++)
        {
            var shooter = _shooters[i];
            var sound = WeaponSoundFromProfile(shooter.ProfileId);
            if (sound == null) continue;
            var weapon = Api.GhostWeaponProfile.From(WeaponFromProfile(shooter.ProfileId), sound, engagementDistance);
            // Squadmates fire staggered and pause longer the more of them there are, so a five-man
            // side sounds like a firefight rather than one 4000 rpm gun.
            var (startOffset, pauseScale) = i < sideBStart
                ? Api.GhostShotScheduler.SideStagger(i, shootersA)
                : Api.GhostShotScheduler.SideStagger(i - sideBStart, shootersB);
            Log.Debug($"GHOST FIGHT SOUNDS: {(i < sideBStart ? "A" : "B")} {DescribeShooter(shooter.ProfileId, sound)}, mode {weapon.Mode} {weapon.CyclicRpm:F0}/{weapon.SemiRpm:F0}rpm bursts {weapon.BurstShare:P0} at {engagementDistance:F0}m, {shooter.Shots} shot(s)");
            QueueShooterShots(sound, weapon, shooter.Position, shooter.Shots, duration, startOffset, pauseScale);
        }
    }

    private readonly List<Api.OrbitEvents.GhostShooter> _shooters = new();
    private readonly List<(string profile, Vector3 pos, float weight)> _shooterScratch = new();

    /// <summary>Splits one side's shot budget across its members that have a weapon sound player,
    /// with a random skew so the split never sounds mechanical. Members without a resolvable weapon
    /// (nothing in hands, despawning) are skipped and their share goes to the others.</summary>
    private void CollectShooters(GhostUnit unit, int budget, List<Api.OrbitEvents.GhostShooter> into)
    {
        if (budget <= 0) return;
        _shooterScratch.Clear();
        var totalWeight = 0f;
        for (var i = 0; i < unit.Count; i++)
        {
            var player = i < unit.Agents.Count ? unit.Agents[i].Player : unit.VanillaBots[i - unit.Agents.Count].GetPlayer;
            var profileId = player?.ProfileId;
            if (profileId == null || WeaponSoundFromProfile(profileId) == null) continue;
            var weight = Random.Range(0.5f, 1.5f);
            _shooterScratch.Add((profileId, player.Position, weight));
            totalWeight += weight;
        }
        if (_shooterScratch.Count == 0) return;

        var assigned = 0;
        for (var i = 0; i < _shooterScratch.Count; i++)
        {
            var (profile, pos, weight) = _shooterScratch[i];
            var share = i == _shooterScratch.Count - 1
                ? budget - assigned
                : Mathf.Max(1, Mathf.RoundToInt(budget * weight / totalWeight));
            share = Mathf.Min(share, budget - assigned);
            if (share <= 0) continue;
            assigned += share;
            into.Add(new Api.OrbitEvents.GhostShooter { ProfileId = profile, Position = pos, Shots = share });
        }
    }

    private void QueueShooterShots(WeaponSoundPlayer sound, Api.GhostWeaponProfile weapon, Vector3 pos, int budget, float duration, float startOffset = 0f, float pauseScale = 1f)
    {
        if (sound == null || budget <= 0) return;
        var times = Api.GhostShotScheduler.Schedule(weapon, budget, duration, startOffset, pauseScale);
        // One entry per TRIGGER PULL, not per round: an automatic weapon's Body clip is a 16-round loop
        // that must be started once and cut, see GhostShotPlayback.
        var pulls = Api.GhostShotPlayback.GroupTriggerPulls(times, weapon, sound.IsAutoWeapon);
        for (var i = 0; i < pulls.Count; i++)
        {
            _pendingShots.Add(new PendingShot
            {
                At = Time.time + pulls[i].At,
                // A shooter shifts around its own spot between shots, it never teleports.
                Pos = pos + new Vector3(Random.Range(-1.5f, 1.5f), 0f, Random.Range(-1.5f, 1.5f)),
                Sound = sound,
                Rounds = pulls[i].Rounds,
            });
        }
    }

    private Weapon WeaponFromProfile(string profileId)
    {
        try
        {
            return profileId == null ? null : _gameWorld.GetAlivePlayerByProfileID(profileId)?.HandsController?.Item as Weapon;
        }
        catch
        {
            return null;
        }
    }

    private WeaponSoundPlayer WeaponSoundFromProfile(string profileId)
    {
        try
        {
            return profileId == null ? null : _gameWorld.GetAlivePlayerBridgeByProfileID(profileId)?.WeaponSoundPlayer;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Distant gunshots in EFT come from the BODY bank: it blends its clips by distance
    /// (PickClips inside PlayAtPoint picks the far "crack" variants), exactly like FireBullet
    /// does for real shots. Tails are only the close-range reverb layer: playing them alone at distance
    /// sounds dull and identical for every weapon (community report: "everything sounds like .50BMG").
    /// A suppressed weapon's sound player is flagged IsSilenced by the game, so its ghost shots use the
    /// silenced body and stay authentically quiet.</summary>
    private static SoundBank PickGhostBank(WeaponSoundPlayer sound)
    {
        return sound.IsSilenced
            ? (sound.BodySilenced != null ? sound.BodySilenced
                : sound.Body != null ? sound.Body : sound.TailSilenced)
            : (sound.Body != null ? sound.Body : sound.Tail);
    }

    /// <summary>Debug-level description of one side of a ghost fight: who fires what, through which
    /// bank. Lets a raid log confirm the shots match the weapon actually in the shooter's hands.</summary>
    private string DescribeShooter(string profileId, WeaponSoundPlayer sound)
    {
        try
        {
            var player = profileId == null ? null : _gameWorld.GetAlivePlayerByProfileID(profileId);
            var nick = player?.Profile?.Nickname ?? "?";
            var weapon = player?.HandsController?.Item is Weapon w ? w.LocalizedName() : "(nothing in hands)";
            var bank = PickGhostBank(sound);
            return $"{nick} firing {weapon}: {(sound.IsAutoWeapon ? "auto" : "semi/bolt")}, " +
                   $"{(sound.IsSilenced ? "suppressed" : "unsuppressed")}, bank '{(bank != null ? bank.name : "none")}'" +
                   $"{(bank != null ? $" rolloff {bank.Rolloff:F0}m" : "")}";
        }
        catch
        {
            return "(shooter despawned)";
        }
    }

    private void PumpGhostFightShots()
    {
        var audio = Singleton<BetterAudio>.Instance;
        if (audio == null)
        {
            _pendingShots.Clear();
            return;
        }
        for (var i = _pendingShots.Count - 1; i >= 0; i--)
        {
            var shot = _pendingShots[i];
            if (Time.time < shot.At) continue;
            _pendingShots.RemoveAt(i);
            try
            {
                var listenerDist = Mathf.Sqrt(MinSqrDistanceToHumans(shot.Pos));
                if (shot.IsTail)
                {
                    Api.GhostShotPlayback.PlayTail(audio, shot.Sound, shot.LoopSource, shot.Pos, listenerDist);
                    continue;
                }
                var rounds = Mathf.Max(1, shot.Rounds);
                var source = Api.GhostShotPlayback.Play(audio, shot.Sound, shot.Pos, listenerDist, rounds, out var tailDelay);
                if (source == null)
                {
                    _windowShotsDropped += rounds;
                    continue;
                }
                _windowShotsPlayed += rounds;
                if (tailDelay > 0f)
                {
                    _pendingShots.Add(new PendingShot
                    {
                        At = Time.time + tailDelay, Pos = shot.Pos, Sound = shot.Sound, IsTail = true, LoopSource = source,
                    });
                }
            }
            catch
            {
                // A despawned weapon/sound player mid-burst — drop the shot.
            }
        }
    }

    /// <summary>
    /// The fight itself is gated on LoS between the two CLOSEST members, but casualties are attributed
    /// per-pair: the victim must be visible to its killer or RR draws kill lines through mountains
    /// (raid 8: one Rat credited with 300m+ kills on bots behind a ridge). Victims are tried in random
    /// order, the killer is the nearest opposing survivor with clear terrain LoS; if no pair sees each
    /// other, the casualty is dropped.
    /// </summary>
    private void KillRandomMember(GhostUnit unit, GhostUnit opposing)
    {
        if (unit.Count == 0) return;
        var start = Random.Range(0, unit.Count);
        for (var n = 0; n < unit.Count; n++)
        {
            var idx = (start + n) % unit.Count;
            // Mid-window kill: a victim that woke since the fight started is no longer a valid
            // scripted casualty (imagine one dropping dead in front of the player) — try another.
            var stillDormant = idx < unit.Agents.Count
                ? unit.Agents[idx].IsDormant
                : _vanillaDormant.Contains(unit.VanillaBots[idx - unit.Agents.Count]);
            if (!stillDormant) continue;
            var victimPos = idx < unit.Agents.Count
                ? unit.Agents[idx].Position
                : unit.VanillaBots[idx - unit.Agents.Count].GetPlayer.Position;
            var killer = PickVisibleKiller(opposing, victimPos);
            if (killer == null) continue;
            if (idx < unit.Agents.Count)
            {
                var victim = unit.Agents[idx];
                unit.Agents.RemoveAt(idx);
                KillGhostAgent(victim, killer);
            }
            else
            {
                var victim = unit.VanillaBots[idx - unit.Agents.Count];
                unit.VanillaBots.Remove(victim);
                KillGhostVanilla(victim, killer);
            }
            return;
        }
        Log.Debug($"GHOST SKIRMISH: no in-range line-of-sight killer/victim pair between {opposing.Label} and {unit.Label} — casualty dropped");
    }

    /// <summary>Strict fight line-of-sight: three rays (head height, chest height, head height
    /// shifted 1.2m sideways) must all be clear. Catches the tree-trunk gaps a single ray slips through.</summary>
    private static bool ClearFightLos(Vector3 posA, Vector3 posB)
    {
        var side = Vector3.Cross((posB - posA).normalized, Vector3.up) * 1.2f;
        return RayClear(posA + new Vector3(0f, 1.6f, 0f), posB + new Vector3(0f, 1.6f, 0f))
               && RayClear(posA + new Vector3(0f, 0.8f, 0f), posB + new Vector3(0f, 0.8f, 0f))
               && RayClear(posA + new Vector3(0f, 1.6f, 0f) + side, posB + new Vector3(0f, 1.6f, 0f) + side);
    }

    private static bool RayClear(Vector3 from, Vector3 to)
    {
        var d = to - from;
        return !Physics.Raycast(from, d.normalized, d.magnitude, LayersMaskController.HighPolyWithTerrainMask);
    }

    private readonly List<(Player player, float weight)> _killerCandidates = new();

    /// <summary>Credited killer for a simulated casualty: a weighted random draw among the opposing
    /// survivors whose gun can plausibly make the shot (a buckshot shotgun never gets a 300m kill even
    /// when its SVD squadmate is dead) and who have a clear line of sight. Weighted by gun fitness at
    /// that distance, not "the closest", so kills spread across the squad: with nearest-wins every
    /// simulated kill of a squad landed on whoever walked in front (release-raid logs).</summary>
    private Player PickVisibleKiller(GhostUnit unit, Vector3 targetPos)
    {
        _killerCandidates.Clear();
        var total = 0f;
        for (var i = 0; i < unit.Count; i++)
        {
            var p = i < unit.Agents.Count ? unit.Agents[i].Player : unit.VanillaBots[i - unit.Agents.Count].GetPlayer;
            if (p == null) continue;
            var d = Vector3.Distance(p.Position, targetPos);
            var maxShot = WeaponKillRange(p) * 1.3f;
            if (d > maxShot) continue;
            if (!ClearFightLos(p.Position, targetPos)) continue;
            // Same fitness curve as the fight roll: a gun comfortably inside its reach is favoured,
            // one at the edge of it still gets a share.
            var weight = Mathf.Lerp(0.3f, 1f, Mathf.Clamp01(maxShot / Mathf.Max(d, 10f) - 1f));
            _killerCandidates.Add((p, weight));
            total += weight;
        }
        if (_killerCandidates.Count == 0) return null;

        var roll = Random.value * total;
        for (var i = 0; i < _killerCandidates.Count; i++)
        {
            roll -= _killerCandidates[i].weight;
            if (roll <= 0f) return _killerCandidates[i].player;
        }
        return _killerCandidates[_killerCandidates.Count - 1].player;
    }

    /// <summary>
    /// Lethal chest hit carrying the killer's player bridge, so EFT routes the death through
    /// OnBeenKilledByAggressor: raid-review gets its kill feed entry and death marker, and ORBIT's
    /// corpse registration credits the killer squad, exactly like a real firefight kill.
    /// </summary>
    private void KillWithAttribution(Player victim, Player killer)
    {
        var damageInfo = new EFT.Ballistics.DamageInfo
        {
            DamageType = EDamageType.Bullet,
            Damage = 500f,
            HitPoint = victim.Position + new Vector3(0f, 1.3f, 0f),
            Direction = killer != null ? (victim.Position - killer.Position).normalized : Vector3.forward,
        };
        if (killer != null)
        {
            try { damageInfo.Player = _gameWorld.GetAlivePlayerBridgeByProfileID(killer.ProfileId); }
            catch { }
            // Killfeed cosmetics: name the killer's in-hands weapon when there is one.
            try { damageInfo.Weapon = killer.HandsController?.Item; }
            catch { }
        }

        // MUST go through the Player-level entry point: ApplyDamageInfo is what sets LastAggressor
        // before the health controller kills, and OnDead only routes through OnBeenKilledByAggressor
        // (raid-review's kill feed hook, EFT's own aggressor stats) when LastAggressor is non-null.
        // Hitting ActiveHealthController.ApplyDamage directly produces an anonymous death: real corpse,
        // no kill feed, no death marker (raid 7 lesson).
        victim.ApplyDamageInfo(damageInfo, EBodyPart.Chest, EBodyPartColliderType.RibcageUp, 0f);
        if (victim.ActiveHealthController is { IsAlive: true })
            victim.ApplyDamageInfo(damageInfo, EBodyPart.Head, EBodyPartColliderType.HeadCommon, 0f);
        if (victim.ActiveHealthController is { IsAlive: true })
            victim.ActiveHealthController.Kill(EDamageType.Bullet); // last-resort unattributed
    }

    /// <summary>Re-activates the body, then kills it through the normal death pipeline: ragdoll, corpse
    /// registration, RemoveAgent (which also finalises our dormancy bookkeeping via OnAgentRemoved).</summary>
    private void KillGhostAgent(Agent victim, Player killer)
    {
        victim.IsDormant = false;
        _dormantAgents.Remove(victim);
        DormantProfileIds.Remove(victim.Player.ProfileId);
        try
        {
            var bot = victim.Bot;
            UnthrottleBrain(bot);
            bot.gameObject.SetActive(true);
            bot.PatrollingData.Unpause();
            bot.PostActivate();
            Log.Info($"GHOST SKIRMISH: {victim} killed in action by {killer?.Profile?.Nickname ?? "?"}");
            KillWithAttribution(victim.Player, killer);
        }
        catch (System.Exception e)
        {
            Log.Error($"GHOST SKIRMISH: killing {victim} failed: {e}");
        }
    }

    private void KillGhostVanilla(BotOwner victim, Player killer)
    {
        var native = _nativeGhosts.Remove(victim);
        _vanillaDormant.Remove(victim);
        UnthrottleBrain(victim);
        DormantProfileIds.Remove(victim.GetPlayer?.ProfileId);
        try
        {
            victim.gameObject.SetActive(true);
            if (!native) victim.PatrollingData.Unpause();
            victim.PostActivate();
            if (native) NativeGhostSystem.ResyncAfterWake(victim);
            Log.Info($"GHOST SKIRMISH: vanilla {victim.GetPlayer?.Profile?.Nickname} killed in action by {killer?.Profile?.Nickname ?? "?"}");
            KillWithAttribution(victim.GetPlayer, killer);
        }
        catch (System.Exception e)
        {
            Log.Error($"GHOST SKIRMISH: killing vanilla {victim.GetPlayer?.Profile?.Nickname} failed: {e}");
        }
    }

    // ── Vanilla (non-ORBIT) sleepers ────────────────────────────────────

    /// <summary>
    /// The vanilla (non-ORBIT) side of the per-type policy: any alive bot ORBIT does not drive whose type
    /// toggle is ON can sleep. Native movement preserves supported decisions and routes without takeover;
    /// disabling it restores stationary sleep. Groups sleep and wake per BSG BotsGroup so a boss never sleeps apart from its
    /// followers. Toggled-off types are left completely untouched.
    /// </summary>
    private void UpdateVanilla()
    {
        _vanillaGroups.Clear();

        var players = _gameWorld.AllAlivePlayersList;
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player == null) continue;
            try
            {
                if (!player.AIData.IsAI) continue;
                var owner = player.AIData.BotOwner;
                if (owner == null) continue;
                if (_botRoster.GetAgent(owner) != null) continue; // ORBIT-driven, handled above
                var role = owner.Profile?.Info?.Settings?.Role;
                if (role.HasValue && role.Value.ToString() == "shooterBTR") continue; // never touch the BTR
                if (!IsDefaultDormant(owner)) continue; // type toggle OFF — leave fully vanilla

                // A dead body that is still dormant must be re-activated NOW (hidden corpse otherwise).
                if (player.HealthController is not { IsAlive: true } || owner.IsDead)
                {
                    if (_vanillaDormant.Remove(owner))
                    {
                        _nativeGhosts.Remove(owner);
                        UnthrottleBrain(owner);
                        DormantProfileIds.Remove(player.ProfileId);
                        if (!owner.gameObject.activeSelf) owner.gameObject.SetActive(true);
                        Log.Info($"vanilla sleeper {player.Profile?.Nickname} died while dormant — body re-activated");
                    }
                    continue;
                }

                var key = (object)owner.BotsGroup ?? owner;
                if (!_vanillaGroups.TryGetValue(key, out var list))
                    _vanillaGroups[key] = list = new List<BotOwner>(4);
                list.Add(owner);
            }
            catch
            {
                // Despawning player mid-teardown — skip it this poll.
            }
        }

        foreach (var kv in _vanillaGroups)
        {
            var group = kv.Value;
            var dormant = 0;
            for (var i = 0; i < group.Count; i++)
                if (_vanillaDormant.Contains(group[i])) dormant++;
            if (dormant > 0)
            {
                var reason = dormant == group.Count ? VanillaWakeReason(kv.Key, group) : "group membership changed";
                if (reason != null) WakeVanillaGroup(kv.Key, group, reason);
            }
            else if (CanVanillaSleep(kv.Key, group))
            {
                SleepVanillaGroup(kv.Key, group);
            }
        }
    }

    private bool CanVanillaSleep(object key, List<BotOwner> group)
    {
        if (_vanillaGroupWokeAt.TryGetValue(key, out var wokeAt) && Time.time - wokeAt < WakeCooldownSeconds)
            return false;

        for (var i = 0; i < group.Count; i++)
        {
            var bot = group[i];
            var player = bot.GetPlayer;
            if (bot.BotState != EBotState.Active || !bot.gameObject.activeSelf) return false;
            if (bot.Memory != null && (bot.Memory.GoalEnemy != null || bot.Memory.IsUnderFire)) return false;
            if (_targetedProfileIds.Contains(player.ProfileId)) return false;
            if (MinSqrDistanceToHumans(player.Position) <= _scavSleepDistanceSqr) return false;
            if (_cfg.NativeGhostMovement && GhostMovementEnabled && !_nativeGhosts.CanSleep(bot)) return false;

            // Same bleed gate as ORBIT squads.
            var hp = VanillaHp(bot);
            if (_vanillaLastHp.TryGetValue(bot, out var lastHp) && hp < lastHp - 0.5f) _vanillaHpDropAt[bot] = Time.time;
            _vanillaLastHp[bot] = hp;
            if (_vanillaHpDropAt.TryGetValue(bot, out var dropAt) && Time.time - dropAt < HpStableSeconds)
            {
                TryGhostPatchUpVanilla(bot);
                return false;
            }
        }
        return true;
    }

    private string VanillaWakeReason(object key, List<BotOwner> group)
    {
        var awakeBotTriggerArmed = !_vanillaGroupSleptAt.TryGetValue(key, out var sleptAt)
                                   || Time.time - sleptAt >= SleepGraceSeconds;

        for (var i = 0; i < group.Count; i++)
        {
            var bot = group[i];
            var player = bot.GetPlayer;
            var nativeReason = _nativeGhosts.WakeReason(bot);
            if (nativeReason != null) return nativeReason;
            if (_targetedProfileIds.Contains(player.ProfileId)) { _wakeByTargeted++; return $"{player.Profile?.Nickname} targeted"; }
            var hp = VanillaHp(bot);
            if (_vanillaHpBaseline.TryGetValue(bot, out var baseline) && hp < baseline - 1f)
            {
                _wakeByDamage++;
                return $"{player.Profile?.Nickname} took {baseline - hp:F0} damage while dormant at {player.Position}" +
                       (DangerZones.IsInside(player.Position) ? " (inside a border/minefield zone)" : "");
            }
            var humanSqr = MinSqrDistanceToHumans(player.Position);
            if (humanSqr <= _wakeDistanceSqr) { _wakeByHuman++; return $"human at {Mathf.Sqrt(humanSqr):F0}m"; }
            if (InScopedView(player.Position, out var scopeDist)) { _wakeByScope++; return $"in scoped view at {scopeDist:F0}m"; }
            if (awakeBotTriggerArmed && AnyAwakeBotNear(player.Position, null)) { _wakeByAwakeBot++; return $"awake bot near {player.Profile?.Nickname}"; }
        }
        return null;
    }

    private void SleepVanillaGroup(object key, List<BotOwner> group)
    {
        var native = _cfg.NativeGhostMovement && GhostMovementEnabled;
        for (var i = 0; i < group.Count; i++)
        {
            var bot = group[i];
            try
            {
                if (native)
                    _nativeGhosts.Add(bot);
                else
                {
                    bot.DecisionQueue.Clear();
                    bot.Memory.GoalEnemy = null;
                    bot.PatrollingData.Pause();
                }
                bot.gameObject.SetActive(false);
            }
            catch (System.Exception e)
            {
                Log.Error($"vanilla sleeper {bot.GetPlayer?.Profile?.Nickname} sleep recipe failed: {e}");
                // Roll back the entire group, including this member if deactivation partly succeeded.
                _vanillaDormant.Add(bot);
                WakeVanillaGroup(key, group, "sleep failed");
                return;
            }
            ThrottleBrain(bot);
            _vanillaDormant.Add(bot);
            _vanillaHpBaseline[bot] = VanillaHp(bot);
            DormantProfileIds.Add(bot.GetPlayer.ProfileId);
        }
        _vanillaGroupSleptAt[key] = Time.time;
        _windowSleeps++;
        Log.Info($"vanilla group ({group[0].GetPlayer?.Profile?.Nickname} +{group.Count - 1}) dormant {(native ? "with native movement" : "in place")} ({_vanillaDormant.Count} vanilla dormant)");
    }

    private void WakeVanillaGroup(object key, List<BotOwner> group, string reason)
    {
        for (var i = 0; i < group.Count; i++)
        {
            var bot = group[i];
            if (!_vanillaDormant.Remove(bot)) continue;
            var native = _nativeGhosts.Remove(bot);
            UnthrottleBrain(bot);
            DormantProfileIds.Remove(bot.GetPlayer.ProfileId);
            _vanillaLastHp[bot] = VanillaHp(bot);
            if (bot.IsDead) continue;
            try
            {
                bot.gameObject.SetActive(true);
                if (!native) bot.PatrollingData.Unpause();
                bot.PostActivate();
                if (native) NativeGhostSystem.ResyncAfterWake(bot);
            }
            catch (System.Exception e)
            {
                Log.Error($"vanilla sleeper {bot.GetPlayer?.Profile?.Nickname} wake recipe failed: {e}");
            }
        }
        _vanillaGroupWokeAt[key] = Time.time;
        _windowWakes++;
        Log.Info($"vanilla group ({group[0].GetPlayer?.Profile?.Nickname} +{group.Count - 1}) awake: {reason} ({_vanillaDormant.Count} vanilla dormant)");
    }

    private static float VanillaHp(BotOwner bot)
    {
        var hc = bot.GetPlayer?.HealthController;
        if (hc == null || !hc.IsAlive) return 0f;
        return hc.GetBodyPartHealth(EBodyPart.Common, true).Current;
    }

    public void OnVanillaRemoved(BotOwner bot)
    {
        var dormant = _vanillaDormant.Remove(bot);
        _nativeGhosts.Remove(bot);
        _vanillaHpBaseline.Remove(bot);
        _vanillaLastHp.Remove(bot);
        _vanillaHpDropAt.Remove(bot);
        if (!dormant) return;
        UnthrottleBrain(bot);
        DormantProfileIds.Remove(bot.ProfileId);
        try { if (bot.gameObject != null) bot.gameObject.SetActive(true); }
        catch { }
    }
}
