using EFT.InventoryLogic;
using Diz.LanguageExtensions;

namespace Diz.LanguageExtensions
{
    public record StringError(string Message);
    public readonly struct OperationResult<T>
    {
        public readonly bool Succeeded;
        public readonly T Value;
        private OperationResult(T value) { Value = value; Succeeded = true; }
        public static implicit operator OperationResult<T>(T value) => new(value);
        public static implicit operator OperationResult<T>(StringError _) => default;
        public static implicit operator OperationResult<T>(NoFreeSpaceError _) => default;
    }
    public static class ResultExtensions
    {
        public static OperationResult<U> Cast<T,U>(this OperationResult<T> value) where T : U => value.Succeeded ? value.Value : default(OperationResult<U>);
    }
}
namespace EFT { }
namespace EFT.Interactive { public class LootItem { public Item Item; } }
namespace EFT.InventoryLogic
{
    public record NoFreeSpaceError(Item Item);
    public enum EquipmentSlot { Backpack, SecuredContainer }
    public enum ItemRotation { Horizontal, Vertical }
    public enum EItemPinLockState { Free, Locked }
    public record LocationInGrid(int x, int y, ItemRotation r);
    public interface IContainer
    {
        Item ParentItem { get; }
        bool TryFindLocationForItem(Item item, out ItemAddress address) { address = null; return false; }
    }
    public interface IItemOwner : IContainer {}
    public interface IAntiRMTItemController { IEnumerable<Item> GetItemsOverDiscardLimit(Item item); }
    public class Item
    {
        public string Name;
        public int W = 1, H = 1, StackObjectsCount = 1;
        public float Price = 10;
        public bool QuestItem, Protected;
        public EItemPinLockState PinLockState;
        public ItemAddress CurrentAddress;
        public object Owner => CurrentAddress?.Container is Grid g ? g.Owner : CurrentAddress?.Container;
        public (int X,int Y) CalculateRotatedSize(ItemRotation r) => r == ItemRotation.Horizontal ? (W,H) : (H,W);
        public string LocalizedName() => Name;
        public bool IsChildOf(Item root) => CurrentAddress?.Container.ParentItem is Item parent && (parent == root || parent.IsChildOf(root));
        public IEnumerable<Item> GetAllItems()
        {
            yield return this;
            if (this is CompoundItem c)
                foreach (var entry in c.Grids.SelectMany(g => g.Items).SelectMany(i => i.GetAllItems())) yield return entry;
        }
    }
    public class CompoundItem : Item { public Grid[] Grids = []; }
    public class Equipment : CompoundItem
    {
        public readonly Dictionary<EquipmentSlot,Slot> Slots = new();
        public Slot GetSlot(EquipmentSlot slot) => Slots.GetValueOrDefault(slot);
        public IEnumerable<IContainer> GetPrioritizedContainersForLoot(Item item) => Slots.Values.SelectMany(s => ((CompoundItem)s.ContainedItem).Grids);
    }
    public class Slot : IContainer { public Item ParentItem {get; init;} public Item ContainedItem; }
    public record ItemAddress(IContainer Container);
    public record GridItemAddress(Grid Grid, LocationInGrid Location) : ItemAddress(Grid) { public LocationInGrid LocationInGrid => Location; }
    public class Grid : IContainer
    {
        public Item ParentItem {get; init;}
        public object Owner;
        public int GridWidth, GridHeight;
        public List<Item> Items = new();
        public bool CheckCompatibility(Item item) => true;
        public LocationInGrid GetItemLocation(Item item) => ((GridItemAddress)item.CurrentAddress).Location;
        public GridItemAddress CreateItemAddress(LocationInGrid pos) => new(this,pos);
        public void Put(Item item, LocationInGrid pos) { Items.Add(item); item.CurrentAddress = CreateItemAddress(pos); }
        public OperationResult<ContainerRemoveResult> Remove(Item item,bool simulate)
        {
            var old = GetItemLocation(item);
            if (!simulate) { Items.Remove(item); item.CurrentAddress = null; }
            return new ContainerRemoveResult(() => Put(item,old));
        }
        public bool Fits(Item item, LocationInGrid p)
        {
            var (w,h) = item.CalculateRotatedSize(p.r);
            if (p.x+w > GridWidth || p.y+h > GridHeight) return false;
            return !Items.Any(i => {
                var b = GetItemLocation(i); var s = i.CalculateRotatedSize(b.r);
                return i != item && p.x < b.x+s.X && p.x+w > b.x && p.y < b.y+s.Y && p.y+h > b.y;
            });
        }
        public bool TryFindLocationForItem(Item item,out ItemAddress address)
        {
            foreach (var rot in Enum.GetValues<ItemRotation>())
            for (int y=0;y<GridHeight;y++) for (int x=0;x<GridWidth;x++)
            {
                var p=new LocationInGrid(x,y,rot);
                if (Fits(item,p)) { address=CreateItemAddress(p); return true; }
            }
            address=null; return false;
        }
    }
    public record ContainerRemoveResult(Action RollBack);
    public interface IOperationResult {}
    public interface IItemOperationResult : IOperationResult {}
    public interface ITransferOrMergeResult : IItemOperationResult {}
    public record MoveResult(Item Item,ItemAddress To) : IItemOperationResult { public bool ItemsDestroyRequired => false; }
    public record SwapResult(ItemAddress To,ItemAddress To2) : IOperationResult;
    public record MergeResult(Item TargetItem) : ITransferOrMergeResult;
    public record TransferResult(Item TargetItem) : ITransferOrMergeResult;
    public record SortResult(Action RollBack, Action Execute) : IOperationResult;
    public record Reply(bool Succeed);
    public class InventoryController : IAntiRMTItemController
    {
        public InventoryController Inventory => this;
        public Equipment Equipment = new();
        public List<Item> Dropped = new();
        public Item Reject, ForbiddenDrop;
        public Action AfterThrow;
        public Func<SortResult> Sort;
        public bool CanThrow(Item item) => true;
        public IEnumerable<Item> GetItemsOverDiscardLimit(Item item) => item == ForbiddenDrop ? [item] : [];
        public void ThrowItem(Item item,bool downDirection,Action<Reply> callback)
        {
            ((Grid)item.CurrentAddress.Container).Remove(item,false);
            Dropped.Add(item); AfterThrow?.Invoke(); callback(new(true));
        }
        public Task<Reply> TryRunNetworkTransaction<T>(OperationResult<T> op, object callback)
        {
            if (op.Value is SortResult sort) { sort.Execute(); return Task.FromResult(new Reply(true)); }
            if (op.Value is MoveResult move && move.To is GridItemAddress a && a.Grid.Fits(move.Item,a.Location))
            {
                if (move.Item.CurrentAddress?.Container is Grid old) old.Remove(move.Item,false);
                a.Grid.Put(move.Item,a.Location);
                return Task.FromResult(new Reply(true));
            }
            return Task.FromResult(new Reply(false));
        }
    }
    public static class ItemManipulator
    {
        public enum EMoveItemOrder { PickUp }
        public static OperationResult<IItemOperationResult> QuickFindAppropriatePlace(Item item,InventoryController ic,Equipment[] targets,EMoveItemOrder order,bool simulate)
        {
            foreach (var c in ic.Equipment.GetPrioritizedContainersForLoot(item))
                if(c.TryFindLocationForItem(item,out var pos)) return Move(item,pos,ic,simulate).Cast<MoveResult,IItemOperationResult>();
            return default;
        }
        public static OperationResult<MoveResult> Move(Item item,ItemAddress to,InventoryController ic,bool simulate) =>
            ic.Reject != item && to is GridItemAddress a && a.Grid.Fits(item,a.Location) ? new MoveResult(item,to) : default(OperationResult<MoveResult>);
        public static bool TryFindMergeableItem(List<IContainer> c,Item item,out Item stack,int count) { stack=null; return false; }
        public static OperationResult<ITransferOrMergeResult> TransferOrMerge(Item item, Item stack, InventoryController ic,bool simulate) => default;
        public static OperationResult<SortResult> Sort(CompoundItem bag,InventoryController ic,bool simulate) =>
            ic.Sort != null ? ic.Sort() : default(OperationResult<SortResult>);
    }
}
namespace Orbit.Looting
{
    internal static class Log { public static bool DebugEnabled => false; public static void Debug(string _) {} }
    internal static class ItemPriceLookup { public static float GetPrice(Item i) => i.Price; }
    internal static class PackNStrapCompat { public static bool IsProtected(Item i) => i.Protected; }
    internal static class LootWorkBudget { public static Task Checkpoint(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } }
    internal static class LootActorGuard {
        public static Task<T> TrackTransaction<T>(object bot,Task<T> work) => work;
        public static Task<T> AwaitTransaction<T>(Task<T> work,CancellationToken ct) => work.WaitAsync(ct);
    }
    internal class LootInventoryBusyException : Exception {}
    internal enum LootKind { Item, Container, Corpse }
    public partial class OrbitLootHandler
    {
        internal LootKind CurrentTargetKind;
        internal object CurrentTarget;
        internal Item _currentSourceRoot;
        internal bool DormantMode, NeedsAwakeInventory;
        readonly object _bot=new();
        readonly BodyGate _bodyGate=new();
        const string Nick="test";
        internal readonly StatsModel Stats=new();
        internal class StatsModel { public float InventoryValue, TotalGained; public float SpawnValue = 0; }
        class BodyGate { public IDisposable Enter(bool physical) => null; }
        void CheckSession(CancellationToken ct) => ct.ThrowIfCancellationRequested();
        void EnsureInventoryAvailable(CancellationToken ct) => CheckSession(ct);
        void ReportInventoryProgress(CancellationToken ct) {}
        void ReportInventoryFailure(CancellationToken ct,bool retryable) {}
        string DescribeFreeSpace(InventoryController ic) => "";
        internal Task<OperationResult<IItemOperationResult>> Prepare(Item item, InventoryController ic, CancellationToken ct = default)
            => FindCargoPlaceAsync(item,ic,ct);
    }
}
