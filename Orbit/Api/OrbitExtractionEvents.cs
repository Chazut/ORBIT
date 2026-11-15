using System;
using EFT.Interactive;

namespace Orbit.Api;

/// <summary>Lets optional transports observe the native lifecycle of an exit used by ORBIT.</summary>
public static class OrbitExtractionEvents
{
    public static event Action<ExfiltrationPoint> Used;

    internal static void Raise(ExfiltrationPoint point)
    {
        try { Used?.Invoke(point); }
        catch (Exception e) { Log.Warning($"EXFIL SYNC: observer failed: {e.Message}"); }
    }
}
