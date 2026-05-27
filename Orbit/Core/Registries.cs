using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EFT;
using Orbit.Entities;
using Orbit.Sain;
using Orbit.Systems;

namespace Orbit.Core;

// ╔══════════════════════════════════════════════════════════════════╗
// ║ Registries — bidirectional maps between BSG runtime entities and  ║
// ║ our own. SquadRegistry groups bots into squads based on BSG's     ║
// ║ BotsGroup id; BotRoster maps BSG bot id back to our Agent via a   ║
// ║ growable array (faster than a dictionary for the small-int keys). ║
// ╚══════════════════════════════════════════════════════════════════╝

/// <summary>
/// BSG-bot-id → Agent lookup. Bot ids are dense small integers, so a
/// growable List with null padding outperforms a Dictionary at the access
/// pattern we have (hot path: per-frame patch hooks checking "is this bot
/// one of ours?").
/// </summary>
public class BotRoster
{
    private readonly List<Agent> _agents = [];

    public void AddAgent(Agent agent)
    {
        var bsgId = agent.Bot.Id;

        if (bsgId >= _agents.Count)
        {
            var padding = bsgId - _agents.Count + 1;
            for (var i = 0; i < padding; i++)
                _agents.Add(null);
        }

        _agents[bsgId] = agent;
    }

