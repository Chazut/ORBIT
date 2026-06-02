using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Looting;

public class OrbitLootHandler : MonoBehaviour, ILootHandler
{
    private BotOwner _bot;
    private CancellationTokenSource _cts;

    public LootStats Stats { get; } = new();
    public bool LootTaskRunning { get; private set; }

    public InteractableObject ActiveLoot { get; set; }
    public LootKind ActiveLootType { get; set; } = LootKind.None;
    public Vector3 Destination { get; set; }
    public Vector3 LootObjectPosition { get; set; }
    public bool ForceBrainEnabled { get; set; }

    public void Init(BotOwner bot)
    {
        _bot = bot;
    }

    public void StartLooting()
    {
        if (LootTaskRunning) return;
        if (_bot == null || ActiveLoot == null || ActiveLootType == LootKind.None) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        LootTaskRunning = true;
        Stats.LastItemsTaken = false;

        var loot = ActiveLoot;
        var kind = ActiveLootType;
        var ct = _cts.Token;

        _ = RunAsync(loot, kind, ct);
    }

    private async Task RunAsync(InteractableObject loot, LootKind kind, CancellationToken ct)
    {
        try
        {
            switch (kind)
            {
                case LootKind.Container:
                    if (loot is LootableContainer container)
                        await LootContainerAsync(container, ct);
                    break;
                case LootKind.Corpse:
                    if (loot is Corpse corpse)
                        await LootCorpseAsync(corpse, ct);
                    break;
                case LootKind.Item:
                    if (loot is LootItem lootItem)
                        await LootLooseItemAsync(lootItem, ct);
                    break;
            }
        }
        catch (System.OperationCanceledException)
        {
            // cancelled mid-loot — normal during combat takeover
        }
        catch (System.Exception e)
        {
            Log.Warning($"OrbitLootHandler.RunAsync failed ({kind}): {e.Message}");
        }
        finally
        {
            LootTaskRunning = false;
        }
    }

    public void StopLooting()
    {
        _cts?.Cancel();
        LootTaskRunning = false;
    }

    public void Cancel()
    {
        _cts?.Cancel();
        LootTaskRunning = false;
    }

    private async Task LootContainerAsync(LootableContainer container, CancellationToken ct)
    {
        if (container.DoorState != EDoorState.Open)
        {
            _bot.LootOpener.Interact(container, EInteractionType.Open);
            await Task.Delay(2500, ct);
        }

        var rootItem = container.ItemOwner?.RootItem;
        if (rootItem != null)
        {
            var items = rootItem.GetAllItems().Where(i => i != rootItem).ToList();
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await PickupItemAsync(item, ct);
            }
        }

        try
        {
            _bot.LootOpener.Interact(container, EInteractionType.Close);
        }
        catch (System.Exception e)
        {
            Log.Debug($"OrbitLootHandler: failed to close container {container.name}: {e.Message}");
        }
    }

    private async Task LootCorpseAsync(Corpse corpse, CancellationToken ct)
    {
        await Task.Delay(2000, ct);

        var rootItem = corpse.Item;
        if (rootItem == null) return;

        var items = rootItem.GetAllItems().Where(i => i != rootItem).ToList();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            await PickupItemAsync(item, ct);
        }
    }

    private async Task LootLooseItemAsync(LootItem lootItem, CancellationToken ct)
    {
        if (lootItem.Item == null) return;
        await Task.Delay(500, ct);
        await PickupItemAsync(lootItem.Item, ct);
    }

    private async Task<bool> PickupItemAsync(Item item, CancellationToken ct)
    {
        var inventoryController = _bot.GetPlayer?.InventoryController;
        if (inventoryController == null || item == null) return false;

        try
        {
            var targets = inventoryController.Inventory.Equipment.ToEnumerable<InventoryEquipment>();
            var place = InteractionsHandlerClass.QuickFindAppropriatePlace(
                item, inventoryController, targets,
                InteractionsHandlerClass.EMoveItemOrder.PickUp, false);
            if (!place.Succeeded) return false;

            var result = await inventoryController.TryRunNetworkTransaction(place, null);
            if (result.Succeed)
            {
                Stats.LastItemsTaken = true;
                return true;
            }
        }
        catch (System.OperationCanceledException) { throw; }
        catch (System.Exception e)
        {
            Log.Debug($"OrbitLootHandler.PickupItemAsync({item.LocalizedName()}): {e.Message}");
        }
        return false;
    }
}
