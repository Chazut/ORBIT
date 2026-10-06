using Orbit.Entities;
using UnityEngine;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private readonly ObjectiveWorkQueue _objectiveWork = new();
    internal bool TryOperationWork(IObjectiveWork owner) => _objectiveWork.TryTake(owner, Time.frameCount);
    internal void ContinueOperationWork(IObjectiveWork owner) => _objectiveWork.Enqueue(owner);
    internal void CancelOperationWork(IObjectiveWork owner) => _objectiveWork.Cancel(owner);

    internal static bool ObjectiveCombat(Squad squad)
    {
        foreach (var member in squad.Members)
            if (member != null && !member.IsActive && member.Player?.HealthController is { IsAlive: true }) return true;
        return false;
    }
}
