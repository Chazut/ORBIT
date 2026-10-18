using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

// Shared by spawn and later travel recovery, so neither can skip an unfinished nearby search.
internal sealed class LocalEscapeSearch
{
    internal bool Active, Complete, HasBest;
    internal Vector3 Origin, Best;
    internal float BestDistance, LastUsed, CompletedAt;
    internal int Cursor, ReferenceCount;
    internal readonly Vector3[] References = new Vector3[3];
    internal void Reset() { Active = Complete = HasBest = false; Cursor = ReferenceCount = 0; }
}

public partial class MovementSystem
{
    private enum EscapeResult { Pending, Found, Exhausted }
    private List<Agent> _recoveryAgents;
    private float _escapeBudgetUntil;
    private int _escapeSamples, _escapePaths;
    private const int EscapeQueryBudget = 32;
    private const int EscapeCandidateCount = 160;

    private void BeginRescueVerification(Agent agent, string source)
    {
        agent.Stuck.Recovery.Progress.Begin(agent.Position, Time.time);
        Log.Info($"{agent} RESCUE VERIFY: pending source={source} landing={agent.Position} failures={agent.Stuck.Recovery.Progress.Failures}");
    }

    private void ObserveRescueProgress(Agent agent, bool paused = false)
    {
        var progress = agent.Stuck.Recovery.Progress;
        if (!progress.Tracking) return;
        var objective = agent.Objective;
        var movement = agent.Movement;
        var goal = objective?.Location;
        var target = goal == null ? agent.Position : goal.ExfilInteriorPosition ?? goal.Position;
        // Extraction deliberately follows complete intermediate legs when the final exit
        // has no single complete path. Keep the selected exit and verify its current leg.
        if (goal?.Category == WaypointCategory.Exfil) target = movement.Target;
        paused |= !agent.IsActive || agent.Bot.Memory.IsUnderFire || agent.Bot.Memory.GoalEnemy != null
            || objective?.Status is ObjectiveStatus.Finished or ObjectiveStatus.Looting or ObjectiveStatus.Extracting
            || Time.time < movement.DoorInteractHoldUntil;
        var walking = !paused && goal != null && objective.Status == ObjectiveStatus.Moving
            && movement.Status == MovementStatus.Moving && movement.HasPath
            && (movement.Target - target).sqrMagnitude <= 2.25f
            && (movement.Path[movement.Path.Length - 1] - target).sqrMagnitude <= 2.25f;
        if (walking)
        {
            var corner = movement.Path[Mathf.Clamp(movement.CurrentCorner, 0, movement.Path.Length - 1)];
            walking = Vector3.Dot(agent.Position - progress.LastPosition, corner - progress.LastPosition) >= 0f;
        }
        progress.Observe(agent.Position, Time.time, !paused, walking, goal?.Id ?? -1, target);
        if (progress.Ready && Time.time >= progress.NextRouteCheck)
        {
            RefreshEscapeBudget();
            if (_escapePaths < EscapeQueryBudget)
            {
                _escapePaths++;
                progress.NextRouteCheck = Time.time + 5f;
                var complete = IsPathComplete(agent.Position, target);
                if (complete)
                {
                    Log.Info($"{agent} RESCUE VERIFY: confirmed objective={goal.Id} target={target} travelled={progress.Distance:F1}m failures={progress.Failures}");
                    progress.Confirm();
                    return;
                }
                Log.Debug($"{agent} RESCUE VERIFY: route-rejected objective={goal.Id} target={target} path={_rescuePath.status}");
            }
        }
        if (!paused && progress.Expired)
        {
            var delay = progress.Fail(Time.time);
            agent.Stuck.LocalEscape.Reset();
            CancelSpawnWaypointSearch(agent);
            Log.Info($"{agent} RESCUE VERIFY: no-progress objective={goal?.Id ?? -1} status={objective?.Status} travelled={progress.Distance:F1}m failures={progress.Failures} retryIn={delay:F0}s");
        }
    }

