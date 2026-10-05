using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Sain;
using Orbit.Settings;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Orbit.Systems;

internal sealed class TravelMotion
{
    internal TravelRequest Request;
    internal bool ExposedSprint, Exposed, Paused;
    internal float NextExposureCheck;
    internal Vector3? OwnedLook;
}

internal sealed class TravelHold
{
    internal float Until, Started, NextAttempt;
    internal int LeaderId = -1, Revision;
    internal Vector3 Position;
    internal bool Initialized;
}

public partial class MovementSystem
{
    private TravelRouteWorld _travelWorld;
    private int _travelPauseProbeFrame = -1;

    private static TravelStyle StyleFor(Agent agent)
    {
        if (agent.BotCategory != "PMC" || !ServerConfig.Personalities.Enabled || agent.Squad?.Personality == null)
            return null;
        var config = ServerConfig.Personalities;
        var style = agent.Squad.Personality.Archetype switch
        {
            PersonalityArchetype.Timmy => config.Timmy.Travel,
            PersonalityArchetype.Cautious => config.Cautious.Travel,
            PersonalityArchetype.Aggressive => config.Aggressive.Travel,
            PersonalityArchetype.VeryAggressive => config.VeryAggressive.Travel,
            _ => config.Average.Travel
        };
        return style is { Enabled: true } ? style : null;
    }

    private TravelRequest CreateTravel(Agent agent, Vector3 destination, int revision)
    {
        var style = StyleFor(agent);
        if (style == null || agent.SoloExtractIsEmergency || (destination - agent.Position).sqrMagnitude < 400f) return null;
        return new TravelRequest
        {
            Style = style.Copy(), SquadId = agent.Squad.Id, Archetype = agent.Squad.Personality.Archetype.ToString(),
            LeaderRoute = agent.IsLeader,
            Current = () => agent.IsActive && agent.Movement.PathRevision == revision && !agent.SoloExtractIsEmergency,
            Report = (request, reason) => ReportTravel(agent, request, reason)
        };
    }

    private void ReportTravel(Agent agent, TravelRequest request, string reason)
    {
        if (!PerformanceJournal.Enabled && !Log.InfoEnabled && request.Error == null) return;
        var route = request.Result;
        var detail = $"archetype={request.Archetype} reason={reason} detours={request.Detours} paths={request.Paths} probes={request.Probes} "
            + $"waitMs={request.WaitSeconds * 1000f:F1} length={request.BaselineLength:F1}->{route?.Length ?? request.BaselineLength:F1} "
            + $"exposed={request.BaselineExposure:F1}->{route?.ExposedDistance ?? request.BaselineExposure:F1} "
            + $"cacheHits={_travelWorld.CacheHits} cacheMisses={_travelWorld.CacheMisses} error={request.Error ?? "none"}";
        if (request.Error != null) Log.Warning($"TRAVEL ROUTE: {agent} {detail}");
        else if (Log.InfoEnabled) Log.Info($"TRAVEL ROUTE: {agent} {detail}");
        PerformanceJournal.Event("travel-route", agent.Bot?.Profile?.Id, detail, agent.Squad?.Id ?? -1);
    }

    private static bool WantsTravelSprint(Agent agent, bool ordinary)
    {
        var state = agent.Movement.Travel;
        if (state == null || agent.SoloExtractIsEmergency || !agent.IsActive) return ordinary;
        if (Time.time >= state.NextExposureCheck)
        {
            state.NextExposureCheck = Time.time + .25f;
            state.Exposed = state.Request.Result.IsExposed(agent.Position);
        }
        // Preserve the usual speed on sheltered segments and on the final interaction approach.
        return ordinary || (state.ExposedSprint && state.Exposed && (agent.Position - agent.Movement.Target).sqrMagnitude > 100f);
    }

    internal static bool IsTravelPaused(Agent agent) => agent.Movement.Travel?.Paused == true && agent.IsActive;

    private static bool CanObserve(Agent agent)
        => agent.IsActive && agent.Movement.Travel != null && agent.Movement.HasPath
            && agent.Movement.Status == MovementStatus.Moving && !agent.SoloExtractRequested && !agent.SoloExtractIsEmergency
            && agent.Squad is { ExtractRequested: false } squad
            && (squad.Rush == null || squad.Rush.Main.Completed)
            && agent.Objective.Location?.Category != WaypointCategory.Exfil
            && Time.time >= squad.GhostFightUntil && Time.time >= agent.Movement.DoorInteractHoldUntil
            && (agent.Position - agent.Movement.Target).sqrMagnitude > 225f;

