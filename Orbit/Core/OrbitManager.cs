using System.Collections.Generic;
using System.Diagnostics;
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

    // TEMP perf diagnostic (1.2.0-pre): per-subsystem tick accumulators, gated by Plugin.PerfTimingDiag and
    // flushed to the Debug log every PerfFlushSeconds so we can see where ORBIT spends its frame budget
    // (Strategy / Action / Movement / Look / Waypoint / Nav) on heavy maps like Streets. Zero overhead when off.
    private double _perfStrategy, _perfAction, _perfMovement, _perfLook, _perfWaypoint, _perfNav;
    private int _perfFrames;
    private float _perfLastFlush;
    private const float PerfFlushSeconds = 5f;

    public OrbitManager(BotsController botsController, BotRoster botRoster)
    {
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

        MovementSystem = new MovementSystem(NavJobExecutor, humanPlayers);
        LookSystem = new LookSystem();
        WaypointSystem = new WaypointSystem(MapId, Waypoints, botsController, humanPlayers);
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
        // Dedup by BSG bot id. MoreBotsAPI swaps a custom bot's brain at runtime (e.g. ISB types), which makes
        // BigBrain tear down and rebuild the brain — re-instantiating our OrbitBrainLayer and re-entering here
        // for a BotOwner that already has an Agent. Without this guard each swap leaves an ORPHAN agent: it
        // keeps a squad slot and gets dispatched objectives, but its layer is dead so it never physically
        // moves. A 7-bot ISB group balloons to 14 "members" (7 movers + 7 frozen orphans), which breaks
        // per-member dispatch and stalls the squad on the "all members arrived" gate (orphans never arrive).
        // Reuse the live agent instead — the newly-active layer drives the same Agent.
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
        // Every brain layer instantiated for this bot wires its own OnPlayerDead handler (the player outlives
        // brain swaps), so death fires RemoveAgent once per layer — all referencing the same deduped Agent.
        // The squad/entity teardown below is NOT idempotent (id slots get recycled), so bail unless this agent
        // is still the live registration for its bot. The first pass tears down and nulls the roster slot; any
        // further passes find GetAgent != agent and no-op.
        if (_botRoster.GetAgent(agent.Bot) != agent) return;

        AgentData.RemoveEntity(agent);
        SquadRegistry.RemoveAgent(agent);
        ActionManager.RemoveEntity(agent);
        _botRoster.RemoveAgent(agent);
    }

    public void Update()
    {
        // Fast path (default): no timing overhead unless the perf diagnostic is toggled on.
        if (Plugin.PerfTimingDiag == null || !Plugin.PerfTimingDiag.Value)
        {
            StrategyManager.Update();
            ActionManager.Update();
            MovementSystem.Update(_liveAgents);
            LookSystem.Update(_liveAgents);
            WaypointSystem.Update();
            NavJobExecutor.Update();
            return;
        }

        // TEMP perf diagnostic: time each subsystem with Stopwatch tick deltas (cheap, no allocation).
        var t0 = Stopwatch.GetTimestamp();
        StrategyManager.Update();
        var t1 = Stopwatch.GetTimestamp();
        ActionManager.Update();
        var t2 = Stopwatch.GetTimestamp();
        MovementSystem.Update(_liveAgents);
        var t3 = Stopwatch.GetTimestamp();
        LookSystem.Update(_liveAgents);
        var t4 = Stopwatch.GetTimestamp();
        WaypointSystem.Update();
        var t5 = Stopwatch.GetTimestamp();
        NavJobExecutor.Update();
        var t6 = Stopwatch.GetTimestamp();

        _perfStrategy += t1 - t0;
        _perfAction += t2 - t1;
        _perfMovement += t3 - t2;
        _perfLook += t4 - t3;
        _perfWaypoint += t5 - t4;
        _perfNav += t6 - t5;
        _perfFrames++;

        if (UnityEngine.Time.time - _perfLastFlush >= PerfFlushSeconds)
        {
            FlushPerfDiag();
            _perfLastFlush = UnityEngine.Time.time;
        }
    }

    private void FlushPerfDiag()
    {
        if (_perfFrames == 0) return;
        var freqMs = Stopwatch.Frequency / 1000.0; // ticks per millisecond
        double Avg(double ticks) => ticks / freqMs / _perfFrames;
        var total = Avg(_perfStrategy + _perfAction + _perfMovement + _perfLook + _perfWaypoint + _perfNav);
        Log.Debug($"PerfDiag ({_perfFrames}f avg, {_liveAgents.Count} agents): total={total:F2}ms/f | " +
                  $"Strategy={Avg(_perfStrategy):F2} Action={Avg(_perfAction):F2} Movement={Avg(_perfMovement):F2} " +
                  $"Look={Avg(_perfLook):F2} Waypoint={Avg(_perfWaypoint):F2} Nav={Avg(_perfNav):F2}");
        _perfStrategy = _perfAction = _perfMovement = _perfLook = _perfWaypoint = _perfNav = 0;
        _perfFrames = 0;
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
