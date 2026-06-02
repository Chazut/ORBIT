using Comfort.Common;
using EFT;
using EFT.InventoryLogic;

namespace Orbit.Looting;

public static class ItemPriceLookup
{
    public static float GetPrice(Item item)
    {
        if (item?.Template == null) return 0f;
        var handbook = Singleton<HandbookClass>.Instance;
        if (handbook == null) return 0f;
        try
        {
            return (float)handbook.GetBasePrice(item.Template._id);
        }
        catch
        {
            return 0f;
        }
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