    private void RefreshEscapeBudget()
    {
        if (Time.time < _escapeBudgetUntil) return;
        _escapeBudgetUntil = Time.time + .25f;
        _escapeSamples = _escapePaths = 0;
    }

    private void AddEscapeReference(LocalEscapeSearch search, Vector3 position, bool destination = false)
    {
        if (search.ReferenceCount == search.References.Length || !BotGroundPlacement.Finite(position)) return;
        if (!destination && (position - search.Origin).sqrMagnitude < 15f * 15f) return;
        if (!NavMesh.SamplePosition(position, out var hit, destination ? 2f : .75f, NavMesh.AllAreas)) return;
        for (var i = 0; i < search.ReferenceCount; i++)
            if ((hit.position - search.References[i]).sqrMagnitude < 4f) return;
        search.References[search.ReferenceCount++] = hit.position;
    }

    private void BeginEscapeSearch(Agent agent, Vector3? objective)
    {
        var search = agent.Stuck.LocalEscape;
        search.Reset();
        search.Active = true;
        search.Origin = agent.Position;
        // The objective remains useful when reachable, but it is not the only proof of an onward route.
        if (objective.HasValue) AddEscapeReference(search, objective.Value, true);
        foreach (var human in _humanPlayers)
            if (human?.HealthController is { IsAlive: true }) AddEscapeReference(search, human.Position);
        // Use the nearest sampled external group as another reference. The human can itself be on
        // an isolated floor; failure to connect to that one point must not reject the whole road.
        Agent nearest = null;
        var distance = float.MaxValue;
        if (_recoveryAgents != null)
            foreach (var other in _recoveryAgents)
            {
                if (other == agent || other?.Player?.HealthController is not { IsAlive: true }
                    || agent.Squad != null && other.Squad == agent.Squad) continue;
                var d = (other.Position - search.Origin).sqrMagnitude;
                if (d < 225f || d >= distance) continue;
                nearest = other;
                distance = d;
            }
        if (nearest != null) AddEscapeReference(search, nearest.Position);
    }

    private bool ClearEscapeLanding(Agent agent, Vector3 point)
    {
        if (DangerZones.IsInside(point) || BotLandingGuard.IsRejected(agent.Bot, point)
            || agent.Stuck.Recovery.RecentlyRescuedAt(point) || !IsRescueDestinationHidden(point)) return false;
        if (_recoveryAgents != null)
            foreach (var other in _recoveryAgents)
                if (other != agent && other?.Player?.HealthController is { IsAlive: true }
                    && (other.Position - point).sqrMagnitude < 1.5f * 1.5f) return false;
        return true;
    }

