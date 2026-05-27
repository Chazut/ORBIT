using System.Collections.Generic;
using Comfort.Common;
using EFT.Interactive;
using Orbit.Core;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Tasks.Actions;

/// <summary>
/// Final stage of an exfil dispatch. The squad has arrived at the Exfil
/// POI and GotoObjectiveAction transitioned us to
/// <see cref="ObjectiveStatus.Extracting"/>. This action holds the bot in
/// place for a short countdown (mirrors the real player exfil animation),
/// then calls BSG's <c>BotLeaveData.RemoveFromMap</c> to despawn the bot.
///
/// Two-mode behaviour:
///   • Standard (foot) exfils — per-agent 5s kneel then despawn. Members
///     extract as they arrive; no synchronisation.
///   • V-Ex / SharedTimer (car) exfils — the squad waits at the exfil
///     for all currently-alive members to arrive (capped at
///     VExWaitTimeout), then a single 60s countdown ticks for the whole
///     squad. At the end every member at the V-Ex despawns simultaneously
///     and the exfil is flipped to NotPresent so the human player can't
///     use it after the car has "left". A real player extract triggers
///     the car to leave for good, so we match that.
/// </summary>
public class ExtractAction(AgentData dataset, float hysteresis) : Task<Agent>(hysteresis)
{
    // Beats LootContainerAction's 0.75 — once we're extracting, nothing
    // else (loot, guard, goto) should preempt.
    private const float UtilityScore = 0.9f;

    // Time a foot-exfil bot kneels at the exfil before being removed.
    // Real player exfils are 7-10s; we keep it short so squads don't
    // pile up at the same point waiting (visually ugly).
    private const float FootExtractStandTime = 5f;

    // V-Ex car countdown — once all squad members have arrived at the
    // SharedTimer exfil (or VExWaitTimeout elapsed), this many seconds
    // tick down before the simultaneous despawn. Matches BSG's typical
    // V-Ex countdown.
    private const float VExCountdownSeconds = 60f;

    // Max time the squad will wait at a V-Ex for the LAST member to
    // arrive before starting the countdown anyway. Without this cap a
    // single straggler (stuck on geometry, killed mid-route without us
    // noticing yet) blocks the rest of the squad's extract.
    private const float VExWaitTimeout = 90f;

    // Per-agent kneel-start timestamp for foot exfils. V-Ex uses a per-
    // squad shared timer instead (VExState below).
    private readonly Dictionary<int, float> _footExtractStartTime = new();

    // Per-squad shared timer for V-Ex extracts.
    private sealed class VExState
    {
        public ExfiltrationPoint Exfil;
        public float FirstArrivalTime;     // when the FIRST member reached the V-Ex
        public float CountdownStartTime;   // 0 until the countdown actually starts
    }
    private readonly Dictionary<int, VExState> _vexSquadStates = new();

    public override void UpdateScore(int ordinal)
    {
        var agents = dataset.Entities.Values;
        for (var i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            agent.TaskScores[ordinal] = agent.Objective.Status == ObjectiveStatus.Extracting ? UtilityScore : 0f;
        }
    }

    public override void Update()
    {
        for (var i = 0; i < ActiveEntities.Count; i++)
            UpdateAgent(ActiveEntities[i]);
    }

    private void UpdateAgent(Agent agent)
    {
        var loc = agent.Objective.Location;
        if (loc == null || loc.Category != WaypointCategory.Exfil
            || loc.Target is not ExfiltrationPoint exfil)
        {
            // Defensive: agent in Extracting state without a valid exfil
            // POI. Should not happen; fall back to the foot-extract path
            // so we don't strand the bot.
            UpdateFootExtract(agent);
            return;
        }

        if (exfil.Settings != null
            && exfil.Settings.ExfiltrationType == EExfiltrationType.SharedTimer)
        {
            UpdateVExExtract(agent, exfil);
            return;
        }

        UpdateFootExtract(agent);
    }

    private void UpdateFootExtract(Agent agent)
    {
        var now = Time.time;
        if (!_footExtractStartTime.TryGetValue(agent.Id, out var startedAt))
        {
            _footExtractStartTime[agent.Id] = now;
            try { agent.Bot.SetPose(0.25f); } catch { /* some boss followers don't expose pose */ }
            Log.Info($"{agent} ExtractAction: stand at {agent.Position} for {FootExtractStandTime}s before despawn");
            return;
        }

        if (now - startedAt < FootExtractStandTime) return;

        _footExtractStartTime.Remove(agent.Id);
        DespawnAgent(agent);
    }

