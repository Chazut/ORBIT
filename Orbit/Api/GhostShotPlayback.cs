using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace Orbit.Api;

/// <summary>One trigger pull of a simulated shooter: <see cref="Rounds"/> fired without releasing,
/// starting <see cref="At"/> seconds from now.</summary>
public struct GhostTriggerPull
{
    public float At;
    public int Rounds;
}

/// <summary>
/// Playback of simulated ghost shots through the fighters' own weapon sound banks, shared by the limiter
/// and the Orbit.Fika addon.
///
/// The catch is BSG's automatic-weapon sound design: for a non-automatic gun the Body bank is a one-shot
/// (the shot and its tail), but for an automatic weapon it is a FIRING LOOP of 16 rounds
/// (WeaponSoundPlayer.BeatLn = clip length / 16). The game starts that loop on the first round, lets it run
/// while the trigger is held, then cuts it on the next beat and plays the Tail bank. Playing the Body bank
/// once per scheduled round, as the first implementation did, stacked a full 16-round loop on every round:
/// a 4-round burst was heard as ~19 rounds of overlapping automatic fire, an 11-round budget as far more
/// than a magazine, on both sides at once. A trigger pull is therefore played the way the game does it:
/// one loop start, a sample-accurate cut after its own number of beats, then the tail.
/// </summary>
public static class GhostShotPlayback
{
    private const int LoopRounds = 16;

    /// <summary>Groups the scheduler's flat shot times into trigger pulls. With a one-shot Body bank every
    /// round is its own pull; with a looping one, rounds spaced at the cyclic rate belong to the same pull.</summary>
    public static List<GhostTriggerPull> GroupTriggerPulls(List<float> times, GhostWeaponProfile weapon, bool loopingBody)
    {
        var pulls = new List<GhostTriggerPull>(times.Count);
        if (times.Count == 0) return pulls;
        if (!loopingBody)
        {
            for (var i = 0; i < times.Count; i++) pulls.Add(new GhostTriggerPull { At = times[i], Rounds = 1 });
            return pulls;
        }

        var maxGap = 60f / Mathf.Max(60f, weapon.CyclicRpm) * 1.35f; // the scheduler jitters the cyclic gap by up to 8%
        var start = times[0];
        var last = times[0];
        var rounds = 1;
        for (var i = 1; i < times.Count; i++)
        {
            if (times[i] - last <= maxGap && rounds < LoopRounds)
            {
                rounds++;
            }
            else
            {
                pulls.Add(new GhostTriggerPull { At = start, Rounds = rounds });
                start = times[i];
                rounds = 1;
            }
            last = times[i];
        }
        pulls.Add(new GhostTriggerPull { At = start, Rounds = rounds });
        return pulls;
    }

    /// <summary>
    /// Plays one trigger pull. Returns the source when the pull is audible (null = out of earshot or no
    /// bank). <paramref name="tailDelay"/> is the time in seconds after which <see cref="PlayTail"/> must be
    /// called with the returned source; 0 means the clip carries its own tail.
    /// </summary>
    public static BetterSource Play(BetterAudio audio, WeaponSoundPlayer sound, Vector3 position, float listenerDistance, int rounds, out float tailDelay)
    {
        tailDelay = 0f;
        var body = BodyBank(sound);
        if (body == null) return null;
        // PlayAtPoint (not the Distant variant): the bank's own source group, mixer and 3D rolloff, the path
        // a real remote gunshot takes. Null = the bank's rolloff says the listener is out of earshot.
        var source = audio.PlayAtPoint(position, body, listenerDistance, 1f);
        if (source == null || !sound.IsAutoWeapon) return source;

        var beat = body.ClipLength / LoopRounds;
        if (beat <= 0f) return source;
        var pitch = source.source1 != null && source.source1.pitch > 0.01f ? source.source1.pitch : 1f;
        tailDelay = Mathf.Clamp(rounds, 1, LoopRounds) * beat / pitch;
        // Sample-accurate release of the loop, the way the game's weapon queue cuts it; PlayTail stops the
        // source as well in case the engine ignored the request.
        try { source.SetScheduledEndTime(AudioSettings.dspTime + tailDelay); } catch { }
        return source;
    }

    /// <summary>End of an automatic trigger pull: silence what is left of the loop, play the weapon's tail.</summary>
    public static void PlayTail(BetterAudio audio, WeaponSoundPlayer sound, BetterSource loopSource, Vector3 position, float listenerDistance)
    {
        try { if (loopSource != null) loopSource.Stop(0.02f); } catch { }
        var tail = sound.IsSilenced
            ? (sound.TailSilenced != null ? sound.TailSilenced : sound.Tail)
            : sound.Tail;
        if (tail != null) audio.PlayAtPoint(position, tail, listenerDistance, 1f);
    }

    /// <summary>Distant gunshots in EFT come from the BODY bank: it blends its clips by distance. A suppressed
    /// weapon's sound player is flagged IsSilenced by the game, so its ghost shots use the silenced body.</summary>
    public static SoundBank BodyBank(WeaponSoundPlayer sound)
    {
        if (sound == null) return null;
        return sound.IsSilenced
            ? (sound.BodySilenced != null ? sound.BodySilenced
                : sound.Body != null ? sound.Body : sound.TailSilenced)
            : (sound.Body != null ? sound.Body : sound.Tail);
    }
}
