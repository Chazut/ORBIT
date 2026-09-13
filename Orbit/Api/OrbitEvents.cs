using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbit.Api;

/// <summary>
/// Public event surface for optional companion mods (Orbit.Fika &amp; co). Same stability contract as
/// <see cref="OrbitTelemetry"/>: safe to subscribe from any mod, subscriber exceptions are swallowed
/// so a companion bug can never break ORBIT itself.
/// </summary>
public static class OrbitEvents
{
    /// <summary>
    /// Raised on the machine that resolved a simulated ghost fight (the one that owns the bots),
    /// once per fight, BEFORE the local earshot gate: audibility is a per-listener judgement, so
    /// relays (the Orbit.Fika addon broadcasts this to every co-op client) must receive the fight
    /// whatever the host's own distance to it. Profile ids identify one member per side; resolve
    /// their <c>WeaponSoundPlayer</c> for authentic sound banks.
    /// </summary>
    public static event Action<GhostFightSounds> GhostFightSoundsResolved;

    /// <summary>One firing member of a simulated fight: whose weapon sound banks to use, from where,
    /// and how many shots of the fight's budget are its own.</summary>
    public struct GhostShooter
    {
        public string ProfileId;
        public Vector3 Position;
        public int Shots;
    }

    public struct GhostFightSounds
    {
        public Vector3 PosA;
        public Vector3 PosB;
        /// <summary>First shooter of each side, kept for pre-2.1 consumers.</summary>
        public string ProfileA;
        public string ProfileB;
        public int Shots;
        public float Duration;
        /// <summary>Per-member breakdown of the budget (2.1+): every member fires its own gun from its
        /// own spot. Null or empty from an older host: fall back to ProfileA / ProfileB.</summary>
        public List<GhostShooter> Shooters;
    }

    internal static void RaiseGhostFightSounds(in GhostFightSounds data)
    {
        try
        {
            GhostFightSoundsResolved?.Invoke(data);
        }
        catch
        {
            // Subscriber bugs must never break the limiter.
        }
    }
}
