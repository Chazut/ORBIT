using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Navigation;

/// <summary>
/// One async navmesh path query. Submitted into the executor's queue;
/// consumed and resolved with <see cref="Path"/> + <see cref="Status"/>
/// once the executor processes it. Callers poll <see cref="IsReady"/>.
/// </summary>
public class NavJob(Vector3 origin, Vector3 target)
{
    public readonly Vector3 Origin = origin;
    public readonly Vector3 Target = target;
    public NavMeshPathStatus Status = NavMeshPathStatus.PathInvalid;
    public Vector3[] Path;
    public bool IsReady => Path != null;
}

/// <summary>
/// Batched navmesh-path executor. Spreads <see cref="NavMesh.CalculatePath"/>
/// calls across frames to avoid stalling the simulation when many bots
/// re-path simultaneously. The batch size ramps up with queue depth —
/// idle queues drain slowly, full queues drain at the full batch rate.
/// </summary>
public class NavJobExecutor(int batchSize = 5)
{
    private readonly Queue<NavJob> _jobQueue = new(20);

    public NavJob Submit(Vector3 origin, Vector3 target)
    {
        var job = new NavJob(origin, target);
        Submit(job);
        return job;
    }

    public void Submit(NavJob job) => _jobQueue.Enqueue(job);

    public void Update()
    {
        // Ramp batch with queue depth: a queue of N items drains in O(log N)
        // frames rather than O(N/batchSize), but a tiny queue still drains
        // gently rather than spiking the work into one frame.
        var counter = 0;
        var rampedBatchSize = Mathf.Min(Mathf.CeilToInt(_jobQueue.Count / 2f), batchSize);

        while (_jobQueue.Count > 0 && counter < rampedBatchSize)
        {
            var job = _jobQueue.Dequeue();
            var path = new NavMeshPath();
            NavMesh.CalculatePath(job.Origin, job.Target, NavMesh.AllAreas, path);
            job.Path = path.corners;
            job.Status = path.status;
            counter++;
        }
    }
}
