using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using EFT;
using HarmonyLib;
using Orbit.Systems;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Patches;

// Keep native SAIN decisions, cover selection, steering and shooting. Only safe
// unseen alerts at an occupied sniper post constrain the routes SAIN may use.
internal static class SniperSainAlertPatch
{
    private static bool _ready;
    private static Func<object, bool> _suppressed, _grenade, _visible, _shotAt;
    private static Func<object, object> _enemy;
    private static Func<object, Vector3?> _known;
    private static Func<object, BotOwner> _moverOwner, _coverOwner, _cachedOwner;
    private static Func<object, IReadOnlyList<Vector3>> _cachedPoints;
    private static Action<object> _stop;
    private static Func<object, Vector3> _coverPosition;
    private static Func<object, NavMeshPath> _coverPath;
    private static Func<object, object> _movingCover, _currentCover;
    private static Action<object> _clearMovingCover, _clearCurrentCover;

    internal static void Enable(Harmony harmony, Type decisionType, Type enemyType)
    {
        var mover = Type.GetType("SAIN.SAINComponent.Classes.Mover.SAINMoverClass, SAIN", true);
        var cover = Type.GetType("SAIN.SAINComponent.Classes.SAINCoverClass, SAIN", true);
        var cached = Type.GetType("SAIN.SAINComponent.Classes.Mover.BotPathDataManual, SAIN", true);
        _suppressed = Getter<bool>(decisionType, "Bot.Suppression.IsSuppressed");
        _grenade = Getter<bool>(decisionType, "Bot.Grenade.GrenadeReactionClass.GrenadeDangerPoint.HasValue");
        _enemy = Getter<object>(decisionType, "Bot.GoalEnemy");
        _known = Getter<Vector3?>(enemyType, "LastKnownPosition");
        _visible = Getter<bool>(enemyType, "IsVisible");
        var shotAt = Getter<bool>(enemyType, "Status.ShotAtMeRecently");
        var shotMe = Getter<bool>(enemyType, "Status.ShotMeRecently");
        _shotAt = value => shotAt(value) || shotMe(value);
        var argument = Expression.Parameter(typeof(object), "value");
        _stop = Expression.Lambda<Action<object>>(Expression.Call(
            Member(Expression.Convert(argument, decisionType), "Bot.Mover"), "Stop", Type.EmptyTypes), argument).Compile();
        _moverOwner = Getter<BotOwner>(mover, "BotOwner");
        _coverOwner = Getter<BotOwner>(cover, "BotOwner");
        _cachedOwner = Getter<BotOwner>(cached, "Bot.BotOwner");
        _cachedPoints = Getter<IReadOnlyList<Vector3>>(cached, "PathPoints");
        _movingCover = Getter<object>(cover, "CoverPoint_MovingTo");
        _currentCover = Getter<object>(cover, "CoverInUse");
        _clearMovingCover = ClearProperty(cover, "CoverPoint_MovingTo");
        _clearCurrentCover = ClearProperty(cover, "CoverInUse");
        var coverType = AccessTools.Property(cover, "CoverInUse").PropertyType;
        _coverPosition = Getter<Vector3>(coverType, "Position");
        _coverPath = Getter<NavMeshPath>(coverType, "PathData.Path");
        Patch(harmony, mover, "WalkToPoint", nameof(PointPrefix), typeof(Vector3));
        Patch(harmony, mover, "RunToPoint", nameof(PointPrefix), typeof(Vector3));
        Patch(harmony, mover, "WalkToPointByWay", nameof(PathPrefix), typeof(NavMeshPath));
        Patch(harmony, mover, "RunToPointByWay", nameof(PathPrefix), typeof(NavMeshPath));
        Patch(harmony, mover, "GoToCoverPoint", nameof(CoverPointPrefix), coverType);
        var reuse = AccessTools.Method(cached, "TryUpdatePath");
        if (reuse?.ReturnType != typeof(bool) || reuse.GetParameters().Length != 1
            || reuse.GetParameters()[0].ParameterType != typeof(Vector3)) throw new MissingMethodException("Unsupported SAIN path reuse");
        harmony.Patch(reuse, postfix: new HarmonyMethod(typeof(SniperSainAlertPatch), nameof(CachedPostfix)));
        var query = AccessTools.Method(mover, "CanGoToPoint");
        if (query?.ReturnType != typeof(bool) || query.GetParameters().Length != 4
            || query.GetParameters()[1].ParameterType != typeof(NavMeshPath).MakeByRefType())
            throw new MissingMethodException("Unsupported SAIN path query");
        harmony.Patch(query, postfix: new HarmonyMethod(typeof(SniperSainAlertPatch), nameof(QueryPostfix)));
        var update = AccessTools.Method(cover, "UpdateCover");
        if (update?.ReturnType != typeof(void) || update.GetParameters().Length != 1
            || update.GetParameters()[0].ParameterType != enemyType) throw new MissingMethodException("Unsupported SAIN cover update");
        harmony.Patch(update, prefix: new HarmonyMethod(typeof(SniperSainAlertPatch), nameof(CoverUpdatePrefix)));
        _ready = true;
        SniperCombat.AlertDanger = Danger;
    }

