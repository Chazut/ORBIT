using System;
using System.Collections.Generic;
using System.Diagnostics;
using Orbit.Settings;
using UnityEngine;

namespace Orbit.Navigation;

// Geometry only. Implementations must not inspect enemies or the human player's position.
internal interface ITravelWorld
{
    float Exposure(Vector3 position);
    void Covers(Vector3 position, List<Vector3> result);
    Vector3[] Path(Vector3 origin, Vector3 target);
    bool Connects(Vector3 origin, Vector3 target);
}

internal sealed class TravelRoute
{
    internal Vector3[] Corners, Samples;
    internal float[] Exposure;
    internal float Length, ExposedDistance;

    internal static float Distance(Vector3[] path)
    {
        var length = 0f;
        for (var i = 1; i < path.Length; i++) length += Vector3.Distance(path[i - 1], path[i]);
        return length;
    }

    internal static Vector3 At(Vector3[] path, float distance)
    {
        for (var i = 1; i < path.Length; i++)
        {
            var length = Vector3.Distance(path[i - 1], path[i]);
            if (distance <= length && length > .001f)
                return Vector3.Lerp(path[i - 1], path[i], distance / length);
            distance -= length;
        }
        return path[path.Length - 1];
    }

    internal bool IsExposed(Vector3 position)
    {
        var nearest = float.MaxValue;
        var exposure = 0f;
        for (var i = 0; i < Samples.Length; i++)
        {
            if (Mathf.Abs(Samples[i].y - position.y) > 2f) continue;
            var distance = (Samples[i] - position).sqrMagnitude;
            if (distance < nearest) { nearest = distance; exposure = Exposure[i]; }
        }
        return nearest < 144f && exposure >= .75f;
    }
}

internal sealed class TravelRequest
{
    internal TravelStyle Style;
    internal int SquadId;
    internal string Archetype;
    internal Func<bool> Current;
    internal TravelRoute Result;
    internal Action<TravelRequest, string> Report;
    internal float BaselineLength, BaselineExposure, WaitSeconds;
    internal int Paths, Probes, Detours;
    internal bool LeaderRoute, CacheHit;
    internal string Error;
}

// Extra navigation is cooperative: one indivisible native path call per frame, at most eight
// work steps, and a 1ms wall-clock budget. A slow native call is timed but cannot be preempted.
internal sealed class TravelRoutePlanner(ITravelWorld world)
{
    private sealed class Pending
    {
        internal NavJob Job;
        internal Vector3[] Baseline;
        internal IEnumerator<bool> Steps;
        internal TravelRoute Best;
        internal float Started;
        internal bool Completed;
    }
    private readonly Queue<Pending> _pending = new();
    private readonly Dictionary<int, (TravelRoute Route, float Time, string Archetype)> _cache = new();
    private readonly Queue<int> _cacheOrder = new();
    internal int PendingCount => _pending.Count;
    internal const float DeadlineSeconds = 1f;

    internal void Submit(NavJob job, Vector3[] path, float now)
    {
        if (_pending.Count >= 24 || path.Length < 2 || TravelRoute.Distance(path) < 20f)
        {
            job.Path = path;
            job.Travel.Report?.Invoke(job.Travel, _pending.Count >= 24 ? "queue-full" : "short-path");
            return;
        }
        var pending = new Pending { Job = job, Baseline = path, Started = now };
        pending.Steps = Plan(pending, now).GetEnumerator();
        _pending.Enqueue(pending);
    }