    private void UpdateVExExtract(Agent agent, ExfiltrationPoint exfil)
    {
        var squad = agent.Squad;
        if (squad == null)
        {
            // No squad — degenerate; fall back to foot-extract path.
            UpdateFootExtract(agent);
            return;
        }

        if (!_vexSquadStates.TryGetValue(squad.Id, out var state) || state.Exfil != exfil)
        {
            // First member of this squad to land on this V-Ex (or squad's
            // exfil target changed). Initialise state, kneel the bot, and
            // start the wait window.
            state = new VExState
            {
                Exfil = exfil,
                FirstArrivalTime = Time.time,
                CountdownStartTime = 0f,
            };
            _vexSquadStates[squad.Id] = state;
            Log.Info($"{squad}: first member {agent} arrived at V-Ex {exfil.name} — waiting up to {VExWaitTimeout:F0}s for the rest of the squad");
        }

        try { agent.Bot.SetPose(0.25f); } catch { /* pose hook missing on some specialisations */ }

        var now = Time.time;

        // Countdown phase: tick down to despawn.
        if (state.CountdownStartTime > 0f)
        {
            if (now - state.CountdownStartTime >= VExCountdownSeconds)
            {
                DespawnSquadAtVEx(squad, exfil);
                _vexSquadStates.Remove(squad.Id);
            }
            return;
        }

        // Pre-countdown: are we ready to start the timer?
        var allReady = IsAllSquadAtVEx(squad, exfil);
        var waitedTooLong = now - state.FirstArrivalTime >= VExWaitTimeout;
        if (allReady || waitedTooLong)
        {
            state.CountdownStartTime = now;
            Log.Info($"{squad}: V-Ex {exfil.name} countdown started ({VExCountdownSeconds:F0}s) — reason: {(allReady ? "all squad members arrived" : "wait timeout reached, leaving stragglers behind")}");
        }
    }

    private static bool IsAllSquadAtVEx(Squad squad, ExfiltrationPoint exfil)
    {
        for (var i = 0; i < squad.Size; i++)
        {
            var member = squad.Members[i];
            if (member == null) continue;
            if (member.Objective.Status != ObjectiveStatus.Extracting) return false;
            if (member.Objective.Location?.Target != exfil) return false;
        }
        return true;
    }

    private void DespawnSquadAtVEx(Squad squad, ExfiltrationPoint exfil)
    {
        Log.Info($"{squad}: V-Ex {exfil.name} countdown ended — despawning all members at the exfil and marking the car departed");
        // Snapshot the member list — DespawnAgent mutates Squad.Members
        // through OrbitManager.RemoveAgent; iterating in-place would skip
        // entries.
        var snapshot = new List<Agent>(squad.Size);
        for (var i = 0; i < squad.Size; i++) snapshot.Add(squad.Members[i]);
        for (var i = 0; i < snapshot.Count; i++)
        {
            var member = snapshot[i];
            if (member == null) continue;
            if (member.Objective.Status != ObjectiveStatus.Extracting) continue;
            if (member.Objective.Location?.Target != exfil) continue;
            // Per-agent foot timer cleanup, just in case.
            _footExtractStartTime.Remove(member.Id);
            DespawnAgent(member);
        }
        // Mark the car as departed so the human player can't pile in
        // after the squad has left. NotPresent is BSG's natural V-Ex
        // post-departure state — broadcasts to clients so the UI hides
        // the exfil from the player.
        try
        {
            exfil.SetStatusLogged(EExfiltrationStatus.NotPresent, "Orbit-VEx-Departed");
        }
        catch (System.Exception e)
        {
            Log.Warning($"V-Ex {exfil.name} SetStatusLogged(NotPresent) failed: {e.Message}");
        }
    }

    private static void DespawnAgent(Agent agent)
    {
        try
        {
            var loc = agent.Objective.Location;
            Log.Info($"{agent} ExtractAction: extracting via {loc} (LeaveData.RemoveFromMap)");
            agent.Bot.LeaveData.RemoveFromMap();
        }
        catch (System.Exception e)
        {
            Log.Error($"{agent} ExtractAction.RemoveFromMap failed: {e}");
            agent.Objective.Status = ObjectiveStatus.Failed;
            return;
        }

        var manager = Singleton<OrbitManager>.Instance;
        manager?.RemoveAgent(agent);
    }

    protected override void Deactivate(Agent entity)
    {
        // Combat takeover or external state change mid-extract — drop the
        // per-agent foot tracker. The V-Ex squad timer is intentionally
        // NOT cleared here: if a member temporarily exits Extracting
        // (combat, takeover), the squad-wide countdown keeps ticking so
        // the rest of the squad isn't stranded.
        if (_footExtractStartTime.Remove(entity.Id))
            Log.Debug($"{entity} ExtractAction.Deactivate: foot-extract timer cancelled");
    }
}
