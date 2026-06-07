using EFT.InventoryLogic;

namespace Orbit.Looting.WeaponSwap;

/// <summary>
/// Score body rigs (TacticalVest items) for the swap decision. Capacity (sum of grid cells) is the main
/// signal for simple rigs; armored rigs add an armor-class bonus that dwarfs the capacity term so a class-5
/// armored rig beats a class-3 armored rig regardless of cell count.
/// </summary>
public static class RigScorer
{
    private const float CellWeight = 5f;
    private const float ArmorClassWeight = 100f;
    private const float PriceTieBreakerWeight = 0.00002f;

    public static float Score(Item rig, string logTag = null)
    {
        if (rig == null) return 0f;
        var cells = TotalCells(rig);
        var cellsScore = cells * CellWeight;

        var armor = rig.GetItemComponent<ArmorComponent>();
        var armorClassScore = armor != null ? armor.ArmorClass * ArmorClassWeight : 0f;

        var price = ItemPriceLookup.GetPrice(rig);
        var priceTie = price * PriceTieBreakerWeight;

        var total = cellsScore + armorClassScore + priceTie;

        if (!string.IsNullOrEmpty(logTag))
            Orbit.Log.Debug($"RigScorer[{logTag}] {rig.LocalizedName()}: cells={cells} armorClass={(armor?.ArmorClass ?? 0)} → cellsScore={cellsScore:F1} armorScore={armorClassScore:F1} priceTie={priceTie:F1} = {total:F1}");

        return total;
    }

    public static bool IsArmoredRig(Item rig)
        => rig != null && rig.GetItemComponent<ArmorComponent>() != null;

    public static int TotalCells(Item rig)
    {
        if (rig is not CompoundItem compound || compound.Grids == null) return 0;
        var total = 0;
        for (var i = 0; i < compound.Grids.Length; i++)
        {
            var g = compound.Grids[i];
            if (g == null) continue;
            total += g.GridWidth * g.GridHeight;
        }
        return total;
    }

    /// <summary>
    /// Number of cells currently occupied by items in this rig's grids. Used for the capacity check on a
    /// swap — if the candidate rig has fewer total cells than the current rig's used cells, the swap is
    /// skipped to avoid losing items.
    /// </summary>
    public static int UsedCells(Item rig)
    {
        if (rig is not CompoundItem compound || compound.Grids == null) return 0;
        var used = 0;
        for (var g = 0; g < compound.Grids.Length; g++)
        {
            var grid = compound.Grids[g];
            if (grid?.Items == null) continue;
            foreach (var item in grid.Items)
            {
                if (item == null) continue;
                used += item.Width * item.Height;
            }
        }
        return used;
    }
}
