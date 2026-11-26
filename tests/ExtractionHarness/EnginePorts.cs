using System;
using System.Collections.Generic;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;

public enum EBotLinkResult { complete, extraConnect }

namespace UnityEngine
{
    public partial struct Vector3
    {
        public static Vector3 down => -up;
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 MoveTowards(Vector3 from, Vector3 to, float distance) =>
            (to - from).magnitude <= distance ? to : from + (to - from).normalized * distance;
    }
    public static partial class Mathf
    {
        public static int CeilToInt(float value) => (int)MathF.Ceiling(value);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static int Min(int a, int b) => Math.Min(a, b);
        public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
        public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);
    }
}
namespace EFT.Interactive
{
    public partial class Door
    {
        public string Id = "test-gate";
        public bool Operatable = true;
        public object InteractingPlayer;
        public int Unlocks, Opens;
        public void Unlock() { Unlocks++; DoorState = EDoorState.Shut; }
        public int GetInstanceID() => GetHashCode();
        public Vector3 GetInteractionPosition(Vector3 feet) =>
            new(transform.position.x + (feet.x < transform.position.x ? -1 : 1), feet.y, transform.position.z);
    }
}
namespace Orbit.Entities
{
    public partial class Agent { public bool? SoloWoodsBunkerChoice; internal GrenadeMemory GrenadeInterruptions; }
    internal sealed class GrenadeMemory { internal bool Armed(float now) => true; }
    public partial class Squad { public bool? WoodsBunkerChoice; public readonly HashSet<int> ForceUnlockDoorIds = new(); }
    public partial class Personality { public float LockedDoorUnlockProba = .3f; }
    public partial class Profile { public string Id = "pmc", Nickname = "Test PMC"; }
    public partial class Movement
    {
        internal Systems.MovementSystem OrdersOwner;
        public int CurrentCorner;
        internal readonly Systems.MovementFailureHistory Failures = new();
    }
    public partial class Bot { public CombatMemory Memory = new(); public TestMover Mover = new(); }
    public class TestMover
    {
        public int Stops;
        public bool Pause = true;
        public void Stop() => Stops++;
    }
    public class CombatMemory { public bool IsUnderFire; public object GoalEnemy; }
}
namespace Orbit.Sain { internal static class PersonalityFallback { internal const float LockedDoorUnlockProba = .3f; } }
namespace Orbit.Helpers
{
    public static class RoleExtensions { public static bool IsPMC(this int role) => role == 0; }
    public static partial class PerformanceJournal
    {
        public static void Event(string name, string profile, string detail, int squad) { }
    }
}
namespace Orbit.Systems
{
    internal class OperationPlan { }
    internal class RushPlan { }
    internal class AmbushSite { }
    internal class ExtractCampApproach { }
    internal interface IObjectiveWork { void ResumeObjectiveWork(); }
    internal enum OperationStepKind { Access }
    internal class OperationStep
    {
        public OperationStepKind Kind;
        public Door Object;
        public Func<Vector3, bool> InteractionCheck => CanInteract;
        public bool CanInteract(Vector3 feet) => (feet - Object.transform.position).sqrMagnitude <= 4f;
    }
    public partial class WaypointSystem
    {
        internal bool CanUseExfil(Agent agent, Waypoint exit) =>
            !ExfilArrival.IsUnavailable((ExfiltrationPoint)exit.Target)
                && (!WoodsBunkerAccess.Applies(exit) || WoodsBunkerAccess.Available(exit)
                    && WoodsBunkerAccess.Accepted(agent.Squad, agent.SoloExtractRequested ? agent : null));
        internal readonly Dictionary<string, Door> Doors = new();
        internal Door RushDoor(string id) => Doors.GetValueOrDefault(id);
        internal Func<Agent, Door, bool> OpenRushDoor;
        internal Func<Agent, Door, Vector3, bool> CrossVoronEntrance;
        internal bool ClaimedByOther;
        internal bool IsClaimedByOther(int id, int agent) => ClaimedByOther;
        internal bool IsUnavailableLooseLoot(Waypoint point) => point.Target == null;
        internal bool WorkAvailable = true;
        internal bool TryOperationWork(IObjectiveWork work) => WorkAvailable;
        internal void ContinueOperationWork(IObjectiveWork work) { }
        internal void CancelOperationWork(IObjectiveWork work) { }
    }
    public partial class MovementSystem
    {
        private static void ResetGait(Agent agent, bool sprint) => agent.Movement.Sprint = sprint;
        private static void AssignPath(Movement movement, NavJob job)
        {
            movement.OrdersOwner.Orders++;
            movement.OrdersOwner.Destination = movement.Target = job.Target;
            movement.Path = job.Path;
            movement.HasPath = true;
            movement.Status = MovementStatus.Moving;
        }
        private static void ResetPath(Agent agent, MovementStatus status)
        {
            ResetPath(agent);
            agent.Movement.Status = status;
        }
        private readonly Dictionary<int, float> _doorInteractCooldown = new();
        private const float DoorInteractCooldownSeconds = 1.5f;
        private void CancelSpawnWaypointSearch(Agent agent) { }
        public bool OpenObjectiveDoor(Agent agent, Door door)
        {
            door.Opens++;
            door.DoorState = EDoorState.Open;
            return true;
        }
    }
}
namespace Orbit.Navigation
{
    internal static class DirectionalMineSafety
    {
        public static bool Blocked;
        public static bool Blocks(Vector3 origin, Vector3[] path, out string mine) { mine = null; return Blocked; }
    }
    internal sealed class NavJob(Vector3 origin, Vector3 target)
    {
        public Vector3 Target = target;
        public readonly Vector3 Origin = origin;
        public Vector3[] Path;
        public UnityEngine.AI.NavMeshPathStatus Status;
    }
}
