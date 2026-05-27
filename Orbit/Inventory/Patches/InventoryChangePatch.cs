using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Orbit.Core;
using SPT.Reflection.Patching;

namespace Orbit.Inventory.Patches;

/// <summary>
/// Fires whenever any Player (human OR bot) picks up an item. We use this
/// to prune the matching LooseLoot waypoint from WaypointSystem so other
/// bots don't path to an empty spot. Covers three cases the dispatcher
/// can't see on its own:
///   1. Human player grabs a loose item.
///   2. Another mod (or vanilla AI) moves an item out of the world.
///   3. A bot loots via our pipeline — the hook still fires, but
///      RemoveLooseLootByItemId is idempotent so the duplicate is cheap.
/// </summary>
public class InventoryChangePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod(nameof(Player.OnItemAddedOrRemoved), BindingFlags.Instance | BindingFlags.Public);
    }

    [PatchPostfix]
    public static void Postfix(Item item, bool added)
    {
        if (!added || item == null) return;

        var manager = Singleton<OrbitManager>.Instance;
        if (manager?.WaypointSystem == null) return;

        manager.WaypointSystem.RemoveLooseLootByItemId(item.Id);
    }
}
