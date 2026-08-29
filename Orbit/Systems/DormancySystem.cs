using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using Orbit.Core;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Looting;
using Orbit.Sain;
using UnityEngine;

namespace Orbit.Systems;

/// <summary>
/// The built-in AI limiter. Sleeps the BODY of far-away squads, not their brain: all ORBIT logic
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
/// slot; ORBIT-driven members ghost-walk their routes while dormant, vanilla members freeze in place (per
/// BSG BotsGroup, so a boss never sleeps apart from its followers). Toggle OFF = ORBIT bots of that type
/// fall back to the standard PMC-like rules, vanilla bots of that type are left untouched. The floor only
/// counts standard-policy bots and self-caps to half of them, so small-population maps still sleep. Bots
/// whose HP dropped recently never sleep (bleeds tick on inactive bodies and a sleeper cannot heal).
/// Corpses can never go dormant, and OnAgentRemoved re-activates a dormant body just in case.
/// </summary>
public class DormancySystem
{
    private const float PollIntervalSeconds = 0.5f;
    private const float WakeCooldownSeconds = 30f;
    private const float SleepGraceSeconds = 15f;
    private const float HpStableSeconds = 15f;

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

    public static void ClearStatics()
    {
        Api.OrbitTelemetry.ClearGhostFights();
        DormantProfileIds.Clear();
        GhostMovementEnabled = false;
        VisionBlocks = 0;
    }

    private readonly MovementSystem _movementSystem;
    private readonly DoorSystem _doorSystem;
    private readonly BotRoster _botRoster;
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
    private readonly ServerConfig.AiLimiterSection _cfg;
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

    // Simulated-fight gunfire: shots queued at resolution time and played over the following seconds
    // through BetterAudio's own sources (the fighters' bodies stay inactive — we only read their
    // weapons' sound banks), so the player hears WHERE the off-screen activity is and can go look.
    private struct PendingShot
    {
        public float At;
        public Vector3 Pos;
        public WeaponSoundPlayer Sound;
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
    private int _farBlockedBleeding, _farBlockedCooldown;
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
        _gameWorld = Singleton<GameWorld>.Instance;

        // Config is read once per raid: ServerConfig is re-fetched in OrbitInitPatch right before this
        // system is constructed, so a web-UI Save applies on the next raid, and values never move mid-raid.
        var cfg = ServerConfig.AiLimiter;
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
            Log.Always($"AI limiter ON — sleep>{sleepDist:F0}m (default-dormant>{scavSleepDist:F0}m) wake<{cfg.WakeDistance:F0}m hostileWake<{cfg.HostileWakeDistance:F0}m minAwake={_minAwakeBots} ghost={(cfg.GhostMovement ? "on" : "off")} ghostLoot={B(cfg.GhostLooting)} fights={_fightsMode.ToString().ToLowerInvariant()}/{freq} lethality={_lethality:F1}x scopedWake={(_scopedWakeEnabled ? $"{_scopedWakeMax:F0}m" : "off")} dormantTypes=[scav={B(cfg.DormantScavs)} goon={B(cfg.DormantGoons)} boss={B(cfg.DormantBosses)} cultist={B(cfg.DormantCultists)} raider={B(cfg.DormantRaiders)} bloodhound={B(cfg.DormantBloodhounds)} other={B(cfg.DormantOthers)}]");
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
                Log.Error($"AI limiter poll failed (#{_pollErrors}) — skipping this tick, limiter stays alive: {e}");
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
        var go = agent.Bot?.gameObject;
        if (go != null && !go.activeSelf) go.SetActive(true);
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
            $"farBlocked=[combat={_farBlockedCombat} loot={_farBlockedLoot} door={_farBlockedDoor} extract={_farBlockedExtract} state={_farBlockedState} bleeding={_farBlockedBleeding} cooldown={_farBlockedCooldown} proximity={_blockedProximity} floor={_blockedFloor}] " +
            $"ghostFights={_windowFights} fightShotsPlayed={_windowShotsPlayed} visionBlocks={VisionBlocks}");
        _summaryWindowStart = Time.time;
        _windowSleeps = _windowWakes = 0;
        _wakeByHuman = _wakeByAwakeBot = _wakeByExtract = _wakeByTargeted = _wakeByDamage = _wakeByScope = 0;
        _farBlockedCombat = _farBlockedLoot = _farBlockedDoor = _farBlockedExtract = _farBlockedState = 0;
        _farBlockedBleeding = _farBlockedCooldown = 0;
        _blockedProximity = _blockedFloor = 0;
        _windowFights = 0;
        _windowShotsPlayed = 0;
        VisionBlocks = 0;
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

