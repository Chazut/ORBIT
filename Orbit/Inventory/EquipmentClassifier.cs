using System;
using EFT.InventoryLogic;

namespace Orbit.Inventory;

/// <summary>
/// Equipment slot taxonomy used by the loot routine to decide whether a
/// candidate item is eligible for pickup / swap given the bot's faction
/// allowlist.
/// </summary>
[Flags]
public enum EquipmentType
{
    Backpack = 1,
    TacticalRig = 2,
    ArmoredRig = 4,
    Chest = 8,
    Weapon = 16,
    Grenade = 32,
    Helmet = 64,
    Dogtag = 128,
    ArmorPlate = 256,
    Earpiece = 512,
    FaceCover = 1024,
    Eyewear = 2048,
    Armband = 4096,

    All = Backpack | TacticalRig | ArmoredRig | Chest | Weapon | Helmet | Grenade
        | Dogtag | ArmorPlate | Earpiece | FaceCover | Eyewear | Armband,
}

/// <summary>
/// Restricted subset of <see cref="EquipmentType"/> for slots a bot is
/// allowed to actually equip (vs. just pick up into a container). Excludes
/// Dogtag + ArmorPlate which never go directly onto the body.
/// </summary>
[Flags]
public enum CanEquipEquipmentType
{
    Backpack = EquipmentType.Backpack,
    TacticalRig = EquipmentType.TacticalRig,
    ArmoredRig = EquipmentType.ArmoredRig,
    Chest = EquipmentType.Chest,
    Weapon = EquipmentType.Weapon,
    Grenade = EquipmentType.Grenade,
    Helmet = EquipmentType.Helmet,
    Earpiece = EquipmentType.Earpiece,
    FaceCover = EquipmentType.FaceCover,
    Eyewear = EquipmentType.Eyewear,
    Armband = EquipmentType.Armband,

    All = Backpack | TacticalRig | ArmoredRig | Chest | Weapon | Helmet | Grenade
        | Earpiece | FaceCover | Eyewear | Armband,
}

public static class EquipmentClassifier
{
    public static bool HasBackpack(this EquipmentType equipmentType) => (equipmentType & EquipmentType.Backpack) != 0;
    public static bool HasTacticalRig(this EquipmentType equipmentType) => (equipmentType & EquipmentType.TacticalRig) != 0;
    public static bool HasArmoredRig(this EquipmentType equipmentType) => (equipmentType & EquipmentType.ArmoredRig) != 0;
    public static bool HasChestArmor(this EquipmentType equipmentType) => (equipmentType & EquipmentType.Chest) != 0;
    public static bool HasGrenade(this EquipmentType equipmentType) => (equipmentType & EquipmentType.Grenade) != 0;
    public static bool HasWeapon(this EquipmentType equipmentType) => (equipmentType & EquipmentType.Weapon) != 0;
    public static bool HasHelmet(this EquipmentType equipmentType) => (equipmentType & EquipmentType.Helmet) != 0;
    public static bool HasArmorPlate(this EquipmentType equipmentType) => (equipmentType & EquipmentType.ArmorPlate) != 0;
    public static bool HasDogtag(this EquipmentType equipmentType) => (equipmentType & EquipmentType.Dogtag) != 0;
    public static bool HasEarpiece(this EquipmentType equipmentType) => (equipmentType & EquipmentType.Earpiece) != 0;
    public static bool HasFaceCover(this EquipmentType equipmentType) => (equipmentType & EquipmentType.FaceCover) != 0;
    public static bool HasEyewear(this EquipmentType equipmentType) => (equipmentType & EquipmentType.Eyewear) != 0;
    public static bool HasArmband(this EquipmentType equipmentType) => (equipmentType & EquipmentType.Armband) != 0;

    /// <summary>
    /// True when the candidate item matches a slot category the bot's
    /// allowlist permits. <paramref name="toPickup"/> = true relaxes the
    /// final fall-through so unclassified items still count as
    /// inventory-grabbable (used by the pickup-into-container path; the
    /// equip path passes false to keep unclassified items out of slots).
    /// </summary>
    public static bool IsItemEligible(this EquipmentType allowedGear, Item item, bool toPickup = false)
    {
        if (IsChestArmor(item)) return allowedGear.HasChestArmor();
        if (IsHelmet(item)) return allowedGear.HasHelmet();
        if (IsBackpack(item)) return allowedGear.HasBackpack();
        if (IsEarpiece(item)) return allowedGear.HasEarpiece();
        if (IsFaceCover(item)) return allowedGear.HasFaceCover();
        if (IsEyewear(item)) return allowedGear.HasEyewear();
        if (IsArmoredRig(item)) return allowedGear.HasArmoredRig();
        if (IsTacticalRig(item)) return allowedGear.HasTacticalRig();
        if (IsArmorPlate(item)) return allowedGear.HasArmorPlate();
        if (IsDogtag(item)) return allowedGear.HasDogtag();
        if (item is KnifeItemClass) { /* falls through to toPickup */ }
        if (item is ThrowWeapItemClass) return allowedGear.HasGrenade();
        if (item is Weapon) return allowedGear.HasWeapon();
        if (IsArmband(item)) return allowedGear.HasArmband();

        return toPickup;
    }

    public static bool IsTacticalRig(Item item) => item is VestItemClass;

    public static bool IsArmoredRig(Item item)
    {
        if (item is VestItemClass vest)
        {
            foreach (var slot in vest.Slots)
            {
                // GClass3125 is BSG's armor-slot type — any rig with an
                // armor-mounting slot counts as armoured.
                if (slot is GClass3125)
                    return true;
            }
        }
        return false;
    }

    public static bool IsBackpack(Item item) => item is BackpackItemClass;
    public static bool IsHelmet(Item item) => item is HeadwearItemClass;
    public static bool IsChestArmor(Item item) => item is ArmorItemClass;
    public static bool IsFaceCover(Item item) => item is FaceCoverItemClass;
    public static bool IsEyewear(Item item) => item is VisorsItemClass;
    public static bool IsArmorPlate(Item item) => item is ArmorPlateItemClass;
    public static bool IsDogtag(Item item) => item is OtherItemClass;
    public static bool IsEarpiece(Item item) => item is HeadphonesItemClass;
    public static bool IsArmband(Item item) => item is ArmBandItemClass;
}
