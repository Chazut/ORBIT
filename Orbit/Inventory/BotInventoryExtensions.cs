using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;

namespace Orbit.Inventory;

/// <summary>
/// Small extension helpers used by the loot pipeline. Closest-living-
/// player lookup (drives "who do I want to spread loot away from?")
/// and a non-LINQ first-item peek used during item-grid traversal.
/// </summary>
public static class BotInventoryExtensions
{
    public static IPlayer GetClosestPlayer(this IEnumerable<IPlayer> players, BotOwner botOwner)
    {
        if (players == null || !players.Any())
            return null;

        IPlayer closestPlayer = null;
        var closestDistance = float.MaxValue;

        foreach (var player in players)
        {
            if (!player.HealthController.IsAlive)
                continue;

            var distance = (botOwner.Position - player.Position).sqrMagnitude;

            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestPlayer = player;
            }
        }

        return closestPlayer;
    }

    public static Item GetFirstItem(this IEnumerable<Item> items)
    {
        if (items == null)
            return null;

        using var enumerator = items.GetEnumerator();
        return enumerator.MoveNext() ? enumerator.Current : null;
    }
}
