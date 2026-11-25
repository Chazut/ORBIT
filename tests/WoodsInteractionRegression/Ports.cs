using EFT.Interactive;
using Orbit.Navigation;
using UnityEngine;

namespace UnityEngine
{
    public record struct Vector3(float x, float y, float z)
    {
        public static Vector3 up => new(0, 1, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public static Vector3 operator -(Vector3 a) => a * -1;
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float k) => new(a.x * k, a.y * k, a.z * k);
        public static Vector3 operator /(Vector3 a, float k) => a * (1 / k);
    }
    public class Transform
    {
        public string name = "";
        public Vector3 position;
        public Transform parent;
        public Matrix4x4 worldToLocalMatrix => Matrix4x4.identity;
        public bool IsChildOf(Transform t) => this == t || parent?.IsChildOf(t) == true;
        public T GetComponent<T>() where T : class => null;
        public void GetComponentsInChildren<T>(bool inactive, List<T> components) { }
    }
    public sealed class LODGroup { }
    public struct Bounds
    {
        public Vector3 center, size;
        public Bounds(Vector3 center, Vector3 size) { this.center = center; this.size = size; }
        public Vector3 extents => size * .5f;
        public bool Contains(Vector3 p) => Math.Abs(p.x - center.x) <= size.x / 2
            && Math.Abs(p.y - center.y) <= size.y / 2 && Math.Abs(p.z - center.z) <= size.z / 2;
    }
    public class Collider
    {
        public string name = "";
        public Func<Ray, float, RaycastHit?> SurfaceProbe;
        public bool Raycast(Ray ray, out RaycastHit hit, float length)
        {
            var result = SurfaceProbe?.Invoke(ray, length);
            hit = result.GetValueOrDefault();
            return result.HasValue;
        }
        public Transform transform = new();
        public Bounds bounds;
        public Func<Vector3, Vector3> Closest = p => p;
        public Vector3 ClosestPoint(Vector3 p) => Closest(p);
    }
    public readonly record struct Ray(Vector3 origin, Vector3 direction);
    public class Mesh { public string name; }
    public class MeshCollider : Collider { public Mesh sharedMesh; }
    public enum QueryTriggerInteraction { Ignore }
    public struct RaycastHit { public Collider collider; public float distance; public Vector3 normal, point; }
    public static class Physics
    {
        public static Collider FirstHit;
        public static Vector3 End;
        public static int Queries, Overflows;
        public static Func<Vector3, Vector3, float, RaycastHit[]> Intersections;
        public static int RaycastNonAlloc(Vector3 start, Vector3 direction, RaycastHit[] buffer, float length, int mask, QueryTriggerInteraction triggers)
        {
            Queries++;
            var hits = Intersections(start, direction, length);
            int count = Math.Min(hits.Length, buffer.Length);
            Array.Copy(hits, buffer, count);
            return count;
        }
        public static RaycastHit[] RaycastAll(Vector3 start, Vector3 direction, float length, int mask, QueryTriggerInteraction triggers)
        {
            Overflows++;
            return Intersections(start, direction, length);
        }
        public static bool Raycast(Vector3 start, Vector3 direction, out RaycastHit hit, float length, int mask, QueryTriggerInteraction triggers)
        {
            End = start + direction * length;
            hit = new() { collider = FirstHit };
            return FirstHit != null;
        }
    }
    public static class Time { public static float time; public static int frameCount; }
}
public static class LayersMaskController { public const int HighPolyWithTerrainMask = 1; }
namespace EFT.Interactive
{
    public class InteractableObject { public Transform transform = new(); public Transform TrackableTransform => transform; }
    public class LootableContainer : InteractableObject { public Collider Collider; }
    public enum EDoorState { Locked, Shut, Interacting, Open }
    public partial class Door { public EDoorState DoorState; }
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
namespace Orbit.Navigation { public partial class Waypoint { public object Target; public Door ExfilGate; public int Id; public Vector3 Position; } }
namespace Orbit.Entities
{
    public enum ObjectiveStatus { None, Moving, Extracting }
    public class Objective
    {
        public Waypoint Location, SplinterParent; public object ArrivalPath;
        public float ExfilOutsideTriggerSince, DispatchTime; public Vector3 ExfilOutsideTriggerLastPosition; public ObjectiveStatus Status;
    }
    public partial class Squad : Entity { public HashSet<int> CompletedPoiIds = new(); public Waypoint NearestExfilCached; public float NearestExfilCachedAt; public Objective Objective = new(); }
    public partial class Agent : Entity { public Squad Squad; public Objective Objective = new(); public Waypoint SoloExtractTarget; public Vector3 Position; }
}
