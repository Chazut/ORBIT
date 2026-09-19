using System;
using System.Collections.Generic;
using System.Reflection;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

/// <summary>Executes native movement orders without registering an ORBIT Agent or assigning objectives.</summary>
public sealed class NativeGhostSystem
{
    private const float DecisionInterval = 0.25f;
    private const float WalkSpeed = 1.52f;
    private const float SprintSpeed = 4.2f;

    private sealed class Sleeper
    {
        public BotOwner Bot;
        public AICoreAgent<BotLogicDecision> Brain;
        public Action UpdateHunt;
        public bool CustomRole;
        public bool CouldStandBy;
        public float NextDecision;
        public float PinnedUntil;
        public float Stamina = 14f;
        public bool Exhausted;
        public float Travelled;
        public float ReportAt;
        public float NextDoorCheck;
        public Vector3 DoorDirection;
        public string Decision;
        public string WakeReason;
    }

    private static readonly Dictionary<BotOwner, Sleeper> Sleepers = new();
    private static readonly Dictionary<AICoreAgent<BotLogicDecision>, Sleeper> Brains = new();
    private static readonly Dictionary<int, string> CustomActions = new();
    private static Type _huntType;
    private static FieldInfo _huntActive;
    private static MethodInfo _huntUpdate;
    private static bool _huntResolved;
    public static bool DecisionGuardReady { get; set; }
    public static bool BrainBridgeReady { get; set; }

    private readonly DoorSystem _doors;
    private readonly HashSet<string> _reportedUnsupported = new();

    public NativeGhostSystem(DoorSystem doors) => _doors = doors;

    public static void Clear()
    {
        foreach (var state in Sleepers.Values)
            RestoreStandBy(state);
        Sleepers.Clear();
        Brains.Clear();
        CustomActions.Clear();
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

    public bool CanSleep(BotOwner bot)
    {
        if (!DecisionGuardReady || !BrainBridgeReady || bot?.Brain?.Agent == null || bot.Mover == null) return false;
        try
        {
            var decision = bot.Brain.LastDecision;
            if (!decision.HasValue || NeedsBody(bot)) return false;
            var name = CustomAction(decision.Value) ?? decision.Value.ToString();
            var hunt = HuntUpdater(bot) != null;
            if (!NativeGhostPolicy.Supports(decision.Value.ToString(), CustomAction(decision.Value), IsCustomRole(bot), hunt))
            {
                if (_reportedUnsupported.Add(bot.ProfileId + "|" + name))
                    Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} kept awake: unsupported {name} (role={bot.Profile.Info.Settings.Role}, hunt={hunt})");
                return false;
            }
            return NavMesh.SamplePosition(bot.Position, out _, 0.75f, NavMesh.AllAreas);
        }
        catch { return false; }
    }

    private static bool NeedsBody(BotOwner bot)
    {
        if (bot.Medecine is { Using: true } || bot.DoorOpener is { Interacting: true }
            || bot.PatrollingData?.CurPatrolPoint?.TargetPoint?.ActionData != null) return true;
        var inventory = bot.GetPlayer?.InventoryController;
        if (inventory != null)
            foreach (var operation in inventory.SelectEvents<ItemEventArgs>()) return true;
        return false;
    }

