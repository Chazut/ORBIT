using System;
using System.Collections.Generic;
using System.Reflection;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using Orbit.Navigation;
using Orbit.Patches;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

/// <summary>Executes native movement orders without registering an ORBIT Agent or assigning objectives.</summary>
public sealed partial class NativeGhostSystem
{
    private const float DecisionInterval = 0.25f;
    private const float WalkSpeed = 1.52f;
    private const float SprintSpeed = 4.2f;

    private sealed class Sleeper
    {
        public BotOwner Bot;
        public BotMover Mover;
        public AICoreAgent<BotLogicDecision> Brain;
        public Action UpdateHunt;
        public bool CustomRole;
        public bool CouldStandBy;
        public float NextDecision;
        public float PinnedUntil;
        public float Stamina = 14f;
        public bool Exhausted;
        public float Travelled;
        public int SainStopsProtected;
        public bool ReportedSainProtection;
        public bool ReportedProneDeferred;
        public float ReportAt;
        public NativeGhostDoors Doors;
        public string Decision;
        public string WakeReason;
        public NativeGhostNavigation Navigation;
        public NativeGhostAdapters Adapter;
        public NativeGhostRegroup Regroup;
        public HearingDetour Hearing;
    }

    private static readonly Dictionary<BotOwner, Sleeper> Sleepers = new();
    private static readonly Dictionary<AICoreAgent<BotLogicDecision>, Sleeper> Brains = new();
    private static readonly Dictionary<BotMover, Sleeper> Movers = new();
    private static readonly Dictionary<int, string> CustomActions = new();
    private static Type _huntType;
    private static FieldInfo _huntActive;
    private static MethodInfo _huntUpdate;
    private static bool _huntResolved;
    public static bool DecisionGuardReady { get; set; }
    public static bool BrainBridgeReady { get; set; }
    public static bool SainCleanupScopeReady { get; set; }
    public static bool SainStopGuardReady { get; set; }
    public static bool MoveOrderReady { get; set; }
    public static bool PointOrderReady { get; set; }
    public static bool WayOrderReady { get; set; }
    public static bool ReachOrderReady { get; set; }
    public static bool GoalOrderReady { get; set; }
    public static bool HasSleepers => Sleepers.Count > 0;

    private readonly DoorSystem _doors;
    private readonly HashSet<string> _reportedUnsupported = new();

    public NativeGhostSystem(DoorSystem doors) => _doors = doors;

    public static void Clear()
    {
        foreach (var state in Sleepers.Values)
        {
            EndHearing(state, "raid cleanup", false);
            RestoreStandBy(state);
            try { state.Navigation.RestorePathReach(); }
            catch (Exception e) { Log.Warning($"NATIVE GHOST: path restore during cleanup failed: {e.GetType().Name}"); }
        }
        Sleepers.Clear();
        Brains.Clear();
        Movers.Clear();
        NativeGhostDoors.Clear();
        NativeGhostNavigation.ResetBudget();
        NativeGhostOrders.Clear();
        CustomActions.Clear();
        NativePatrolDiagnostics.Clear();
        NativeGhostDiagnostics.Clear();
        SanitarPatrolMedicinePatch.Clear();
        HearingWakeGoals.Clear();
    }

    private static string CustomAction(BotLogicDecision decision)
    {
        var id = (int)decision;
        if (id < 9000) return null;
        if (CustomActions.TryGetValue(id, out var name)) return name;
        foreach (var pair in BrainManager.CustomLogicsReadOnly)
            CustomActions[pair.Value] = pair.Key.FullName;
        return CustomActions.TryGetValue(id, out name) ? name : "unsupported";
    }

    internal static string ActionName(BotLogicDecision decision) => CustomAction(decision) ?? decision.ToString();

    internal static bool CanRetainEnemy(BotOwner bot)
        => NativeGhostPartisan.CanRetainEnemy(bot) || NativeGhostIsb.CanRetainEnemy(bot);

