using System;
using EFT.Interactive;
using EFT.InventoryLogic;
using Orbit.Systems;

namespace Orbit.Navigation;

internal static class ExtractCampEligibility
{
    internal static bool Allows(ExfiltrationPoint exit)
    {
        if (exit == null || FlareExfil.RequiresSignal(exit)) return false;
        if (exit.Requirements == null) return true;
        var activeOperation = MultiStepAccess.IsConditional(exit) && MultiStepAccess.ExitActive(exit);
        foreach (var requirement in exit.Requirements)
        {
            if (requirement == null) continue;
            if (requirement.RequiredSlot == EquipmentSlot.Backpack
                && requirement.Requirement is ERequirementState.Empty or ERequirementState.EmptyOrSize) return false;
            // Handle both expanded native requirements and an unresolved Alpinist reference.
            if (string.Equals(requirement.Id, "5c12688486f77426843c7d32", StringComparison.OrdinalIgnoreCase)
                || requirement.Requirement == ERequirementState.Reference
                    && string.Equals(requirement.Id, "Alpinist", StringComparison.OrdinalIgnoreCase)) return false;
            // Camping may target another player's exit, but never an unsupported access mechanic.
            // Read live requirements so paid co-op conversions and switch changes are respected.
            switch (requirement.Requirement)
            {
                case ERequirementState.None:
                    break;
                case ERequirementState.TransferItem:
                    if (exit.Settings?.ExfiltrationType != EExfiltrationType.SharedTimer) return false;
                    break;
                case ERequirementState.WorldEvent:
                case ERequirementState.SecretTransferItem:
                    if (!activeOperation) return false;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }
}
