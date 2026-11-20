namespace BepInEx.Bootstrap
{
    public static class Chainloader
    {
        public static readonly Dictionary<string, object> PluginInfos = new();
    }
}

namespace Comfort.Common
{
    public static class Singleton<T> where T : class
    {
        public static T Instance;
    }
}

namespace EFT
{
    public class Player { public bool IsAI; }
    public class GameWorld
    {
        public readonly Dictionary<string, Player> Victims = new();
        public Player GetEverExistedPlayerByID(string id) => Victims.GetValueOrDefault(id);
    }
}

namespace EFT.InventoryLogic
{
    public enum EquipmentSlot { ArmBand }
    public class Slot { public Item ContainedItem; }
    public class Item
    {
        public Item Parent;
        public virtual object Owner => Parent?.Owner;
        public bool IsChildOf(Item item)
        {
            for (var parent = Parent; parent != null; parent = parent.Parent)
                if (parent == item) return true;
            return false;
        }
    }
    public class CompoundItem : Item { }
    public class InventoryEquipment : CompoundItem
    {
        public readonly Slot ArmBand = new();
        public object Controller;
        public override object Owner => Controller;
        public Slot GetSlot(EquipmentSlot slot) => ArmBand;
    }
    public class CorpseItemController { public string KilledProfileID; public Item RootItem; }
    public interface IOperationResult { }
    public interface IItemOperationResult : IOperationResult { Item Item { get; } }
    public record Pickup(Item Item) : IItemOperationResult;
    public record SwapResult(Item Item, Item Item2) : IOperationResult;
}

namespace PackNStrap.Core.Items
{
    public class CustomBeltItemClass : EFT.InventoryLogic.CompoundItem { }
}