    internal void Update(float now, double budgetMs = 1d)
    {
        var start = Stopwatch.GetTimestamp();
        for (var step = 0; step < 8 && _pending.Count > 0; step++)
        {
            if (step > 0 && (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency >= budgetMs) break;
            var pending = _pending.Dequeue();
            var request = pending.Job.Travel;
            request.WaitSeconds = now - pending.Started;
            var usedPath = false;
            if (!request.Current()) Finish(pending, "cancelled");
            else if (request.WaitSeconds >= DeadlineSeconds) Finish(pending, "deadline");
            else
            {
                try
                {
                    if (!pending.Steps.MoveNext()) Finish(pending, request.CacheHit ? "cache" : "planned");
                    else usedPath = pending.Steps.Current;
                }
                catch (Exception error)
                {
                    request.Error = error.GetType().Name;
                    Finish(pending, "world-error");
                }
            }
            if (!pending.Completed) _pending.Enqueue(pending);
            if (usedPath) break;
        }
    }

    private void Finish(Pending pending, string reason)
    {
        var request = pending.Job.Travel;
        pending.Completed = true;
        pending.Steps.Dispose();
        if (reason == "cancelled") pending.Best = null;
        pending.Job.Path = pending.Best?.Corners ?? pending.Baseline;
        request.Result = pending.Best;
        if (request.LeaderRoute && reason == "planned" && pending.Best != null)
        {
            if (!_cache.ContainsKey(request.SquadId))
            {
                if (_cache.Count >= 64) _cache.Remove(_cacheOrder.Dequeue());
                _cacheOrder.Enqueue(request.SquadId);
            }
            _cache[request.SquadId] = (pending.Best, pending.Started, request.Archetype);
        }
        request.Report?.Invoke(request, reason);
    }

    private IEnumerable<bool> Plan(Pending pending, float now)
    {
        var request = pending.Job.Travel;
        var baseline = pending.Baseline;
        request.BaselineLength = TravelRoute.Distance(baseline);
        // Reuse a nearby squad route only after checking the physical navigation connector.
        // Geometry probes are cached across squads too, without sharing knowledge of enemies.
        if (_cache.TryGetValue(request.SquadId, out var cached) && now - cached.Time < 20f
            && cached.Archetype == request.Archetype
            && (cached.Route.Corners[cached.Route.Corners.Length - 1] - baseline[baseline.Length - 1]).sqrMagnitude < 1f
            && (cached.Route.Corners[0] - baseline[0]).sqrMagnitude < 100f
            && Mathf.Abs(cached.Route.Corners[0].y - baseline[0].y) < 2f
            && world.Connects(baseline[0], cached.Route.Corners[0]))
        {
            var joined = new Vector3[cached.Route.Corners.Length + 1];
            joined[0] = baseline[0];
            Array.Copy(cached.Route.Corners, 0, joined, 1, cached.Route.Corners.Length);
            var length = TravelRoute.Distance(joined);
            if (length <= request.BaselineLength * request.Style.MaxDetourRatio)
            {
                pending.Best = new TravelRoute { Corners = joined, Length = length, Samples = cached.Route.Samples,
                    Exposure = cached.Route.Exposure, ExposedDistance = cached.Route.ExposedDistance };
                // A cache hit is a completed plan, not an unvalidated replacement of an active path.
                request.BaselineExposure = cached.Route.ExposedDistance;
                request.CacheHit = true;
                yield break;
            }
        }
        var metric = CreateMetric(baseline);
        foreach (var step in Measure(metric, request)) yield return step;
        pending.Best = metric;
        request.BaselineExposure = metric.ExposedDistance;
        if (request.Style.CoverPreference <= 0f || request.Style.MaxDetourRatio <= 1f) yield break;

        var covers = new List<Vector3>(16);
        // Three windows can produce several local covered sections along a long journey. Each
        // accepted replacement is scored over the entire resulting polyline and the original cap.
        for (var window = 0; window < 3; window++)
        {
            metric = pending.Best;
            var selected = -1;
            var first = window * metric.Samples.Length / 3;
            var last = (window + 1) * metric.Samples.Length / 3;
            for (var i = first; i < last; i++)
                if (metric.Exposure[i] >= .75f && (selected < 0 || metric.Exposure[i] > metric.Exposure[selected])) selected = i;
            if (selected < 0) continue;
            var along = (selected + .5f) * metric.Length / metric.Samples.Length;
            // Keep the final interaction approach and the start intact.
            var entry = Math.Max(0f, along - 20f);
            var exit = Math.Min(metric.Length, along + 20f);
            if (exit - entry < 20f) continue;
            world.Covers(metric.Samples[selected], covers);
            yield return false;
            if (covers.Count == 0) continue;
            // The world ranks hard cover first. One candidate per window bounds path work.
            var anchor = covers[0];
            var left = world.Path(TravelRoute.At(metric.Corners, entry), anchor);
            request.Paths++;
            yield return true;
            if (left == null || left.Length < 2) continue;
            var right = world.Path(anchor, TravelRoute.At(metric.Corners, exit));
            request.Paths++;
            yield return true;
            if (right == null || right.Length < 2) continue;
            var candidate = CreateMetric(Splice(metric.Corners, entry, exit, left, right));
            if (candidate.Length > request.BaselineLength * request.Style.MaxDetourRatio) continue;
            foreach (var step in Measure(candidate, request)) yield return step;
            if (request.Style.RouteCost(candidate.Length, candidate.ExposedDistance) + .5f
                >= request.Style.RouteCost(metric.Length, metric.ExposedDistance)) continue;
            pending.Best = candidate;
            request.Detours++;
        }
    }

    private static TravelRoute CreateMetric(Vector3[] corners)
    {
        var length = TravelRoute.Distance(corners);
        var count = Math.Max(1, Math.Min(64, (int)Math.Ceiling(length / 8f)));
        return new TravelRoute { Corners = corners, Length = length, Samples = new Vector3[count], Exposure = new float[count] };
    }

    private IEnumerable<bool> Measure(TravelRoute route, TravelRequest request)
    {
        var spacing = route.Length / route.Samples.Length;
        for (var i = 0; i < route.Samples.Length; i++)
        {
            route.Samples[i] = TravelRoute.At(route.Corners, (i + .5f) * spacing);
            route.Exposure[i] = world.Exposure(route.Samples[i]);
            route.ExposedDistance += route.Exposure[i] * spacing;
            request.Probes++;
            yield return false;
        }
    }

    private static Vector3[] Splice(Vector3[] original, float entry, float exit, Vector3[] left, Vector3[] right)
    {
        var result = new List<Vector3>(original.Length + left.Length + right.Length);
        var distance = 0f;
        for (var i = 0; i < original.Length; i++)
        {
            if (i > 0) distance += Vector3.Distance(original[i - 1], original[i]);
            if (distance < entry) result.Add(original[i]);
        }
        result.AddRange(left);
        for (var i = 1; i < right.Length; i++) result.Add(right[i]);
        distance = 0f;
        for (var i = 1; i < original.Length; i++)
        {
            distance += Vector3.Distance(original[i - 1], original[i]);
            if (distance > exit) result.Add(original[i]);
        }
        return result.ToArray();
    }
}
