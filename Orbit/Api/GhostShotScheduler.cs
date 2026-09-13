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

    public static GhostWeaponProfile Default(bool autoWeapon) => new()
    {
        Mode = autoWeapon ? Kind.Auto : Kind.Semi,
        CyclicRpm = 600f,
        SemiRpm = 240f,
        BurstShare = 0.5f,
    };

    public static float BurstShareAt(float engagementDistance)
        => Mathf.Lerp(0.9f, 0.15f, Mathf.InverseLerp(40f, 220f, engagementDistance));

    public static GhostWeaponProfile From(Weapon weapon, WeaponSoundPlayer sound, float engagementDistance)
    {
        var profile = Default(sound != null && sound.IsAutoWeapon);
        profile.BurstShare = BurstShareAt(engagementDistance);
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
            // Capped at 850: above that (MP7, Vector, KEDR, SR-2M, P90) the per-shot one-shots pile up into
            // a buzz once heard from a distance, and players report the fights as "way too fast".
            profile.CyclicRpm = Mathf.Clamp(template.bFirerate > 0 ? template.bFirerate : 600f, 300f, 850f);
        }
        profile.SemiRpm = Mathf.Clamp(template.SingleFireRate > 0 ? template.SingleFireRate : 240f, 60f, 600f);
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
        var times = new List<float>(Mathf.Max(0, shotBudget));
        pauseScale = Mathf.Max(1f, pauseScale);
        var t = Mathf.Max(0f, startOffset) + Random.Range(0.1f, Mathf.Min(1.5f, duration * 0.3f));
        while (shotBudget > 0 && t < duration)
        {
            switch (weapon.Mode)
            {
                case GhostWeaponProfile.Kind.Auto when Random.value < weapon.BurstShare:
                {
                    // Mostly 2-5 round bursts, the occasional long one, at the gun's own cyclic rate
                    // with a touch of trigger jitter; then a re-aim pause, sometimes a reposition.
                    var burst = Random.value < 0.15f ? Random.Range(6, 9) : Random.Range(2, 6);
                    var gap = 60f / weapon.CyclicRpm;
                    for (var j = 0; j < burst && shotBudget > 0 && t < duration; j++, shotBudget--)
                    {
                        times.Add(t);
                        t += gap * Random.Range(0.95f, 1.08f);
                    }
                    t += (Random.value < 0.15f ? Random.Range(3f, 6f) : Random.Range(0.6f, 3f)) * pauseScale;
                    break;
                }
                case GhostWeaponProfile.Kind.Burst when Random.value < weapon.BurstShare:
                {
                    var burst = Random.Range(2, 4);
                    var gap = 60f / weapon.CyclicRpm;
                    for (var j = 0; j < burst && shotBudget > 0 && t < duration; j++, shotBudget--)
                    {
                        times.Add(t);
                        t += gap;
                    }
                    t += Random.Range(0.5f, 2.2f) * pauseScale;
                    break;
                }
                case GhostWeaponProfile.Kind.Auto:
                case GhostWeaponProfile.Kind.Burst:
                case GhostWeaponProfile.Kind.Semi:
                {
                    // Aimed single, a quick double tap now and then; the aim time is never the same
                    // twice and never faster than the trigger allows.
                    var trigger = 60f / weapon.SemiRpm;
                    times.Add(t);
                    shotBudget--;
                    if (shotBudget > 0 && Random.value < 0.25f)
                    {
                        t += trigger * Random.Range(1f, 1.6f);
                        if (t < duration)
                        {
                            times.Add(t);
                            shotBudget--;
                        }
                    }
                    t += Mathf.Max(trigger, (Random.value < 0.15f ? Random.Range(2f, 4f) : Random.Range(0.35f, 1.6f)) * pauseScale);
                    break;
                }
                default:
                {
                    // Bolt / pump: cycle the action at the template rate, then aim again.
                    times.Add(t);
                    shotBudget--;
                    t += 60f / weapon.CyclicRpm + Random.Range(0.4f, 1.6f) * pauseScale;
                    break;
                }
            }
        }
        return times;
    }

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
