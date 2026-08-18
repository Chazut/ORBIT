using EFT;
using EFT.InventoryLogic;

namespace Orbit.Looting.WeaponSwap;

/// <summary>
/// Score audio headsets (Earpiece slot) for the swap decision. Handbook price alone — headsets all do
/// roughly the same job (boost the bot's hearing pickup), and price tracks quality closely enough in
/// vanilla EFT (ComTac &gt; Sordin &gt; GSSH-01 &gt; basic). Stat-based scoring (Distortion / Compressor /
/// HighFrequenciesGain on HeadphonesTemplateClass) is possible but not worth the indirection at this
/// scope.
/// </summary>
public static class HeadsetScorer
{
    public static float Score(Item headset, string logTag = null)
    {
        if (headset == null) return 0f;
        var price = ItemPriceLookup.GetPrice(headset);

        if (!string.IsNullOrEmpty(logTag))
            Orbit.Log.Debug($"HeadsetScorer[{logTag}] {headset.LocalizedName()}: price={price:N0}₽ = {price:F1}");

        return price;
    }
}
