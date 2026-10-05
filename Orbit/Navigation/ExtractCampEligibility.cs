using System;
using EFT.Interactive;
using EFT.InventoryLogic;

namespace Orbit.Navigation;

internal static class ExtractCampEligibility
{
    internal static bool Allows(ExfiltrationPoint exit)
    {
        if (exit == null) return false;
        if (exit.Requirements == null) return true;
        foreach (var requirement in exit.Requirements)
        {
            if (requirement == null) continue;
            if (requirement.RequiredSlot == EquipmentSlot.Backpack
                && requirement.Requirement is ERequirementState.Empty or ERequirementState.EmptyOrSize) return false;
            // Handle both expanded native requirements and an unresolved Alpinist reference.
            if (string.Equals(requirement.Id, "5c12688486f77426843c7d32", StringComparison.OrdinalIgnoreCase)
                || requirement.Requirement == ERequirementState.Reference
                    && string.Equals(requirement.Id, "Alpinist", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
