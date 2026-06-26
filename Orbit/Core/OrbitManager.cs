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

    // Wedged-emergency-extracter watchdog. Force-despawn a committed emergency extracter that has been engaged
    // AND stationary AND out of combat past the timeout. Runs independent of IsActive: the common stuck case is
    // a bot that started healing (medsWorking → OrbitBrainLayer.IsActive=false), which detaches ORBIT so it
    // never reaches the exfil and ExtractAction (IsActive-gated) never despawns it.
    private const float EmergencyExtractStuckTimeoutSeconds = 30f;
    private const float EmergencyExtractStuckMoveRadiusSqr = 2f * 2f;
    private const float EmergencyExtractHealProgressEps = 0.005f; // HP-fraction rise since the last reset that counts as "heal working"
    private readonly List<Agent> _emergencyExtractDespawn = new();

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
        StrategyManager.Update();
        ActionManager.Update();
        TickEmergencyExtractWatchdog();
        MovementSystem.Update(_liveAgents);
        LookSystem.Update(_liveAgents);
        WaypointSystem.Update();
        NavJobExecutor.Update();
    }

    // See the EmergencyExtractStuck* constants. Force-despawn a committed emergency extracter that has been
    // engaged AND stationary AND out of combat past the timeout — handles the case where ORBIT is detached (the
    // bot started healing, or SAIN took over) so the normal exfil-arrival → Extracting → despawn path can never
    // run for it. A force-despawn here = the bot leaves the map (extracts) instead of standing still bleeding out.
    private void TickEmergencyExtractWatchdog()
    {
        var now = UnityEngine.Time.time;
        for (var i = 0; i < _liveAgents.Count; i++)
        {
            var agent = _liveAgents[i];
            if (agent == null || !agent.SoloExtractRequested || !agent.SoloExtractIsEmergency) continue;
            var bot = agent.Bot;
            var inCombat = bot?.Memory != null && (bot.Memory.HaveEnemy || bot.Memory.IsUnderFire);
            var hp = GotoObjectiveStrategy.HpFraction(agent);
            var moved = (agent.Position - agent.EmergencyExtractLastPos).sqrMagnitude > EmergencyExtractStuckMoveRadiusSqr;
            // A heal that's actually WORKING = HP climbing since the last reset. This is the real "is it healing"
            // test, not BotOwner.Medecine.Using: the death-spiral case (Butchery_Boss) had meds in progress the
            // whole time yet HP only fell, so gating on "using meds" would never fire and re-break that case. A
            // genuine heal (incl. a long multi-limb surgery, which restores HP in steps) bumps HP → re-arms here
            // → never cut short.
            var healing = hp > agent.EmergencyExtractLastHp + EmergencyExtractHealProgressEps;
            if (inCombat || moved || healing)
            {
                // Still moving, pinned by SAIN combat, or recovering HP — not stuck. Reset the no-progress clock.
                agent.EmergencyExtractLastPos = agent.Position;
                agent.EmergencyExtractLastHp = hp;
                agent.EmergencyExtractStillSince = now;
                continue;
            }
            if (now - agent.EmergencyExtractRequestedAt >= EmergencyExtractStuckTimeoutSeconds
                && now - agent.EmergencyExtractStillSince >= EmergencyExtractStuckTimeoutSeconds)
                _emergencyExtractDespawn.Add(agent);
        }
        // Despawn AFTER the scan — ForceDespawn → RemoveAgent mutates _liveAgents.
        for (var i = 0; i < _emergencyExtractDespawn.Count; i++)
        {
            var agent = _emergencyExtractDespawn[i];
            Log.Info($"{agent} emergency-extract watchdog: engaged {now - agent.EmergencyExtractRequestedAt:F0}s, stationary {now - agent.EmergencyExtractStillSince:F0}s, ORBIT not driving it (detached / can't reach exfil) — force-despawning (counts as extracted)");
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
