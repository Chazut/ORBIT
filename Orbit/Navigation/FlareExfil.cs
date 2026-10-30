using System;
using EFT.Interactive;

namespace Orbit.Navigation;

internal static class FlareExfil
{
    // SPT 4.1 lists these as Individual/None. Their signal-flare permission is
    // enforced by a separate sniper zone, not ExfiltrationPoint.Requirements.
    // ORBIT cannot perform that interaction, even when the exit is RegularMode.
    internal static bool RequiresSignal(ExfiltrationPoint exit) => exit != null
        && (IsFlareId(exit.Settings?.Name) || IsFlareId(exit.name));

    private static bool IsFlareId(string name) =>
        string.Equals(name, "E9_sniper", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Sniper_exit", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "customs_sniper_exit", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "wood_sniper_exit", StringComparison.OrdinalIgnoreCase);
}
