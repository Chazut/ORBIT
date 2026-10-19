using System;
using System.Collections;
using System.Linq.Expressions;
using EFT;
using UnityEngine;

namespace Orbit.Sain;

// Bind optional SAIN entry points once during raid setup. Only used for a confirmed awake contact.
internal sealed class SniperContactBridge
{
    private readonly Type _manager;
    private readonly Func<object, IDictionary> _bots;
    private readonly Func<object, object> _controller;
    private readonly Func<object, IPlayer, object> _add;
    private readonly Action<object, Vector3, float> _seen;
    private readonly Action<object, object> _goal;
    private readonly Func<object, object> _current;
    private readonly Func<object, string> _profile;
    private readonly Func<object, bool> _visible, _shoot, _known, _los;

    internal readonly struct Snapshot(bool managed, bool current, bool visible, bool shoot, bool known, bool los)
    {
        internal readonly bool Managed = managed, Current = current, Visible = visible,
            CanShoot = shoot, Known = known, LineOfSight = los;
    }

    internal SniperContactBridge()
    {
        try
        {
            _manager = Type.GetType("SAIN.Components.BotManagerComponent, SAIN", false);
            if (_manager == null) return;
            var value = Expression.Parameter(typeof(object));
            var bots = Expression.Property(Expression.Convert(value, _manager), "Bots");
            _bots = Expression.Lambda<Func<object, IDictionary>>(Expression.Convert(bots, typeof(IDictionary)), value).Compile();
            var component = bots.Type.GetGenericArguments()[1];
            var controller = Expression.Property(Expression.Convert(value, component), "EnemyController");
            _controller = Expression.Lambda<Func<object, object>>(Expression.Convert(controller, typeof(object)), value).Compile();
            var ctl = Expression.Convert(value, controller.Type);
            var target = Expression.Parameter(typeof(IPlayer));
            var add = Expression.Call(ctl, "CheckAddEnemy", null, target);
            _add = Expression.Lambda<Func<object, IPlayer, object>>(Expression.Convert(add, typeof(object)), value, target).Compile();
            var position = Expression.Parameter(typeof(Vector3)); var time = Expression.Parameter(typeof(float));
            _seen = Expression.Lambda<Action<object, Vector3, float>>(Expression.Call(Expression.Convert(value, add.Type),
                "UpdateLastSeenPosition", null, position, time), value, position, time).Compile();
            var enemy = Expression.Parameter(typeof(object));
            _goal = Expression.Lambda<Action<object, object>>(Expression.Assign(Expression.Property(ctl, "GoalEnemy"),
                Expression.Convert(enemy, add.Type)), value, enemy).Compile();
            _current = Getter<object>(controller.Type, "GoalEnemy");
            _profile = Getter<string>(add.Type, "EnemyProfileId");
            _visible = Getter<bool>(add.Type, "IsVisible");
            _shoot = Getter<bool>(add.Type, "CanShoot");
            _known = Getter<bool>(add.Type, "EnemyKnown");
            _los = Getter<bool>(add.Type, "Vision.InLineOfSight");
        }
        catch (Exception error)
        {
            _goal = null;
            Log.Warning($"GHOST SNIPER AWAKE: SAIN contact binding unavailable, native target retained: {error.Message}");
        }
    }

    private static Func<object, T> Getter<T>(Type type, string path)
    {
        var value = Expression.Parameter(typeof(object));
        Expression member = Expression.Convert(value, type);
        foreach (var name in path.Split('.')) member = Expression.PropertyOrField(member, name);
        return Expression.Lambda<Func<object, T>>(Expression.Convert(member, typeof(T)), value).Compile();
    }

    internal Snapshot Read(GameWorld world, BotOwner bot, string target)
    {
        if (_goal == null) return default;
        var manager = world.GetComponent(_manager);
        var component = manager != null ? _bots(manager)[bot.ProfileId] : null;
        if (component == null) return default;
        var current = _current(_controller(component));
        if (current == null || _profile(current) != target) return new(true, false, false, false, false, false);
        return new(true, true, _visible(current), _shoot(current), _known(current), _los(current));
    }

    internal string Assign(GameWorld world, BotOwner bot, IPlayer target, Vector3 position)
    {
        if (_goal == null) return "native-only";
        try
        {
            var manager = world.GetComponent(_manager);
            var component = manager != null ? _bots(manager)[bot.ProfileId] : null;
            if (component == null) return "unmanaged";
            var controller = _controller(component);
            var enemy = _add(controller, target);
            if (enemy == null) return "rejected";
            _seen(enemy, position, Time.time);
            _goal(controller, enemy);
            return "assigned";
        }
        catch (Exception error)
        {
            Log.Warning($"GHOST SNIPER AWAKE: SAIN contact failed for {bot.ProfileId}: {error.Message}");
            return "failed";
        }
    }
}
