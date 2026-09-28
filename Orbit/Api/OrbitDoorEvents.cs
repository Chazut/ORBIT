using System;
using System.Runtime.CompilerServices;
using EFT.Interactive;

namespace Orbit.Api;

/// <summary>Optional notifications of ORBIT door operations. Subscribers never own the local operation.</summary>
public static class OrbitDoorEvents
{
    public enum Operation { Unlock, Open, Close, Finalize }

    public static event Action<Door, Operation> Changed;
    private sealed class Generation { internal ulong Value; }
    private static readonly ConditionalWeakTable<Door, Generation> Generations = new();

    /// <summary>A newer external interaction supersedes deferred ORBIT finalization of this door.</summary>
    public static void NotifyExternalInteraction(Door door)
    {
        if (door != null) Generations.GetOrCreateValue(door).Value++;
    }

    internal static ulong Revision(Door door)
        => door != null && Generations.TryGetValue(door, out var value) ? value.Value : 0;

    internal static void Raise(Door door, Operation operation)
    {
        try { Changed?.Invoke(door, operation); }
        catch { /* A companion must not interrupt movement or a door transition. */ }
    }
}
