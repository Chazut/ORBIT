using System;
using System.Linq.Expressions;
using EFT;
using HarmonyLib;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

/// <summary>Asks the original hunt manager for a replacement regroup point after a stalled journey.</summary>
internal sealed class NativeGhostRegroup
{
    private const string RegroupAction = "MoreBotsAPI.Behavior.Actions.HuntRegroupAction";
    private static bool _resolved;
    private static Type _type;
    private static Func<object, BotOwner> _owner;
    private static Func<object, bool> _active, _regroup, _regrouping, _ignore, _dirty;
    private static Func<object, Vector3> _point, _choose;
    private static Action<object, Vector3> _setPoint;
    private static int _searchFrame = -1;
    private readonly object _manager;
    private readonly NavMeshPath _path = new();
    private float _retryAt;

    private NativeGhostRegroup(object manager) => _manager = manager;

    private static Func<object, T> Getter<T>(string name)
    {
        var field = AccessTools.Field(_type, name);
        if (field?.FieldType != typeof(T)) throw new MissingFieldException(_type.FullName, name);
        var instance = Expression.Parameter(typeof(object), "instance");
        return Expression.Lambda<Func<object, T>>(
            Expression.Field(Expression.Convert(instance, _type), field), instance).Compile();
    }

    internal static NativeGhostRegroup Resolve(BotOwner bot)
    {
        if (!_resolved)
        {
            _resolved = true;
            _type = AccessTools.TypeByName("MoreBotsAPI.Components.BotHuntManager");
            if (_type != null)
            {
                try
                {
                    _owner = Getter<BotOwner>("botOwner");
                    _active = Getter<bool>("active");
                    _regroup = Getter<bool>("shouldRegroup");
                    _regrouping = Getter<bool>("isRegrouping");
                    _ignore = Getter<bool>("ignoreRegroup");
                    _dirty = Getter<bool>("regroupPointDirty");
                    _point = Getter<Vector3>("regroupPoint");
                    var choose = AccessTools.Method(_type, "GetRegroupPoint", Type.EmptyTypes);
                    if (choose?.ReturnType != typeof(Vector3)) throw new MissingMethodException(_type.FullName, "GetRegroupPoint");
                    var instance = Expression.Parameter(typeof(object), "instance");
                    var point = Expression.Parameter(typeof(Vector3), "point");
                    _choose = Expression.Lambda<Func<object, Vector3>>(
                        Expression.Call(Expression.Convert(instance, _type), choose), instance).Compile();
                    _setPoint = Expression.Lambda<Action<object, Vector3>>(Expression.Assign(
                        Expression.Field(Expression.Convert(instance, _type), "regroupPoint"), point), instance, point).Compile();
                }
                catch (Exception e)
                {
                    _setPoint = null;
                    Log.Warning($"NATIVE GHOST: regroup recovery unavailable: {e.Message}");
                }
            }
        }
        if (_setPoint == null) return null;
        var manager = bot.GetComponent(_type);
        return manager != null && _active(manager) && ReferenceEquals(_owner(manager), bot)
            ? new NativeGhostRegroup(manager) : null;
    }

    private bool Eligible(BotOwner bot, string decision, Vector3? target)
    {
        if (decision != RegroupAction || !target.HasValue || !NativeGhostSystem.OwnsInactiveMovement(bot)
            || NativeGhostSystem.MovementPinned(bot) || bot.Mover.Pause && bot.Mover.RemainPause > 0f
            || !ReferenceEquals(bot.GetComponent(_type), _manager) || !ReferenceEquals(_owner(_manager), bot)
            || !_active(_manager) || !_regroup(_manager) || !_regrouping(_manager) || _ignore(_manager) || _dirty(_manager)
            || bot.Boss?.IamBoss != false) return false;
        var leader = bot.BotFollower?.BossToFollow;
        var point = _point(_manager);
        return leader?.Player()?.HealthController?.IsAlive == true
            && Vector3.Distance(bot.Position, leader.Position) > 10f
            && Vector3.Distance(point, target.Value) <= 0.5f
            && Vector3.Distance(bot.Position, point) > 1f;
    }

    internal bool Refresh(BotOwner bot, string decision, NativeGhostNavigation navigation)
    {
        if (!Eligible(bot, decision, navigation.Target) || navigation.StalledFor < 45f
            || Time.time < _retryAt || _searchFrame == Time.frameCount || !NativeGhostNavigation.TakeQuery()) return false;
        _searchFrame = Time.frameCount;
        _retryAt = Time.time + 45f;
        var previous = _point(_manager);
        var leader = bot.BotFollower.BossToFollow;
        // GetRegroupPoint is the author's selector, including any installed mod patches. Never
        // manufacture a destination or clear the flags that make the leader wait for its squad.
        var candidate = _choose(_manager);
        if (!Eligible(bot, decision, navigation.Target) || !ReferenceEquals(bot.BotFollower.BossToFollow, leader)
            || (_point(_manager) - previous).sqrMagnitude > 0.0001f) return false;
        var rejection = Rejection(bot.Position, previous, candidate);
        if (rejection != null)
        {
            Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} regroup retry: no alternative reachable native point; current goal retained reason={rejection} from={bot.Position} current={previous} candidate={candidate}");
            return false;
        }
        _setPoint(_manager, candidate);
        Log.Info($"NATIVE GHOST: {bot.Profile.Nickname} regroup retry: native point refreshed after 45s without progress from={previous} to={candidate}");
        return true;
    }

    private string Rejection(Vector3 from, Vector3 previous, Vector3 candidate)
    {
        if (!Finite(candidate)) return "nonfinite-point";
        if (Vector3.Distance(previous, candidate) < 1f) return "unchanged-point";
        if (DangerZones.IsInside(candidate)) return "danger";
        if (!NavMesh.CalculatePath(from, candidate, NavMesh.AllAreas, _path)) return "no-path";
        if (_path.status != NavMeshPathStatus.PathComplete) return "incomplete-path";
        var corners = _path.corners;
        if (corners.Length < 2) return "missing-corners";
        if (!Finite(corners[corners.Length - 1]) || Vector3.Distance(corners[corners.Length - 1], candidate) > 0.5f)
            return "end-outside-goal";
        return NativeGhostNavigation.CanStartRoute(from, corners, out var reason) ? null : reason;
    }

    private static bool Finite(Vector3 p)
        => !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z)
            && !float.IsInfinity(p.x) && !float.IsInfinity(p.y) && !float.IsInfinity(p.z);
}
