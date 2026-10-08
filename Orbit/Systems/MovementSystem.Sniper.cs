using EFT;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

public partial class MovementSystem
{
    private readonly NavMeshPath _sniperPath = new();

    internal static bool SniperRelocationAllowed(Agent agent)
        => agent.IsActive && agent.Player?.HealthController is { IsAlive: true }
            && agent.Bot != null && !agent.Bot.IsDead && !CorpseEscort.InFlight(agent)
            && agent.Bot.Memory != null && !agent.Bot.Memory.HaveEnemy && !agent.Bot.Memory.IsUnderFire
            && agent.Bot.Memory.GoalEnemy == null && Time.time - agent.LastHpDropTime >= 5f
            && agent.Squad != null && agent.Squad.CombatCallerMemberIdx < 0 && Time.time >= agent.Squad.GhostFightUntil
            && Time.time >= agent.Movement.DoorInteractHoldUntil && agent.Movement.Travel?.Paused != true
            && !GhostBodyTransition.Busy(agent.Player);

    private bool TryEnterSniperPost(Agent agent, Vector3 post, MainObjective main)
    {
        if (main == null || main.Completed || agent.SoloExtractRequested || agent.SniperReturn != null
            || !SniperRelocationAllowed(agent) || !SniperReturnRecovery.Near(agent.Position, post)) return false;
        if (!NavMesh.SamplePosition(agent.Position, out var origin, .75f, NavMesh.AllAreas)
            || (origin.position - agent.Position).sqrMagnitude > .75f * .75f) return false;
        // Ordinary connected access stays with the mover. Bridge only the final local mesh gap.
        if (NavMesh.CalculatePath(origin.position, post, NavMesh.AllAreas, _sniperPath)
            && _sniperPath.status == NavMeshPathStatus.PathComplete) return false;
        if (!BotGroundPlacement.TryResolve(agent.Player, origin.position, out _, out var reason))
            return DeferSniperPlacement(agent, "post", "approach-" + reason);
        if (!TrySniperPlacement(agent, post, "post")) return false;
        agent.SniperReturn = new SniperReturnRecovery(agent, main, origin.position, post);
        return true;
    }

    internal bool TryReturnFromSniperPost(Agent agent, Vector3 anchor)
    {
        if (!TrySniperPlacement(agent, anchor, "return")) return false;
        ResumeAfterRescue(agent);
        return true;
    }

    private bool TrySniperPlacement(Agent agent, Vector3 target, string stage)
    {
        using var timing = PerformanceJournal.Measure(TransitionPhase.SniperPlanning, "sniper-relocation", stage,
            agent.Squad?.Id ?? -1, agent.Bot?.ProfileId);
        if (!SniperRelocationAllowed(agent) || !BotGroundPlacement.Finite(target)
            || !SniperReturnRecovery.Near(agent.Position, target)) return false;
        var from = agent.Position;
        if (!NavMesh.SamplePosition(target, out var hit, .5f, NavMesh.AllAreas)
            || (hit.position - target).sqrMagnitude > .25f
            || !SniperReturnRecovery.Near(from, hit.position)) return DeferSniperPlacement(agent, stage, "destination-mesh");
        if (!BotGroundPlacement.TryResolve(agent.Player, hit.position, out var landing, out var reason))
            return DeferSniperPlacement(agent, stage, reason);
        if (!SniperReturnRecovery.Near(from, landing)) return DeferSniperPlacement(agent, stage, "landing-distance-or-height");
        if (!TeleportSafe(agent, _humanPlayers) || !IsRescueDestinationHidden(from)
            || !IsRescueDestinationHidden(landing)) return DeferSniperPlacement(agent, stage, "human-visible-or-close");
        var direction = landing - from;
        var distance = direction.magnitude;
        if (distance < 1.5f) return false;
        var mask = LayersMaskController.HighPolyWithTerrainMask;
        if (Physics.SphereCast(from + Vector3.up * .8f, .25f, direction.normalized, out _, distance,
            mask, QueryTriggerInteraction.Ignore)) return DeferSniperPlacement(agent, stage, "solid-obstacle");
        for (var d = 0f; d <= distance; d += .5f)
            if (DangerZones.IsInside(from + direction.normalized * d)) return DeferSniperPlacement(agent, stage, "danger-volume");
        if (DangerZones.IsInside(landing)) return DeferSniperPlacement(agent, stage, "danger-volume");
        var ray = new Ray(from + Vector3.up * .8f, direction.normalized);
        foreach (var door in _doorSystem.Doors)
        {
            if (door == null || door.DoorState == EDoorState.Open || door.Collider == null) continue;
            var bounds = door.Collider.bounds; bounds.Expand(.5f);
            if (bounds.Contains(ray.origin) || bounds.IntersectRay(ray, out var length) && length <= distance)
                return DeferSniperPlacement(agent, stage, "closed-door");
        }
        // Include native bots and squadmates as well as human players.
        var players = Comfort.Common.Singleton<GameWorld>.Instance?.AllAlivePlayersList;
        if (players == null) return false;
        foreach (var player in players)
            if (player != agent.Player && player?.HealthController is { IsAlive: true }
                && (player.Position - landing).sqrMagnitude < 2.25f) return DeferSniperPlacement(agent, stage, "another-body");
        if (!BotLandingGuard.TryPlace(agent.Bot, hit.position, "sniper-" + stage,
            () => ResumeGroundPlacement(agent), validateGround: true)) return DeferSniperPlacement(agent, stage, "landing-rejected");
        if (agent.Bot.Mover != null) OrbitMovementRecovery.InvalidateNativePosition(agent.Bot.Mover, agent.Position);
        agent.Stuck.Recovery.RecordLocalRescue(from, agent.Position);
        ResetAfterRescue(agent, resume: false);
        var detail = $"stage={stage} from={from} to={agent.Position} distance={distance:F1}m heightGap={landing.y - from.y:F2}m";
        Log.Info($"SNIPER RELOCATION: {agent} {detail}");
        PerformanceJournal.Event("sniper-relocation", agent.Bot.ProfileId, detail, agent.Squad.Id);
        return true;
    }

    private bool DeferSniperPlacement(Agent agent, string stage, string reason)
    {
        if (Time.time < agent.NextSniperRelocationDiagnosticAt) return false;
        agent.NextSniperRelocationDiagnosticAt = Time.time + 15f;
        var detail = $"stage={stage} deferred reason={reason} from={agent.Position}";
        Log.Debug($"SNIPER RELOCATION: {agent} {detail}");
        PerformanceJournal.Event("sniper-relocation", agent.Bot.ProfileId, detail, agent.Squad.Id);
        return false;
    }
}
