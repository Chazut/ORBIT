using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.HandBook;
using EFT.InventoryLogic;
using UnityEngine;

namespace Orbit.Inventory;

/// <summary>
/// Rouble valuation for items via SPT's handbook. Loaded async on raid
/// start (the handbook isn't fully ready until shortly after game world
/// init). Weapons can optionally be valued by the sum of their mods
/// rather than the bare weapon entry, which gives a much more realistic
/// price for upgraded guns — the bare lower-receiver handbook price is
/// typically only ~20% of a real weapon's value once you add a stock,
/// grip, sight, etc.
/// </summary>
public class ItemValuator
{
    public readonly Stopwatch LastPriceUpdate = Stopwatch.StartNew();

    public Dictionary<MongoID, HandbookData> HandbookData;

    public bool IsUpdatingPrices { get; private set; }

    public async Task UpdatePricesAsync()
    {
        IsUpdatingPrices = true;
        try
        {
            // Handbook is the only price source. The flea-market path was
            // gated on a config flag hardcoded to false; the entire market
            // branch + helper methods were removed along with it.
            HandbookData = Singleton<HandbookClass>.Instance.Items.ToDictionary(item => new MongoID(item.Id));
            if (HandbookData is null)
                Log.Error("ItemValuator: failed to get handbook data");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error($"ItemValuator: failed to get item prices — {ex}");
        }
        finally
        {
            LastPriceUpdate.Restart();
            IsUpdatingPrices = false;
        }
    }

    /// <summary>
    /// Rouble value of <paramref name="item"/>. Weapons return the sum of
    /// their mods when <see cref="LootConfig.ValueFromMods"/> is true
    /// (default) — bare lower-receiver pricing dramatically undervalues
    /// upgraded guns. Ammo boxes resolve via their cartridge type. The
    /// <paramref name="botLog"/> parameter is reserved for per-bot debug
    /// hooks; pass null at non-debug sites.
    /// </summary>
    public float GetItemPrice(Item item, object botLog = null)
    {
        if (item == null) return 0f;

        // Ammo box → value via its cartridge.
        if (item is AmmoBox box)
        {
            var ammoItem = box.Cartridges.Items.GetFirstItem();
            if (ammoItem != null)
                item = ammoItem;
        }

        if (HandbookData == null)
        {
            Log.Debug("ItemValuator: data is null");
            return 0f;
        }

        return item is Weapon weapon && LootConfig.ValueFromMods
            ? GetWeaponHandbookPrice(weapon)
            : GetItemHandbookPrice(item);
    }

    /// <summary>
    /// Sum of the weapon's mods' handbook prices. Approximates the
    /// real-world value far better than the bare receiver entry.
    /// </summary>
    public float GetWeaponHandbookPrice(Weapon lootWeapon)
    {
        var finalPrice = 0f;
        foreach (var weaponMod in lootWeapon.Mods)
            finalPrice += GetItemHandbookPrice(weaponMod);
        return finalPrice;
    }

    /// <summary>
    /// Per-stack handbook price (price × <see cref="Item.StackObjectsCount"/>).
    /// </summary>
    public float GetItemHandbookPrice(Item lootItem)
    {
        HandbookData.TryGetValue(lootItem.TemplateId, out var value);
        var price = value?.Price ?? 0f;
        price *= lootItem.StackObjectsCount;
        return Mathf.Max(0f, price);
    }
}
