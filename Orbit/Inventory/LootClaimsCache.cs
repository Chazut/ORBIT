using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;

namespace Orbit.Inventory;

/// <summary>
/// Tracks which bot is actively interacting with which lootable object,
/// keyed on the container/corpse string id. Prevents two bots from
/// converging on the same container, and gives the human-player list a
/// cheap O(1) lookup for "is this loot held by an ORBIT bot".
/// </summary>
public static class LootClaimsCache
{
    /// <summary>Snapshot of live human players, refreshed at raid start.</summary>
    public static List<IPlayer> ActivePlayers { get; } = [];

    /// <summary>Live container/corpse id → claiming bot.</summary>
    public static Dictionary<string, BotOwner> ActiveLoot { get; } = [];

    public static void Init()
    {
        if (ActivePlayers.Count > 0) return;

        foreach (var player in Singleton<GameWorld>.Instance.RegisteredPlayers)
        {
            if (player.IsAI) continue;
            if (!player.HealthController.IsAlive) continue;
            ActivePlayers.Add(player);
        }
    }

    public static void Reset()
    {
        ActiveLoot.Clear();
        ActivePlayers.Clear();
    }

    public static bool CacheActiveLootId(string containerId, BotOwner botOwner)
        => !string.IsNullOrEmpty(botOwner.name)
            && !string.IsNullOrEmpty(containerId)
            && ActiveLoot.TryAdd(containerId, botOwner);

    public static bool IsLootInUse(string lootId) => ActiveLoot.ContainsKey(lootId);

    // Scratch buffer to avoid allocating during the linear scan in Cleanup.
    private static readonly List<string> _keysToRemoveScratch = [];

    public static void Cleanup(BotOwner botOwner)
    {
        try
        {
            if (botOwner == null || botOwner.name == null)
            {
                Log.Error("LootClaimsCache: cleanup issued on a bot with no name");
                return;
            }

            _keysToRemoveScratch.Clear();

            foreach (var keyValue in ActiveLoot)
            {
                // Defensive: the cached BotOwner may have been GC'd or
                // come in with a null name during teardown.
                if (keyValue.Value == null || keyValue.Value.name == null)
                {
                    Log.Error("LootClaimsCache: bot in claims cache has no name");
                    _keysToRemoveScratch.Add(keyValue.Key);
                    continue;
                }

                if (keyValue.Value.name == botOwner.name)
                    _keysToRemoveScratch.Add(keyValue.Key);
            }

            foreach (var key in _keysToRemoveScratch)
                ActiveLoot.Remove(key);
        }
        catch (Exception e)
        {
            Log.Error($"LootClaimsCache.Cleanup failed: {e}");
        }
    }
}