    private EscapeResult TryNearbyEscape(Agent agent, Vector3? objective, out Vector3 destination)
    {
        destination = default;
        if (!agent.Stuck.Recovery.Progress.CanAttempt(Time.time)) return EscapeResult.Pending;
        var search = agent.Stuck.LocalEscape;
        if (!search.Active || (agent.Position - search.Origin).sqrMagnitude > 9f
            || Time.time - search.LastUsed > 10f || search.Complete && Time.time - search.CompletedAt >= 5f)
            BeginEscapeSearch(agent, objective);
        search.LastUsed = Time.time;
        if (search.Complete) return EscapeResult.Exhausted;
        if (search.ReferenceCount == 0)
        {
            search.Complete = true;
            search.CompletedAt = Time.time;
            return EscapeResult.Exhausted;
        }
        if (!TeleportSafe(agent, _humanPlayers)) return EscapeResult.Pending;
        RefreshEscapeBudget();
        var sampled = 0;
        var rejectedSurface = 0;
        var rejectedSafety = 0;
        var rejectedRoute = 0;
        var rejectedGround = 0;
        while (search.Cursor < EscapeCandidateCount && _escapeSamples < EscapeQueryBudget
            && _escapePaths + search.ReferenceCount <= EscapeQueryBudget)
        {
            var index = search.Cursor++;
            var alternateFloor = index >= 32;
            var band = alternateFloor ? (index - 32) / 8 : index / 8;
            var radius = alternateFloor ? 2f + band / 4 * 2f : 2f + band * 2f;
            var vertical = !alternateFloor ? 0f : (band % 4) switch { 0 => -3f, 1 => 3f, 2 => -6f, _ => 6f };
            var angle = index % 8 * Mathf.PI / 4f;
            var candidate = search.Origin + new Vector3(Mathf.Cos(angle) * radius, vertical, Mathf.Sin(angle) * radius);
            _escapeSamples++;
            sampled++;
            if (!NavMesh.SamplePosition(candidate, out var hit, alternateFloor ? 1.75f : 1f, NavMesh.AllAreas))
                rejectedSurface++;
            else
            {
                var point = hit.position;
                var delta = point - search.Origin;
                var flat = delta.x * delta.x + delta.z * delta.z;
                var height = Mathf.Abs(delta.y);
                if (!BotGroundPlacement.Finite(point) || flat > 9f * 9f || flat < 1.5f * 1.5f
                    || height > (alternateFloor ? 6.5f : 2f) || alternateFloor && height <= 2f)
                    rejectedSurface++;
                else if (!ClearEscapeLanding(agent, point)) rejectedSafety++;
                else
                {
                    var connected = false;
                    for (var i = 0; i < search.ReferenceCount; i++)
                    {
                        _escapePaths++;
                        if (IsPathComplete(point, search.References[i])) { connected = true; break; }
                    }
                    if (!connected) rejectedRoute++;
                    else if (!BotGroundPlacement.TryResolve(agent.Player, point, out _, out _)) rejectedGround++;
                    else
                    {
                        var d = delta.sqrMagnitude;
                        if (!search.HasBest || d < search.BestDistance)
                        {
                            search.HasBest = true;
                            search.Best = point;
                            search.BestDistance = d;
                        }
                    }
                }
            }
            // Finish the ring before choosing its nearest actual landing, including snapped points.
            if (search.Cursor % 8 == 0 && search.HasBest)
            {
                destination = search.Best;
                return EscapeResult.Found;
            }
        }
        if (search.Cursor >= EscapeCandidateCount)
        {
            search.Complete = true;
            search.CompletedAt = Time.time;
        }
        if (sampled > 0)
            Log.Debug($"{agent} local escape search: next={search.Cursor}/{EscapeCandidateCount} sampled={sampled} surface={rejectedSurface} safety={rejectedSafety} route={rejectedRoute} ground={rejectedGround} complete={search.Complete}");
        return search.Complete ? EscapeResult.Exhausted : EscapeResult.Pending;
    }

    private bool CompleteNearbyEscape(Agent agent, Vector3 destination, bool resume = true)
    {
        if (!agent.Stuck.Recovery.Progress.CanAttempt(Time.time)) return false;
        if (!TeleportSafe(agent, _humanPlayers) || !ClearEscapeLanding(agent, destination))
        { agent.Stuck.LocalEscape.Reset(); return false; }
        var from = agent.Position;
        var alternateFloor = Mathf.Abs(destination.y - from.y) > 2f;
        if (!BotLandingGuard.TryPlace(agent.Bot, destination, alternateFloor ? "local-floor-escape" : "local-escape",
                () => ResumeGroundPlacement(agent), validateGround: true))
        { agent.Stuck.LocalEscape.Reset(); return false; }
        agent.Stuck.Recovery.RecordLocalRescue(from, agent.Position);
        BeginRescueVerification(agent, "local-escape");
        if (agent.Squad != null)
        {
            agent.Squad.NextDispatchAttemptAt = 0f;
            if (resume) _waypointSystem?.ClearSquadUnreachability(agent.Squad);
        }
        ResetAfterRescue(agent, resume);
        Log.Info($"{agent} local escape rescue: teleported {Vector3.Distance(from, agent.Position):F1}m from={from} to={agent.Position} alternateFloor={alternateFloor}");
        return true;
    }
}
