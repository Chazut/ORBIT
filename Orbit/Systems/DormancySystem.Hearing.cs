using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Sain;
using UnityEngine;

namespace Orbit.Systems;

public partial class DormancySystem
{
    // ── Ghost hearing ──────────────────────────────────────────────────────────────────────────────
    // A sleeper has no ears: its body is inactive, SAIN is not running. Firefights therefore register here
    // as NOISE events and sleeping groups roll whether to go and look, by personality or category. Two sources: the
    // simulated ghost fights (their window and their weapons are known) and real gunfire, read from the
    // game's own AI sound event, the one BSG's hearing sensor subscribes to, so the player's shots and any
    // awake bot's shots count. A single stray shot is not a fight: a real cluster needs a few shots first.
    private const float NoiseRangeLoud = 350f;
    private const float NoiseRangeSuppressed = 120f;
    private const float NoiseMinDistance = 40f;          // closer than this the skirmish / wake logic owns it
    private const float NoiseClusterRadius = 60f;        // shots this close together are the same firefight
    private const float NoiseLingerSeconds = 45f;        // a fight stays "audible" this long after its last shot
    private const int NoiseMinRealShots = 4;
    private const float NoiseReactionCooldownSeconds = 150f;
    private const float NoisePollIntervalSeconds = 2f;

    private readonly struct NoiseSource
    {
        public readonly Vector3 Position;
        public readonly float Range;
        public NoiseSource(Vector3 position, float range) { Position = position; Range = range; }
    }

    private sealed class NoiseEvent
    {
        public int Id;
        public Vector3 Position;
        public float Range;
        public float LastShotAt;
        public int Shots;
        public bool Simulated;
        public List<NoiseSource> Shooters;
        public readonly HashSet<int> SourceSquadIds = new();
        public readonly HashSet<int> RolledSquadIds = new();
        public readonly HashSet<object> RolledNativeGroups = new();
        public readonly HashSet<string> SourceProfiles = new();
    }

    private readonly List<NoiseEvent> _noises = new();
    private int _nextNoiseId;
    private readonly bool _hearingEnabled;
    private readonly float _hearingVeryAggressive, _hearingAggressive, _hearingAverage;
    private readonly float _hearingCautious, _hearingTimmy, _hearingPlayerScav;
    private bool _soundHooked;
    private float _nextNoisePollAt;
    private readonly Dictionary<object, float> _nativeNoiseReactionAt = new();
    private readonly List<object> _expiredNoiseGroups = new();

    private void HookGunfire()
    {
        if (!_hearingEnabled || _soundHooked) return;
        try
        {
            var dispatcher = Singleton<GlobalEventDispatcher>.Instance;
            if (dispatcher == null) return;
            dispatcher.OnSoundPlayed += OnAiSoundPlayed;
            _soundHooked = true;
        }
        catch (System.Exception e)
        {
            Log.Debug($"Ghost hearing: could not subscribe to the AI sound event ({e.Message}), real gunfire will not be heard");
        }
    }

    public void Dispose()
    {
        NativeGhostSystem.Clear();
        if (!_soundHooked) return;
        try { Singleton<GlobalEventDispatcher>.Instance.OnSoundPlayed -= OnAiSoundPlayed; } catch { }
        _soundHooked = false;
    }

    // Fires for every AI-audible sound in the raid: keep it to a type test and a short list walk.
    private void OnAiSoundPlayed(IPlayer player, Vector3 position, float power, AISoundType type)
    {
        if (_spectatorSuspended) return;
        if (type != AISoundType.gun && type != AISoundType.silencedGun) return;
        try
        {
            var range = type == AISoundType.gun ? NoiseRangeLoud : NoiseRangeSuppressed;
            var sourceSquadId = -1;
            if (player is Player shooter && shooter.IsAI)
            {
                var agent = _botRoster.GetAgent(shooter.AIData?.BotOwner);
                // A sleeper does not fire real rounds; a dormant shooter here is a simulated-fight artefact.
                if (agent != null && agent.IsDormant) return;
                if (agent?.Squad != null) sourceSquadId = agent.Squad.Id;
            }
            RegisterNoise(position, range, Time.time, simulated: false, sourceSquadId, -1, player?.ProfileId);
        }
        catch
        {
            // Never let a listener break the game's sound dispatch.
        }
    }

