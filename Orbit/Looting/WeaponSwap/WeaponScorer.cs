using System.Collections.Generic;
using EFT.InventoryLogic;
using UnityEngine;

namespace Orbit.Looting.WeaponSwap;

/// <summary>
/// Multi-factor weapon comparison used by the swap decision. Combines available ammo count (compatible mags +
/// loose rounds), best reachable ammo quality (pen × 3 + dmg × 0.5), weapon stats (ergo, recoil, sighting
/// range — weighted per map profile), and handbook price as a tie-breaker. Bricks (no usable ammo) collapse
/// to ~0.
/// </summary>
public static class WeaponScorer
{
    private const float NoAmmoFloor = 0.05f;
    private const float AmmoCountWeight = 8f;
    private const int AmmoCountCap = 200;
    private const float PriceTieBreakerWeight = 0.00002f;
    private const float PenWeight = 3.0f;
    private const float DamageWeight = 0.5f;
    private const float LowPenThreshold = 20f;
    private const float LowPenPenalty = 30f;
    private const float ShotgunRecoilFactor = 0.5f;
    private const float SemiAutoRecoilFactor = 0.7f;

    /// <summary>
    /// Compute a comparable score for <paramref name="weapon"/>, drawing usable ammo from <paramref
    /// name="ammoSourceItems"/>. The same source is reused for the bot's current weapons (full equipment +
    /// secure container) and for the candidate (corpse / loose item's full subtree).
    /// </summary>
    public static float Score(Weapon weapon, IEnumerable<Item> ammoSourceItems, WeaponWeights weights, string logTag = null)
    {
        if (weapon == null) return 0f;

        var ammoBag = CollectUsableAmmo(weapon, ammoSourceItems);
        var cappedRounds = System.Math.Min(ammoBag.TotalRounds, AmmoCountCap);
        var ammoCountScore = Mathf.Log(1 + cappedRounds) * AmmoCountWeight;
        var ammoQuality = ammoBag.BestPenetration * PenWeight + ammoBag.BestDamage * DamageWeight;
        var lowPenPenalty = ammoBag.BestPenetration < LowPenThreshold ? LowPenPenalty : 0f;

        var ergo = weapon.ErgonomicsTotal;
        var recoil = weapon.RecoilTotal;
        var sighting = weapon.GetSightingRange();

        var recoilFactor = ResolveRecoilFactor(weapon);
        var statsScore = ergo * weights.Ergo
                       - recoil * weights.Recoil * recoilFactor
                       + sighting * weights.EffectiveDist;

        var qualityScore = ammoQuality * weights.AmmoQuality - lowPenPenalty;

        var price = ItemPriceLookup.GetPrice(weapon);
        var priceTie = price * PriceTieBreakerWeight;

        var total = ammoCountScore + statsScore + qualityScore + priceTie;

        // Zero-ammo brick guard: scale down hard if no compatible round is reachable from the source pool.
        var bricked = ammoBag.TotalRounds <= 0;
        if (bricked) total *= NoAmmoFloor;

        if (!string.IsNullOrEmpty(logTag))
        {
            Orbit.Log.Debug($"WeaponScorer[{logTag}] {weapon.LocalizedName()} ({weapon.AmmoCaliber}): rounds={ammoBag.TotalRounds}(cap={cappedRounds}) pen={ammoBag.BestPenetration} dmg={ammoBag.BestDamage} ergo={ergo:F1} recoil={recoil:F1}×{recoilFactor:F2} sight={sighting:F0} → count={ammoCountScore:F1} stats={statsScore:F1} quality={qualityScore:F1} priceTie={priceTie:F1}{(bricked ? " (BRICK ×0.05)" : "")} = {total:F1}");
            Orbit.Log.Debug($"WeaponScorer[{logTag}-mods] {DescribeWeaponMods(weapon)}");
        }

        return total;
    }

