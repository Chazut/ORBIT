using System;
using System.Collections.Generic;
using System.Text;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using Orbit.Core;
using Orbit.Entities;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Brain;

/// <summary>
/// Inert no-op BigBrain action. Registered as the layer's "next action" so
/// BSG's brain machinery stays satisfied — actual movement / look / loot
/// behaviour is driven by our own systems, not by CustomLogic ticks.
/// </summary>
internal class IdleAction(BotOwner botOwner) : CustomLogic(botOwner)
{
    public override void Start() { }
    public override void Stop() { }
    public override void Update(CustomLayer.ActionData data) { }
}

/// <summary>
/// BigBrain layer registered against every non-combat brain we care about.
/// Priority 19 sits below SAIN Combat (20) so an enemy contact preempts it,
/// and above PatrolAssault (0) so it owns idle/loot/quest behaviour.
///
/// Per-bot construction either attaches an Agent (and overrides BSG's
/// mover + door-collision) or stays inert when the bot's role is excluded
/// by the faction-mod takeover / vanilla-scavs / vanilla-goons toggles.
/// </summary>
public class OrbitBrainLayer : CustomLayer
{
    private const string LayerName = "OrbitBrainLayer";

    private readonly OrbitManager _orbit;
    private readonly Agent _agent;
    private readonly bool _excluded;

    // Substrings (case-insensitive) of WildSpawnType names whose bots
    // should NOT be hijacked. Populated at boot by Plugin when the user
    // toggles OFF a faction-mod takeover (UNTAR / RUAF / BlackDiv).
    private static readonly HashSet<string> _excludedRoleSubstrings = new(StringComparer.OrdinalIgnoreCase);

    // Vanilla-behaviour opt-outs. PlayerScavs share WildSpawnType.assault
    // with bot scavs but are NEVER excluded by VanillaScavs (explicit
    // Profile.WillBeAPlayerScav check inside IsExcludedRole).
    private static bool _vanillaScavs;
    private static bool _vanillaGoons;

    public static void AddExcludedRoleSubstring(string sub)
    {
        if (!string.IsNullOrEmpty(sub)) _excludedRoleSubstrings.Add(sub);
    }

    public static void SetVanillaScavExclusion(bool excluded) => _vanillaScavs = excluded;
    public static void SetVanillaGoonExclusion(bool excluded) => _vanillaGoons = excluded;

    private static bool IsExcludedRole(BotOwner botOwner)
    {
        var role = botOwner?.Profile?.Info?.Settings?.Role;
        if (!role.HasValue) return false;

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

        // Make doors invisible to bot colliders — bots open them via our
        // DoorSystem and shouldn't physically push them.
        var botCollider = _agent.Bot.GetPlayer.CharacterController.GetCollider();
        var pomCollider = _agent.Bot.GetPlayer.POM.Collider;

        var doors = _orbit.DoorSystem.Doors;
        for (var i = 0; i < doors.Length; i++)
        {
            var door = doors[i];
            Physics.IgnoreCollision(pomCollider, door.Collider);
            EFTPhysicsClass.IgnoreCollision(botCollider, door.Collider);
        }
    }

    private void OnDead(Player player, IPlayer lastAggressor, DamageInfoStruct damageInfo, EBodyPart part)
    {
        player.OnPlayerDead -= OnDead;
        _agent.IsActive = false;
        var squad = _agent.Squad;
        _orbit.RemoveAgent(_agent);
        // Re-evaluate the cumulative extract threshold: the dead member no
        // longer counts toward squad-total Stats.Looted, and if survivors
        // already sum above the threshold ExtractRequested must flip
        // immediately rather than wait for the next loot completion.
        if (squad != null && squad.Members.Count > 0)
        {
            Orbit.Tasks.Actions.LootContainerAction.ReevaluateExtractForSquad(squad);
        }
    }

    private void OnLayerChanged(AICoreLayerClass<BotLogicDecision> layer)
    {
        var mover = _agent.Bot.Mover;

        if (layer.Name() == LayerName)
        {
            Log.Debug($"{_agent} stopping builtin bot mover");
            mover.Stop();
            _agent.IsActive = true;
        }
        else
        {
            if (_agent.IsActive)
            {
                Log.Debug($"{_agent} setting player to navmesh");
                // Make every mover state variable reflect the current position
                // so SetPlayerToNavMesh doesn't snap the bot back to a stale
                // target after our layer hands the brain back to BSG.
                mover.LastGoodCastPoint = mover.PrevSuccessLinkedFrom_1 = mover.PrevLinkPos = mover.PositionOnWayInner = _agent.Position;
                mover.LastGoodCastPointTime = Time.time;
                mover.PrevPosLinkedTime_1 = 0f;
                mover.SetPlayerToNavMesh(_agent.Position);
                _agent.IsActive = false;
            }
        }

        Log.Debug($"{_agent} layer changed to: {layer.Name()} priority: {layer.Priority}");
    }

    public override string GetName() => LayerName;

    public override Action GetNextAction() => new(typeof(IdleAction), "Idle");

    public override bool IsActive()
    {
        if (_excluded) return false;
        var lastEnemyTimeSeen = Time.time - BotOwner.Memory.LastEnemyTimeSeen;
        // Force isHealing off when no enemy seen recently — otherwise SAIN's
        // medical loops trap the bot indefinitely.
        var isHealing = (BotOwner.Medecine.Using || BotOwner.Medecine.SurgicalKit.HaveWork || BotOwner.Medecine.FirstAid.Have2Do) && lastEnemyTimeSeen < 60f;
        // Reading BotOwner.Memory.HaveEnemy directly is unreliable — SAIN keeps
        // GoalEnemy alive long past actual combat, so once HaveEnemy flipped
        // true this layer would never reactivate. Gate on actively-shot-at OR
        // saw-enemy-within-15s; after that window we reclaim the bot and
        // SAIN's combat layer (priority 20) preempts on the next real contact.
        var isInCombat = BotOwner.Memory.IsUnderFire || lastEnemyTimeSeen < 15f;
        return !isHealing && !isInCombat;
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