    private static bool IsCustomRole(BotOwner bot) => (int)bot.Profile.Info.Settings.Role >= 200;

    private static Action HuntUpdater(BotOwner bot)
    {
        if (!_huntResolved)
        {
            _huntResolved = true;
            _huntType = AccessTools.TypeByName("MoreBotsAPI.Components.BotHuntManager");
            if (_huntType != null)
            {
                _huntActive = AccessTools.Field(_huntType, "active");
                _huntUpdate = AccessTools.Method(_huntType, "Update", Type.EmptyTypes);
            }
        }
        if (_huntType == null || _huntActive == null || _huntUpdate == null) return null;
        var component = bot.GetComponent(_huntType);
        if (component == null || _huntActive.GetValue(component) is not true) return null;
        return (Action)Delegate.CreateDelegate(typeof(Action), component, _huntUpdate);
    }

    public bool CanSleep(BotOwner bot, float humanDistance = -1f, int groupSize = 1)
    {
        bool Refuse(string reason) => NativeGhostDiagnostics.Refuse(bot, reason, humanDistance, groupSize);
        if (!DecisionGuardReady || !BrainBridgeReady || bot?.Brain?.Agent == null || bot.Mover == null)
            return Refuse("brain-bridge");
        if (!SainCleanupScopeReady || !SainStopGuardReady) return Refuse("sain-bridge");
        if (!MoveOrderReady || !PointOrderReady || !WayOrderReady || !GoalOrderReady) return Refuse("movement-bridge");
        if (!NativeGhostBodyPatches.Ready) return Refuse("body-operation-bridge");
        try
        {
            var decision = bot.Brain.LastDecision;
            if (!decision.HasValue) return Refuse("no-decision");
            var bodyReason = BodyReason(bot);
            if (bodyReason != null) return Refuse(bodyReason);
            if (CombatRequiresBody(bot)) return Refuse("native-combat");
            var name = CustomAction(decision.Value) ?? decision.Value.ToString();
            var hunt = HuntUpdater(bot) != null;
            var adapter = NativeGhostAdapters.Resolve(bot);
            if (!NativeGhostPolicy.Supports(decision.Value.ToString(), CustomAction(decision.Value), IsCustomRole(bot), hunt,
                adapter?.Checkpoint, adapter?.Warband == true, NativeGhostPartisan.Supports(bot, decision.Value.ToString()),
                NativeGhostCover.Supports(bot, decision.Value.ToString()), adapter?.Isb == true,
                NativeGhostZryachiy.Supports(bot, decision.Value.ToString())
                    || NativeGhostMarksman.SupportsLay(bot, decision.Value),
                NativeGhostMarksman.SupportsStandBy(bot, decision.Value), NativeGhostLoot.Supports(bot, decision.Value),
                NativeGhostPolicy.IsBlackDivisionPatrol((int)bot.Profile.Info.Settings.Role, NativeGhostPartisan.Layer(bot))))
            {
                if (_reportedUnsupported.Add(bot.ProfileId + "|" + name))
                    Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} kept awake: unsupported {name} (role={bot.Profile.Info.Settings.Role}, hunt={hunt})");
                return Refuse("unsupported-action");
            }
            return NavMesh.SamplePosition(bot.Position, out _, 0.75f, NavMesh.AllAreas)
                || Refuse("off-navmesh");
        }
        catch (Exception e) { return Refuse("sleep-check-" + e.GetType().Name); }
    }

    private static bool NeedsBody(BotOwner bot) => BodyReason(bot) != null;

    private static bool CombatRequiresBody(BotOwner bot)
        => bot.Memory?.IsUnderFire == true
            || (bot.Memory?.GoalEnemy != null && !CanRetainEnemy(bot));

    private static string BodyReason(BotOwner bot)
    {
        var partisan = NativeGhostPartisan.BodyReason(bot);
        if (partisan != null) return partisan;
        var isb = NativeGhostIsb.BodyReason(bot);
        if (isb != null) return isb;
        if (bot.WeaponManager?.Reload?.Reloading == true) return "reload";
        if (bot.WeaponManager?.Selector is { IsWeaponReady: false }) return "weapon-switch";
        if (bot.WeaponManager?.Grenades?.ThrowindNow == true) return "grenade";
        if (bot.Medecine is { Using: true }) return "medicine";
        if (bot.DoorOpener is { Interacting: true }) return "door";
        if (bot.DoorOpener is { _enteringDoorSequence: true }
            || bot.Mover?.CurrentState == EBotMoverState.NearDoor) return "door-sequence";
        var patrol = NativeGhostPatrol.BodyReason(bot);
        if (patrol != null) return patrol;
        var loot = NativeGhostLoot.BodyReason(bot);
        if (loot != null) return loot;
        var inventory = bot.GetPlayer?.InventoryController;
        if (inventory != null)
            foreach (var operation in inventory.SelectEvents<ItemEventArgs>()) return "inventory-operation";
        return null;
    }

    public void Add(BotOwner bot)
    {
        NativePatrolDiagnostics.BeforeSleep(bot);
        var state = new Sleeper
        {
            Bot = bot,
            Mover = bot.Mover,
            Doors = new NativeGhostDoors(bot, _doors),
            Brain = bot.Brain.Agent,
            UpdateHunt = HuntUpdater(bot),
            CustomRole = IsCustomRole(bot),
            CouldStandBy = bot.StandBy.CanDoStandBy,
            NextDecision = Time.time + (bot.Id & 7) * DecisionInterval / 8f,
            ReportAt = Time.time + 30f,
            Decision = bot.Brain.LastDecision.HasValue
                ? CustomAction(bot.Brain.LastDecision.Value) ?? bot.Brain.LastDecision.Value.ToString() : null,
            Navigation = new NativeGhostNavigation(bot, _doors),
            Adapter = NativeGhostAdapters.Resolve(bot),
            Regroup = NativeGhostRegroup.Resolve(bot),
        };
        Sleepers.Add(bot, state);
        Brains.Add(state.Brain, state);
        Movers.Add(bot.Mover, state);
        NativeGhostOrders.AdoptOnSleep(bot.Mover, state.Navigation);
        bot.StandBy.CanDoStandBy = false;
        NativeGhostMarksman.ReleaseStandBy(bot);
        state.Adapter?.ReissueOrder();
        if (state.Adapter != null)
            Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} adapter={state.Adapter.Name} ready; original goals and waits retained");
        if (NativeGhostPartisan.IsPartisan(bot))
            Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} adapter=Partizan tracking ready; original goals and waits retained");
        if (NativeGhostZryachiy.Supports(bot, state.Decision))
            Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} adapter=Zryachiy peaceful lay ready; native cover and posture retained");
        if (bot.Profile.Info.Settings.Role == WildSpawnType.marksman)
            Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} adapter=Sniper scav ready; native position and decisions retained ({state.Decision})");
        if (NativeGhostPolicy.IsBlackDivisionPatrol((int)bot.Profile.Info.Settings.Role, NativeGhostPartisan.Layer(bot)))
            Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} Black Division native patrol admitted; role={bot.Profile.Info.Settings.Role} layer={NativeGhostPartisan.Layer(bot)} decision={state.Decision}");
        Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} sleeping with original behaviour ({state.Decision}, hunt={state.UpdateHunt != null})");
    }

    public bool Remove(BotOwner bot)
    {
        if (ReferenceEquals(bot, null) || !Sleepers.TryGetValue(bot, out var state)) return false;
        PrepareHearingWake(state);
        Sleepers.Remove(bot);
        Brains.Remove(state.Brain);
        Movers.Remove(state.Mover);
        NativeGhostOrders.Forget(state.Mover);
        try { state.Navigation.RestorePathReach(); }
        catch (Exception e) { Log.Warning($"NATIVE GHOST: path restore on wake failed: {e.GetType().Name}"); }
        try { state.Adapter?.ReissueOrder(); }
        catch (Exception e) { Log.Warning($"NATIVE GHOST: native order refresh on wake failed: {e.Message}"); }
        RestoreStandBy(state);
        return true;
    }

    private static void RestoreStandBy(Sleeper state)
    {
        try { if (state.Bot?.StandBy != null) state.Bot.StandBy.CanDoStandBy = state.CouldStandBy; }
        catch { }
    }

    public string WakeReason(BotOwner bot)
        => Sleepers.TryGetValue(bot, out var state) ? state.WakeReason : null;

    public static bool OwnsInactiveMovement(BotOwner bot)
        => RetainsNativeState(bot) && Sleepers[bot].WakeReason == null;

    internal static bool RetainsNativeState(BotOwner bot)
        => bot != null && Sleepers.TryGetValue(bot, out var state) && !bot.IsDead
            && bot.GetPlayer?.HealthController?.IsAlive == true && !bot.gameObject.activeSelf && bot.Brain?.Agent == state.Brain;

    internal static bool DeferBodyOperation(BotOwner bot, string operation)
    {
        if (!RetainsNativeState(bot)) return false;
        RequestWake(Sleepers[bot], "body operation deferred: " + operation);
        return true;
    }

    internal static bool DeferProne(BotOwner bot)
    {
        if (!RetainsNativeState(bot)) return false;
        var state = Sleepers[bot];
        if (state.WakeReason != null) return true;
        if (NativeGhostMarksman.CanDeferProne(bot, state.Decision))
        {
            if (!state.ReportedProneDeferred)
            {
                state.ReportedProneDeferred = true;
                Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} sniper posture deferred; peaceful lay retained until wake");
            }
            return true;
        }
        RequestWake(state, "body operation deferred: BotLay.TryLay");
        return true;
    }

    internal static bool HandleDoorOperation(BotDoorOpener opener, Door requested, bool physical, out bool waiting)
    {
        waiting = false;
        var bot = opener?._owner;
        if (!RetainsNativeState(bot)) return false;
        var state = Sleepers[bot];
        waiting = true;
        if (physical) { RequestWake(state, "native door interaction requires its body"); return true; }
        if (state.WakeReason != null || Time.time < state.PinnedUntil
            || bot.Mover.Pause && bot.Mover.RemainPause > 0f) return true;
        try { waiting = WaitForDoor(state, requested); }
        catch (Exception e) { RequestWake(state, $"door bridge failed: {e.GetType().Name}: {e.Message}"); }
        return true;
    }

    private static bool WaitForDoor(Sleeper state, Door requested = null)
    {
        if (!state.Doors.Check(out var failure, requested)) return false;
        state.Navigation.Suspend();
        state.Adapter?.SuspendProgress();
        state.Bot.Mover.IsMoving = false;
        if (failure != null) RequestWake(state, failure);
        return true;
    }

    public static bool MovementPinned(BotOwner bot)
        => Sleepers.TryGetValue(bot, out var state) && Time.time < state.PinnedUntil;

    public static bool TryMoveOrder(BotMover mover, Vector3 destination, float reach, out NavMeshPathStatus status)
    {
        status = NavMeshPathStatus.PathInvalid;
        if (!Movers.TryGetValue(mover, out var state) || !RetainsNativeState(state.Bot)) return false;
        // A door/reload request can defer midway through a native action. Suppress its remaining
        // movement calls until wake instead of falling through to the inactive physical mover.
        if (state.WakeReason != null) return true;
        if (state.Hearing != null)
        {
            state.Hearing.Previous.Queue(destination, reach);
            status = NavMeshPathStatus.PathPartial;
            return true;
        }
        try { status = state.Navigation.Request(destination, reach); }
        catch (Exception e) { RequestWake(state, $"navigation failed: {e.GetType().Name}: {e.Message}"); }
        return true;
    }

    public static void CancelMoveOrder(BotMover mover)
    {
        NativeGhostOrders.Forget(mover);
        if (Movers.TryGetValue(mover, out var state)) (state.Hearing?.Previous ?? state.Navigation).Cancel();
    }

    public static void RecordMoveOrder(BotMover mover, Vector3 destination, NavMeshPathStatus status, string source)
    {
        if (!Movers.ContainsKey(mover)) NativeGhostOrders.Record(mover, destination, status, source);
    }

    public static bool AllowWayOrder(BotMover mover, Vector3[] way, float reach)
    {
        if (!Movers.TryGetValue(mover, out var state) || !RetainsNativeState(state.Bot)) return true;
        if (state.WakeReason != null) return false;
        try
        {
            var original = false;
            var target = NativeGhostOrders.ValidWay(way)
                ? NativeGhostOrders.WayGoal(mover, way[way.Length - 1], out original) : new Vector3(float.NaN, 0f, 0f);
            if (state.Hearing != null) { state.Hearing.Previous.Queue(target, reach); return false; }
            state.Navigation.RequestWay(target, way, reach, original ? "go-to-way-goal" : "go-to-way");
        }
        catch (Exception e) { RequestWake(state, $"navigation failed: {e.GetType().Name}: {e.Message}"); }
        return false;
    }

    public static bool TryRepeatGoal(BotMover mover, Vector3 target, out bool success)
    {
        success = false;
        if (mover == null || !Movers.TryGetValue(mover, out var state) || !RetainsNativeState(state.Bot)) return false;
        if (state.WakeReason != null) return true;
        if (state.Hearing != null) { state.Hearing.Previous.Queue(target, -1f); success = true; return true; }
        if (!state.Navigation.SameGoal(target)) return false;
        try { success = state.Navigation.Repeat() == NavMeshPathStatus.PathComplete; }
        catch (Exception e) { RequestWake(state, $"navigation failed: {e.GetType().Name}: {e.Message}"); }
        return true;
    }

    public static void RetainWayOrder(BotMover mover, Vector3[] way, float reach)
    {
        // Ghost orders were accepted before the original mover ran. A postfix must not
        // overwrite a recovery's status, including its explicit failure result.
        if (Movers.ContainsKey(mover)) return;
        if (!NativeGhostOrders.ValidWay(way)) { CancelMoveOrder(mover); return; }
        var target = NativeGhostOrders.WayGoal(mover, way[way.Length - 1], out var original);
        var source = original ? "go-to-way-goal" : "go-to-way";
        NativeGhostOrders.Record(mover, target, NavMeshPathStatus.PathComplete, source);
    }

    public static void SetReachDistance(BotMover mover, float reach)
    {
        if (Movers.TryGetValue(mover, out var state) && OwnsInactiveMovement(state.Bot))
            (state.Hearing?.Previous ?? state.Navigation).SetReachDistance(reach);
    }

    public static bool PreservePathDuringSainCleanup(BotOwner bot, BotMover mover)
    {
        if (!OwnsInactiveMovement(bot) || !ReferenceEquals(bot.Mover, mover)) return false;
        var state = Sleepers[bot];
        if (mover.ActualPathController.HavePath)
        {
            state.SainStopsProtected++;
            if (!state.ReportedSainProtection)
            {
                state.ReportedSainProtection = true;
                Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} preserved native route during SAIN inactive cleanup ({state.Decision})");
            }
        }
        return true;
    }

    public static void FailSainCleanupBridge(Exception exception)
    {
        SainCleanupScopeReady = false;
        foreach (var state in Sleepers.Values)
            RequestWake(state, $"SAIN cleanup bridge failed: {exception.GetType().Name}: {exception.Message}");
    }

    public void Pin(BotOwner bot, float until)
    {
        if (Sleepers.TryGetValue(bot, out var state)) state.PinnedUntil = until;
    }

    public bool InFight(BotOwner bot)
        => Sleepers.TryGetValue(bot, out var state) && Time.time < state.PinnedUntil;

    public static bool ScheduleBrain(AICoreAgent<BotLogicDecision> brain, out bool skip)
    {
        skip = false;
        if (!Brains.TryGetValue(brain, out var state)) return false;
        skip = true;
        if (state.WakeReason != null || Time.time < state.PinnedUntil || Time.time < state.NextDecision)
            return true;
        state.NextDecision = Time.time + DecisionInterval;
        try
        {
            if (state.Bot.IsDead || state.Bot.gameObject.activeSelf || state.Bot.Brain.Agent != brain)
            {
                RequestWake(state, "native lifecycle changed");
                return true;
            }
            if (NeedsBody(state.Bot))
            {
                RequestWake(state, "native interaction requires its body");
                return true;
            }
            NativeGhostCover.UpdateVoxel(state.Bot);
            NativeGhostPartisan.UpdateTracking(state.Bot);
            if (NeedsBody(state.Bot) || CombatRequiresBody(state.Bot))
            {
                RequestWake(state, "native tracking requires its body or combat");
                return true;
            }
            if (state.Doors.Pending && WaitForDoor(state)) return true;
            NativeGhostPartisan.ReleaseInvalidCover(state.Bot, state.Navigation);
            // Unity does not call this component while the body is inactive. Its own timers, target
            // selection and knowledge remain authoritative; the bridge never reads a hunt target directly.
            state.UpdateHunt?.Invoke();
            if (state.Adapter != null)
            {
                if (!state.Adapter.Valid())
                {
                    RequestWake(state, "native adapter state changed");
                    return true;
                }
                if (!state.Navigation.HasOrder && !state.Bot.Mover.ActualPathController.HavePath)
                    state.Adapter.ReissueOrder();
            }
            SyncMover(state.Bot);
            if (state.Hearing != null) return true;
            skip = false;
        }
        catch (Exception e) { RequestWake(state, $"adapter failed: {e.GetType().Name}: {e.Message}"); }
        return true;
    }

    public static void GuardDecision(AICoreStrategy<BotLogicDecision> strategy,
        ref AICoreActionResult<BotLogicDecision, CoreActionResultParams>? result)
    {
        if (strategy is not BaseBrain brain || brain._owner == null || !Sleepers.TryGetValue(brain._owner, out var state)) return;
        if (!result.HasValue) return;
        var decision = result.Value.Action;
        var customAction = CustomAction(decision);
        // RvR can register the group after it has already gone to sleep. Bind only when its own
        // action becomes active; an absent/invalid blackboard still takes the normal awake fallback.
        if (state.WakeReason == null && state.Adapter == null
            && customAction?.StartsWith("RoguesVRaiders.Objective.", StringComparison.Ordinal) == true)
        {
            state.Adapter = NativeGhostAdapters.ResolveWarband(state.Bot);
            if (state.Adapter != null)
            {
                state.Adapter.ReissueOrder();
                Log.Info($"NATIVE GHOST: {state.Bot.Profile.Nickname} adapter=RoguesVRaiders ready after registration; native order retained");
            }
        }
        if (state.WakeReason != null || !NativeGhostPolicy.Supports(decision.ToString(), CustomAction(decision), state.CustomRole, state.UpdateHunt != null,
                state.Adapter?.Checkpoint, state.Adapter?.Warband == true, NativeGhostPartisan.Supports(state.Bot, decision.ToString()),
                NativeGhostCover.Supports(state.Bot, decision.ToString()), state.Adapter?.Isb == true,
                NativeGhostZryachiy.Supports(state.Bot, decision.ToString())
                    || NativeGhostMarksman.SupportsLay(state.Bot, decision),
                NativeGhostMarksman.CanKeepStandByDecision(state.Bot, decision), NativeGhostLoot.Supports(state.Bot, decision),
                NativeGhostPolicy.IsBlackDivisionPatrol((int)state.Bot.Profile.Info.Settings.Role, NativeGhostPartisan.Layer(state.Bot)))
            || CombatRequiresBody(state.Bot)
            || NeedsBody(state.Bot))
        {
            RequestWake(state, $"action requires its body: {CustomAction(decision) ?? decision.ToString()}");
            result = null; // BigBrain must not start or tick an unsupported action on an inactive body.
            return;
        }
        var name = CustomAction(decision) ?? decision.ToString();
        if (state.Hearing != null) { result = null; return; }
        if (name != state.Decision)
        {
            CancelMoveOrder(state.Bot.Mover);
            state.Bot.Mover.ActualPathController.Stop();
            state.Decision = name;
            Log.Debug($"NATIVE GHOST: {state.Bot.Profile.Nickname} original decision {name}");
        }
    }

    public static bool HandleBrainException(AICoreAgent<BotLogicDecision> brain, Exception exception)
    {
        if (exception == null || !Brains.TryGetValue(brain, out var state)) return false;
        NativeGhostDiagnostics.BrainFailure(state.Bot, state.Decision, exception);
        RequestWake(state, $"brain failed: {exception.GetType().Name}: {exception.Message}");
        return true;
    }

    private static void RequestWake(Sleeper state, string reason)
    {
        if (state.WakeReason != null) return;
        state.WakeReason = reason;
        Log.Info($"NATIVE GHOST: {state.Bot?.Profile?.Nickname} fallback to awake: {reason}");
    }

    public void Move(BotOwner bot)
    {
        if (!Sleepers.TryGetValue(bot, out var state)) return;
        try
        {
            if (Time.time >= state.ReportAt)
            {
                if (state.Travelled > 0.1f)
                    Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} moved {state.Travelled:F1}m on native route ({state.Decision})");
                var route = bot.Mover.ActualPathController;
                Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} route status: moved={state.Travelled:F1}m/30s path={route.HavePath} paused={bot.Mover.Pause} fight={Time.time < state.PinnedUntil} sainStopsProtected={state.SainStopsProtected} {state.Navigation.Summary}");
                state.Travelled = 0f;
                state.SainStopsProtected = 0;
                state.ReportAt = Time.time + 30f;
            }
            if (state.WakeReason != null || Time.time < state.PinnedUntil)
            { state.Navigation.Suspend(); state.Adapter?.SuspendProgress(); return; }
            if (bot == null || bot.IsDead || bot.Brain?.Agent != state.Brain || bot.gameObject.activeSelf)
            {
                RequestWake(state, "native lifecycle changed");
                return;
            }
            var mover = bot.Mover;
            SyncMover(bot);
            if (mover.Pause && mover.RemainPause > 0f)
            { state.Navigation.Suspend(); state.Adapter?.SuspendProgress(); return; }
            if (mover.Pause) mover.MovementResume();
            if (state.Doors.Pending && WaitForDoor(state)) return;
            UpdateHearing(state);
            state.Navigation.Update();
            if (state.Hearing == null) NativeGhostPatrolRecovery.Update(bot, state.Decision, state.Navigation);
            if (state.Hearing == null && (state.Adapter?.RefreshStalledCheckpoint(bot, state.Decision, state.Navigation.Target, state.Navigation.RecoveringLocally) == true
                || state.Regroup?.Refresh(bot, state.Decision, state.Navigation) == true))
            {
                state.Navigation.Cancel();
                mover.ActualPathController.Stop();
                mover.IsMoving = false;
                return;
            }
            var path = mover.ActualPathController;
            if (!path.HavePath || !path.CheckShouldMove()) { mover.IsMoving = false; return; }

            var dt = Mathf.Min(Time.deltaTime, 0.1f);
            var sprint = state.Hearing == null && mover.Sprinting && !mover.NoSprint && !state.Exhausted && mover.TargetPose >= 0.5f;
            if (sprint)
            {
                state.Stamina = Mathf.Max(0f, state.Stamina - dt);
                if (state.Stamina == 0f) state.Exhausted = true;
            }
            else
            {
                state.Stamina = Mathf.Min(14f, state.Stamina + dt * 14f / 22f);
                if (state.Stamina >= 8.4f) state.Exhausted = false;
            }
            var speed = state.Hearing != null ? WalkSpeed : sprint ? SprintSpeed : WalkSpeed * Mathf.Clamp(mover.DestMoveSpeed, 0f, 1f);
            if (state.Hearing == null && !sprint && mover.TargetPose < 0.5f) speed *= 0.55f;
            var budget = speed * dt;
            for (var i = 0; i < 16 && path.HavePath && budget > 0f; i++)
            {
                var from = bot.Position;
                var corner = path.CurrentCorner();
                var distance = Vector3.Distance(from, corner);
                if (WaitForDoor(state)) return;
                var next = Vector3.MoveTowards(from, corner, budget);
                var danger = DangerZones.IsInside(next);
                var sampled = NavMesh.SamplePosition(next, out var hit, 0.75f, NavMesh.AllAreas);
                var edgeHit = default(NavMeshHit);
                var blocked = danger ? "danger-zone" : !sampled ? "off-navmesh"
                    : NavMesh.Raycast(from, hit.position, out edgeHit, NavMesh.AllAreas) ? "navmesh-edge"
                    : (hit.position - from).sqrMagnitude < 0.000001f && distance > 0.1f ? "no-navmesh-progress" : null;
                if (blocked != null)
                {
                    if (!state.Navigation.Blocked(blocked, next, corner, sampled ? hit.position : null,
                        blocked == "navmesh-edge" ? edgeHit.position : null)) RequestWake(state, "native route needs physical navigation");
                    return;
                }
                bot.GetPlayer.Transform.position = hit.position;
                var travelled = Vector3.Distance(from, hit.position);
                state.Travelled += travelled;
                state.Navigation.Walked(from, hit.position);
                SyncMover(bot);
                mover.IsMoving = travelled > 0.001f;
                if (travelled > 0.001f)
                {
                    mover.NormDirCurPoint = (hit.position - from).normalized;
                    mover._dirCurPoint = corner - hit.position;
                }
                if (!path.CheckShouldMove()) break;
                if (distance > budget) break;
                budget -= distance;
                if (path.CurPath.CurIndex + 1 >= path.CurPath.Length)
                {
                    // Reaching the last corner need not mean reaching the requested destination.
                    // Keep that order alive so the next update replans without waking the body.
                    path.Stop();
                    break;
                }
                path.IncCornerIndex();
            }
        }
        catch (Exception e) { RequestWake(state, $"movement failed: {e.GetType().Name}: {e.Message}"); }
    }

    internal static void SyncMover(BotOwner bot)
    {
        var mover = bot.Mover;
        var position = bot.Position;
        mover._lastGoodCastPoint = position;
        mover._prevSuccessLinkedFrom = position;
        mover._prevLinkPos = position;
        mover.PositionOnWayInner = position;
    }

    public static void ResyncAfterWake(BotOwner bot)
    {
        try
        {
            var player = bot.GetPlayer;
            player.MovementContext?.ResetFlying();
            if (NavMesh.SamplePosition(player.Position, out var hit, 0.75f, NavMesh.AllAreas))
                player.Teleport(hit.position);
            SyncMover(bot);
            ResumeHearingAfterWake(bot);
            if (bot.Mover.HasPathAndNoComplete) bot.Mover.RecalcWay();
            NativePatrolDiagnostics.AfterWake(bot);
        }
        catch (Exception e)
        {
            Log.Warning($"NATIVE GHOST: {bot?.Profile?.Nickname} wake resync failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