    public void RemoveAgent(Agent agent)
    {
        var bsgId = agent.Bot.Id;

        if (bsgId >= _agents.Count)
            return;

        _agents[bsgId] = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsOrbitActive(BotOwner bot)
    {
        var bsgId = bot.Id;

        if (bot.Id >= _agents.Count)
            return false;

        var agent = _agents[bsgId];

        return agent != null && agent.IsActive;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Agent GetAgent(BotOwner bot)
    {
        if (bot == null) return null;
        var bsgId = bot.Id;
        if (bsgId < 0 || bsgId >= _agents.Count) return null;
        return _agents[bsgId];
    }
}

/// <summary>
/// Groups agents into squads based on BSG's BotsGroup id, with special
/// handling for vanilla scavs (always solo, never grouped) and PMC
/// squads (deferred SAIN personality resolution before main-objective
/// generation).
/// </summary>
public class SquadRegistry(SquadData squadData, StrategyManager strategyManager, WaypointSystem waypointSystem)
{
    private readonly Dictionary<int, int> _squadIdMap = new(16);

    public void AddAgent(Agent agent)
    {
        var bsgSquadId = agent.Bot.BotsGroup.Id;

        Squad squad;

        var role = agent.Bot.Profile.Info.Settings.Role;

        // Vanilla scavs (assault / assaultGroup) always get their own
        // 1-member squad — never grouped. The old 'Brown Tide' toggle that
        // let scavs share squads (and congeal into map-spanning waves) has
        // been removed; the settled preference is solo-scav behaviour.
        // Goons, raiders, bosses, PMCs etc still group via BSG BotsGroup id.
        if (role is WildSpawnType.assault or WildSpawnType.assaultGroup)
        {
            squad = AddNewSquad(agent);
        }
        else
        {
            if (_squadIdMap.TryGetValue(bsgSquadId, out var squadId))
            {
                squad = squadData.Entities[squadId];
            }
            else
            {
                squad = AddNewSquad(agent);
                _squadIdMap.Add(bsgSquadId, squad.Id);
            }
        }

        squad.AddAgent(agent);
        agent.Squad = squad;
        Log.Debug($"Added {agent} to {squad} with {squad.Size} members");
    }

    public void RemoveAgent(Agent agent)
    {
        var squad = agent.Squad;
        squad.RemoveAgent(agent);
        Log.Debug($"Removed {agent} from {squad} with {squad.Size} members remaining");

        if (squad.Size > 0)
        {
            // Reassign leader if we just removed it.
            if (agent != squad.Leader) return;

            squad.Leader = squad.Members[^1];
            squad.Leader.IsLeader = true;
            Log.Debug($"{squad} assigned new leader {squad.Leader}");
            return;
        }

        Log.Debug($"Removing empty {squad}");
        _squadIdMap.Remove(agent.Bot.BotsGroup.Id);
        squadData.Entities.Remove(squad);
        strategyManager.RemoveEntity(squad);
    }

    private Squad AddNewSquad(Agent agent)
    {
        // BSG quirk: BotsGroup.TargetMembersCount is off-by-one (always 0
        // for scavs, etc.). Bump by 1 to get the real count.
        var targetMembersCount = agent.Bot.BotsGroup.TargetMembersCount + 1;
        var squad = squadData.AddEntity(strategyManager.Tasks.Length, targetMembersCount);
        Log.Debug($"Registered new {squad} with {targetMembersCount} target members");
        squad.Leader = agent;
        squad.Leader.IsLeader = true;
        // Capture leader's spawn world position — used by the waypoint
        // system to derive an Infiltration name when Profile.Info.EntryPoint
        // is empty (mod-spawned PMCs, special-spawn bots).
        squad.SpawnPosition = agent.Bot.GetPlayer != null ? agent.Bot.GetPlayer.Position : agent.Bot.Position;
        Log.Debug($"{squad} assigned new leader {squad.Leader} (spawn pos captured: {squad.SpawnPosition})");

        // Personality resolution is DEFERRED for PMC squads. SAIN attaches
        // its BotComponent asynchronously after spawn (1-2s typical) so
        // calling SainPersonality.GetBrainName at squad creation almost
        // always returns null. Flag the squad as "pending" and let the
        // strategy retry the lookup at every tick until it resolves or the
        // 5 s deadline elapses. Main objectives are NOT generated here for
        // PMCs — they're rolled once the personality is locked, so the mix
        // weights match the resolved archetype.
        //
        // Non-PMC (scavs, PlayerScavs, bosses): no personality, no deferral.
        // Generate mains immediately using the global weights.
        var roleLeader = agent.Bot.Profile?.Info?.Settings?.Role;
        var isPmc = roleLeader.HasValue && IsPmcRole(roleLeader.Value);
        squad.Archetype = PersonalityArchetype.Average;
        squad.Personality = null;
        squad.SainResolutionPending = false;
        if (isPmc && (Plugin.SainPersonalityEnabled?.Value ?? false))
        {
            squad.SainResolutionPending = true;
            squad.SainResolveDeadline = UnityEngine.Time.time + 5f;
            Log.Info($"{squad} PMC personality resolution deferred — strategy will retry every tick for 5s before locking to Average");
            return squad; // skip MainObjectiveBuilder until resolved (see TryResolvePersonality)
        }

        // Roll the squad's main objectives at creation. Builder is
        // player-blind and self-contained — skips boss/raider/goon roles
        // internally, picks anchors from static map data.
        MainObjectiveBuilder.Generate(squad, waypointSystem);

        return squad;
    }

    /// <summary>
    /// Strategy-tick callback for PMC squads with deferred personality
    /// resolution. Retries the SAIN brain lookup; once resolved (or the
    /// 5 s deadline elapses, locking to Average), rolls the
    /// PersonalityProfile and generates the squad's main objectives using
    /// the resolved archetype's mix weights. Safe to call every tick —
    /// early-returns if the squad isn't pending.
    /// </summary>
    public void TryResolvePersonality(Squad squad)
    {
        if (squad == null || !squad.SainResolutionPending) return;

        var brainName = SainPersonality.GetBrainName(squad.Leader.Bot);
        var resolved = !string.IsNullOrEmpty(brainName);
        var timedOut = UnityEngine.Time.time >= squad.SainResolveDeadline;
        if (!resolved && !timedOut) return; // still waiting for SAIN

        squad.Archetype = SainPersonality.MapBrainToArchetype(brainName);
        squad.Personality = PersonalityProfile.Roll(squad.Archetype);
        squad.SainResolutionPending = false;
        var reason = resolved ? "SAIN resolved" : "timeout, lock to Average";
        Log.Info($"{squad} PMC personality locked: brain='{brainName ?? "(none)"}' → {squad.Archetype} ({reason}) | extract={squad.Personality.ExtractLootThreshold:N0}₽ coverage={squad.Personality.LootCoverage:P0} mains={squad.Personality.MainCount} sprint={squad.Personality.SprintPropensity:F1}");

        // Now that the archetype is known, generate the main objectives
        // using the locked mix weights.
        MainObjectiveBuilder.Generate(squad, waypointSystem);
    }

    // Inlined PMC check — full BotTypeUtils helper lands in Phase 5.
    private static bool IsPmcRole(WildSpawnType role)
        => role == WildSpawnType.pmcBEAR || role == WildSpawnType.pmcUSEC;
}