    private void RegisterNoise(Vector3 position, float range, float lastShotAt, bool simulated, int sourceA, int sourceB, string profileId = null)
    {
        NoiseEvent noise = null;
        for (var i = 0; i < _noises.Count; i++)
        {
            var n = _noises[i];
            if (n.Simulated != simulated) continue;
            if (Time.time - n.LastShotAt > NoiseLingerSeconds) continue;
            if ((n.Position - position).sqrMagnitude > NoiseClusterRadius * NoiseClusterRadius) continue;
            noise = n;
            break;
        }
        if (noise == null)
        {
            noise = new NoiseEvent { Id = ++_nextNoiseId, Position = position, Simulated = simulated };
            _noises.Add(noise);
        }
        noise.Range = Mathf.Max(noise.Range, range);
        noise.LastShotAt = Mathf.Max(noise.LastShotAt, lastShotAt);
        noise.Shots++;
        if (sourceA >= 0) noise.SourceSquadIds.Add(sourceA);
        if (sourceB >= 0) noise.SourceSquadIds.Add(sourceB);
        if (profileId != null) noise.SourceProfiles.Add(profileId);
    }

    /// <summary>Capture each shooter's position and hearing range once per simulated fight.</summary>
    private void RegisterFightNoise(GhostUnit a, GhostUnit b, float duration)
    {
        if (!_hearingEnabled) return;
        // Keep one event and one curiosity roll per squad/fight. Separate sources must not
        // multiply rolls, and separate fights must not merge just because their centres overlap.
        var noise = new NoiseEvent
        {
            Id = ++_nextNoiseId, Simulated = true, LastShotAt = Time.time + duration, Shots = 1,
            Shooters = new List<NoiseSource>(a.Count + b.Count),
        };
        AddFightNoiseSources(a, noise);
        AddFightNoiseSources(b, noise);
        if (noise.Shooters.Count > 0) _noises.Add(noise);
    }

    private void AddFightNoiseSources(GhostUnit unit, NoiseEvent noise)
    {
        if (unit.Squad != null) noise.SourceSquadIds.Add(unit.Squad.Id);
        for (var i = 0; i < unit.Agents.Count; i++)
            AddFightNoiseSource(unit.Agents[i].Player, noise);
        for (var i = 0; i < unit.VanillaBots.Count; i++)
            AddFightNoiseSource(unit.VanillaBots[i].GetPlayer, noise);
    }

    private void AddFightNoiseSource(Player player, NoiseEvent noise)
    {
        if (player?.HealthController is not { IsAlive: true }) return;
        noise.SourceProfiles.Add(player.ProfileId);
        var sound = WeaponSoundFromProfile(player.ProfileId);
        // Preserve the existing conservative range for an unresolved weapon.
        noise.Shooters.Add(new NoiseSource(player.Position, sound?.IsSilenced == true ? NoiseRangeSuppressed : NoiseRangeLoud));
    }

    private static bool TryGetNoiseSource(NoiseEvent noise, Vector3 listener,
        out Vector3 position, out float range, out float distance)
    {
        position = noise.Position;
        range = noise.Range;
        distance = 0f;
        var nearest = float.PositiveInfinity;
        var count = noise.Shooters?.Count ?? 0;
        for (var i = 0; i < (count == 0 ? 1 : count); i++)
        {
            var source = count == 0 ? new NoiseSource(noise.Position, noise.Range) : noise.Shooters[i];
            var squared = (source.Position - listener).sqrMagnitude;
            if (squared < NoiseMinDistance * NoiseMinDistance || squared > source.Range * source.Range
                || squared >= nearest) continue;
            nearest = squared;
            position = source.Position;
            range = source.Range;
        }
        if (float.IsPositiveInfinity(nearest)) return false;
        distance = Mathf.Sqrt(nearest);
        return true;
    }

    private GhostHearingCategory HearingCategory(Squad squad)
    {
        if (squad == null || squad.Members.Count == 0) return GhostHearingCategory.None;
        for (var i = 0; i < squad.Members.Count; i++)
            if (GhostHearingPolicy.Category(squad.Members[i].Bot) == GhostHearingCategory.None) return GhostHearingCategory.None;
        return GhostHearingPolicy.Category(squad.Members[0].Bot);
    }

