using System.Collections.Generic;
using UnityEngine;

namespace Orbit.Api;

/// <summary>
/// Shot-timing generator for simulated ghost-fight audio, shared by the limiter's local playback and
/// the Orbit.Fika addon so co-op clients hear the same fight shape. Timings respect the firing
/// weapon's capability: automatic weapons trade short bursts at a realistic cadence with pauses
/// between them, semi-auto/bolt weapons fire aimed single shots — an SVD must never sound like a mag
/// dump (community report from the 2.0 RC).
/// </summary>
public static class GhostShotScheduler
{
    /// <summary>
    /// Timings (seconds from now) for one side of a fight. <paramref name="shotBudget"/> is a cap,
    /// not a target: when the window closes before the budget is spent, the leftover shots are
    /// dropped — a sparse exchange reads better than a compressed one.
    /// </summary>
    public static List<float> Schedule(bool autoWeapon, int shotBudget, float duration)
    {
        var times = new List<float>(shotBudget);
        var t = Random.Range(0.1f, Mathf.Min(1.5f, duration * 0.3f));
        while (shotBudget > 0 && t < duration)
        {
            if (autoWeapon && Random.value < 0.75f)
            {
                // Burst: 3-8 rounds at a plausible cyclic rate, then a re-aim pause.
                var burst = Random.Range(3, 9);
                var gap = Random.Range(0.08f, 0.13f);
                for (var j = 0; j < burst && shotBudget > 0 && t + j * gap < duration; j++, shotBudget--)
                    times.Add(t + j * gap);
                t += burst * gap + Random.Range(0.7f, 2.8f);
            }
            else
            {
                // Aimed single shot (the only option for semi/bolt weapons).
                times.Add(t);
                shotBudget--;
                t += autoWeapon ? Random.Range(0.3f, 1.1f) : Random.Range(0.45f, 1.5f);
            }
        }
        return times;
    }
}
