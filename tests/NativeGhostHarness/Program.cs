using EFT;
using MoreBotsAPI.Components;
using Orbit.Systems;
using UnityEngine;
using UnityEngine.AI;
using System.Text.Json;
using Orbit.Server.Config;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}

BotOwner Bot(BotLogicDecision action = BotLogicDecision.simplePatrol, int role = 1)
{
    var bot = new BotOwner();
    bot.Profile.Info.Settings.Role = (WildSpawnType)role;
    bot.Brain.LastDecision = action;
    bot.Brain.Agent._strategy = new BaseBrain { _owner = bot };
    return bot;
}

NativeGhostSystem System()
{
    NativeGhostSystem.Clear();
    NativeGhostSystem.DecisionGuardReady = NativeGhostSystem.BrainBridgeReady = true;
    Time.time = 0f;
    Time.deltaTime = 0.1f;
    NavMesh.Passable = true;
    Orbit.Navigation.DangerZones.Inside = false;
    return new NativeGhostSystem(new DoorSystem());
}

void Sleep(NativeGhostSystem system, BotOwner bot)
{
    Check(system.CanSleep(bot), "known behaviour can sleep");
    system.Add(bot);
    bot.gameObject.activeSelf = false;
}

void Step(NativeGhostSystem system, BotOwner bot, int count = 1)
{
    for (var i = 0; i < count; i++)
    {
        Time.time += Time.deltaTime;
        system.Move(bot);
    }
}

var system = System();
var patrol = Bot();
NativeGhostSystem.DecisionGuardReady = false;
Check(!system.CanSleep(patrol), "failed guard patch leaves native bots awake");
NativeGhostSystem.DecisionGuardReady = true;
NativeGhostSystem.BrainBridgeReady = false;
Check(!system.CanSleep(patrol), "failed brain patch leaves native bots awake");
NativeGhostSystem.BrainBridgeReady = true;
Check(!system.CanSleep(Bot(BotLogicDecision.shootFromPlace)), "combat is not executed asleep");
Check(!system.CanSleep(Bot(role: 1170)), "unrecognised faction behaviour is not frozen");
patrol.Inventory.Events.Add(new EFT.InventoryLogic.ItemEventArgs());
Check(!system.CanSleep(patrol), "inventory operation prevents sleep");
patrol.Inventory.Events.Clear();
Sleep(system, patrol);
Check(!patrol.StandBy.CanDoStandBy, "native standby cannot stop ghost decisions");
Step(system, patrol, 10);
Check(patrol.Position.sqrMagnitude == 0, "no destination is invented for an idle bot");

patrol.Mover.ActualPathController.SetPath(patrol, new(0, 0, 0), new(1, 0, 0), new(1, 0, 1));
Step(system, patrol, 30);
Check(Vector3.Distance(patrol.Position, new(1, 0, 1)) < 0.11f, "native corners followed to arrival");
Check(!patrol.Mover.HasPathAndNoComplete, "native path completion visible to original behaviour");
Check(patrol.Mover.ActualPathController.Completions == 1, "arrival is signalled once");
Check(system.WakeReason(patrol) == null, "supported arrival stays asleep");

// The next destination is supplied by the original behaviour, after observing path completion.
patrol.Mover.ActualPathController.SetPath(patrol, patrol.Position, new(2, 0, 1));
Step(system, patrol, 20);
Check(Vector3.Distance(patrol.Position, new(2, 0, 1)) < 0.11f, "successive native order is followed");
Check(patrol.Mover.ActualPathController.Completions == 2, "successive arrival delivered");

patrol.Mover.ActualPathController.SetPath(patrol, patrol.Position, new(8, 0, 1));
patrol.Mover.Pause = true;
patrol.Mover.RemainPause = 3f;
var before = patrol.Position;
Step(system, patrol, 5);
Check(patrol.Position == before, "author movement pause is preserved");
patrol.Mover.RemainPause = 0f;
Step(system, patrol);
Check(patrol.Position != before, "expired native pause resumes movement");

system.Pin(patrol, Time.time + 5f);
Check(system.InFight(patrol), "fight membership follows the bot even if the group leader changes");
before = patrol.Position;
Step(system, patrol, 20);
Check(patrol.Position == before, "simulated fight pins native movement");
Check(NativeGhostSystem.ScheduleBrain(patrol.Brain.Agent, out var skip) && skip, "simulated fight pins decisions");
system.Pin(patrol, 0f);
Check(!system.InFight(patrol), "fight pin is released");
Step(system, patrol);
Check(patrol.Position != before, "fight release resumes original route");