    /// <summary>One policy per group; an excluded member keeps the whole group on its assignment.</summary>
    private float NoiseCuriosity(Squad squad)
    {
        var category = HearingCategory(squad);
        if (category == GhostHearingCategory.Pmc && squad.Personality != null)
        {
            switch (squad.Archetype)
            {
                case PersonalityArchetype.VeryAggressive: return _hearingVeryAggressive;
                case PersonalityArchetype.Aggressive: return _hearingAggressive;
                case PersonalityArchetype.Cautious: return _hearingCautious;
                case PersonalityArchetype.Timmy: return _hearingTimmy;
                default: return _hearingAverage;
            }
        }
        return CategoryCuriosity(category);
    }

    internal Api.OrbitGhostHearingState GetGhostHearingState(Agent agent)
    {
        var squad = agent?.Squad;
        // PollGhostHearing measures from member zero, once for the entire squad.
        if (!_hearingEnabled || !GhostMovementEnabled || agent == null || !agent.IsDormant || squad == null
            || squad.Members.Count == 0 || squad.Members[0] != agent || NoiseCuriosity(squad) <= 0f)
            return new Api.OrbitGhostHearingState();

        var now = Time.time;
        var state = squad.ExtractRequested ? "Extracting"
            : squad.InvestigateNoisePosition.HasValue ? "Investigating"
            : now < squad.GhostFightUntil ? "Fighting"
            : now - squad.LastNoiseReactionAt < NoiseReactionCooldownSeconds ? "Cooldown"
            : "Listening";
        return new Api.OrbitGhostHearingState
        {
            Range = NoiseRangeLoud,
            SuppressedRange = NoiseRangeSuppressed,
            MinimumDistance = NoiseMinDistance,
            State = state,
        };
    }

    private void PollGhostHearing(List<Squad> squads)
    {
        if (!_hearingEnabled || !GhostMovementEnabled) return;
        HookGunfire(); // the dispatcher may not exist yet when the system is constructed
        var now = Time.time;
        if (now < _nextNoisePollAt) return;
        _nextNoisePollAt = now + NoisePollIntervalSeconds;

        for (var i = _noises.Count - 1; i >= 0; i--)
            if (now - _noises[i].LastShotAt > NoiseLingerSeconds) _noises.RemoveAt(i);
        PruneNativeNoiseReactions(now);
        if (_noises.Count == 0) return;

        for (var s = 0; s < squads.Count; s++)
        {
            var squad = squads[s];
            if (squad == null || squad.Members.Count == 0 || !IsSquadDormant(squad)) continue;
            if (squad.ExtractRequested || squad.InvestigateNoisePosition.HasValue) continue;
            if (now < squad.GhostFightUntil || now - squad.LastNoiseReactionAt < NoiseReactionCooldownSeconds) continue;
            var curiosity = NoiseCuriosity(squad);
            if (curiosity <= 0f) continue;

            var listener = squad.Members[0].Position;
            for (var n = 0; n < _noises.Count; n++)
            {
                var noise = _noises[n];
                if (!noise.Simulated && noise.Shots < NoiseMinRealShots) continue;
                if (noise.SourceSquadIds.Contains(squad.Id) || noise.RolledSquadIds.Contains(squad.Id)
                    || SquadMadeNoise(squad, noise)) continue;
                if (!TryGetNoiseSource(noise, listener, out var sourcePosition, out var sourceRange, out var dist)) continue;

                noise.RolledSquadIds.Add(squad.Id); // one roll per squad per firefight, whatever the outcome
                // A fight at the edge of earshot is less tempting than one next door.
                var chance = curiosity * Mathf.Lerp(1f, 0.5f, dist / sourceRange);
                var investigate = Random.value <= chance;
                RecordGhostHearing(squad, noise, sourcePosition, sourceRange, listener, dist, chance, investigate, HearingLabel(squad));
                if (!investigate)
                {
                    Log.Debug($"GHOST HEARING: {squad} ({HearingLabel(squad)}) heard {(noise.Simulated ? "a ghost fight" : "real gunfire")} {dist:F0}m away and ignored it (chance {chance:P0})");
                    continue;
                }
                squad.InvestigateNoisePosition = sourcePosition;
                squad.LastNoiseReactionAt = now;
                _windowNoiseReactions++;
                Log.Info($"GHOST HEARING: {squad} ({HearingLabel(squad)}) heard {(noise.Simulated ? "a ghost fight" : $"real gunfire ({noise.Shots} shots)")} {dist:F0}m away (audible to {sourceRange:F0}m) and goes to look (chance {chance:P0}) source={sourcePosition}");
                break;
            }
        }
        PollNativeGhostHearing(now);
    }

