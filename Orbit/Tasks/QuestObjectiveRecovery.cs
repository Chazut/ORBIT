using Orbit.Helpers;
using Orbit.Entities;
using Orbit.Navigation;

namespace Orbit.Tasks;

internal static class QuestObjectiveRecovery
{
    internal static void Retire(Squad squad, Waypoint point, string reason)
    {
        if (point?.Category != WaypointCategory.Quest || squad?.MainObjectives == null) return;
        var removed = false;
        for (var i = squad.MainObjectives.Count - 1; i >= 0; i--)
        {
            var main = squad.MainObjectives[i];
            if (main.Completed || main.Type != MainObjectiveType.Quest || main.QuestTriggerId != point.Name) continue;
            squad.MainObjectives.RemoveAt(i);
            removed = true;
        }
        if (!removed) return;
        Api.OrbitTelemetry.MainObjectivesRevision++;
        Log.Info($"{squad} Quest main '{point.Name}' retired after {reason}; attraction removed, quest not completed");
        if (squad.MainObjectives.Count == 0 && !squad.ExtractRequested
            && ServerConfig.MainObjectives.ExtractOnAllCompleted)
        {
            squad.ExtractRequested = true;
            squad.ExtractRequestedReason = "no reachable mains";
            Log.Info($"{squad} no main objectives remain after failed quest; requesting extraction");
        }
    }
}