AICoreActionResult<BotLogicDecision, CoreActionResultParams>? result = new() { Action = BotLogicDecision.shootFromPlace };
NativeGhostSystem.GuardDecision(patrol.Brain.Agent._strategy, ref result);
Check(result == null && system.WakeReason(patrol) != null, "unsafe next action is blocked before execution");
before = patrol.Position;
Step(system, patrol, 5);
Check(patrol.Position == before, "pending fallback stops stale movement");
Check(system.Remove(patrol), "wake removes sleeper");
Check(patrol.StandBy.CanDoStandBy, "wake restores standby preference");
Check(!NativeGhostSystem.ScheduleBrain(patrol.Brain.Agent, out _), "awake brain no longer intercepted");
Check(!system.Remove(patrol), "death or despawn cleanup is idempotent");

system = System();
var hunter = Bot((BotLogicDecision)9001, 1170);
hunter.Hunt = new BotHuntManager { active = true };
Sleep(system, hunter);
Time.time = 1f;
Check(NativeGhostSystem.ScheduleBrain(hunter.Brain.Agent, out skip) && !skip, "hunt decision scheduled while body inactive");
Check(hunter.Hunt.Updates == 1, "original hunt component updated");
NativeGhostSystem.ScheduleBrain(hunter.Brain.Agent, out skip);
Check(skip && hunter.Hunt.Updates == 1, "hunt updates are throttled");
foreach (var action in new[] { 9001, 9002, 9003 })
{
    result = new() { Action = (BotLogicDecision)action };
    NativeGhostSystem.GuardDecision(hunter.Brain.Agent._strategy, ref result);
    Check(result.HasValue, "hunt regroup and search remain original decisions");
}
result = new() { Action = (BotLogicDecision)9004 };
NativeGhostSystem.GuardDecision(hunter.Brain.Agent._strategy, ref result);
Check(result == null, "unknown custom action cannot run on a sleeping body");

system = System();
var blocked = Bot();
Sleep(system, blocked);
blocked.Mover.ActualPathController.SetPath(blocked, new(0, 0, 0), new(2, 0, 0));
NavMesh.Passable = false;
Step(system, blocked);
Check(blocked.Position.sqrMagnitude == 0 && system.WakeReason(blocked) != null, "off-mesh segment wakes without teleporting through it");

system = System();
var doorBot = Bot();
var doors = new DoorSystem { Doors = [new EFT.Interactive.Door { DoorState = EFT.Interactive.EDoorState.Shut }] };
system = new NativeGhostSystem(doors);
Sleep(system, doorBot);
doorBot.Mover.ActualPathController.SetPath(doorBot, new(0, 0, 0), new(3, 0, 0));
Step(system, doorBot);
Check(doorBot.Position.sqrMagnitude == 0 && system.WakeReason(doorBot)?.Contains("door") == true, "closed door wakes before crossing");

system = System();
var partial = Bot();
Sleep(system, partial);
partial.Mover.ActualPathController.SetPath(partial, new(0, 0, 0), new(1, 0, 0));
partial.Mover.ActualPathController.TargetOverride = new(10, 0, 0);
Step(system, partial, 20);
Check(partial.Mover.ActualPathController.Completions == 0, "partial route does not falsely signal arrival");
Check(system.WakeReason(partial)?.Contains("replan") == true, "partial route returns to native mover for replan");

system = System();
var failed = Bot();
Sleep(system, failed);
Check(NativeGhostSystem.HandleBrainException(failed.Brain.Agent, new InvalidOperationException("test")), "native brain failure contained");
Check(system.WakeReason(failed) != null, "native brain failure requests wake");
var oldBrain = failed.Brain.Agent;
NativeGhostSystem.Clear();
Check(!NativeGhostSystem.ScheduleBrain(oldBrain, out _), "raid teardown removes old brain references");
Check(failed.StandBy.CanDoStandBy, "raid teardown restores saved state");

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
var existingConfig = JsonSerializer.Deserialize<OrbitServerConfig>("{\"ai_limiter\":{\"ghost_movement\":true,\"ghost_fights_mode\":\"simulated\"}}", jsonOptions);
Check(existingConfig.GhostMode.NativeGhostMovement, "existing configs enable native movement by default");
existingConfig.GhostMode.NativeGhostMovement = false;
var json = JsonSerializer.Serialize(existingConfig, jsonOptions);
Check(json.Contains("\"native_ghost_movement\":false"), "native toggle uses matching snake case wire key");
var restoredConfig = JsonSerializer.Deserialize<OrbitServerConfig>(json, jsonOptions);
Check(!restoredConfig.GhostMode.NativeGhostMovement, "explicit opt-out survives round trip");
Check(restoredConfig.GhostMode.GhostFightsMode == "simulated", "native toggle does not replace simulated fights");
Console.WriteLine($"Native Ghost: {checks} checks passed (engine API doubles; raid validation still required).");
