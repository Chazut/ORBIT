using System.Collections.Generic;
using EFT.InventoryLogic;
using UnityEngine;

namespace Orbit.Api;

/// <summary>
/// What a ghost shooter's gun can do, resolved once per fight from the weapon actually in its hands
/// (the template's fire modes, cyclic rate and fastest plausible semi-auto trigger rate) and from the
/// engagement distance. A ghost has no fire-mode logic of its own, so the choice a live bot would make
/// is modelled here: an automatic weapon fires mostly bursts up close and mostly aimed singles at range.
/// </summary>
public struct GhostWeaponProfile
{
    public enum Kind { Auto, Burst, Semi, Bolt }

    public Kind Mode;
    /// <summary>Cyclic rate for automatic / burst fire, or the bolt/pump cycle rate (rounds per minute).</summary>
    public float CyclicRpm;
    /// <summary>Fastest plausible trigger rate in semi-auto (rounds per minute).</summary>
    public float SemiRpm;
    /// <summary>Auto / Burst modes only: share of firing sequences that are bursts rather than aimed
    /// singles. Driven by the engagement distance (0.9 at 40m and under, 0.15 at 220m and beyond).</summary>
    public float BurstShare;
    public float EngagementDistance;
    public int MagazineCapacity;
    // Negative means unknown. These counters only shape audio, never mutate inventory.
    public int LoadedRounds;

    public static GhostWeaponProfile Default(bool autoWeapon) => new()
    {
        Mode = autoWeapon ? Kind.Auto : Kind.Semi,
        CyclicRpm = 600f,
        SemiRpm = 240f,
        BurstShare = 0.5f,
        LoadedRounds = -1,
    };

    public static float BurstShareAt(float engagementDistance)
        => Mathf.Lerp(0.9f, 0.15f, Mathf.InverseLerp(40f, 220f, engagementDistance));

    public static GhostWeaponProfile From(Weapon weapon, WeaponSoundPlayer sound, float engagementDistance)
    {
        var profile = Default(sound != null && sound.IsAutoWeapon);
        profile.BurstShare = BurstShareAt(engagementDistance);
        profile.EngagementDistance = engagementDistance;
        var template = weapon?.Template;
        if (template == null) return profile;

        var modes = template.weapFireType;
        var hasAuto = modes != null && System.Array.IndexOf(modes, Weapon.EFireMode.fullauto) >= 0;
        var hasBurst = modes != null && System.Array.IndexOf(modes, Weapon.EFireMode.burst) >= 0;

        if (template.BoltAction)
        {
            profile.Mode = Kind.Bolt;
            // Bolt and pump guns carry their cycle rate in bFirerate (a Mosin sits around 30).
            profile.CyclicRpm = Mathf.Clamp(template.bFirerate > 0 ? template.bFirerate : 40f, 15f, 120f);
        }
        else
        {
            profile.Mode = hasAuto || (modes == null && profile.Mode == Kind.Auto) ? Kind.Auto
                : hasBurst ? Kind.Burst
                : Kind.Semi;
            // The weapon's real cyclic rate, no cap. The old 850 limit was a workaround for fights that
            // sounded "way too fast": the actual cause was the automatic Body bank being a 16-round loop
            // stacked once per round (see GhostShotPlayback). The bounds only guard against bad template data.
            profile.CyclicRpm = Mathf.Clamp(template.bFirerate > 0 ? template.bFirerate : 600f, 300f, 1500f);
        }
        profile.SemiRpm = Mathf.Clamp(template.SingleFireRate > 0 ? template.SingleFireRate : 240f, 60f, 600f);
        var magazine = weapon.GetCurrentMagazine();
        profile.MagazineCapacity = Mathf.Max(1, (magazine?.MaxCount ?? 0) + (weapon.Chambers?.Length ?? 0));
        profile.LoadedRounds = weapon.ChamberAmmoCount + (magazine?.Count ?? 0);
        return profile;
    }
}