    public void Add(BotOwner bot)
    {
        var state = new Sleeper
        {
            Bot = bot,
            Brain = bot.Brain.Agent,
            UpdateHunt = HuntUpdater(bot),
            CustomRole = IsCustomRole(bot),
            CouldStandBy = bot.StandBy.CanDoStandBy,
            NextDecision = Time.time + (bot.Id & 7) * DecisionInterval / 8f,
            ReportAt = Time.time + 30f,
            Decision = bot.Brain.LastDecision?.ToString(),
        };
        Sleepers.Add(bot, state);
        Brains.Add(state.Brain, state);
        bot.StandBy.CanDoStandBy = false;
        Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} sleeping with original behaviour ({state.Decision}, hunt={state.UpdateHunt != null})");
    }

    public bool Remove(BotOwner bot)
    {
        if (ReferenceEquals(bot, null) || !Sleepers.TryGetValue(bot, out var state)) return false;
        Sleepers.Remove(bot);
        Brains.Remove(state.Brain);
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
            // Unity does not call this component while the body is inactive. Its own timers, target
            // selection and knowledge remain authoritative; the bridge never reads a hunt target directly.
            state.UpdateHunt?.Invoke();
            SyncMover(state.Bot);
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
        if (!NativeGhostPolicy.Supports(decision.ToString(), CustomAction(decision), state.CustomRole, state.UpdateHunt != null)
            || NeedsBody(state.Bot))
        {
            RequestWake(state, $"action requires its body: {CustomAction(decision) ?? decision.ToString()}");
            result = null; // BigBrain must not start or tick an unsupported action on an inactive body.
            return;
        }
        var name = CustomAction(decision) ?? decision.ToString();
        if (name != state.Decision)
        {
            state.Decision = name;
            Log.Debug($"NATIVE GHOST: {state.Bot.Profile.Nickname} original decision {name}");
        }
    }

    public static bool HandleBrainException(AICoreAgent<BotLogicDecision> brain, Exception exception)
    {
        if (exception == null || !Brains.TryGetValue(brain, out var state)) return false;
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
                state.Travelled = 0f;
                state.ReportAt = Time.time + 30f;
            }
            if (state.WakeReason != null || Time.time < state.PinnedUntil) return;
            if (bot == null || bot.IsDead || bot.Brain?.Agent != state.Brain || bot.gameObject.activeSelf)
            {
                RequestWake(state, "native lifecycle changed");
                return;
            }
            var mover = bot.Mover;
            SyncMover(bot);
            if (mover.Pause && mover.RemainPause > 0f) return;
            if (mover.Pause) mover.MovementResume();
            var path = mover.ActualPathController;
            if (!path.HavePath || !path.CheckShouldMove()) { mover.IsMoving = false; return; }

            var dt = Mathf.Min(Time.deltaTime, 0.1f);
            var sprint = mover.Sprinting && !mover.NoSprint && !state.Exhausted && mover.TargetPose >= 0.5f;
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
            var speed = sprint ? SprintSpeed : WalkSpeed * Mathf.Clamp(mover.DestMoveSpeed, 0f, 1f);
            if (!sprint && mover.TargetPose < 0.5f) speed *= 0.55f;
            var budget = speed * dt;
            for (var i = 0; i < 16 && path.HavePath && budget > 0f; i++)
            {
                var from = bot.Position;
                var corner = path.CurrentCorner();
                var distance = Vector3.Distance(from, corner);
                if (BlockedByDoor(state, from, corner)) { RequestWake(state, "door on native route"); return; }
                var next = Vector3.MoveTowards(from, corner, budget);
                if (DangerZones.IsInside(next) || !NavMesh.SamplePosition(next, out var hit, 0.75f, NavMesh.AllAreas)
                    || NavMesh.Raycast(from, hit.position, out _, NavMesh.AllAreas))
                {
                    RequestWake(state, "native route needs physical navigation");
                    return;
                }
                bot.GetPlayer.Transform.position = hit.position;
                var travelled = Vector3.Distance(from, hit.position);
                state.Travelled += travelled;
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
                    // IncCornerIndex completes a BSG path even when its last corner falls short of
                    // the requested destination. Let the awake mover handle that partial route.
                    RequestWake(state, "end of native path requires a replan");
                    break;
                }
                path.IncCornerIndex();
            }
        }
        catch (Exception e) { RequestWake(state, $"movement failed: {e.GetType().Name}: {e.Message}"); }
    }

    private bool BlockedByDoor(Sleeper state, Vector3 from, Vector3 target)
    {
        var direction = target - from;
        var distance = Mathf.Min(direction.magnitude, 1.5f);
        if (distance < 0.01f) return false;
        if (Time.time < state.NextDoorCheck && Vector3.Dot(direction.normalized, state.DoorDirection) > 0.95f)
            return false;
        state.NextDoorCheck = Time.time + (distance >= 1.5f ? 0.2f : 0f);
        state.DoorDirection = direction.normalized;
        var ray = new Ray(from + Vector3.up * 0.6f, direction.normalized);
        foreach (var door in _doors.Doors)
        {
            if (door == null || door.DoorState == EDoorState.Open
                || door.Collider == null || (door.transform.position - from).sqrMagnitude > 16f) continue;
            var bounds = door.Collider.bounds;
            bounds.Expand(0.35f);
            if (bounds.Contains(ray.origin) || bounds.IntersectRay(ray, out var hit) && hit <= distance) return true;
        }
        return false;
    }

    private static void SyncMover(BotOwner bot)
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
            if (bot.Mover.HasPathAndNoComplete) bot.Mover.RecalcWay();
        }
        catch (Exception e)
        {
            Log.Warning($"NATIVE GHOST: {bot?.Profile?.Nickname} wake resync failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
