namespace Orbit.Inventory;

/// <summary>
/// Three-way discriminator for what the loot brain is currently working
/// on. Originally a nested enum on the upstream loot-finder; pulled out
/// to its own type so it survives the autonomous-finder removal (loot
/// POIs are produced by the waypoint system now, not by per-frame scans).
/// </summary>
public enum LootKind : byte
{
    None = 0,
    Corpse = 1,
    Container = 2,
    Item = 3,
}
