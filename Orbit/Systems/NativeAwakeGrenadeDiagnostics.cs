using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Orbit.Systems;

// Called only by the bounded stationary snapshot. Reads SAIN state without updating its
// decisions, invoking grenade checks, querying navigation or touching the bot's movement.
internal static class NativeAwakeGrenadeDiagnostics
{
    internal const string ActionName = "SAIN.Layers.Combat.Solo.AvoidGrenadeAction";
    private static readonly Dictionary<(Type, string), MemberInfo> Members = new();
    private static bool _resolved;
    private static Type _managerType;

    internal static void Report(BotOwner bot, float stillFor, float decisionFor)
    {
        string state;
        try { state = Snapshot(bot); }
        catch (Exception e) { state = "sain=unavailable error=" + e.GetBaseException().GetType().Name; }
        Log.Info($"NATIVE AWAKE GRENADE: {bot.Profile?.Nickname} [{bot.ProfileId}]"
            + $" stillFor={stillFor:F1}s decisionFor={decisionFor:F1}s bodyActive={bot.gameObject.activeSelf}"
            + $" position={bot.Position} enemy={bot.Memory?.GoalEnemy != null} underFire={bot.Memory?.IsUnderFire} " + state);
    }

    private static string Snapshot(BotOwner bot)
    {
        if (!_resolved)
        {
            _resolved = true;
            _managerType = AccessTools.TypeByName("SAIN.Components.BotManagerComponent");
        }
        if (_managerType == null) return "sain=unavailable reason=manager-type";
        var world = Singleton<GameWorld>.Instance;
        var manager = world == null ? null : world.GetComponent(_managerType);
        if (manager == null) return "sain=unavailable reason=manager-instance";
        if (Read(manager, "Bots") is not IDictionary bots) return "sain=unavailable reason=bot-registry";
        // Do not retain a manager or bot across raids. Match the actual owner as well as the key.
        var component = bots[bot.ProfileId];
        if (component == null || !ReferenceEquals(Read(component, "BotOwner"), bot))
        {
            component = null;
            foreach (var candidate in bots.Values)
                if (candidate != null && ReferenceEquals(Read(candidate, "BotOwner"), bot))
                { component = candidate; break; }
        }
        if (component == null) return "sain=unavailable reason=bot-component";
        var decision = Read(component, "Decision");
        var reaction = Read(Read(component, "Grenade"), "GrenadeReactionClass");
        if (reaction == null) return "sain=unavailable reason=grenade-reaction";
        var tracker = Read(reaction, "DangerGrenade");
        var danger = Read(reaction, "GrenadeDangerPoint");
        var grenade = Read(tracker, "Grenade");
        var action = Read(component, "CurrentAction");
        var activeAction = action?.GetType().FullName ?? "none";
        var elapsed = Read(tracker, "TimeThrown") is float thrown ? Number(Time.time - thrown) : "none";
        var distance = danger is Vector3 point ? Number(Vector3.Distance(bot.Position, point)) : "none";
        var tracked = Read(reaction, "EnemyGrenadesList") as ICollection;
        var details = activeAction == ActionName
            ? $" destination={Format(Read(action, "_destination"))} committedDanger={Format(Read(action, "_committedDanger"))}"
                + $" nextRepathIn={(Read(action, "_nextRepathTime") is float next ? Number(next - Time.time) : "unknown")}s"
            : "";
        return $"sain=ready active={Format(Read(component, "BotActive"))} standby={Format(Read(component, "BotInStandBy"))}"
            + $" combat={Format(Read(decision, "CurrentCombatDecision"))} self={Format(Read(decision, "CurrentSelfDecision"))}"
            + $" decisionAge={Format(Read(decision, "TimeSinceChangeDecision"))}s activeAction={activeAction}"
            + $" tracked={tracked?.Count.ToString(CultureInfo.InvariantCulture) ?? "unknown"} reaction={Format(Read(reaction, "Reaction"))}"
            + $" danger={Format(danger)} dangerDistance={distance}m tracker={tracker != null} grenadeObject={ObjectState(grenade)}"
            + $" thrownAgo={elapsed}s remaining={Format(Read(reaction, "EstimatedTimeRemaining"))}s"
            + $" canReact={Format(Read(tracker, "CanReact"))}" + details;
    }

    private static object Read(object instance, string name)
    {
        if (instance == null) return null;
        var type = instance.GetType();
        var key = (type, name);
        if (!Members.TryGetValue(key, out var member))
            Members[key] = member = (MemberInfo)AccessTools.Property(type, name) ?? AccessTools.Field(type, name);
        return member switch
        {
            PropertyInfo property => property.GetValue(instance),
            FieldInfo field => field.GetValue(instance),
            _ => throw new MissingMemberException(type.FullName, name),
        };
    }

    private static string ObjectState(object value)
        => value == null ? "none" : value is UnityEngine.Object unity
            ? unity == null ? "destroyed" : "alive:" + unity.GetInstanceID() : "present";

    private static string Number(float value) => value.ToString("F1", CultureInfo.InvariantCulture);
    private static string Format(object value) => value is float number ? Number(number) : value?.ToString() ?? "none";
}
