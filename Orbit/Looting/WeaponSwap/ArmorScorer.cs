using EFT.InventoryLogic;

namespace Orbit.Looting.WeaponSwap;

/// <summary>
/// Score body armor and helmets for the swap decision. Armor class dominates the result (1-6 BSG classes scale
/// to 100 points each), durability ratio contributes a smaller bonus, handbook price acts as a tie-breaker.
/// Items with no ArmorComponent (cosmetic-only hats, balaclavas, etc.) return 0.
/// </summary>
public static class ArmorScorer
{
    private const float ArmorClassWeight = 100f;
    private const float DurabilityWeight = 30f;
    private const float PriceTieBreakerWeight = 0.00002f;

    public static float Score(Item item, string logTag = null)
    {
        if (item == null) return 0f;
        var armor = item.GetItemComponent<ArmorComponent>();
        if (armor == null) return 0f;

        var armorClass = armor.ArmorClass;
        var classScore = armorClass * ArmorClassWeight;

        var repairable = item.GetItemComponent<RepairableComponent>();
        var durabilityRatio = 1f;
        if (repairable != null && repairable.MaxDurability > 0f)
            durabilityRatio = UnityEngine.Mathf.Clamp01(repairable.Durability / repairable.MaxDurability);
        var durabilityScore = durabilityRatio * DurabilityWeight;

        var price = ItemPriceLookup.GetPrice(item);
        var priceTie = price * PriceTieBreakerWeight;

        var total = classScore + durabilityScore + priceTie;

        if (!string.IsNullOrEmpty(logTag))
            Orbit.Log.Debug($"ArmorScorer[{logTag}] {item.LocalizedName()}: class={armorClass} durRatio={durabilityRatio:F2} → classScore={classScore:F1} dur={durabilityScore:F1} priceTie={priceTie:F1} = {total:F1}");

        return total;
    }
}
