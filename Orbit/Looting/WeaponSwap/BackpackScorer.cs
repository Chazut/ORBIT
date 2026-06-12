using EFT.InventoryLogic;

namespace Orbit.Looting.WeaponSwap;

/// <summary>
/// Score backpacks for the swap decision. Capacity (sum of grid cells) is the signal — a bigger bag means
/// more loot carried to extract. Price only tie-breaks between same-size bags (a Tri-Zip beats a same-size
/// scav bag for resale, nothing more).
/// </summary>
public static class BackpackScorer
{
    private const float CellWeight = 5f;
    private const float PriceTieBreakerWeight = 0.00002f;

    public static float Score(Item backpack, string logTag = null)
    {
        if (backpack == null) return 0f;
        var cells = RigScorer.TotalCells(backpack);
        var cellsScore = cells * CellWeight;

        var price = ItemPriceLookup.GetPrice(backpack);
        var priceTie = price * PriceTieBreakerWeight;

        var total = cellsScore + priceTie;

        if (!string.IsNullOrEmpty(logTag))
            Orbit.Log.Debug($"BackpackScorer[{logTag}] {backpack.LocalizedName()}: cells={cells} → cellsScore={cellsScore:F1} priceTie={priceTie:F1} = {total:F1}");

        return total;
    }
}