    private static void RecordGhostHearing(Squad squad, NoiseEvent noise, Vector3 sourcePosition, float sourceRange, Vector3 listener,
        float distance, float chance, bool investigate, string category)
    {
        var members = new string[squad.Members.Count];
        for (var i = 0; i < members.Length; i++)
            members[i] = squad.Members[i].Player?.ProfileId;
        Api.OrbitTelemetry.PushGhostHearing(new Api.OrbitGhostHearing
        {
            RecordedAt = Time.realtimeSinceStartup,
            NoiseId = noise.Id,
            SquadId = squad.Id,
            ProfileId = members[0],
            MemberProfileIds = members,
            SourceX = sourcePosition.x, SourceY = sourcePosition.y, SourceZ = sourcePosition.z,
            ListenerX = listener.x, ListenerY = listener.y, ListenerZ = listener.z,
            Range = sourceRange,
            MinimumDistance = NoiseMinDistance,
            Distance = distance,
            Chance = chance,
            Simulated = noise.Simulated,
            Shots = noise.Shots,
            Investigate = investigate,
            Personality = category,
        });
    }

    private int _windowNoiseReactions;

    private float CategoryCuriosity(GhostHearingCategory category)
    {
        if (category == GhostHearingCategory.PlayerScav) return _hearingPlayerScav;
        if (category == GhostHearingCategory.Pmc) return _hearingAverage;
        var percent = category switch
        {
            GhostHearingCategory.Scav => _cfg.GhostHearingScavPct,
            GhostHearingCategory.Goons => _cfg.GhostHearingGoonsPct,
            GhostHearingCategory.Bosses => _cfg.GhostHearingBossesPct,
            GhostHearingCategory.Cultists => _cfg.GhostHearingCultistsPct,
            GhostHearingCategory.Raiders => _cfg.GhostHearingRaidersPct,
            GhostHearingCategory.Bloodhounds => _cfg.GhostHearingBloodhoundsPct,
            GhostHearingCategory.UntarHunters or GhostHearingCategory.RuafHunters => _cfg.GhostHearingUntarRuafHuntersPct,
            GhostHearingCategory.RoguesVsRaiders => _cfg.GhostHearingRoguesVsRaidersPct,
            GhostHearingCategory.ArmyOfTwo => _cfg.GhostHearingArmyOfTwoPct,
            GhostHearingCategory.Isb => _cfg.GhostHearingIsbPct,
            GhostHearingCategory.BlackDivision => _cfg.GhostHearingBlackDivisionPct,
            _ => 0, // Unclassified vanilla roles never investigate; no configurable fallback.
        };
        return Mathf.Clamp(percent, 0, 100) / 100f;
    }

    private string HearingLabel(Squad squad) => squad.Personality != null && HearingCategory(squad) == GhostHearingCategory.Pmc
        ? squad.Archetype.ToString() : HearingCategory(squad).ToString();

    private static bool SquadMadeNoise(Squad squad, NoiseEvent noise)
    {
        for (var i = 0; i < squad.Members.Count; i++)
            if (noise.SourceProfiles.Contains(squad.Members[i].Player.ProfileId)) return true;
        return false;
    }

    private bool NativeHearingGroup(List<BotOwner> group, out GhostHearingCategory category)
    {
        category = GhostHearingCategory.None;
        if (!_cfg.NativeGhostMovement || group.Count == 0) return false;
        for (var i = 0; i < group.Count; i++)
        {
            var bot = group[i];
            if (!_vanillaDormant.Contains(bot) || !NativeGhostSystem.OwnsInactiveMovement(bot)
                || GhostHearingPolicy.Category(bot) == GhostHearingCategory.None) return false;
        }
        category = GhostHearingPolicy.Category(group[0]);
        return CategoryCuriosity(category) > 0f;
    }

    private void PruneNativeNoiseReactions(float now)
    {
        _expiredNoiseGroups.Clear();
        foreach (var entry in _nativeNoiseReactionAt)
            if (!_vanillaGroups.ContainsKey(entry.Key) || now - entry.Value >= NoiseReactionCooldownSeconds)
                _expiredNoiseGroups.Add(entry.Key);
        foreach (var key in _expiredNoiseGroups) _nativeNoiseReactionAt.Remove(key);
    }

