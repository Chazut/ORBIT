namespace UnityEngine
{
    internal record struct Vector3(float x, float y, float z)
    {
        internal static Vector3 down => new(0, -1, 0);
        internal static Vector3 up => new(0, 1, 0);
        internal float sqrMagnitude => x * x + y * y + z * z;
        internal static Vector3 zero => default;
        internal float magnitude => MathF.Sqrt(sqrMagnitude);
        internal Vector3 normalized => magnitude > 0 ? this * (1f / magnitude) : zero;
        internal static Vector3 MoveTowards(Vector3 from, Vector3 to, float length)
            => Distance(from,to) <= length ? to : from + (to-from).normalized * length;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x+b.x, a.y+b.y, a.z+b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x-b.x, a.y-b.y, a.z-b.z);
        public static Vector3 operator *(Vector3 a, float f) => new(a.x*f, a.y*f, a.z*f);
        internal static float Distance(Vector3 a, Vector3 b) => MathF.Sqrt((a-b).sqrMagnitude);
    }
    internal static class Mathf
    {
        internal const float PI = MathF.PI;
        internal static float Abs(float a) => MathF.Abs(a);
        internal static float Min(float a,float b) => MathF.Min(a,b);
        internal static int Max(int a,int b) => Math.Max(a,b);
        internal static float Max(float a,float b) => MathF.Max(a,b);
        internal static int Clamp(int value,int min,int max) => Math.Clamp(value,min,max);
        internal static int CeilToInt(float value) => (int)MathF.Ceiling(value);
        internal static float Sqrt(float value) => MathF.Sqrt(value);
        internal static float Cos(float a) => MathF.Cos(a);
        internal static float Sin(float a) => MathF.Sin(a);
    }
    internal class TerrainCollider { }
    internal record struct RaycastHit(Vector3 point, object collider);
    internal enum QueryTriggerInteraction { Ignore }
    internal static class Physics
    {
        internal static Func<Vector3,RaycastHit?> Ground = _ => null;
        internal static bool Raycast(Vector3 from,Vector3 direction,out RaycastHit hit,float range,int mask,QueryTriggerInteraction triggers)
        { var floor=Ground(from); hit=floor.GetValueOrDefault(); return floor.HasValue; }
        internal static Func<Vector3,bool> Blocked = _ => false;
        internal static bool CheckCapsule(Vector3 a, Vector3 b, float radius, int mask, QueryTriggerInteraction triggers)
            => Blocked(a);
    }
}
namespace UnityEngine.AI
{
    internal enum NavMeshPathStatus { PathComplete, PathPartial, PathInvalid }
    internal class NavMeshPath { internal Vector3[] corners; internal NavMeshPathStatus status; }
    internal record struct NavMeshHit(Vector3 position);
    internal static class NavMesh
    {
        internal const int AllAreas = -1;
        internal static Func<Vector3,float,Vector3?> Sample;
        internal static Func<Vector3,Vector3,NavMeshPath,bool> Path;
        internal static bool SamplePosition(Vector3 pos,out NavMeshHit hit,float radius,int mask)
        { var result=Sample(pos,radius); hit=new(result.GetValueOrDefault()); return result.HasValue; }
        internal static bool CalculatePath(Vector3 a,Vector3 b,int mask,NavMeshPath path) => Path(a,b,path);
    }
}
namespace EFT { internal static class LayersMaskController { internal const int HighPolyWithTerrainMask=1; } }
namespace Orbit.Settings
{
    internal class RushPoint { internal string Id="site", Name="Test", Kind="Custom", FloorId; internal float Radius=9; }
}
namespace Orbit.Entities
{
    internal partial class Agent
    {
        internal UnityEngine.Vector3 Position { get; set; }
        internal sealed record ProfileData(string Id);
        internal sealed record BotData(ProfileData Profile);
        internal sealed record SquadData(int Id);
        internal BotData Bot = new(new("actor"));
        internal SquadData Squad = new(1);
    }
}
namespace Orbit.Helpers
{
    internal static class PerformanceJournal
    {
        internal static bool Enabled => true;
        internal static void Event(string name, string actor, string detail, int squad) { }
    }
}
namespace Orbit
{
    internal static class Log
    {
        internal static bool InfoEnabled => true;
        internal static List<string> Messages = new();
        internal static void Info(string s) => Messages.Add(s);
    }
}
namespace Orbit.Systems
{
    public partial class WaypointSystem
    {
        private readonly string _zoneKey="test-map";
        private bool MatchesZoneFloor(string floor, UnityEngine.Vector3 p) => floor==null || floor=="upper" && p.y>5;
    }
}