    private bool HoldForTravelObservation(Agent agent)
    {
        var state = agent.Movement.Travel;
        if (state == null) return false;
        using var timing = PerformanceJournal.Measure(TransitionPhase.TravelGait, "travel-gait", squad: agent.Squad?.Id ?? -1);
        var squad = agent.Squad;
        var hold = squad?.TravelHold;
        var leader = squad?.Leader;
        if (hold == null || leader == null || !CanObserve(leader)
            || (leader.Look.Target != null && leader.Look.Target != leader.Movement.Travel.OwnedLook))
        {
            if (hold != null) hold.Until = 0f;
            EndTravelObservation(agent);
            return false;
        }
        if (!CanObserve(agent)) { EndTravelObservation(agent); return false; }
        if (hold.LeaderId != leader.Id || hold.Revision != leader.Movement.PathRevision) hold.Until = 0f;
        var now = Time.time;
        if (!hold.Initialized)
        {
            hold.Initialized = true;
            hold.NextAttempt = now + state.Request.Style.PauseInterval;
            hold.Position = leader.Position;
        }
        if (agent == leader && now >= hold.NextAttempt && _travelPauseProbeFrame != Time.frameCount)
        {
            var style = state.Request.Style;
            hold.NextAttempt = now + style.PauseInterval;
            if (style.PauseChance > 0f && style.PauseDurationMax > 0f && agent.Look.Target == null
                && (agent.Position - hold.Position).sqrMagnitude >= 100f && Random.value < style.PauseChance)
            {
                _travelPauseProbeFrame = Time.frameCount;
                if (_travelWorld.Exposure(agent.Position) <= .4f)
                {
                    hold.Started = now;
                    hold.Until = now + Random.Range(style.PauseDurationMin, style.PauseDurationMax);
                    hold.LeaderId = leader.Id;
                    hold.Revision = leader.Movement.PathRevision;
                    hold.Position = leader.Position;
                    if (PerformanceJournal.Enabled || Log.InfoEnabled)
                    {
                        var detail = $"archetype={state.Request.Archetype} seconds={hold.Until - now:F2}";
                        if (Log.InfoEnabled) Log.Info($"TRAVEL OBSERVE: {agent} {detail}");
                        PerformanceJournal.Event("travel-observe", agent.Bot?.Profile?.Id, detail, squad.Id);
                    }
                }
            }
        }
        // Distant followers keep catching up. Nobody stops in the open just because the leader did.
        if (now >= hold.Until || (agent.Position - hold.Position).sqrMagnitude > 64f
            || (agent != leader && !state.Paused && !NearShelteredSample(state.Request.Result, agent.Position)))
        {
            EndTravelObservation(agent);
            return false;
        }
        state.Paused = true;
        agent.Stuck.Soft.Reset();
        agent.Stuck.Soft.LastUpdate = now;
        agent.Stuck.Hard.Timer = 0f;
        agent.Stuck.Hard.LastUpdate = now;
        agent.Stuck.Hard.Status = HardStuckStatus.None;
        agent.Stuck.Hard.PositionHistory.Reset();
        agent.Stuck.Hard.AverageSpeed.Reset();
        agent.Stuck.Recovery.Suspend();
        agent.Stuck.IdleRescueSince = -1f;
        agent.Stuck.IdleRescueIntent = false;
        if (!agent.IsDormant)
        {
            agent.Player.EnableSprint(false);
            agent.Bot.Mover.SetTargetMoveSpeed(0f);
            agent.Player.Move(Vector2.zero);
        }
        if (agent.Look.Target == null || agent.Look.Target == state.OwnedLook)
        {
            var forward = (agent.Movement.Target - agent.Position).normalized;
            var scan = Quaternion.Euler(0f, Mathf.Sin((now - hold.Started) * 2f) * 50f, 0f) * forward;
            state.OwnedLook = agent.Position + Vector3.up * 1.5f + scan * 12f;
            agent.Look.Type = LookType.Position;
            agent.Look.Target = state.OwnedLook;
        }
        return true;
    }

    private static bool NearShelteredSample(TravelRoute route, Vector3 position)
    {
        for (var i = 0; i < route.Samples.Length; i++)
            if (route.Exposure[i] <= .4f && (route.Samples[i] - position).sqrMagnitude < 4f) return true;
        return false;
    }

    private static void EndTravelObservation(Agent agent)
    {
        var state = agent.Movement.Travel;
        if (state == null) return;
        if (state.OwnedLook != null && agent.Look.Target == state.OwnedLook) agent.Look.Target = null;
        state.OwnedLook = null;
        state.Paused = false;
    }
}
