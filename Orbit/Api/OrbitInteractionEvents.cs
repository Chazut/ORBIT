using System;
using EFT;
using EFT.Interactive;

namespace Orbit.Api;

/// <summary>World interactions performed independently of an awake bot body.</summary>
public static class OrbitInteractionEvents
{
    public static event Action<WorldInteractiveObject, EInteractionType> Changed;
    internal static void Raise(WorldInteractiveObject target, EInteractionType interaction)
    {
        try { Changed?.Invoke(target, interaction); }
        catch { /* A companion cannot interrupt the host's world interaction. */ }
    }
}
