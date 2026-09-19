using System.Reflection;

namespace UnityEngine
{
    public static class Time { public static float time, deltaTime; }
    public struct Vector3(float x, float y, float z)
    {
        public float x = x, y = y, z = z;
        public static Vector3 up => new(0, 1, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector3 normalized => magnitude > 0 ? this * (1f / magnitude) : default;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float b) => new(a.x * b, a.y * b, a.z * b);
        public static bool operator ==(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-8f;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public override bool Equals(object obj) => obj is Vector3 v && this == v;
        public override int GetHashCode() => HashCode.Combine(x, y, z);
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 MoveTowards(Vector3 a, Vector3 b, float delta) => Distance(a, b) <= delta ? b : a + (b - a).normalized * delta;
    }
    public static class Mathf
    {
        public static float Min(float a, float b) => Math.Min(a, b);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Clamp(float v, float a, float b) => Math.Clamp(v, a, b);
    }
    public class Transform { public Vector3 position; }
    public class GameObject { public bool activeSelf = true; }
    public struct Ray(Vector3 origin, Vector3 direction) { public Vector3 origin = origin, direction = direction; }
    public struct Bounds
    {
        public void Expand(float value) { }
        public bool Contains(Vector3 value) => false;
        public bool IntersectRay(Ray ray, out float distance) { distance = 0.8f; return true; }
    }
    public class Collider { public Bounds bounds; }
}
namespace UnityEngine.AI
{
    public struct NavMeshHit { public UnityEngine.Vector3 position; }
    public static class NavMesh
    {
        public const int AllAreas = -1;
        public static bool Passable = true;
        public static bool SamplePosition(UnityEngine.Vector3 p, out NavMeshHit hit, float radius, int areas)
        { hit = new() { position = p }; return Passable; }
        public static bool Raycast(UnityEngine.Vector3 a, UnityEngine.Vector3 b, out NavMeshHit hit, int areas)
        { hit = new(); return !Passable; }
    }
}
public enum BotLogicDecision { simplePatrol, followerPatrol, alternativePatrol, holdPosition, shootFromPlace }
public class CoreActionResultParams { }
public struct AICoreActionResult<T, U> { public T Action; }
public class AICoreStrategy<T> { }
public class AICoreAgent<T> { public AICoreStrategy<T> _strategy; }
public class BaseBrain : AICoreStrategy<BotLogicDecision> { public EFT.BotOwner _owner; }
namespace EFT
{
    using UnityEngine;
    public enum WildSpawnType { assault = 1 }
    public class BotOwner
    {
        public int Id;
        public string ProfileId = Guid.NewGuid().ToString();
        public Profile Profile = new();
        public Brain Brain = new();
        public BotMover Mover = new();
        public StandBy StandBy = new();
        public Medecine Medecine = new();
        public DoorOpener DoorOpener = new();
        public PatrollingData PatrollingData = new();
        public Player GetPlayer = new();
        public Inventory Inventory => GetPlayer.InventoryController;
        public Vector3 Position => GetPlayer.Transform.position;
        public bool IsDead;
        public GameObject gameObject = new();
        public MoreBotsAPI.Components.BotHuntManager Hunt;
        public object GetComponent(Type type) => type.IsInstanceOfType(Hunt) ? Hunt : null;
    }
    public class Profile { public string Nickname = "Test"; public Info Info = new(); }
    public class Info { public Settings Settings = new(); }
    public class Settings { public WildSpawnType Role; }
    public class Brain { public BotLogicDecision? LastDecision; public AICoreAgent<BotLogicDecision> Agent = new(); }
    public class StandBy { public bool CanDoStandBy = true; }
    public class Medecine { public bool Using; }
    public class DoorOpener { public bool Interacting; }
    public class PatrollingData { public PatrolPoint CurPatrolPoint; }
    public class PatrolPoint { public PatrolTarget TargetPoint; }
    public class PatrolTarget { public object ActionData; }
    public class Inventory
    {
        public List<InventoryLogic.ItemEventArgs> Events = new();
        public IEnumerable<T> SelectEvents<T>() => Events.OfType<T>();
    }
    public class Player
    {
        public Transform Transform = new();
        public Vector3 Position => Transform.position;
        public MovementContext MovementContext = new();
        public Inventory InventoryController = new();
        public void Teleport(Vector3 p) => Transform.position = p;
    }
    public class MovementContext { public void ResetFlying() { } }
    public class BotMover
    {
        public bool Pause, Sprinting, NoSprint, IsMoving;
        public float RemainPause, TargetPose = 1f, DestMoveSpeed = 1f;
        public Vector3 _lastGoodCastPoint, _prevSuccessLinkedFrom, _prevLinkPos, PositionOnWayInner, NormDirCurPoint, _dirCurPoint;
        public BotPathController ActualPathController = new();
        public bool HasPathAndNoComplete => ActualPathController.HavePath;
        public void MovementResume() => Pause = false;
        public void RecalcWay() { }
    }
    public class BotPathController
    {
        private Vector3[] points;
        private int index;
        private BotOwner owner;
        public int Completions;
        public Vector3? TargetOverride;
        public BotPathController CurPath => HavePath ? this : null;
        public int CurIndex => index;
        public int Length => points.Length;
        public bool HavePath => points != null;
        public void SetPath(BotOwner bot, params Vector3[] route) { owner = bot; points = route; index = 0; }
        public Vector3 CurrentCorner() => points[index];
        public bool IsLast() => index >= points.Length;
        public bool CheckShouldMove()
        {
            if (Vector3.Distance(owner.Position, TargetOverride ?? points[^1]) > 0.1f) return true;
            points = null; Completions++; return false;
        }
        public void IncCornerIndex()
        {
            index++;
            if (IsLast()) { points = null; Completions++; }
        }
    }
}
namespace EFT.InventoryLogic { public class ItemEventArgs { } }
namespace EFT.Interactive
{
    public enum EDoorState { Open, Shut, Locked }
    public class Door
    {
        public EDoorState DoorState;
        public UnityEngine.Collider Collider = new();
        public UnityEngine.Transform transform = new();
    }
}
namespace HarmonyLib
{
    public static class AccessTools
    {
        public static Type TypeByName(string name) => typeof(AccessTools).Assembly.GetType(name);
        public static FieldInfo Field(Type type, string name) => type.GetField(name);
        public static MethodInfo Method(Type type, string name, Type[] args) => type.GetMethod(name, args);
    }
}
namespace MoreBotsAPI.Components
{
    public class BotHuntManager { public bool active; public int Updates; public void Update() => Updates++; }
}
namespace MoreBotsAPI.Behavior.Actions
{
    public class HuntTargetAction { }
    public class HuntRegroupAction { }
    public class SearchForTargetAction { }
    public class UnknownAction { }
}
namespace DrakiaXYZ.BigBrain.Brains
{
    public static class BrainManager
    {
        public static Dictionary<Type, int> CustomLogicsReadOnly => new()
        {
            [typeof(MoreBotsAPI.Behavior.Actions.HuntTargetAction)] = 9001,
            [typeof(MoreBotsAPI.Behavior.Actions.HuntRegroupAction)] = 9002,
            [typeof(MoreBotsAPI.Behavior.Actions.SearchForTargetAction)] = 9003,
            [typeof(MoreBotsAPI.Behavior.Actions.UnknownAction)] = 9004,
        };
    }
}
namespace Orbit.Navigation
{
    public static class DangerZones { public static bool Inside; public static bool IsInside(UnityEngine.Vector3 p) => Inside; }
}
namespace Orbit.Systems
{
    public class DoorSystem { public EFT.Interactive.Door[] Doors = []; }
    public static class Log
    {
        public static void Info(string message) { }
        public static void Debug(string message) { }
        public static void Warning(string message) { }
    }
}