    private void PollNativeGhostHearing(float now)
    {
        foreach (var entry in _vanillaGroups)
        {
            var group = entry.Value;
            if (!NativeHearingGroup(group, out var category) || _nativeNoiseReactionAt.ContainsKey(entry.Key)) continue;
            var available = true;
            for (var i = 0; i < group.Count; i++) available &= _nativeGhosts.CanInvestigate(group[i]);
            if (!available) continue;
            var lead = group[0];
            var listener = lead.Position;
            for (var n = 0; n < _noises.Count; n++)
            {
                var noise = _noises[n];
                if (!noise.Simulated && noise.Shots < NoiseMinRealShots || noise.RolledNativeGroups.Contains(entry.Key)) continue;
                var own = false;
                for (var i = 0; i < group.Count; i++) own |= noise.SourceProfiles.Contains(group[i].ProfileId);
                if (own || !TryGetNoiseSource(noise, listener, out var source, out var range, out var distance)) continue;
                // Budget contention postpones the first roll, rather than silently consuming it.
                if (!NativeGhostNavigation.QueryAvailable) break;
                var chance = CategoryCuriosity(category) * Mathf.Lerp(1f, 0.5f, distance / range);
                var rolled = Random.value <= chance;
                noise.RolledNativeGroups.Add(entry.Key);
                var started = rolled && _nativeGhosts.Investigate(group, source);
                RecordNativeHearing(group, category, noise, source, range, listener, distance, chance, started);
                if (!started)
                {
                    if (rolled) Log.Info($"GHOST HEARING: native group ({lead.Profile.Nickname}) ({category}) could not investigate source={source}; native route retained");
                    continue;
                }
                _nativeNoiseReactionAt[entry.Key] = now;
                _windowNoiseReactions++;
                Log.Info($"GHOST HEARING: native group ({lead.Profile.Nickname} +{group.Count - 1}) ({category}) heard {(noise.Simulated ? "a ghost fight" : $"real gunfire ({noise.Shots} shots)")} {distance:F0}m away (audible to {range:F0}m) and goes to look (chance {chance:P0}) source={source}");
                break;
            }
        }
    }

    private static void RecordNativeHearing(List<BotOwner> group, GhostHearingCategory category, NoiseEvent noise,
        Vector3 source, float range, Vector3 listener, float distance, float chance, bool investigate)
    {
        var members = new string[group.Count];
        for (var i = 0; i < group.Count; i++) members[i] = group[i].ProfileId;
        Api.OrbitTelemetry.PushGhostHearing(new Api.OrbitGhostHearing
        {
            RecordedAt = Time.realtimeSinceStartup, NoiseId = noise.Id, SquadId = -group[0].Id - 1,
            ProfileId = members[0], MemberProfileIds = members,
            SourceX = source.x, SourceY = source.y, SourceZ = source.z,
            ListenerX = listener.x, ListenerY = listener.y, ListenerZ = listener.z,
            Range = range, MinimumDistance = NoiseMinDistance, Distance = distance, Chance = chance,
            Simulated = noise.Simulated, Shots = noise.Shots, Investigate = investigate, Personality = category.ToString(),
        });
    }

    internal Api.OrbitGhostHearingState GetNativeGhostHearingState(string profileId)
    {
        if (_hearingEnabled && GhostMovementEnabled)
            foreach (var entry in _vanillaGroups)
            {
                var group = entry.Value;
                if (group.Count == 0 || group[0].ProfileId != profileId || !NativeHearingGroup(group, out _)) continue;
                var fighting = false;
                var investigating = false;
                var available = true;
                for (var i = 0; i < group.Count; i++)
                {
                    fighting |= _nativeGhosts.InFight(group[i]);
                    investigating |= _nativeGhosts.IsInvestigating(group[i]);
                    available &= _nativeGhosts.CanInvestigate(group[i]);
                }
                var cooldown = _nativeNoiseReactionAt.TryGetValue(entry.Key, out var at) && Time.time - at < NoiseReactionCooldownSeconds;
                return new Api.OrbitGhostHearingState
                {
                    Range = NoiseRangeLoud, SuppressedRange = NoiseRangeSuppressed, MinimumDistance = NoiseMinDistance,
                    State = fighting ? "Fighting" : investigating ? "Investigating" : cooldown ? "Cooldown" : available ? "Listening" : "Busy",
                };
            }
        return new Api.OrbitGhostHearingState();
    }

}
