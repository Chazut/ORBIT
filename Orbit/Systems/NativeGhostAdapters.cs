using System;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using EFT;
using HarmonyLib;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

/// <summary>Checks optional mod state without replacing its decisions, targets or timers.</summary>
internal sealed class NativeGhostAdapters
{
    private sealed class CheckpointBinding
    {
        internal Type Type;
        internal Func<object, bool> Available;
        internal Func<object, CustomNavigationPoint> Point;
        internal Func<object, CustomNavigationPoint> Choose;
        internal Action<object, CustomNavigationPoint> Set;

        internal CheckpointBinding(string typeName)
        {
            Type = AccessTools.TypeByName(typeName);
            if (Type == null) return;
            var available = AccessTools.Method(Type, "CanDoCheckpointActions", Type.EmptyTypes);
            var point = AccessTools.Field(Type, "guardPoint");
            if (available == null || available.ReturnType != typeof(bool)
                || point == null || point.FieldType != typeof(CustomNavigationPoint)) return;
            var instance = Expression.Parameter(typeof(object), "instance");
            Available = Expression.Lambda<Func<object, bool>>(
                Expression.Call(Expression.Convert(instance, Type), available), instance).Compile();
            Point = FieldGetter<CustomNavigationPoint>(point);
            var choose = AccessTools.Method(Type, "GetCheckpointCoverPoint", Type.EmptyTypes);
            var set = AccessTools.Method(Type, "SetGuardPoint", new[] { typeof(CustomNavigationPoint) });
            var dirty = AccessTools.Field(Type, "guardPointDirty");
            if (choose?.ReturnType != typeof(CustomNavigationPoint) || set == null || dirty?.FieldType != typeof(bool)) return;
            Choose = Expression.Lambda<Func<object, CustomNavigationPoint>>(
                Expression.Call(Expression.Convert(instance, Type), choose), instance).Compile();
            var value = Expression.Parameter(typeof(CustomNavigationPoint), "point");
            Set = Expression.Lambda<Action<object, CustomNavigationPoint>>(Expression.Block(
                Expression.Call(Expression.Convert(instance, Type), set, value),
                Expression.Assign(Expression.Field(Expression.Convert(instance, Type), dirty), Expression.Constant(false))),
                instance, value).Compile();
        }
    }

    private static bool _resolved;
    private static CheckpointBinding _untar, _ruaf, _isb;
    private static Func<BotOwner, bool> _rvrMember;
    private static Func<BotsGroup, object> _rvrBoard;
    private static Func<object, IDictionary> _rvrOrders;
    private readonly Func<bool> _valid;
    private readonly Action _reissue;
    private Func<CustomNavigationPoint> _checkpointPoint, _chooseCover;
    private Action<CustomNavigationPoint> _setCover;
    private CustomNavigationPoint _observedPoint;
    private Vector3 _progressPosition;
    private float _progressAt, _retryAt;
    private bool _observing;
    private static int _coverSearchFrame = -1;
    internal readonly string Checkpoint;
    internal readonly bool Warband;
    internal readonly bool Isb;
    internal string Name => Warband ? "RoguesVRaiders" : Isb ? "ISB tactics/hunt" : Checkpoint + " checkpoint";

    private NativeGhostAdapters(string checkpoint, bool warband, Func<bool> valid, Action reissue = null, bool isb = false)
    { Checkpoint = checkpoint; Warband = warband; _valid = valid; _reissue = reissue; Isb = isb; }

