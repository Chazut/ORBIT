using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Settings;
using UnityEngine;
using UnityEngine.AI;

namespace UnityEngine { internal static class Time { internal static float time; } }
namespace EFT.Interactive
{
    internal enum EDoorState { Open, Shut, Locked, Interacting }
    internal sealed class Door
    {
        internal string Id;
        internal EDoorState DoorState;
        internal bool Operatable = true;
        internal Func<Vector3, bool> InteractionCheck;
    }
}
namespace Orbit.Entities
{
    internal enum ObjectiveStatus { None, Moving, Finished, Failed }
    internal enum SquadObjectiveState { Active }
    internal sealed class Objective { internal ObjectiveStatus Status; }
    internal partial class Agent
    {
        internal readonly Objective Objective = new();
        internal bool IsActive => true;
        internal bool SoloExtractRequested => false;
    }
    internal sealed class Squad
    {
        internal readonly List<Agent> Members = new();
        internal sealed class SquadOrder { internal Vector3 Location; internal SquadObjectiveState Status; }
        internal readonly SquadOrder Objective = new();
    }
}
namespace Orbit.Systems
{
    internal static class CorpseEscort { internal static bool InFlight(Agent member) => false; }
    public partial class WaypointSystem
    {
        internal string RecordedMap = "Shoreline";
        internal Dictionary<string, Door> Doors = new();
        internal Door RushDoor(string id) => Doors.GetValueOrDefault(id);
        internal bool TryOperationWork(RushPlan plan) => true;
        internal Func<Agent, Door, bool> OpenRushDoor;
        internal Func<Agent, Door, Vector3, bool> CrossVoronEntrance;
    }
    internal sealed partial class RushPlan
    {
        private bool _inside, _doorApproach = true, _partial;
        private float _nextWork;
        private const float DoorArrivalRadius = .25f;
        private string Step => "VoronHideout";
        private readonly Agent _actor = new();
        private readonly NavMeshPath _path = new();
        private readonly OperationRouteSearch _route = new();
        private readonly Dictionary<Agent, Vector3> _orders = new();
        private Vector3 _anchor;
        private string _result;
        private static Door DoorStep(Door d) => d;
        private static Vector3 Point(WaypointSystem w, Vector3 p, string name, float radius = 1) => p;
        private void SetOrder(Agent a, Vector3 p, WaypointSystem w)
        { _orders[a] = p; a.Objective.Status = ObjectiveStatus.None; }
        private void Release(Squad s, WaypointSystem w) => _orders.Clear();
        private bool Skip(Squad s, WaypointSystem w, string reason) { _result = reason; return false; }
        private bool InteractDoor(Squad s, WaypointSystem w, RushPoint site, Door d)
        {
            if (!d.InteractionCheck(_actor.Position)) throw new Exception("remote marked interaction");
            _result = "reached marked handle"; return true;
        }
    }
}
