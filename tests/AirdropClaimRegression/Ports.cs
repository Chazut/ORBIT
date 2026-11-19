using EFT.Interactive;
using Orbit.Navigation;

namespace EFT.Interactive
{
    public sealed class LootableContainer
    {
        public bool Destroyed;
        public readonly Transform transform = new();
        public sealed class Transform { public int position; }

        // Unity objects compare equal to null after the native object is destroyed.
        public static bool operator ==(LootableContainer a, LootableContainer b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null) return b.Destroyed;
            return b is null && a.Destroyed;
        }

        public static bool operator !=(LootableContainer a, LootableContainer b) => !(a == b);
        public override bool Equals(object obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => base.GetHashCode();
    }
}

namespace UnityEngine.AI
{
    internal static class NavMesh
    {
        internal const int AllAreas = -1;
        internal static bool Reachable = true;
        internal struct Hit { internal int position; }
        internal static bool SamplePosition(int position, out Hit hit, float radius, int mask)
        {
            hit = new Hit { position = position };
            return Reachable;
        }
    }
}

namespace Orbit.Navigation
{
    public enum WaypointCategory { ContainerLoot }
    public class Waypoint
    {
        public readonly int Id;
        public bool IsAirdrop;
        public Waypoint(int id, WaypointCategory category, string name, int position, float radius,
            List<object> doors, List<object> covers, LootableContainer target) => Id = id;
    }
}

namespace Orbit
{
    internal static class Log
    {
        internal static readonly List<string> Events = new();
        internal static void Debug(string message) { }
        internal static void Info(string message) => Events.Add(message);
    }
}

namespace Orbit.Systems
{
    public partial class WaypointSystem
    {
        private int _nextId = 1000000;
        private readonly Dictionary<int, int> _waypointCells = new();
        private int NewRuntimeWaypointId() => ++_nextId;
        private void RegisterAmbushAirdrop(LootableContainer container) { }
        internal Waypoint Discover(LootableContainer container) => AirdropPoint(container);
        internal bool Retire(int id) => RetireLandedAirdrop(id);
        internal bool InGrid(int id) => _waypointCells.ContainsKey(id);
        internal void AddGridPoint(int id) => _waypointCells.Add(id, 0);
        internal void Prune() => PruneDestroyedAirdrops();
    }
}
