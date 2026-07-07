using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using Orbit.Config;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Systems;
using Orbit.Tasks;
using Orbit.Tasks.Actions;
using Orbit.Tasks.Strategies;

namespace Orbit.Core;

/// <summary>
/// The wire-everything orchestrator. Owns one instance of every subsystem (waypoint grid, movement, look,
/// doors, navmesh jobs, action/strategy managers, squad + bot rosters), drives them per- frame from the BSG
/// game loop, and routes Add/RemoveAgent through the right ECS datasets + registries.
///
/// External integrations (e.g. raid-review hooks, custom F12 actions) can extend the action / strategy /
/// component registries by handling the static <see cref="OnRegisterActions"/> etc. callbacks.
/// </summary>
public class OrbitManager
{
    public delegate void RegisterComponentsDelegate(DefinitionRegistry<IComponentArray> definitionRegistry);
    public delegate void RegisterActionsDelegate(DefinitionRegistry<Task<Agent>> actions);
    public delegate void RegisterStrategiesDelegate(DefinitionRegistry<Task<Squad>> strategies);

    public static RegisterComponentsDelegate OnRegisterAgentComponents;
    public static RegisterComponentsDelegate OnRegisterSquadComponents;

    public static RegisterActionsDelegate OnRegisterActions;
    public static RegisterStrategiesDelegate OnRegisterStrategies;

    public readonly string MapId;
    public readonly WaypointConfig Waypoints;

    public readonly AgentData AgentData;
    public readonly SquadData SquadData;

    public readonly NavJobExecutor NavJobExecutor;

    public readonly MovementSystem MovementSystem;
    public readonly LookSystem LookSystem;
    public readonly WaypointSystem WaypointSystem;
    public readonly DoorSystem DoorSystem;

    public readonly ActionManager ActionManager;
    public readonly StrategyManager StrategyManager;

    public readonly SquadRegistry SquadRegistry;

    private readonly BotRoster _botRoster;
    private readonly List<Agent> _liveAgents;

    private const float EmergencyExtractExfilProximitySqr = 12f * 12f;
    private const float EmergencyExtractStuckMoveRadiusSqr = 2f * 2f;
    // Kept above GotoObjectiveStrategy.EmergencyBleedStoppedSeconds (12s) so a bot that recovers can be cancelled
    // and rejoin its squad before this fires, instead of being force-despawned just for bleeding near an exfil.
    private const float EmergencyExtractStuckSeconds = 14f;
    private readonly List<Agent> _emergencyExtractDespawn = new();

    public OrbitManager(BotsController botsController, BotRoster botRoster)
    {
        Orbit.Helpers.PerfMonitor.Reset();

        var gameWorld = Singleton<GameWorld>.Instance;

        MapId = gameWorld.LocationId;
        Waypoints = new WaypointConfig();

        // Human players list — passed to MovementSystem's stuck-rescue path so teleports never happen within
        // line-of-sight of a real player.
        List<Player> humanPlayers = [];
        var allPlayers = gameWorld.AllAlivePlayersList;
        for (var i = 0; i < allPlayers.Count; i++)
        {
            var player = allPlayers[i];
            if (player != null && !player.AIData.IsAI)
                humanPlayers.Add(player);
        }

        AgentData = new AgentData();
        SquadData = new SquadData();

        _liveAgents = AgentData.Entities.Values;

        NavJobExecutor = new NavJobExecutor();

        WaypointSystem = new WaypointSystem(MapId, Waypoints, botsController, humanPlayers);
        MovementSystem = new MovementSystem(NavJobExecutor, humanPlayers, WaypointSystem);
        LookSystem = new LookSystem();
        DoorSystem = new DoorSystem();

        RegisterComponents();
        var actions = RegisterActions();
        var strategies = RegisterStrategies();

        ActionManager = new ActionManager(AgentData, actions);
        StrategyManager = new StrategyManager(SquadData, strategies);

        SquadRegistry = new SquadRegistry(SquadData, StrategyManager, WaypointSystem);
        _botRoster = botRoster;
    }

    public Agent AddAgent(BotOwner bot)
    {
        // A runtime brain swap (e.g. MoreBotsAPI on custom bots) makes BigBrain rebuild the brain and re-enter
        // here for a BotOwner that already has an Agent. Without this dedup each swap leaves an orphan agent that
        // holds a squad slot and never moves (its layer is dead), stalling the squad on the "all arrived" gate.
        var existing = _botRoster.GetAgent(bot);
        if (existing != null)
        {
            Log.Debug($"AddAgent: reusing {existing} (brain re-instantiation, no duplicate agent created)");
            return existing;
        }

        var agent = AgentData.AddEntity(bot, ActionManager.Tasks.Length);
        SquadRegistry.AddAgent(agent);
        _botRoster.AddAgent(agent);
        return agent;
    }

