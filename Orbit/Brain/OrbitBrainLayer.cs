using System;
using System.Collections.Generic;
using System.Text;
using Comfort.Common;
using EFT.Ballistics;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using Orbit.Core;
using Orbit.Entities;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Brain;

/// <summary>
/// Inert no-op BigBrain action. Registered as the layer's "next action" so BSG's brain machinery stays
/// satisfied — actual movement / look / loot behaviour is driven by our own systems, not by CustomLogic
/// ticks.
/// </summary>
internal class IdleAction(BotOwner botOwner) : CustomLogic(botOwner)
{
    public override void Start() { }
    public override void Stop() { }
    public override void Update(CustomLayer.ActionData data) { }
}

/// <summary>
/// BigBrain layer registered against every non-combat brain we care about. Priority 19 sits below SAIN Combat
/// (20) so an enemy contact preempts it, and above PatrolAssault (0) so it owns idle/loot/quest behaviour.
///
/// Per-bot construction either attaches an Agent (and overrides BSG's mover + door-collision) or stays inert
/// when the bot's role is excluded by the faction-mod takeover / vanilla-scavs / vanilla-goons toggles.
/// </summary>
public class OrbitBrainLayer : CustomLayer
{
    private const string LayerName = "OrbitBrainLayer";

    private readonly OrbitManager _orbit;
    private readonly Agent _agent;
    private readonly bool _excluded;
    private readonly Collider _botCollider;

    // Tracks SAIN combat layer state from OnLayerChanged. Replaces BotOwner.Memory.LastEnemyTimeSeen which
    // SAIN keeps fresh long past actual combat (sticky enemy memory), trapping ORBIT off.
    private const string SainCombatLayerName = "SAIN : Combat Layer";
    private bool _sainCombatActive;
    private float _sainCombatEndedAt = float.NegativeInfinity;
    private float _nextHandoffWarningAt;

    // Diag throttling — log gate state on transition and every 5s if off.
    private bool _lastIsActive = true;
    private float _lastIsActiveDiagAt;

    // Substrings (case-insensitive) of WildSpawnType names whose bots should NOT be hijacked. Populated at
    // boot by Plugin when the user toggles OFF a faction-mod takeover (UNTAR / RUAF / BlackDiv / ISB).
    private static readonly HashSet<string> _excludedRoleSubstrings = new(StringComparer.OrdinalIgnoreCase);

    // Vanilla-behaviour opt-outs. PlayerScavs share WildSpawnType.assault with bot scavs but are NEVER
    // excluded by VanillaScavs (explicit Profile.WillBeAPlayerScav check inside IsExcludedRole).
    private static bool _vanillaScavs;
    private static bool _vanillaGoons;
    private static bool _vanillaCultists;
    private static bool _vanillaRaiders;
    private static bool _vanillaRogues;
    private static bool _vanillaBloodhounds;
    internal static bool LegacyUntarHunts { get; set; }

    public static void AddExcludedRoleSubstring(string sub)
    {
        if (!string.IsNullOrEmpty(sub)) _excludedRoleSubstrings.Add(sub);
    }

    public static void SetVanillaScavExclusion(bool excluded) => _vanillaScavs = excluded;
    public static void SetVanillaGoonExclusion(bool excluded) => _vanillaGoons = excluded;
    public static void SetVanillaCultistExclusion(bool excluded) => _vanillaCultists = excluded;
    public static void SetVanillaRaiderExclusion(bool excluded) => _vanillaRaiders = excluded;
    public static void SetVanillaRogueExclusion(bool excluded) => _vanillaRogues = excluded;
    public static void SetVanillaBloodhoundExclusion(bool excluded) => _vanillaBloodhounds = excluded;