        if (squad.ExtractRequested) { _farBlockedExtract++; return false; }

        for (var i = 0; i < squad.Members.Count; i++)
        {
            var agent = squad.Members[i];
            var bot = agent.Bot;
            if (bot == null || bot.IsDead || bot.BotState != EBotState.Active) { _farBlockedState++; return false; }
            if (!bot.gameObject.activeSelf) { _farBlockedState++; return false; } // someone else owns the GameObject — never fight over it
            if (agent.SoloExtractRequested) { _farBlockedExtract++; return false; }
            if (agent.Objective.Status == ObjectiveStatus.Looting) { _farBlockedLoot++; return false; } // let the loot animation finish
            if (Time.time < agent.Movement.DoorInteractHoldUntil) { _farBlockedDoor++; return false; } // mid door interaction
            if (bot.Memory != null && (bot.Memory.GoalEnemy != null || bot.Memory.IsUnderFire)) { _farBlockedCombat++; return false; }
            if (_targetedProfileIds.Contains(agent.Player.ProfileId)) { _farBlockedCombat++; return false; }
            if (Time.time - agent.LastHpDropTime < HpStableSeconds) { _farBlockedBleeding++; return false; }
        }
        return true;
    }

    /// <summary>Why a dormant squad must wake, or null to keep sleeping. The string goes straight to the
    /// wake log line so a single raid read tells premature wakes from legit ones.</summary>
    private string WakeReason(Squad squad)
    {
        if (squad.ExtractRequested) { _wakeByExtract++; return "squad extract requested"; }

        // The awake-bot trigger gets a short grace after sleep entry — that alone killed the 2 Hz
        // ping-pong pairs. Human proximity, damage, targeting and extracts always wake instantly.
        var awakeBotTriggerArmed = Time.time - squad.DormancySleptAt >= SleepGraceSeconds;

        for (var i = 0; i < squad.Members.Count; i++)
        {
            var agent = squad.Members[i];
            if (agent.SoloExtractRequested) { _wakeByExtract++; return $"{agent} solo extract"; }
            if (_targetedProfileIds.Contains(agent.Player.ProfileId)) { _wakeByTargeted++; return $"{agent} targeted"; }
            // Position-based damage (border minefields at least) lands on inactive bodies, and a sleeper
            // can neither react nor heal — hand it back to SAIN immediately.
            var hp = TotalHp(agent);
            if (hp < agent.DormantHpBaseline - 1f)
            {
                _wakeByDamage++;
                return $"{agent} took {agent.DormantHpBaseline - hp:F0} damage while dormant";
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
            bot.gameObject.SetActive(true);
            bot.PatrollingData.Unpause();
            bot.PostActivate();

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
                // Savage-family units (scavs, bosses, cultists...) are allies of each other; every
                // pairing involving a PMC-side unit is hostile (PMC vs PMC included, SPT free-for-all).
                if (a.IsSavage && b.IsSavage) continue;

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

    private void BuildGhostUnits(List<Squad> squads)
    {
        _ghostUnits.Clear();

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
            }
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
            }
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
            var weapon = bot.GetPlayer?.HandsController?.Item;
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
        var rollA = UnitStrength(a) * rangeA * Random.Range(0.7f, 1.3f);
        var rollB = UnitStrength(b) * rangeB * Random.Range(0.7f, 1.3f);
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
        if (winner.Squad != null) winner.Squad.GhostFightUntil = fight.EndsAt;
        if (loser.Squad != null) loser.Squad.GhostFightUntil = fight.EndsAt;

        _windowFights++;
        Log.Info($"GHOST SKIRMISH at {distance:F0}m: {a.Label} (str {rollA:F1}, reach {a.Reach:F0}m) vs {b.Label} (str {rollB:F1}, reach {b.Reach:F0}m), {winner.Label} wins over {duration:F0}s, {loserDeaths + winnerDeaths} killed");

        QueueFightSounds(a, posA, b, posB, loserDeaths + winnerDeaths, duration, shotsPerSecond);
    }

    private bool UnitInFight(GhostUnit unit)
        => _unitFightingUntil.TryGetValue(unit.Key, out var until) && Time.time < until;

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
            var damageInfo = new EFT.Ballistics.DamageInfo
            {
                DamageType = EDamageType.Bullet,
                Damage = damage,
                Direction = Vector3.forward,
                HitPoint = player.Position + new Vector3(0f, 1f, 0f),
            };
            hc.ApplyDamage(AttritionParts[Random.Range(0, AttritionParts.Length)], damage, damageInfo);
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

        var profileA = RandomMemberProfileId(a);
        var profileB = RandomMemberProfileId(b);
        // Shot count follows the window at the fight's own firing rate (protracted fights trade
        // sporadic pot-shots, not a continuous mag dump), capped to bound the queue.
        var shots = Mathf.Min(90, Mathf.RoundToInt(duration * shotsPerSecond) + casualties * 3);

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
        });

        const float earshotSqr = 1500f * 1500f;
        var listenerDist = Mathf.Sqrt(Mathf.Min(MinSqrDistanceToHumans(posA), MinSqrDistanceToHumans(posB)));
        if (listenerDist * listenerDist > earshotSqr)
        {
            Log.Debug($"GHOST FIGHT SOUNDS: skipped, closest human {listenerDist:F0}m is out of earshot");
            return;
        }

        var soundA = WeaponSoundFromProfile(profileA);
        var soundB = WeaponSoundFromProfile(profileB);
        if (soundA == null && soundB == null)
        {
            Log.Debug("GHOST FIGHT SOUNDS: skipped, no weapon sound player resolved on either side");
            return;
        }

        Log.Info($"GHOST FIGHT SOUNDS: queueing {shots} shots over {duration:F1}s, closest human {listenerDist:F0}m");
        for (var i = 0; i < shots; i++)
        {
            var sideA = Random.value < 0.5f;
            var sound = sideA ? soundA : soundB;
            if (sound == null) { sound = sideA ? soundB : soundA; }
            _pendingShots.Add(new PendingShot
            {
                At = Time.time + Random.Range(0.1f, duration),
                Pos = (sideA ? posA : posB) + new Vector3(Random.Range(-3f, 3f), 0f, Random.Range(-3f, 3f)),
                Sound = sound,
            });
        }
    }

    private static string RandomMemberProfileId(GhostUnit unit)
    {
        try
        {
            return unit.Agents.Count > 0
                ? unit.Agents[Random.Range(0, unit.Agents.Count)].Player?.ProfileId
                : unit.VanillaBots.Count > 0 ? unit.VanillaBots[Random.Range(0, unit.VanillaBots.Count)].GetPlayer?.ProfileId : null;
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
                // The tail bank IS what a distant gunshot sounds like in EFT; body as fallback.
                var bank = shot.Sound.Tail != null ? shot.Sound.Tail : shot.Sound.Body;
                if (bank == null) continue;
                var listenerDist = Mathf.Sqrt(MinSqrDistanceToHumans(shot.Pos));
                audio.PlayAtPointDistant(shot.Pos, bank, listenerDist, 1f);
                _windowShotsPlayed++;
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
            var killer = ClosestVisibleSurvivor(opposing, victimPos);
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
        Log.Debug($"GHOST SKIRMISH: no line-of-sight killer/victim pair between {opposing.Label} and {unit.Label} — casualty dropped");
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

    private static Player ClosestVisibleSurvivor(GhostUnit unit, Vector3 targetPos)
    {
        Player best = null;
        var bestSqr = float.MaxValue;
        for (var i = 0; i < unit.Count; i++)
        {
            var p = i < unit.Agents.Count ? unit.Agents[i].Player : unit.VanillaBots[i - unit.Agents.Count].GetPlayer;
            if (p == null) continue;
            var d = (p.Position - targetPos).sqrMagnitude;
            if (d >= bestSqr) continue;
            if (!ClearFightLos(p.Position, targetPos)) continue;
            best = p;
            bestSqr = d;
        }
        return best;
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
        _vanillaDormant.Remove(victim);
        DormantProfileIds.Remove(victim.GetPlayer?.ProfileId);
        try
        {
            victim.gameObject.SetActive(true);
            victim.PatrollingData.Unpause();
            victim.PostActivate();
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
    /// toggle is ON sleeps frozen in place — no ORBIT path, no ghost movement, which suits guard-type
    /// vanilla AI fine. Groups sleep and wake per BSG BotsGroup so a boss never sleeps apart from its
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
            if (_vanillaDormant.Contains(group[0]))
            {
                var reason = VanillaWakeReason(kv.Key, group);
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

            // Same bleed gate as ORBIT squads.
            var hp = VanillaHp(bot);
            if (_vanillaLastHp.TryGetValue(bot, out var lastHp) && hp < lastHp - 0.5f) _vanillaHpDropAt[bot] = Time.time;
            _vanillaLastHp[bot] = hp;
            if (_vanillaHpDropAt.TryGetValue(bot, out var dropAt) && Time.time - dropAt < HpStableSeconds) return false;
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
            if (_targetedProfileIds.Contains(player.ProfileId)) { _wakeByTargeted++; return $"{player.Profile?.Nickname} targeted"; }
            var hp = VanillaHp(bot);
            if (_vanillaHpBaseline.TryGetValue(bot, out var baseline) && hp < baseline - 1f)
            {
                _wakeByDamage++;
                return $"{player.Profile?.Nickname} took {baseline - hp:F0} damage while dormant";
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
        for (var i = 0; i < group.Count; i++)
        {
            var bot = group[i];
            try
            {
                bot.DecisionQueue.Clear();
                bot.Memory.GoalEnemy = null;
                bot.PatrollingData.Pause();
                bot.gameObject.SetActive(false);
            }
            catch (System.Exception e)
            {
                Log.Error($"vanilla sleeper {bot.GetPlayer?.Profile?.Nickname} sleep recipe failed: {e}");
            }
            _vanillaDormant.Add(bot);
            _vanillaHpBaseline[bot] = VanillaHp(bot);
            DormantProfileIds.Add(bot.GetPlayer.ProfileId);
        }
        _vanillaGroupSleptAt[key] = Time.time;
        _windowSleeps++;
        Log.Info($"vanilla group ({group[0].GetPlayer?.Profile?.Nickname} +{group.Count - 1}) dormant in place ({_vanillaDormant.Count} vanilla dormant)");
    }

    private void WakeVanillaGroup(object key, List<BotOwner> group, string reason)
    {
        for (var i = 0; i < group.Count; i++)
        {
            var bot = group[i];
            _vanillaDormant.Remove(bot);
            DormantProfileIds.Remove(bot.GetPlayer.ProfileId);
            _vanillaLastHp[bot] = VanillaHp(bot);
            if (bot.IsDead) continue;
            try
            {
                bot.gameObject.SetActive(true);
                bot.PatrollingData.Unpause();
                bot.PostActivate();
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
}
