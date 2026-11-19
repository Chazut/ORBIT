using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace UnityEngine
{
    public struct Vector3(float x, float y, float z)
    {
        public float x = x, y = y, z = z;
        public readonly float sqrMagnitude => x * x + y * y + z * z;
        public readonly Vector3 normalized => sqrMagnitude > 0 ? this * (1f / MathF.Sqrt(sqrMagnitude)) : default;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float f) => new(a.x * f, a.y * f, a.z * f);
        public override readonly string ToString() => $"({x},{y},{z})";
    }
    internal static class Mathf { public static float Clamp01(float n) => Math.Clamp(n, 0, 1); }
    internal static class Time { public static int frameCount; public static float time; }
}
namespace UnityEngine.AI
{
    public enum NavMeshPathStatus { PathComplete, PathPartial, PathInvalid }
    internal static class NavMesh
    {
        internal const int AllAreas = -1;
        internal record struct Hit(Vector3 position);
        internal static bool SamplePosition(Vector3 pivot, out Hit hit, float radius, int areas)
        {
            hit = new Hit(pivot);
            return true;
        }
    }
}
namespace EFT
{
    public class Grenade(int id)
    {
        public record Item(string ThrowType, string StringTemplateId);
        public record Transform(Vector3 position);
        public Item WeaponSource { get; set; } = new("smoke_grenade", "test-template");
        public Transform transform { get; set; } = new(default);
        public int GetInstanceID() => id;
    }
}
namespace SAIN
{
    public static class SAINEnableClass
    {
        public static readonly Dictionary<string, Components.BotComponent> Bots = new();
        public static bool GetSAIN(string id, out Components.BotComponent bot) => Bots.TryGetValue(id, out bot);
    }
}
namespace SAIN.Components
{
    public class BotComponent
    {
        public Decisions Decision { get; } = new();
        public Grenades Grenade { get; } = new();
    }
    public enum CombatDecision { DogFight, AvoidGrenade }
    public class Decisions { public CombatDecision CurrentCombatDecision { get; set; } }
    public class Grenades { public Reactions GrenadeReactionClass { get; } = new(); }
    public class Reactions { public Tracker DangerGrenade { get; set; } }
    public class Tracker
    {
        public EFT.Grenade Grenade { get; set; }
        public Vector3 DangerPoint { get; set; }
        public float TimeThrown { get; set; }
    }
}
namespace Orbit
{
    internal static class Log
    {
        internal static bool InfoEnabled = true;
        internal static readonly List<string> Lines = new();
        internal static void Info(string line) => Lines.Add(line);
        internal static void Warning(string line) => Lines.Add(line);
    }
}
namespace Orbit.Entities
{
    internal enum MovementStatus { Moving, Stopped }
    internal class Movement { internal MovementStatus Status = MovementStatus.Moving; }
    internal class Agent
    {
        internal Movement Movement = new();
        internal record Owner(string ProfileId);
        internal Owner Bot = new("test-bot");
        internal Vector3 Position;
        internal GrenadeInterruptions GrenadeInterruptions;
        internal bool IsDormant;
        internal Squad Squad;
    }
    internal class Squad { internal Rush Rush = new(); }
    internal class Rush { internal Sniper Sniper = new(); }
    internal class Sniper { internal bool IsPostShift(Agent agent) => true; }
}
namespace Orbit.Navigation
{
    internal class NavJob(Vector3 origin, Vector3 target)
    {
        internal Vector3 Target = target;
        internal Vector3[] Path = [origin, target];
        internal NavMeshPathStatus Status = NavMeshPathStatus.PathComplete;
        internal object Travel = new();
    }
    internal static class DangerZones
    {
        internal static Func<Vector3, bool> IsInside = _ => false;
    }
    internal static class DirectionalMineSafety
    {
        internal static Func<Vector3[], bool> Block = _ => false;
        internal static bool Blocks(Vector3 from, Vector3[] corners, out int mine)
        {
            mine = 0;
            return Block(corners);
        }
    }
}
namespace Orbit.Systems
{
    internal static class BotGroundPlacement
    {
        internal static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
    }
    internal class TravelWorld
    {
        internal int Queries;
        internal Func<Vector3, Vector3, Vector3[]> CompletePath = (a, b) => [a, b];
        internal Vector3[] Path(Vector3 from, Vector3 to)
        {
            Queries++;
            return CompletePath(from, to);
        }
    }
    public partial class MovementSystem
    {
        private const float TargetEpsSqr = 2.25f;
        internal readonly TravelWorld _travelWorld = new();
        internal bool Resolve(Agent agent, NavJob job, int revision = 1) => ResolveGrenadeRoute(agent, job, revision);
    }
}
