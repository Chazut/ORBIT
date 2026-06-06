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
            Orbit.Log.Debug($"WeaponScorer[{logTag}] {weapon.LocalizedName()} ({weapon.AmmoCaliber}): rounds={ammoBag.TotalRounds}(cap={cappedRounds}) pen={ammoBag.BestPenetration} dmg={ammoBag.BestDamage} ergo={ergo:F1} recoil={recoil:F1}×{recoilFactor:F2} sight={sighting:F0} → count={ammoCountScore:F1} stats={statsScore:F1} quality={qualityScore:F1} priceTie={priceTie:F1}{(bricked ? " (BRICK ×0.05)" : "")} = {total:F1}");

        return total;
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
        var bestPen = 0;
        var bestDmg = 0;

        if (items == null)
            return new AmmoSnapshot(0, 0, 0);

        // Ammo already loaded in the weapon's current mag + chamber (handles regular and cylinder mags).
        var currentMag = weapon.GetCurrentMagazine();
        if (currentMag != null)
        {
            totalRounds += currentMag.Count;
            ConsiderAmmoQuality(currentMag.FirstRealAmmo() as AmmoItemClass, ref bestPen, ref bestDmg);
        }

        foreach (var item in items)
        {
            if (item == null) continue;
            switch (item)
            {
                case MagazineItemClass mag when MagFitsWeapon(mag, magSlot):
                    if (mag != currentMag)
                        totalRounds += mag.Count;
                    ConsiderAmmoQuality(mag.FirstRealAmmo() as AmmoItemClass, ref bestPen, ref bestDmg);
                    break;
                case AmmoItemClass ammo when string.Equals(ammo.Caliber, caliber, System.StringComparison.OrdinalIgnoreCase):
                    totalRounds += ammo.StackObjectsCount;
                    ConsiderAmmoQuality(ammo, ref bestPen, ref bestDmg);
                    break;
            }
        }

        return new AmmoSnapshot(totalRounds, bestPen, bestDmg);
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

    private static bool MagFitsWeapon(MagazineItemClass mag, Slot weaponMagSlot)
    {
        if (mag == null || weaponMagSlot == null) return false;
        try { return weaponMagSlot.CheckCompatibility(mag); }
        catch { return false; }
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