    /// <summary>
    /// Walk the weapon's slots and produce a compact human-readable summary of attached mods (suppressor,
    /// scope, grip, foregrip, mag, muzzle, etc.) plus their immediate children. Used in the dashboard
    /// scoring view to explain WHY a weapon's ergo / recoil / sight numbers landed where they did.
    /// </summary>
    private static string DescribeWeaponMods(Weapon weapon)
    {
        if (weapon?.Slots == null || weapon.Slots.Length == 0) return "<no slots>";
        var parts = new List<string>(weapon.Slots.Length);
        for (var i = 0; i < weapon.Slots.Length; i++)
        {
            var slot = weapon.Slots[i];
            var item = slot?.ContainedItem;
            if (item == null) continue;
            parts.Add($"{slot.ID}={item.LocalizedName()}");
        }
        return parts.Count == 0 ? "<empty>" : string.Join(", ", parts);
    }

    public readonly struct AmmoSnapshot
    {
        public readonly int TotalRounds;
        public readonly int BestPenetration;
        public readonly int BestDamage;
        public AmmoSnapshot(int rounds, int pen, int dmg) { TotalRounds = rounds; BestPenetration = pen; BestDamage = dmg; }
    }

    public static AmmoSnapshot CollectUsableAmmo(Weapon weapon, IEnumerable<Item> items)
    {
        var caliber = weapon.AmmoCaliber;
        var magSlot = weapon.GetMagazineSlot();
        var totalRounds = 0;
        var fallbackPen = 0;
        var fallbackDmg = 0;

        if (items == null)
            return new AmmoSnapshot(0, 0, 0);

        // Walk descends into mag.Cartridges, so every round is yielded as an AmmoItemClass. To count each
        // round exactly once AND only when it's usable (= loose, or sitting in a mag the weapon accepts),
        // gate on the cartridge's PARENT:
        //   - parent is a MagazineItemClass → only count if that mag fits this weapon's mag slot.
        //     Rounds locked in incompatible mag types (e.g. AK 7.62x39 mag vs SKS) stay invisible — a
        //     bot can't transfer them mid-combat, so they're not "usable" for this weapon.
        //   - parent is a grid (ammo box, rig pouch, etc.) → loose ammo, always count.
        //   - parent is the weapon's chamber → counted as loaded, always count.
        foreach (var item in items)
        {
            if (item is not AmmoItemClass ammo) continue;
            if (!string.Equals(ammo.Caliber, caliber, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsAmmoUsableByWeapon(ammo, magSlot)) continue;
            totalRounds += ammo.StackObjectsCount;
            // Track best pen/dmg across the pool as a fallback only — used when the weapon's currently
            // loaded mag is empty (rare). The bot's actual first-mag firing quality comes from the loaded
            // ammo below.
            ConsiderAmmoQuality(ammo, ref fallbackPen, ref fallbackDmg);
        }

        // Quality reflects what the bot will actually FIRE first — the round currently loaded in their
        // mag. A bot with 5× M61 (pen 70) + 200× M80 (pen 50) but M80 loaded scores as pen=50, because
        // that's what comes out the barrel next. BSG combat AI doesn't hot-swap mid-fight to load the
        // "best" spare ammo, so scoring by the loaded round is the realistic forecast. Falls back to the
        // pool's max-pen/max-dmg only when the mag is empty.
        var loadedPen = 0;
        var loadedDmg = 0;
        var loadedAmmo = weapon.GetCurrentMagazine()?.FirstRealAmmo() as AmmoItemClass;
        if (loadedAmmo != null) ConsiderAmmoQuality(loadedAmmo, ref loadedPen, ref loadedDmg);
        var bestPen = loadedPen > 0 ? loadedPen : fallbackPen;
        var bestDmg = loadedDmg > 0 ? loadedDmg : fallbackDmg;

        return new AmmoSnapshot(totalRounds, bestPen, bestDmg);
    }

    /// <summary>
    /// True when <paramref name="ammo"/>'s parent container is either NOT a magazine (loose ammo in a
    /// grid / chamber / etc.) or IS a magazine that physically fits the weapon's mag slot. Rounds locked
    /// inside an incompatible mag type that happens to share the caliber are NOT usable — the bot has no
    /// reload-from-other-mag flow in combat — and must be filtered out so the score reflects what the
    /// weapon can actually feed.
    /// </summary>
    private static bool IsAmmoUsableByWeapon(AmmoItemClass ammo, Slot weaponMagSlot)
    {
        var parent = ammo?.Parent?.Container?.ParentItem;
        if (parent is not MagazineItemClass mag) return true; // loose ammo (grid / chamber / etc.) — usable
        if (weaponMagSlot == null) return false;
        try { return weaponMagSlot.CheckCompatibility(mag); }
        catch { return false; }
    }

    /// <summary>
    /// Recoil-weight multiplier. Shotguns and semi-auto-only platforms take a discount on the recoil
    /// contribution; full-auto rifles keep the map's raw recoil weight.
    /// </summary>
    private static float ResolveRecoilFactor(Weapon weapon)
    {
        var factor = 1f;
        var caliber = weapon.AmmoCaliber;
        var isShotgun = !string.IsNullOrEmpty(caliber)
            && (caliber.IndexOf("12g", System.StringComparison.OrdinalIgnoreCase) >= 0
                || caliber.IndexOf("20g", System.StringComparison.OrdinalIgnoreCase) >= 0
                || caliber.IndexOf("23x", System.StringComparison.OrdinalIgnoreCase) >= 0);
        if (isShotgun) factor *= ShotgunRecoilFactor;
        var fireModes = weapon.WeapFireType;
        var hasFullAuto = false;
        if (fireModes != null)
        {
            for (var i = 0; i < fireModes.Length; i++)
            {
                if (fireModes[i] == Weapon.EFireMode.fullauto) { hasFullAuto = true; break; }
            }
        }
        if (!hasFullAuto) factor *= SemiAutoRecoilFactor;
        return factor;
    }

    private static void ConsiderAmmoQuality(AmmoItemClass ammo, ref int bestPen, ref int bestDmg)
    {
        if (ammo == null) return;
        if (ammo.PenetrationPower > bestPen) bestPen = ammo.PenetrationPower;
        if (ammo.Damage > bestDmg) bestDmg = ammo.Damage;
    }

    /// <summary>
    /// Flatten an item subtree into an enumerable of every item it contains. Used to harvest a corpse /
    /// source-tree's full ammo pool, including ammo loaded in magazines attached to other weapons.
    /// SecuredContainer slots (gamma) are skipped — their contents are not lootable and must not contribute
    /// to the candidate's ammo pool.
    /// </summary>
    public static IEnumerable<Item> Walk(Item root)
    {
        if (root == null) yield break;
        var stack = new Stack<Item>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            yield return item;
            if (item is CompoundItem compound)
            {
                if (compound.Slots != null)
                    foreach (var slot in compound.Slots)
                    {
                        if (slot?.ContainedItem == null) continue;
                        if (IsSecuredContainerSlot(slot)) continue;
                        stack.Push(slot.ContainedItem);
                    }
                if (compound.Grids != null)
                    foreach (var grid in compound.Grids)
                        if (grid?.Items != null)
                            foreach (var child in grid.Items)
                                if (child != null) stack.Push(child);
            }
            // Mag cartridges. Without this branch the walker stops at the magazine — its rounds are only
            // counted via mag.Count when MagFitsWeapon passes. AK / SKS / similar cross-platform mags all
            // hold the same caliber rounds but fail each other's slot filters, so the corpse's full ammo
            // carry was invisible to candidate scoring even though the loose rounds would be perfectly
            // usable post-strip. BSG's own Equipment.GetAllItems() descends into cartridges; matching that
            // behaviour here closes the CAND-vs-PRI asymmetry on cross-platform calibers.
            if (item is MagazineItemClass mag && mag.Cartridges?.Items != null)
                foreach (var round in mag.Cartridges.Items)
                    if (round != null) stack.Push(round);
        }
    }

    private static bool IsSecuredContainerSlot(Slot slot)
    {
        var id = slot?.ID;
        if (string.IsNullOrEmpty(id)) return false;
        // BSG slot IDs are stable strings, e.g. "SecuredContainer".
        return id.IndexOf("Secured", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
