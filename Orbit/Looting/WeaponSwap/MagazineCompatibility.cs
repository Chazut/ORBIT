using EFT.InventoryLogic;

namespace Orbit.Looting.WeaponSwap;

internal static class MagazineCompatibility
{
    internal static bool Fits(Weapon weapon, Magazine magazine)
    {
        var slot = weapon.GetMagazineSlot();
        // Empty magazines remain useful. For loaded ones EFT checks the next
        // cartridge against the chamber, independently of the magazine body.
        return slot != null && slot.CheckCompatibility(magazine) && magazine.IsAmmoCompatible(weapon.Chambers);
    }
}
