namespace Orbit.Looting;

/// <summary>
/// Looting design constants. Every tunable looting knob moved to the ORBIT server mod's config
/// (see Helpers.ServerConfig: Loot / General / PlayerScav sections, edited via the server web UI
/// at /orbit) - only true constants remain here.
/// </summary>
public static class LootConfig
{
    public const int LootTimeout = 180;

    // PMC fallback when the SAIN personality system is OFF / unresolved.
    public const float ExtractAtLootValuePmc = 500_000f;

    /// <summary>
    /// Swap threshold shared by every gear kind (weapon / armor / helmet / rig / backpack / headset):
    /// a candidate must score this multiple of the currently-equipped item to trigger a swap. The
    /// per-kind F12 toggles + margins were removed on purpose - the swap layer is always on, this is
    /// a design constant, not a tunable.
    /// </summary>
    public const float SwapMargin = 1.10f;
}
