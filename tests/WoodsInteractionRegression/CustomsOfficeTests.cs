using EFT.Interactive;
using Orbit.Navigation;
using UnityEngine;

internal static class CustomsOfficeTests
{
    internal static void Run(Action<bool, string> check)
    {
        // Bounds from Customs level511, container positions from level8.
        SpatialAudioRoom Room(string name, float x, float z, float width, float depth) =>
            new() { name = name, Bounds = new(new(x, 8.54f, z), new(width, 3.09f, depth)) };
        var hall = Room("Entry", -205.82f, -103.90f, 2.23f, 12.15f);
        var pcs = Room("PCs", -202.80f, -106.95f, 4.49f, 5.97f);
        var safe = Room("Safe", -202.62f, -101.02f, 4.48f, 6.14f);
        var scope = new MarkedRoomScope(new(-205.82f, 7.05f, -104.05f), 7.86f, hall);
        Vector3 pc = new(-203.3f, 7.3f, -105), cash = new(-201.2f, 7.0f, -102.2f);
        check(!scope.Contains(pc) && !scope.Contains(cash), "The entrance volume alone misses the loot in both offices");
        scope.IncludeOffice(hall, pcs, hall, null, EDoorState.Shut);
        scope.IncludeOffice(hall, hall, safe, null, EDoorState.Locked);
        check(scope.Contains(pc) && !scope.Contains(cash), "An ordinary closed door leads to the PCs, a locked inner door excludes the safe");
        scope.IncludeOffice(hall, hall, safe, null, EDoorState.Open);
        check(scope.Contains(cash), "A breached or opened safe office joins the searchable suite when its door is open");
        var other = Room("Warehouse", -210, -106, 4, 4);
        scope.IncludeOffice(hall, pcs, other, null, EDoorState.Open);
        check(!scope.Contains(new(-210, 7, -106)) && !scope.Contains(new(pc.x, 1.3f, pc.z)),
            "Unrelated rooms and the floor below stay outside");
    }
}

public class SpatialAudioRoom
{
    public string name;
    public bool IsOutdoor;
    public Bounds Bounds;
    public BoxCollider[] Colliders;
}

namespace UnityEngine
{
    public struct Matrix4x4
    {
        public static Matrix4x4 identity => new();
        public Vector3 MultiplyPoint3x4(Vector3 point) => point;
    }
    public class BoxCollider : Collider { public Vector3 center, size; }
}

namespace UnityEngine.AI
{
    public struct NavMeshHit { public Vector3 position; }
    public static class NavMesh
    {
        public const int AllAreas = -1;
        public static Func<Vector3,Vector3,Vector3?> Boundary;
        public static bool Raycast(Vector3 a, Vector3 b, out NavMeshHit hit, int mask)
        {
            var edge = Boundary?.Invoke(a, b);
            hit = new() { position = edge.GetValueOrDefault() };
            return edge.HasValue;
        }
        public static bool SamplePosition(Vector3 seed, out NavMeshHit hit, float range, int areas)
        { hit = new() { position = seed }; return true; }
    }
}