    private static void Patch(Harmony harmony, Type type, string name, string prefix, Type first)
    {
        var method = AccessTools.Method(type, name);
        if (method?.ReturnType != typeof(bool) || method.GetParameters().Length == 0
            || method.GetParameters()[0].ParameterType != first) throw new MissingMethodException("Unsupported SAIN " + name);
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(SniperSainAlertPatch), prefix));
    }
    private static Expression Member(Expression value, string path)
    {
        foreach (var name in path.Split('.')) value = Expression.PropertyOrField(value, name);
        return value;
    }
    private static Func<object, T> Getter<T>(Type type, string path)
    {
        var value = Expression.Parameter(typeof(object), "value");
        return Expression.Lambda<Func<object, T>>(Expression.Convert(Member(Expression.Convert(value, type), path), typeof(T)), value).Compile();
    }
    private static Action<object> ClearProperty(Type type, string name)
    {
        var value = Expression.Parameter(typeof(object), "value");
        var property = AccessTools.Property(type, name);
        return Expression.Lambda<Action<object>>(Expression.Call(Expression.Convert(value, type),
            property.GetSetMethod(true), Expression.Constant(null, property.PropertyType)), value).Compile();
    }
    private static bool Danger(object context)
    {
        try
        {
            if (!SafeForDistantHold(context)) return true;
            var enemy = _enemy(context);
            return enemy != null && _visible(enemy);
        }
        catch (Exception error) { Fail(error); return true; }
    }
    internal static bool SafeForDistantHold(object context)
    {
        try
        {
            if (!_ready || context == null || _suppressed(context) || _grenade(context)) return false;
            var enemy = _enemy(context);
            return enemy == null || !_shotAt(enemy);
        }
        catch (Exception error) { Fail(error); return false; }
    }
    internal static void Observe(object context, BotOwner bot, string decision, bool independent, object enemy)
    {
        if (!_ready || !SniperCombat.HasLease(bot)) return;
        if (SniperCombat.ObserveAlert(bot, decision, independent, enemy != null && _visible(enemy),
            enemy == null ? null : _known(enemy), Danger(context), context)) _stop(context);
    }

    private static bool PointPrefix(object __instance, Vector3 __0, ref bool __result)
    {
        try
        {
            if (!_ready || SniperCombat.AllowAlertPoint(_moverOwner(__instance), __0)) return true;
            __result = false; return false;
        }
        catch (Exception error) { Fail(error); return true; }
    }
    private static bool PathPrefix(object __instance, NavMeshPath __0, ref bool __result)
    {
        try
        {
            if (!_ready || SniperCombat.AllowAlertPath(_moverOwner(__instance), __0)) return true;
            __result = false; return false;
        }
        catch (Exception error) { Fail(error); return true; }
    }
    private static bool CoverPointPrefix(object __instance, object __0, ref bool __result)
    {
        try
        {
            var bot = _moverOwner(__instance);
            if (!_ready || __0 == null || !SniperCombat.IsAlertHeld(bot)
                || SniperCombat.AllowAlertPoint(bot, _coverPosition(__0))) return true;
            __result = false; return false;
        }
        catch (Exception error) { Fail(error); return true; }
    }
    private static void CachedPostfix(object __instance, ref bool __result)
    {
        try
        {
            // Check only the cache which native movement would actually reuse.
            // A rejected cache lets the mover try its other cache or ordinary query.
            if (_ready && __result && !SniperCombat.AllowAlertCachedPath(_cachedOwner(__instance), _cachedPoints(__instance)))
                __result = false;
        }
        catch (Exception error) { Fail(error); }
    }
    private static void QueryPostfix(object __instance, NavMeshPath __1, ref bool __result)
    {
        try { if (_ready && __result && !SniperCombat.AllowAlertPath(_moverOwner(__instance), __1)) __result = false; }
        catch (Exception error) { Fail(error); }
    }
    private static void CoverUpdatePrefix(object __instance)
    {
        try
        {
            var bot = _coverOwner(__instance);
            if (!_ready || !SniperCombat.IsAlertHeld(bot)) return;
            bool Allowed(object point) => point == null || SniperCombat.AllowAlertPoint(bot, _coverPosition(point))
                && SniperCombat.AllowAlertPath(bot, _coverPath(point));
            if (!Allowed(_movingCover(__instance))) _clearMovingCover(__instance);
            if (!Allowed(_currentCover(__instance))) _clearCurrentCover(__instance);
        }
        catch (Exception error) { Fail(error); }
    }
    internal static void Fail(Exception error)
    {
        if (!_ready) return;
        _ready = false;
        Log.Warning($"SNIPER ALERT: integration disabled; ordinary SAIN movement retained: {error.Message}");
    }
}