    private static Func<object, T> FieldGetter<T>(FieldInfo field)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        return Expression.Lambda<Func<object, T>>(Expression.Convert(
            Expression.Field(Expression.Convert(instance, field.DeclaringType), field), typeof(T)), instance).Compile();
    }

    private static void ResolveBindings()
    {
        if (_resolved) return;
        _resolved = true;
        // These managers have no per-bot Update loop. Their original BigBrain actions advance them.
        try { _untar = new CheckpointBinding("TacticalToasterUNTARGH.Components.BotUntarManager"); }
        catch (Exception e) { Log.Warning($"NATIVE GHOST: UNTAR checkpoint binding unavailable: {e.Message}"); }
        try { _ruaf = new CheckpointBinding("RUAFComeHome.Components.BotRuafManager"); }
        catch (Exception e) { Log.Warning($"NATIVE GHOST: RUAF checkpoint binding unavailable: {e.Message}"); }
        try { _isb = new CheckpointBinding("ISBSpecialForces.Components.BotISBManager"); }
        catch (Exception e) { Log.Warning($"NATIVE GHOST: ISB checkpoint binding unavailable: {e.Message}"); }
        try
        {
            var registry = AccessTools.TypeByName("RoguesVRaiders.SquadRegistry");
            var controller = AccessTools.TypeByName("RoguesVRaiders.Objective.RvRObjectiveController");
            if (registry == null || controller == null) return;
            var member = AccessTools.Method(registry, "IsRvRSquadMember", new[] { typeof(BotOwner) });
            var board = AccessTools.Method(controller, "GetBlackboard", new[] { typeof(BotsGroup) });
            var orders = board == null ? null : AccessTools.Field(board.ReturnType, "LastOrderTarget");
            if (member == null || board == null || orders == null || !typeof(IDictionary).IsAssignableFrom(orders.FieldType)) return;
            _rvrMember = (Func<BotOwner, bool>)Delegate.CreateDelegate(typeof(Func<BotOwner, bool>), member);
            var group = Expression.Parameter(typeof(BotsGroup), "group");
            _rvrBoard = Expression.Lambda<Func<BotsGroup, object>>(
                Expression.Convert(Expression.Call(board, group), typeof(object)), group).Compile();
            _rvrOrders = FieldGetter<IDictionary>(orders);
        }
        catch (Exception e) { Log.Warning($"NATIVE GHOST: RoguesVRaiders binding unavailable: {e.Message}"); }
    }

    internal static NativeGhostAdapters Resolve(BotOwner bot)
    {
        ResolveBindings();
        // ISB also assigns event checkpoints to Black Division roles.
        var isbCheckpoint = CheckpointAdapter(bot, _isb, "ISB");
        if (isbCheckpoint != null) return isbCheckpoint;
        var role = bot.Profile.Info.Settings.Role.ToString();
        var checkpoint = role.IndexOf("untar", StringComparison.OrdinalIgnoreCase) >= 0
            ? CheckpointAdapter(bot, _untar, "UNTAR")
            : role.IndexOf("ruaf", StringComparison.OrdinalIgnoreCase) >= 0
                || role.IndexOf("remnant", StringComparison.OrdinalIgnoreCase) >= 0
                ? CheckpointAdapter(bot, _ruaf, "RUAF") : null;
        if (checkpoint != null) return checkpoint;
        var isb = NativeGhostIsb.Agent(bot);
        if (isb != null && NativeGhostIsb.Valid(bot, isb))
            return new NativeGhostAdapters(null, false, () => NativeGhostIsb.Valid(bot, isb),
                () => NativeGhostIsb.ReissueOrder(bot, isb), isb: true);
        return ResolveWarband(bot);
    }

    internal static NativeGhostAdapters ResolveWarband(BotOwner bot)
    {
        ResolveBindings();
        if (_rvrMember == null || _rvrBoard == null || _rvrOrders == null || bot.BotsGroup == null || !_rvrMember(bot)) return null;
        var group = bot.BotsGroup;
        var board = _rvrBoard(group);
        if (board == null || _rvrOrders(board) == null) return null;
        // The scheduler lives outside bot bodies and keeps ticking once per five seconds.
        // Never tick it a second time or substitute ORBIT objectives for the mod's blackboard.
        return new NativeGhostAdapters(null, true,
            () => bot.BotsGroup == group && _rvrMember(bot) && ReferenceEquals(_rvrBoard(group), board),
            () => _rvrOrders(board).Remove(bot));
    }

    internal static bool IsWarbandMember(BotOwner bot)
    {
        ResolveBindings();
        return bot?.BotsGroup != null && _rvrMember != null && _rvrMember(bot);
    }

    private static NativeGhostAdapters CheckpointAdapter(BotOwner bot, CheckpointBinding binding, string faction)
    {
        if (binding?.Available == null || binding.Point == null) return null;
        var manager = bot.GetComponent(binding.Type);
        if (manager == null || !binding.Available(manager) || binding.Point(manager) == null) return null;
        var adapter = new NativeGhostAdapters(faction, false,
            () => ReferenceEquals(bot.GetComponent(binding.Type), manager)
                && binding.Available(manager) && binding.Point(manager) != null);
        if (binding.Choose != null && binding.Set != null)
        {
            adapter._checkpointPoint = () => binding.Point(manager);
            adapter._chooseCover = () => binding.Choose(manager);
            adapter._setCover = point => binding.Set(manager, point);
        }
        return adapter;
    }

    internal bool Valid() => _valid();

    // RvR remembers that an order was already sent. Initial body deactivation can erase that route.
    // Invalidating only its deduplication entry lets the ORIGINAL action reissue its current target.
    internal void ReissueOrder() => _reissue?.Invoke();

    internal void SuspendProgress() => _observing = false;

    internal bool RefreshStalledCheckpoint(BotOwner bot, string decision, Vector3? target, bool recoveringLocally = false)
    {
        // Never shorten a SitAtCheckpoint wait or move a bot that has no native travel order.
        if (_chooseCover == null || !target.HasValue || decision == null
            || !(decision.EndsWith(".GoToCheckpoint", StringComparison.Ordinal)
                || decision.EndsWith(".SwitchCheckpointCover", StringComparison.Ordinal))
            || Vector3.Distance(bot.Position, target.Value) <= 0.6f)
        { SuspendProgress(); return false; }
        var point = _checkpointPoint();
        if (point == null || Vector3.Distance(point.Position, target.Value) > 0.5f)
        { SuspendProgress(); return false; }
        if (!_observing || !ReferenceEquals(point, _observedPoint)
            || !recoveringLocally && (bot.Position - _progressPosition).sqrMagnitude > 9f)
        {
            _observing = true;
            _observedPoint = point;
            _progressPosition = bot.Position;
            _progressAt = Time.time;
            return false;
        }
        // Repeated one-metre seam rescues do not count as progress out of the stuck area.
        if (Time.time - _progressAt < 45f || Time.time < _retryAt || _coverSearchFrame == Time.frameCount
            || !NativeGhostNavigation.TakeQuery()) return false;
        _coverSearchFrame = Time.frameCount;
        _retryAt = Time.time + 45f;
        if (!Valid()) return false;
        var candidate = _chooseCover();
        var path = new NavMeshPath();
        if (candidate == null || ReferenceEquals(candidate, point) || Vector3.Distance(candidate.Position, point.Position) < 1f
            || DangerZones.IsInside(candidate.Position)
            || !NavMesh.CalculatePath(bot.Position, candidate.Position, NavMesh.AllAreas, path)
            || path.status != NavMeshPathStatus.PathComplete || path.corners.Length < 2
            || Vector3.Distance(path.corners[path.corners.Length - 1], candidate.Position) > 0.5f)
        {
            Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} checkpoint retry: no alternative reachable native cover; current goal retained");
            return false;
        }
        _setCover(candidate);
        SuspendProgress();
        Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} checkpoint retry: native cover refreshed after 45s without progress from={point.Position} to={candidate.Position} faction={Checkpoint}");
        return true;
    }
}
