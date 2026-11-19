using System;
using System.Reflection;
using EFT;
using UnityEngine;

namespace Orbit.Sain;

// Invoked on a transfer out of ORBIT only. "Avoid Threat" also owns DogFight.
internal static class GrenadeThreatReader
{
    private static readonly Type Component = Type.GetType("SAIN.Components.BotComponent, SAIN");
    private static readonly MethodInfo Lookup = Component == null ? null :
        Type.GetType("SAIN.SAINEnableClass, SAIN")?.GetMethod("GetSAIN", new[] { typeof(string), Component.MakeByRefType() });
    private static readonly PropertyInfo Decision = Component?.GetProperty("Decision");
    private static readonly PropertyInfo Combat = Decision?.PropertyType.GetProperty("CurrentCombatDecision");
    private static readonly PropertyInfo Manager = Component?.GetProperty("Grenade");
    private static readonly PropertyInfo Reactions = Manager?.PropertyType.GetProperty("GrenadeReactionClass");
    private static readonly PropertyInfo Danger = Reactions?.PropertyType.GetProperty("DangerGrenade");
    private static readonly PropertyInfo Projectile = Danger?.PropertyType.GetProperty("Grenade");
    private static readonly PropertyInfo Point = Danger?.PropertyType.GetProperty("DangerPoint");
    private static readonly PropertyInfo Thrown = Danger?.PropertyType.GetProperty("TimeThrown");

    private static bool _reportedFailure;

    internal static void OnHandoff(Entities.Agent agent, string layerName)
    {
        if (agent.IsDormant || layerName != "SAIN : Avoid Threat"
            || agent.Movement.Status != Entities.MovementStatus.Moving) return;

        try { Observe(agent); }
        catch (Exception e) { ReportUnavailable(e.GetType().Name); }
    }

    private static void ReportUnavailable(string reason)
    {
        if (_reportedFailure) return;
        _reportedFailure = true;
        Log.Warning($"GRENADE DETOUR: optional threat data unavailable ({reason})");
    }

    private static void Observe(Entities.Agent agent)
    {
        if (Lookup == null || Combat == null || Projectile == null || Point == null)
        {
            ReportUnavailable("missing grenade tracker API");
            return;
        }
        var args = new object[] { agent.Bot.ProfileId, null };
        if (Lookup.Invoke(null, args) is not true || args[1] == null) return;
        var decision = Decision.GetValue(args[1]);
        if (decision == null || Combat.GetValue(decision)?.ToString() != "AvoidGrenade") return;
        var manager = Manager.GetValue(args[1]);
        var reactions = manager == null ? null : Reactions.GetValue(manager);
        var tracker = reactions == null ? null : Danger.GetValue(reactions);
        if (tracker == null || Projectile.GetValue(tracker) is not Grenade grenade || grenade == null
            || Point.GetValue(tracker) is not Vector3 danger || !Systems.BotGroundPlacement.Finite(danger)) return;

        var now = Time.time;
        var memory = agent.GrenadeInterruptions ??= new();
        if (!memory.Observe(danger, agent.Position, now) || !Log.InfoEnabled) return;
        var age = Thrown?.GetValue(tracker) is float thrown ? $"{now - thrown:F1}s" : "unknown";
        var item = grenade.WeaponSource;
        Log.Info($"GRENADE DETOUR: {agent} action=repeated projectile={grenade.GetInstanceID()} type={item?.ThrowType.ToString() ?? grenade.GetType().Name} template={item?.StringTemplateId ?? "unknown"} age={age} grenade={grenade.transform.position} danger={danger} bot={agent.Position}");
    }
}
