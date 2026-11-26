using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace UnityEngine
{
    public static class Mathf
    {
        public static int Max(int a, int b) => Math.Max(a, b);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static int CeilToInt(float n) => (int)Math.Ceiling(n);
        public static float Sqrt(float v) => MathF.Sqrt(v);
        public static float Abs(float v) => MathF.Abs(v);
        public static float Clamp(float v, float min, float max) => Math.Clamp(v, min, max);
        public static float Min(float a, float b) => Math.Min(a, b);
        public static int Min(int a, int b) => Math.Min(a, b);
    }
    public static class Random
    {
        public static int Draws;
        public static float Next;
        public static float value { get { Draws++; return Next; } }
    }
}
namespace UnityEngine.AI
{
    public enum NavMeshPathStatus { PathComplete, PathPartial, PathInvalid }
    public class NavMeshPath { public NavMeshPathStatus status; public Vector3[] corners; }
}
namespace EFT.Interactive
{
    public partial class Door
    {
        public string Id = "gate";
        public Transform transform = new();
        public bool Operatable = true, Obstructed, DelayedUnlock;
        public object InteractingPlayer;
        public int Unlocks, Opens;
        public int GetInstanceID() => GetHashCode();
        public Vector3 GetInteractionPosition(Vector3 from) => new(
            transform.position.x + (from.x < transform.position.x ? -1 : 1), from.y, transform.position.z);
        public void Unlock() { Unlocks++; if (!DelayedUnlock) DoorState = EDoorState.Shut; }
    }
}
namespace Orbit.Api
{
    public static class OrbitDoorEvents
    {
        public enum Operation { Unlock }
        public static void Raise(Door door, Operation op) { }
    }
}
namespace Orbit.Sain { internal static class PersonalityFallback { internal const float LockedDoorUnlockProba = .3f; } }
namespace Orbit.Helpers
{
    public enum TransitionPhase { ExfilSearch, ExfilEligibility, ExfilPath }
    public static class PerformanceJournal
    {
        public static bool Enabled => false;
        public static IDisposable Measure(TransitionPhase phase, string tag, int squad) => null;
        public static void Event(string tag, string profile, string detail, int squad) { }
    }
    public static class Log
    {
        public static readonly List<string> Lines = new();
        public static void Info(string line) => Lines.Add(line);
        public static void Warning(string line) => Lines.Add(line);
        public static void Debug(string line) { }
    }
    public static class Roles { public static bool IsPMC(this Role role) => role == Role.Pmc; }
}
namespace Orbit.Entities
{
    public enum Role { Pmc, Scav }
    public class Bot { public bool IsDead; public Profile Profile = new(); }
    public class Profile { public Info Info = new(); public string Id = "pmc"; }
    public class Info { public Settings Settings = new(); }
    public class Settings { public Role Role; }
    public class Personality { public float LockedDoorUnlockProba = .3f; }
    public partial class Squad
    {
        public Squad() : base(1, []) { }
        public Personality Personality = new();
        public bool? WoodsBunkerChoice;
        public float GhostFightUntil;
        public Agent Leader;
        public List<Agent> Members = new();
    }
    public enum MovementStatus { Moving, Stopped, Failed }
    public class Movement { public bool HasPath => Status == MovementStatus.Moving; public float DoorInteractHoldUntil; public MovementStatus Status; }
    public partial class Agent
    {
        public Agent(int id = 1) : base(id, []) { }
        public bool? SoloWoodsBunkerChoice;
        public bool SoloExtractRequested, IsDormant, IsActive = true;
        public Bot Bot = new(); public Movement Movement = new();
    }
}
namespace Orbit.Tasks { public struct TaskAssignment { } }
namespace Orbit.Navigation
{
    public enum WaypointCategory { Exfil }
    public partial class Waypoint
    {
        public string Name; public WaypointCategory Category;
        public Vector3? ExfilGateApproach, ExfilInteriorPosition;
    }
    internal static class DangerZones { internal static bool IsInside(Vector3 v) => false; }
    internal class OperationRouteSearch
    {
        public bool Pending => false;
        public string Diagnostics => "test route";
        public Vector3[] RouteCorners;
        public static void Remember(List<Vector3> points, Vector3 point) => points.Add(point);
        public void Reset() { }
        public bool Find(Vector3 from, Vector3 target, out Vector3 point, out bool final, int retries,
            List<Vector3> history, bool floorAware, Func<Vector3, bool> allowedPoint,
            Func<Vector3, bool> finalPoint, bool localAccess)
        {
            point = target; RouteCorners = [from, target]; final = true;
            return finalPoint?.Invoke(target) ?? true;
        }
    }
}
namespace Orbit.Systems
{
    internal interface IObjectiveWork { void ResumeObjectiveWork(); }
    internal enum OperationStepKind { Access }
    internal class OperationStep
    {
        public OperationStepKind Kind;
        public Door Object;
        public Func<Vector3, bool> InteractionCheck => CanInteract;
        public bool CanInteract(Vector3 feet) => !Object.Obstructed && (feet - Object.transform.position).sqrMagnitude <= 4;
    }
    public partial class WaypointSystem
    {
        private string _mapId = "woods";
        private readonly FrameSearchQueue _dispatchQueue = new(clock: () => 0);
        public void Add(Waypoint point) => _exfilPoints.Add(point);
        internal bool TryOperationWork(IObjectiveWork work) => true;
        internal void ContinueOperationWork(IObjectiveWork work) { }
        internal void CancelOperationWork(IObjectiveWork work) { }
        private bool BeginExfilDiagnostics(Squad squad, bool? pmc) => false;
        private void LogEligibleExfil(Squad squad, Waypoint point, Vector3 from, bool entry) { }
        private bool SquadCanUseWaypoint(Squad squad, bool? pmc, Waypoint point, Agent solo) =>
            ((ExfiltrationPoint)point.Target).Status == EExfiltrationStatus.RegularMode;
        internal static readonly List<Vector3> Queried = new();
        internal static Vector3? GatePathEnd;
        private static bool CalculateTimedPath(Vector3 from, Vector3 target, NavMeshPath path, TransitionPhase phase, string name, int id)
        {
            Queried.Add(target);
            var end = name == "ZB-014" ? GatePathEnd : null;
            path.corners = [from, end ?? target];
            path.status = end.HasValue ? NavMeshPathStatus.PathPartial : NavMeshPathStatus.PathComplete;
            return true;
        }
    }
    public partial class MovementSystem
    {
        private readonly Dictionary<int, float> _doorInteractCooldown = new();
        private const float DoorInteractCooldownSeconds = 1.5f;
        public List<Vector3> Orders = new();
        public static void StopExfilApproach(Agent a) => a.Movement.Status = MovementStatus.Stopped;
        public static bool IsTravelPaused(Agent a) => false;
        public void MoveToByPath(Agent a, Vector3 point, bool sprint)
        { Orders.Add(point); a.Movement.Status = MovementStatus.Moving; }
        public bool TryFollowPreparedPath(Agent agent, Vector3 point, Vector3[] corners, bool sprint)
        {
            if (corners == null || corners.Length < 2) return false;
            MoveToByPath(agent, point, sprint);
            return true;
        }
        public bool OpenObjectiveDoor(Agent a, Door door)
        { door.Opens++; door.DoorState = EDoorState.Open; return true; }
    }
}
