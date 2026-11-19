using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

public partial class MovementSystem
{
    private int _lastGrenadeProbeFrame = -1;

    // The baseline is already calculated (including any travel styling). Check the exact
    // corners the bot would walk, after normal job ownership and revision checks.
    private bool ResolveGrenadeRoute(Agent agent, NavJob job, int revision)
    {
        var memory = agent.GrenadeInterruptions;
        if (memory == null) return true;
        if (!memory.Armed(Time.time))
        {
            memory.Plan = null;
            return true;
        }
        if (agent.IsDormant
            || agent.Squad?.Rush?.Sniper?.IsPostShift(agent) == true
            || job.Status == NavMeshPathStatus.PathInvalid || job.Path.Length < 2) return true;

        if (memory.Plan == null || memory.Revision != revision)
        {
            if (GrenadeDetourPlan.Clears(agent.Position, job.Path, memory.Danger)) return true;
            memory.Revision = revision;
            // Reuse the baseline's sampled start. The body can be above the mesh on stairs or a slope.
            var start = (job.Path[0] - agent.Position).sqrMagnitude <= TargetEpsSqr ? job.Path[0] : agent.Position;
            memory.Plan = new GrenadeDetourPlan(start, job.Target, memory.Danger);
            if (memory.Plan.Exhausted) LogGrenadeRoute(agent, job, "wait", "target-inside-or-vertical");
        }

        var plan = memory.Plan;
        // With no safe path, keep the original order pending until the local memory expires.
        // No repeated native queries, arrival at an intermediate point, or global POI blacklist.
        if (plan.Exhausted || _lastGrenadeProbeFrame == Time.frameCount) return false;
        _lastGrenadeProbeFrame = Time.frameCount;
        plan.Step(SampleGrenadePivot, GrenadeLeg);
        if (plan.Result == null)
        {
            if (plan.Exhausted) LogGrenadeRoute(agent, job, "wait", "no-safe-path");
            return false;
        }

        job.Path = plan.Result;
        job.Status = NavMeshPathStatus.PathComplete;
        job.Travel = null; // Baseline exposure samples describe a different path.
        LogGrenadeRoute(agent, job, "routed", "safe-bypass");
        memory.Plan = null;
        return true;
    }

    private static Vector3? SampleGrenadePivot(Vector3 pivot) =>
        NavMesh.SamplePosition(pivot, out var hit, 4f, NavMesh.AllAreas)
        && !DangerZones.IsInside(hit.position) ? hit.position : null;

    private Vector3[] GrenadeLeg(Vector3 from, Vector3 to)
    {
        var corners = _travelWorld.Path(from, to);
        return corners != null && !DirectionalMineSafety.Blocks(from, corners, out _) ? corners : null;
    }

    private static void LogGrenadeRoute(Agent agent, NavJob job, string action, string reason)
    {
        if (Log.InfoEnabled)
            Log.Info($"GRENADE DETOUR: {agent} action={action} reason={reason} danger={agent.GrenadeInterruptions.Danger} from={agent.Position} target={job.Target}");
    }
}
