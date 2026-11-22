using EFT.Interactive;
using Orbit.Navigation;
using UnityEngine;

namespace UnityEngine
{
    public readonly record struct Vector3(float x, float y, float z)
    {
        public static Vector3 up => new(0, 1, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float k) => new(a.x * k, a.y * k, a.z * k);
        public static Vector3 operator /(Vector3 a, float k) => a * (1 / k);
    }
    public class Transform
    {
        public Vector3 position;
        public Transform parent;
        public bool IsChildOf(Transform t) => this == t || parent?.IsChildOf(t) == true;
        public T GetComponent<T>() where T : class => null;
        public void GetComponentsInChildren<T>(bool inactive, List<T> components) { }
    }
    public sealed class LODGroup { }
    public struct Bounds
    {
        public Vector3 center, size;
        public bool Contains(Vector3 p) => Math.Abs(p.x - center.x) <= size.x / 2
            && Math.Abs(p.y - center.y) <= size.y / 2 && Math.Abs(p.z - center.z) <= size.z / 2;
    }
    public class Collider
    {
        public Transform transform = new();
        public Bounds bounds;
        public Func<Vector3, Vector3> Closest = p => p;
        public Vector3 ClosestPoint(Vector3 p) => Closest(p);
    }
    public enum QueryTriggerInteraction { Ignore }
    public struct RaycastHit { public Collider collider; }
    public static class Physics
    {
        public static Collider FirstHit;
        public static Vector3 End;
        public static bool Raycast(Vector3 start, Vector3 direction, out RaycastHit hit, float length, int mask, QueryTriggerInteraction triggers)
        {
            End = start + direction * length;
            hit = new() { collider = FirstHit };
            return FirstHit != null;
        }
    }
    public static class Time { public static float time; }
}
public static class LayersMaskController { public const int HighPolyWithTerrainMask = 1; }
namespace EFT.Interactive
{
    public class InteractableObject { public Transform transform = new(); public Transform TrackableTransform => transform; }
    public class LootableContainer : InteractableObject { public Collider Collider; }
    public enum EDoorState { Locked, Shut, Interacting, Open }
    public class Door { public EDoorState DoorState; }
    public enum EExfiltrationType { Individual, SharedTimer }
    public enum EExfiltrationStatus { RegularMode, NotPresent, Hidden, AwaitsManualActivation }
    public class Settings { public string Name; public EExfiltrationType ExfiltrationType; }
    public class ExfiltrationPoint
    {
        public Settings Settings = new();
        public EExfiltrationStatus Status;
        public Collider Volume;
        public T GetComponent<T>() where T : class => Volume as T;
    }
}
namespace Orbit.Navigation { public class Waypoint { public object Target; public Door ExfilGate; public int Id; public Vector3 Position; } }
namespace Orbit.Entities
{
    public enum ObjectiveStatus { None }
    public class Objective
    {
        public Waypoint Location, SplinterParent; public object ArrivalPath;
        public float ExfilOutsideTriggerSince, DispatchTime; public Vector3 ExfilOutsideTriggerLastPosition; public ObjectiveStatus Status;
    }
    public class Squad { public HashSet<int> CompletedPoiIds = new(); public Waypoint NearestExfilCached; public float NearestExfilCachedAt; public Objective Objective = new(); }
    public class Agent { public Squad Squad; public Objective Objective = new(); public Waypoint SoloExtractTarget; public Vector3 Position; }
}