    /// <summary>The spawn id of a MoreBotsAPI hunt squad member, null for everything else.</summary>
    private static string HuntSpawnId(BotOwner botOwner)
    {
        try
        {
            var id = botOwner?.SpawnProfileData?.SpawnParams?.Id_spawn;
            return id != null && id.IndexOf("hunt", StringComparison.OrdinalIgnoreCase) >= 0 ? id : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsExcludedRole(BotOwner botOwner)
    {
        var role = botOwner?.Profile?.Info?.Settings?.Role;
        if (!role.HasValue) return false;

        // White Tusks always keep ISB's behaviour, independently of the takeover toggle.
        if (IsbRolePolicy.IsWhiteTusk((int)role.Value))
        {
            Log.Info($"FACTION ISB: {botOwner.Profile.Nickname} role={role.Value} ({(int)role.Value}) keeps native White Tusk behaviour");
            return true;
        }

        // ORBIT registers on these vanilla brains only to drive custom factions that borrow them; never take over
        // the real vanilla bots, so exclude their WildSpawnTypes unconditionally. Custom faction types differ and
        // fall through to the toggle logic below.
        switch (role.Value)
        {
            case EFT.WildSpawnType.bossGluhar:
            case EFT.WildSpawnType.followerGluharScout:
                return true;
        }

        // Faction hunts use their owner's toggle, including UNTAR's legacy generic "hunt" marker.
        // Unknown owners keep their native behaviour instead of bypassing every exclusion.
        if (role.Value == EFT.WildSpawnType.pmcBot && HuntSpawnId(botOwner) is { } huntId)
        {
            var excluded = HuntFactionPolicy.IsExcluded(huntId, LegacyUntarHunts, _excludedRoleSubstrings);
            var owner = HuntFactionPolicy.Owner(huntId, LegacyUntarHunts) ?? "unknown";
            Log.Info($"FACTION HUNT: {botOwner.Profile.Nickname} role={role.Value} spawn={huntId} owner={owner} control={(excluded ? "native" : "ORBIT")}");
            return excluded;
        }

        if (_excludedRoleSubstrings.Count > 0)
        {
            var roleName = role.Value.ToString();
            foreach (var sub in _excludedRoleSubstrings)
            {
                if (roleName.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }

        if (_vanillaScavs && role.Value.IsScav())
        {
            var isPlayerScav = botOwner?.Profile != null && botOwner.Profile.WillBeAPlayerScav();
            if (!isPlayerScav) return true;
        }

        if (_vanillaGoons && role.Value.IsGoon())
        {
            return true;
        }

        if (_vanillaCultists && role.Value.IsCultist())
        {
            return true;
        }

        if (_vanillaRaiders && role.Value == WildSpawnType.pmcBot)
        {
            return true;
        }

        if (_vanillaRogues && role.Value == WildSpawnType.exUsec)
        {
            return true;
        }

        if (_vanillaBloodhounds && role.Value.IsBloodhound())
        {
            return true;
        }

        return false;
    }

    public OrbitBrainLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
    {
        if (IsExcludedRole(botOwner))
        {
            _excluded = true;
            return;
        }

        // BSG would otherwise deactivate the bot when far from the player.
        botOwner.StandBy.CanDoStandBy = false;
        botOwner.StandBy.Activate();

        _orbit = Singleton<OrbitManager>.Instance;
        _agent = _orbit.AddAgent(botOwner);

        botOwner.Brain.BaseBrain.OnLayerChangedTo += OnLayerChanged;
        botOwner.GetPlayer.OnPlayerDead += OnDead;

        // Bot<->door collision is managed per door state by DoorSystem: the bot physically stops at a CLOSED
        // leaf (so it can't phase through a carved-open locked door) and passes freely through an open / opening
        // one so the swing never shoves it.
        _botCollider = _agent.Bot.GetPlayer.CharacterController.GetCollider();
        _orbit.DoorSystem.RegisterBot(_botCollider, _agent.Bot.GetPlayer.POM.Collider);
    }

    private void OnDead(Player player, IPlayer lastAggressor, DamageInfo damageInfo, EBodyPart part)
    {
        player.OnPlayerDead -= OnDead;
        _agent.IsActive = false;
        if (_botCollider != null) _orbit.DoorSystem.UnregisterBot(_botCollider);
        var squad = _agent.Squad;
        _orbit.RemoveAgent(_agent);
        // Re-evaluate the cumulative extract threshold without the dead member's contribution, in case
        // survivors already sum above it.
        if (squad != null && squad.Members.Count > 0)
        {
            Orbit.Tasks.Actions.LootContainerAction.ReevaluateExtractForSquad(squad);
        }
    }

    private void OnLayerChanged(AICoreLayer<BotLogicDecision> layer)
    {
        // BigBrain has already selected the new layer. Release our movement hooks before any
        // physical operation can throw, otherwise both controllers can keep driving this bot.
        var wasActive = _agent.IsActive;
        _agent.IsActive = false;
        var layerName = layer.Name();
        var sainCombatNow = layerName == SainCombatLayerName;
        if (_sainCombatActive && !sainCombatNow)
            _sainCombatEndedAt = Time.time;
        _sainCombatActive = sainCombatNow;

        try
        {
            var mover = _agent.Bot.Mover;
            if (layerName == LayerName)
            {
                Log.Debug($"{_agent} stopping builtin bot mover");
                mover.Stop();
                _agent.IsActive = true;
            }
            else
            {
                if (_agent.IsDormant)
                    Log.Info($"{_agent} dormant body handed to BSG layer {layerName} (priority {layer.Priority})");
                if (wasActive)
                {
                    // Release our path/loot pause now, before the new layer starts its action.
                    // Deferred loot cleanup must not unpause a mover subsequently owned by SAIN.
                    mover.Pause = false;
                    Log.Debug($"{_agent} setting player to navmesh");
                    _agent.Stuck.Recovery.Observe(_agent.Position, mover, force: true);
                    if (_agent.Stuck.Recovery.OnMesh)
                        mover.SetPlayerToNavMesh(_agent.Stuck.Recovery.Anchor);
                    else
                    {
                        // Do not feed an airborne position into the native repair loop at handoff.
                        mover.Stop();
                        Log.Warning($"{_agent} combat handoff outside NavMesh: native route cleared, invalid recovery anchor rejected");
                    }
                }
            }
        }
        catch (Exception e)
        {
            // Do not abort the remaining layer-change subscribers or the new layer's Start.
            if (Time.time >= _nextHandoffWarningAt)
            {
                _nextHandoffWarningAt = Time.time + 5f;
                Log.Warning($"{_agent} layer handoff to {layerName} failed, ORBIT movement released: {e}");
            }
        }

        Log.Debug($"{_agent} layer changed to: {layerName} priority: {layer.Priority}");
    }

    public override string GetName() => LayerName;

    public override Action GetNextAction() => new(typeof(IdleAction), "Idle");

    public override bool IsActive()
    {
        if (_excluded) return false;
        // A sleeper never hands its inactive body to BSG. Healing and combat cannot happen on it, and
        // when the meds gate below let PatrolAssault take a dormant bot, BSG started a first aid on a
        // body whose animator was off: Medecine.Using stayed stuck and the bot could not walk a single
        // step after it woke, only teleport (Customs raid: FantaSipper, 30 rescues in 4 minutes). The
        // ghost patch-up handles bleeds while asleep; real healing resumes on wake.
        if (_agent.IsDormant)
        {
            if (!_lastIsActive)
            {
                Log.Debug($"{_agent} IsActive transition: False → True (dormant)");
                _lastIsActive = true;
            }
            return true;
        }
        var timeSinceSainCombatEnded = _sainCombatActive ? 0f : Time.time - _sainCombatEndedAt;
        var inCombatWindow = _sainCombatActive || timeSinceSainCombatEnded < 15f;
        var medsWorking = BotOwner.Medecine.Using || BotOwner.Medecine.SurgicalKit.HaveWork || BotOwner.Medecine.FirstAid.Have2Do;
        var isHealing = medsWorking && (_sainCombatActive || timeSinceSainCombatEnded < 60f);
        var isInCombat = BotOwner.Memory.IsUnderFire || inCombatWindow;
        var active = !isHealing && !isInCombat;

        if (!active && (active != _lastIsActive || Time.time - _lastIsActiveDiagAt > 5f))
        {
            Log.Debug($"{_agent} IsActive=false: sainCombatActive={_sainCombatActive}, timeSinceSainEnd={timeSinceSainCombatEnded:F1}s, IsUnderFire={BotOwner.Memory.IsUnderFire}, medsWorking={medsWorking} (Using={BotOwner.Medecine.Using}, Surgical={BotOwner.Medecine.SurgicalKit.HaveWork}, FirstAid={BotOwner.Medecine.FirstAid.Have2Do})");
            _lastIsActiveDiagAt = Time.time;
        }
        if (active != _lastIsActive)
        {
            Log.Debug($"{_agent} IsActive transition: {_lastIsActive} → {active}");
            _lastIsActive = active;
        }
        return active;
    }

    public override bool IsCurrentActionEnding() => false;

    public override void BuildDebugText(StringBuilder sb)
    {
        var pose = BotOwner.GetPlayer.MovementContext.PoseLevel;
        var actualSpeed = _agent.Player.MovementContext.CharacterMovementSpeed;

        var distMove = 0f;
        if (_agent.Movement.HasPath)
        {
            distMove = (_agent.Movement.Target - _agent.Position).sqrMagnitude;
        }

        var distObj = 0f;
        if (_agent.Objective.Location != null)
        {
            distObj = (_agent.Objective.Location.Position - _agent.Position).sqrMagnitude;
        }

        sb.AppendLine($"{_agent} Task: {_agent.TaskAssignment.Task}");
        sb.AppendLine($"{_agent.Movement} dist {distMove}");
        sb.AppendLine(_agent.Stuck.Soft.ToString());
        sb.AppendLine(_agent.Stuck.Hard.ToString());
        sb.AppendLine($"{_agent.Objective} dist {distObj}/{_agent.Objective.Location?.RadiusSqr}");
        sb.AppendLine($"{_agent.Guard}");
        sb.AppendLine("*** Generic ***");
        sb.AppendLine($"HasEnemy: {BotOwner.Memory.HaveEnemy} UnderFire: {BotOwner.Memory.IsUnderFire}");
        sb.AppendLine($"Pose: {pose} ActualSpeed: {actualSpeed} Stamina: {BotOwner.GetPlayer.Physical.Stamina.NormalValue}");
        sb.AppendLine("*** Squad ***");
        sb.AppendLine($"{_agent.Squad}, size: {_agent.Squad.Size}");
        sb.AppendLine($"{_agent.Squad.Objective}");
        sb.AppendLine("*** Actions ***");
        GenerateUtilityReport(sb);
    }

    private void GenerateUtilityReport(StringBuilder sb)
    {
        var actions = _orbit.ActionManager.Tasks;
        for (var i = 0; i < actions.Length; i++)
        {
            var action = actions[i];
            var score = _agent.TaskScores[i];
            var prefix = action == _agent.TaskAssignment.Task ? "*" : "";
            sb.AppendLine($"{prefix}{action.GetType().Name}: {score:0.00}");
        }
    }
}
