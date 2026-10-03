using System;
using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Orbit.Tasks.Actions;

// A combat interruption retires its lease; only completed jobs return to the pool.
// Persistent arrays can safely outlive four frames without forcing a main-thread wait.
internal sealed class AreaSweepPool : IDisposable
{
    internal const int MaxJobs = 32;
    internal const int MaxCandidates = 25;
    internal const int MaxSubmissionsPerFrame = 4;
    private readonly List<AreaSweepJob> _jobs = new();
    private int _frame = -1, _submitted;
    private bool _disposed, _needsFlush;

    internal AreaSweepJob Submit(Vector3 origin, List<Vector3> directions, int mask)
    {
        if (_disposed || directions.Count == 0) return null;
        if (_frame != Time.frameCount) { _frame = Time.frameCount; _submitted = 0; }
        if (_submitted >= MaxSubmissionsPerFrame) return null;
        AreaSweepJob job = null;
        for (var i = 0; i < _jobs.Count; i++)
            if (!_jobs[i].InUse) { job = _jobs[i]; break; }
        if (job == null && _jobs.Count >= MaxJobs) return null;

        using var timing = TransitionPerformance.Measure(TransitionPhase.GuardSchedule);
        if (job == null)
        {
            job = new AreaSweepJob();
            try
            {
                job.Commands = new NativeArray<RaycastCommand>(MaxCandidates, Allocator.Persistent);
                job.Hits = new NativeArray<RaycastHit>(MaxCandidates, Allocator.Persistent);
            }
            catch
            {
                if (job.Commands.IsCreated) job.Commands.Dispose();
                if (job.Hits.IsCreated) job.Hits.Dispose();
                throw;
            }
            _jobs.Add(job);
        }
        job.Count = Math.Min(directions.Count, MaxCandidates);
        var parameters = new QueryParameters { layerMask = mask };
        for (var i = 0; i < job.Count; i++)
            job.Commands[i] = new RaycastCommand(origin, directions[i], parameters, 100);
        job.Handle = RaycastCommand.ScheduleBatch(job.Commands.GetSubArray(0, job.Count),
            job.Hits.GetSubArray(0, job.Count), 1);
        job.InUse = true;
        job.Retired = false;
        _submitted++;
        _needsFlush = true;
        PerfMonitor.SweepJobsSubmitted++;
        return job;
    }

    internal void Retire(AreaSweepJob job)
    {
        if (!_disposed && job.InUse) job.Retired = true;
    }

    internal void Release(AreaSweepJob job)
    {
        // Caller has completed and consumed the results. The guard drops its reference first.
        job.InUse = job.Retired = false;
    }

    internal void FlushScheduled()
    {
        if (_disposed || !_needsFlush) return;
        JobHandle.ScheduleBatchedJobs();
        _needsFlush = false;
    }

    internal void Poll()
    {
        if (_disposed) return;
        FlushScheduled();
        for (var i = 0; i < _jobs.Count; i++)
        {
            var job = _jobs[i];
            if (!job.InUse || !job.Retired || !job.Handle.IsCompleted) continue;
            using var timing = TransitionPerformance.Measure(TransitionPhase.GuardComplete);
            job.Handle.Complete();
            Release(job);
            PerfMonitor.SweepJobsDrained++;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Raid teardown is the only place allowed to wait for unfinished work.
        foreach (var job in _jobs)
        {
            if (job.InUse) job.Handle.Complete();
            if (job.Commands.IsCreated) job.Commands.Dispose();
            if (job.Hits.IsCreated) job.Hits.Dispose();
            job.InUse = job.Retired = false;
        }
        _jobs.Clear();
    }
}
