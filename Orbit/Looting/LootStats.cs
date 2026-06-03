namespace Orbit.Looting;

public class LootStats
{
    public float Looted;
    public float NetWorth;
    public float InitialNetWorth;
    public int AvailableGridSpaces;
    public bool LastItemsTaken;

    /// <summary>
    /// Highest per-slot price the looter inspected during the last loot
    /// session (across normal-gate items, not bypass items). 0 if the loot
    /// session saw zero non-bypass items. Used to decide which squad
    /// members would also have rejected this POI by their own threshold,
    /// so we can per-agent-blacklist them without preventing softer-gated
    /// members (Rat / Normal) from coming to clean up.
    /// </summary>
    public float LastMaxPerSlotSeen;

    /// <summary>
    /// True if at least one value-gate-bypass item (currency / frag
    /// grenade) was present in the last loot session. When set, smart
    /// per-member blacklist is skipped — bypass items are universally
    /// attractive and the no-take must be a transient cause (inventory
    /// full, transaction error) rather than a threshold rejection.
    /// </summary>
    public bool LastHadBypassItem;
}