    public void RemoveAgent(Agent agent)
    {
        // Death can fire RemoveAgent once per brain layer wired for this bot (each layer's OnPlayerDead survives
        // brain swaps), but the teardown below is not idempotent (id slots get recycled). Bail unless this agent
        // is still the live registration; the first pass nulls the roster slot and later passes no-op.
        if (_botRoster.GetAgent(agent.Bot) != agent) return;

        AgentData.RemoveEntity(agent);
        SquadRegistry.RemoveAgent(agent);
        ActionManager.RemoveEntity(agent);
        _botRoster.RemoveAgent(agent);
    }

    public void Update()
    {
        Orbit.Helpers.PerfMonitor.Tick(_liveAgents.Count);
        StrategyManager.Update();
        ActionManager.Update();
        TickEmergencyExtractWatchdog();
        MovementSystem.Update(_liveAgents);
        LookSystem.Update(_liveAgents);
        WaypointSystem.Update();
        NavJobExecutor.Update();
    }

    // Force-despawn (= extract) an emergency extracter sat still at its exfil, out of combat, past the timeout.
    // Covers the case where ORBIT is detached (healing / SAIN) so the normal arrival -> Extracting path never runs.
    private void TickEmergencyExtractWatchdog()
    {
        var now = UnityEngine.Time.time;
        for (var i = 0; i < _liveAgents.Count; i++)
        {
            var agent = _liveAgents[i];
            if (agent == null) continue;
            // Committed extracters (solo emergency, or squad extract with the exfil as current objective)
            // sitting still within 12m of the exit, out of combat, are despawned past the timeout — covers
            // partial navmesh paths that end just outside the trigger volume. SharedTimer exits (V-Ex / BTR)
            // are excluded: their group wait+countdown flow owns the despawn.
            Waypoint target = null;
            if (agent.SoloExtractRequested && agent.SoloExtractIsEmergency)
            {
                target = agent.SoloExtractTarget;
            }
            else if (agent.Squad is { ExtractRequested: true }
                     && agent.Objective.Location is { Category: WaypointCategory.Exfil } squadExfil
                     && !(squadExfil.Target is EFT.Interactive.ExfiltrationPoint
                          { Settings.ExfiltrationType: EFT.Interactive.EExfiltrationType.SharedTimer }))
            {
                target = squadExfil;
            }
            if (target == null) continue;
            var bot = agent.Bot;
            var inCombat = bot?.Memory != null && (bot.Memory.HaveEnemy || bot.Memory.IsUnderFire);
            var atExfil = target != null && (agent.Position - target.Position).sqrMagnitude <= EmergencyExtractExfilProximitySqr;
            var moved = (agent.Position - agent.EmergencyExtractLastPos).sqrMagnitude > EmergencyExtractStuckMoveRadiusSqr;
            // Any state other than sitting still at the exfil resets the clock, so it's never despawned elsewhere.
            if (!atExfil || inCombat || moved)
            {
                agent.EmergencyExtractLastPos = agent.Position;
                agent.EmergencyExtractStillSince = now;
                continue;
            }
            if (agent.Objective.Status != ObjectiveStatus.Extracting
                && now - agent.EmergencyExtractStillSince >= EmergencyExtractStuckSeconds)
                _emergencyExtractDespawn.Add(agent);
        }
        // Despawn after the scan, since ForceDespawn -> RemoveAgent mutates _liveAgents.
        for (var i = 0; i < _emergencyExtractDespawn.Count; i++)
        {
            var agent = _emergencyExtractDespawn[i];
            Log.Info($"{agent} extract watchdog: stuck at exfil {(object)agent.SoloExtractTarget ?? agent.Objective.Location} for {now - agent.EmergencyExtractStillSince:F0}s without extracting — force-despawning (counts as extracted)");
            ExtractAction.ForceDespawn(agent);
        }
        _emergencyExtractDespawn.Clear();
    }

    private void RegisterComponents()
    {
        var agentComponentDefs = new DefinitionRegistry<IComponentArray>();
        var squadComponentDefs = new DefinitionRegistry<IComponentArray>();

        OnRegisterAgentComponents?.Invoke(agentComponentDefs);
        foreach (var value in agentComponentDefs.Values)
            AgentData.RegisterComponent(value);

        OnRegisterSquadComponents?.Invoke(squadComponentDefs);
        foreach (var value in squadComponentDefs.Values)
            SquadData.RegisterComponent(value);
    }

    private Task<Agent>[] RegisterActions()
    {
        var actions = new DefinitionRegistry<Task<Agent>>();

        actions.Add(new GotoObjectiveAction(AgentData, MovementSystem, WaypointSystem, 0.15f));
        actions.Add(new LootContainerAction(AgentData, WaypointSystem, 0.1f));
        actions.Add(new ExtractAction(AgentData, 0.1f));
        actions.Add(new GuardAction(AgentData, MovementSystem, 0.1f));

        OnRegisterActions?.Invoke(actions);

        return actions.Values.ToArray();
    }

    private Task<Squad>[] RegisterStrategies()
    {
        var strategies = new DefinitionRegistry<Task<Squad>>();

        strategies.Add(new GotoObjectiveStrategy(SquadData, WaypointSystem, 0.25f));

        OnRegisterStrategies?.Invoke(strategies);

        return strategies.Values.ToArray();
    }
}
