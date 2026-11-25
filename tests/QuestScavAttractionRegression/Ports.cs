using UnityEngine;
using EFT.Quests;

namespace UnityEngine
{
    public readonly record struct Vector2(float x, float y)
    {
        public static Vector2 zero => default;
        public float sqrMagnitude => x * x + y * y;
        public Vector2 normalized => sqrMagnitude > 0 ? this * (1f / MathF.Sqrt(sqrMagnitude)) : zero;
        public static Vector2 operator *(Vector2 v, float f) => new(v.x * f, v.y * f);
    }

    public readonly record struct Vector3(float x, float y, float z)
    {
        public static Vector3 up => new(0, 1, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 v, float f) => new(v.x * f, v.y * f, v.z * f);
        public static Vector3 MoveTowards(Vector3 a, Vector3 b, float step)
        {
            var delta = b - a;
            var length = MathF.Sqrt(delta.sqrMagnitude);
            return length <= step ? b : a + delta * (step / length);
        }
    }

    public readonly record struct Bounds(Vector3 min, Vector3 max)
    {
        public Vector3 center => (min + max) * .5f;
        public Vector3 Closest(Vector3 p) => new(Math.Clamp(p.x, min.x, max.x),
            Math.Clamp(p.y, min.y, max.y), Math.Clamp(p.z, min.z, max.z));
        public float SqrDistance(Vector3 p) => (Closest(p) - p).sqrMagnitude;
        public bool Contains(Vector3 p) => p == Closest(p);
    }

    public static class Mathf
    {
        public static float Clamp(float v, float min, float max) => Math.Clamp(v, min, max);
        public static float Sqrt(float v) => MathF.Sqrt(v);
    }

    public class GameObject { public bool activeInHierarchy = true; }
    public class Collider
    {
        public Bounds bounds;
        public bool enabled = true;
        public GameObject gameObject = new();
        public Vector3 ClosestPoint(Vector3 point) => bounds.Closest(point);
    }
    public class BoxCollider : Collider;
    public class SphereCollider : Collider;
    public class CapsuleCollider : Collider;
    public class MeshCollider : Collider { public bool convex; }
}

namespace EFT.Interactive
{
    public record TriggerWithId(string Id, params Collider[] Volumes)
    {
        public T[] GetComponents<T>() => Volumes.OfType<T>().ToArray();
    }
}

namespace EFT.Quests
{
    public enum EQuestStatus { Started, AvailableForFinish }
    public class Condition { public string id = Guid.NewGuid().ToString(); }
    public class ConditionKills(string who, params string[] roles) : Condition
    { public string target = who; public string[] savageRole = roles; }
    public class ConditionLocation(params string[] maps) : Condition { public string[] target = maps; }
    public class ConditionInZone(params string[] zones) : Condition { public string[] zoneIds = zones; }
    public class ConditionCounterCreator(params Condition[] conditions) : Condition
    { public Condition[] Conditions = conditions; }
    public class Template { public Dictionary<EQuestStatus, Condition[]> Conditions = new(); }
    public class Quest
    {
        public string Id = Guid.NewGuid().ToString();
        public EQuestStatus QuestStatus = EQuestStatus.Started;
        public Template Template = new();
        public Dictionary<Condition, bool> ProgressCheckers = new();
        public HashSet<string> CompletedConditions = new();
        public HashSet<Condition> Hidden = new();
        public bool CheckVisibilityStatus(Condition c) => !Hidden.Contains(c);
        public bool IsConditionDone(Condition c) => ProgressCheckers[c];
    }
}

namespace EFT
{
    public enum EPlayerSide { Bear, Savage }
    public enum Role { assault, assaultGroup, pmc }
    public class Settings { public Role Role; }
    public class Info { public Settings Settings = new(); }
    public class Profile
    {
        public EPlayerSide Side;
        public Info Info = new();
        public bool IsPlayerScav;
        public bool WillBeAPlayerScav() => IsPlayerScav;
    }
    public class Health { public bool IsAlive = true; }
    public class QuestController { public List<Quest> Quests = new(); }
    public class Player
    {
        public bool IsAI;
        public Profile Profile = new();
        public Health HealthController = new();
        public QuestController QuestController = new();
    }
    public class GameWorld { public List<Player> AllAlivePlayersList = new(); }
}

namespace Orbit.Entities
{
    public class Agent { public EFT.Player Bot = new(); }
    public class Squad { public bool ExtractRequested; public Agent Leader = new(); }
}
namespace Orbit.Navigation
{
    public record Waypoint(int Id, Vector3 Position);
    public static class QuestWaypointPlacement
    {
        public static bool Contains(Collider volume, Vector3 feet)
            => (volume.ClosestPoint(feet + Vector3.up * .75f) - (feet + Vector3.up * .75f)).sqrMagnitude < .01f;
    }
}
namespace Orbit.Helpers
{
    public static class Roles { public static bool IsScav(this EFT.Role r) => r is EFT.Role.assault or EFT.Role.assaultGroup; }
    public static class Log
    {
        public static void Warning(string message) => throw new Exception(message);
        public static void Info(string message) => Console.WriteLine(message);
    }
}