/// <summary>
/// Shot-timing generator for simulated ghost-fight audio, shared by the limiter's local playback and
/// the Orbit.Fika addon so co-op clients hear the same fight shape. Timings respect the firing
/// weapon: automatic weapons trade short controlled bursts at their real cyclic rate with re-aim
/// pauses between them (nobody empties a mag in one go at 200m), semi-auto weapons fire aimed singles
/// whose aim time varies every shot (with the odd double tap), bolt and pump guns cycle at their own
/// rate plus an aim.
/// </summary>
public static class GhostShotScheduler
{
    /// <summary>
    /// Timings (seconds from now) for one shooter. <paramref name="shotBudget"/> is a cap, not a
    /// target: when the window closes before the budget is spent, the leftover shots are dropped,
    /// a sparse exchange reads better than a compressed one.
    /// </summary>
    /// <param name="startOffset">Seconds before this shooter opens up: squadmates are staggered so a
    /// five-man side does not empty five magazines in the same second.</param>
    /// <param name="pauseScale">Multiplier on the re-aim pauses between bursts and singles; grows with the
    /// number of shooters on the side so the side's aggregate rate stays that of a firefight, not a wall.</param>
    public static List<float> Schedule(GhostWeaponProfile weapon, int shotBudget, float duration, float startOffset = 0f, float pauseScale = 1f)
    {
        shotBudget = Mathf.Clamp(shotBudget, 0, 90);
        var times = new List<float>(shotBudget);
        if (shotBudget == 0 || !Finite(duration) || duration <= 0f) return times;
        pauseScale = Finite(pauseScale) ? Mathf.Clamp(pauseScale, 1f, 3f) : 1f;
        var distance = Finite(weapon.EngagementDistance) ? weapon.EngagementDistance : 0f;
        var far = Mathf.InverseLerp(40f, 300f, distance);
        var bolt = weapon.Mode == GhostWeaponProfile.Kind.Bolt;
        var cycle = 60f / SafeRate(weapon.CyclicRpm, bolt ? 30f : 600f);
        var trigger = Mathf.Max(cycle, 60f / SafeRate(weapon.SemiRpm, 240f));
        var capacity = Mathf.Clamp(weapon.MagazineCapacity > 0 ? weapon.MagazineCapacity : bolt ? 5 : 30, 1, 200);
        var remaining = weapon.LoadedRounds < 0 ? capacity : Mathf.Clamp(weapon.LoadedRounds, 0, capacity);
        // One temperament per shooter, then fresh decisions within each exchange. No fixed playlist
        // or shared metronome: the opening, activity, pauses and reloads all vary independently.
        var tempo = Random.Range(0.75f, 1.4f);
        var cautious = Random.value;
        var sequences = PhaseLength(bolt, cautious);
        var t = (Finite(startOffset) ? Mathf.Max(0f, startOffset) : 0f)
                + Random.Range(0.15f, Mathf.Min(3.5f, duration * 0.3f));
        if (Random.value < 0.2f && duration > 15f) t += Random.Range(3f, 8f);
        while (shotBudget > 0 && t < duration)
        {
            if (remaining == 0)
            {
                t += (bolt ? Random.Range(5f, 10f) : Random.Range(3f, 6.5f)) * tempo;
                remaining = capacity;
                if (t >= duration) break;
            }
            var automatic = weapon.Mode == GhostWeaponProfile.Kind.Auto || weapon.Mode == GhostWeaponProfile.Kind.Burst;
            var burst = automatic && Random.value < weapon.BurstShare;
            var rounds = burst ? weapon.Mode == GhostWeaponProfile.Kind.Burst ? Random.Range(2, 4)
                : Random.value < 0.12f * (1f - far) ? Random.Range(6, 9) : Random.Range(2, 6)
                : !bolt && Random.value < Mathf.Lerp(0.3f, 0.08f, far) ? 2 : 1;
            rounds = Mathf.Min(rounds, Mathf.Min(remaining, shotBudget));
            for (var j = 0; j < rounds && t < duration; j++)
            {
                times.Add(t);
                remaining--;
                shotBudget--;
                t += burst ? cycle * Random.Range(1f, 1.08f) : trigger * Random.Range(1f, 1.5f);
            }
            t += (bolt ? Random.Range(0.4f, 2.2f) : Random.Range(0.35f, 1.8f))
                 * tempo * pauseScale * Mathf.Lerp(1f, 1.6f, far);
            if (--sequences > 0) continue;
            // Observation is part of the fight window. Never compress the rest of the budget to
            // compensate for silence, even if this shooter fires only a few rounds in a long duel.
            var pause = Random.value < 0.12f + far * 0.18f + cautious * 0.1f
                ? Random.Range(20f, 30f)
                : bolt ? Random.Range(6f, 15f) : Random.Range(2f, 7f) + far * Random.Range(3f, 8f);
            t += pause * tempo * Mathf.Sqrt(pauseScale);
            sequences = PhaseLength(bolt, cautious);
        }
        return times;
    }

    private static int PhaseLength(bool bolt, float cautious)
        => Random.value < cautious * 0.6f ? 1 : Random.Range(1, bolt ? 4 : 5);

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static float SafeRate(float rate, float fallback)
        => Finite(rate) && rate > 0f ? Mathf.Clamp(rate, 1f, 3000f) : fallback;

    /// <summary>Stagger for the k-th of n shooters on one side: later members open up a little later and
    /// everyone pauses longer, so the side's aggregate rate stays constant whatever the squad size.</summary>
    public static (float startOffset, float pauseScale) SideStagger(int indexOnSide, int shootersOnSide)
    {
        var offset = indexOnSide <= 0 ? 0f : indexOnSide * Random.Range(0.4f, 0.9f);
        var scale = Mathf.Min(2.4f, 1f + 0.35f * Mathf.Max(0, shootersOnSide - 1));
        return (offset, scale);
    }

    /// <summary>Pre-2.1 entry point: only knows auto vs not, uses generic rates.</summary>
    public static List<float> Schedule(bool autoWeapon, int shotBudget, float duration)
        => Schedule(GhostWeaponProfile.Default(autoWeapon), shotBudget, duration);
}
