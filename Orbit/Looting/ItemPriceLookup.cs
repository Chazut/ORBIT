using Comfort.Common;
using EFT;
using EFT.InventoryLogic;

namespace Orbit.Looting;

public static class ItemPriceLookup
{
    private static readonly System.Collections.Generic.Dictionary<string, float> Prices = new();
    private static object _source;
    internal static void Reset() { Prices.Clear(); _source = null; }

    public static float GetPrice(Item item)
    {
        if (item?.Template == null) return 0f;
        // Normal clients: read EFT's handbook directly (unchanged). On a FIKA headless client this Instance
        // is never created (issue #5), so fall back to the server-fetched price cache populated at init.
        var handbook = Singleton<EFT.HandBook.Handbook>.Instance;
        if (!ReferenceEquals(_source, handbook)) { Prices.Clear(); _source = handbook; }
        var id = item.Template._id;
        if (id == null) return 0f;
        if (Prices.TryGetValue(id, out var cached)) return cached;
        if (handbook != null)
        {
            try { return Prices[id] = (float)handbook.GetBasePrice(id); }
            catch { /* fall through to the cache */ }
        }
        if (HandbookPriceCache.TryGet(id, out var price)) return Prices[id] = price;
        return 0f;
    }

    public static float GetPricePerSlot(Item item)
    {
        var price = GetPrice(item);
        if (price <= 0f || item == null) return 0f;
        var slots = item.Width * item.Height;
        if (slots <= 0) slots = 1;
        return price / slots;
    }

    public static float SumInventoryWorth(BotOwner bot)
    {
        var equipment = bot?.GetPlayer?.Profile?.Inventory?.Equipment;
        if (equipment == null) return 0f;
        var sum = 0f;
        foreach (var item in equipment.GetAllItems())
        {
            sum += GetPrice(item);
        }
        return sum;
    }
}
