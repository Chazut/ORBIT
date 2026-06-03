namespace Orbit.Looting;

public class LootStats
{
    public float TotalGained;
    public float InventoryValue;
    public float SpawnValue;
    public int FreeGridCells;
    public bool LastItemsTaken;

    /// <summary>
    /// Highest non-bypass per-slot price inspected during the last loot
    /// session. Drives the post-loot smart blacklist: squad members whose
    /// own threshold exceeds this value would also reject the POI.
    /// </summary>
    public float LastMaxPerSlotSeen;

    /// <summary>
    /// True if at least one bypass item (currency / frag / dogtag) was
    /// present in the last session. When set, a no-take is treated as a
    /// transient transaction failure rather than a threshold rejection.
    /// </summary>
    public bool LastHadBypassItem;
}
