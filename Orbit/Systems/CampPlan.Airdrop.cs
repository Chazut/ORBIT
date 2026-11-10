using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Systems;

internal sealed partial class CampPlan
{
    internal bool AirdropPaused { get; private set; }
    internal bool ReservesPendingAirdrop => PendingAirdrop != null && !AirdropPaused;
    private bool AirdropLootCompleted => AirdropTarget != null && Looter != null && Owns(Looter)
        && Looter.Objective.Status == ObjectiveStatus.Finished;
    private float _resumeCheckAt;
    private string _pauseReason;

    internal string AirdropAbandonReason(Squad squad)
    {
        if (squad.Leader == null || squad.Size == 0) return "no leader or members";
        if (!ServerConfig.Ambush.Allows(squad.Leader.BotCategory) || !ServerConfig.Ambush.Airdrops.Enabled)
            return "airdrops disabled";
        if (squad.ExtractRequested || squad.Objective.Location?.Category == WaypointCategory.Exfil)
            return "extract requested";
        foreach (var member in squad.Members)
            if (member?.Player?.HealthController?.IsAlive == true && !member.SoloExtractRequested
                && member.LootExtractSweep == null) return null;
        return "no remaining participants";
    }

    internal void Interrupt(Squad squad, string reason)
    {
        if (AirdropLootCompleted) { End(squad, "airdrop loot completed"); return; }
        var terminal = AirdropTarget != null ? AirdropAbandonReason(squad) : null;
        if (AirdropTarget != null && terminal == null) SuspendAirdrop(squad, reason);
        else End(squad, terminal ?? reason);
    }

    internal bool PreserveInterruptedAirdropLoot(Squad squad, Waypoint location, WaypointSystem waypoints)
    {
        // LootContainerAction can observe combat cancellation before the strategy tick.
        // Do not blacklist this crate as a completed/failed visit in that short gap.
        if (AirdropTarget?.LootWaypoint != location || location == null || AirdropAbandonReason(squad) != null)
            return false;
        if (AirdropPaused) return true;
        var reason = AmbushDirector.AirdropBusyReason(squad, waypoints);
        if (reason == null) return false;
        SuspendAirdrop(squad, "loot interrupted: " + reason);
        return true;
    }

    internal void SuspendAirdrop(Squad squad, string reason)
    {
        var target = AirdropTarget;
        if (target == null || AirdropPaused) return;
        PauseMission(squad);
        AirdropEvent(squad, "paused", reason);
        Approach?.Release();
        Approach = null;
        ReleaseSlots(squad);
        PendingAirdrop = target;
        Site = null;
        Looter = null;
        Holding = false;
        AirdropFormationSince = 0;
        AirdropPaused = true;
        _pauseReason = reason;
        // Give death/own-kill registration and inventory cancellation time to settle.
        _resumeCheckAt = Time.time + 2f;
        Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
    }

    internal bool ResumeCheckDue()
    {
        if (!AirdropPaused) return true;
        if (Time.time < _resumeCheckAt) return false;
        _resumeCheckAt = Time.time + .5f;
        return true;
    }

    internal void ResumeAirdrop(Squad squad)
    {
        if (!AirdropPaused) return;
        // A kill detour may have changed the underlying mission. Keep its current anchor.
        _mission = squad.Objective.Location;
        _missionDuration = squad.Objective.Duration;
        _lastTick = PendingSince = FormationRetryAt = Time.time;
        AirdropPaused = false;
        AirdropEvent(squad, "resumed", _pauseReason);
        _pauseReason = null;
        Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
    }

    private void ResetAirdropContinuation()
    {
        AirdropPaused = false;
        _pauseReason = null;
        _resumeCheckAt = 0;
    }

    private void AirdropEvent(Squad squad, string state, string reason)
    {
        if (!Log.InfoEnabled && !PerformanceJournal.Enabled) return;
        var target = AirdropTarget;
        var detail = $"state={state} reason={reason} stage={AirdropStage} drop={target?.Drop?.GetInstanceID()} target={target?.Position} held={_held:F1}s duration={_duration:F1}s roll=preserved";
        Log.Info($"AIRDROP CONTINUATION: {squad} {detail}");
        PerformanceJournal.Event("airdrop-continuation", squad.Leader?.Bot?.Profile?.Id, detail, squad.Id);
    }
}
