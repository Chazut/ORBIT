using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

// Observes existing native/SAIN results. Never updates vision or forces a target visible.
internal static class AwakeVisionDiagnostics
{
    private sealed class Observation
    {
        internal string EnemyId;
        internal float Since, NextPoll, NextReport, LastCheck = -1;
        internal string Filter = "not observed";
    }
    private static readonly Dictionary<string, Observation> Observations = new();
    private static readonly Dictionary<(Type, string), MemberInfo> Members = new();
    private static Type _managerType, _rayCheckType;
    private static bool _resolved;
    private static float _nextGlobalReport;
    private static bool Enabled => Log.DebugEnabled || PerformanceJournal.Enabled;

    internal static void Clear() { Observations.Clear(); _nextGlobalReport = 0; }
    internal static void Forget(BotOwner bot)
    { if (bot?.ProfileId != null) Observations.Remove(bot.ProfileId); }

    internal static void Checked(EnemyInfo enemy, string filter)
    {
        if (!Enabled || enemy?.Owner?.ProfileId == null) return;
        if (Observations.TryGetValue(enemy.Owner.ProfileId, out var state)
            && state.EnemyId == enemy.Person?.ProfileId)
        { state.LastCheck = Time.time; state.Filter = filter; }
    }

    internal static void Observe(BotOwner bot)
    {
        if (!Enabled || bot?.ProfileId == null) return;
        var id = bot.ProfileId;
        var enemy = bot.Memory?.GoalEnemy;
        if (bot.IsDead || !bot.gameObject.activeInHierarchy || bot.BotState != EBotState.Active
            || DormancySystem.IsDormantProfile(id) || enemy?.Person == null
            || DormancySystem.IsDormantProfile(enemy.Person.ProfileId) || enemy.IsVisible
            || (enemy.Person.Position - bot.Position).sqrMagnitude > 3600f)
        { Observations.Remove(id); return; }
        var now = Time.time;
        if (!Observations.TryGetValue(id, out var state) || state.EnemyId != enemy.Person.ProfileId)
            Observations[id] = state = new Observation { EnemyId = enemy.Person.ProfileId, Since = now };
        if (now < state.NextPoll) return;
        state.NextPoll = now + 1f;
        if (now - state.Since < 5f || now < state.NextReport || now < _nextGlobalReport) return;
        state.NextReport = now + 30f;
        _nextGlobalReport = now + .25f;
        string detail;
        try
        {
            detail = $"target={state.EnemyId} distance={Number(Vector3.Distance(bot.Position, enemy.Person.Position))}m"
                + $" invisibleFor={Number(now - state.Since)}s canShoot={enemy.CanShoot} ownerActive={bot.gameObject.activeInHierarchy}"
                + $" targetActive={(enemy.Person is Player player ? player.gameObject.activeInHierarchy.ToString() : "unknown")}"
                + $" ownerGhost={DormancySystem.IsDormantProfile(id)} targetGhost={DormancySystem.IsDormantProfile(state.EnemyId)}"
                + $" checkAgo={(state.LastCheck < 0 ? "never" : Number(now - state.LastCheck))} filter={state.Filter}"
                + $" ownerPos={bot.Position} targetPos={enemy.Person.Position} " + SainSnapshot(bot, state.EnemyId, now);
        }
        catch (Exception e) { detail = $"target={state.EnemyId} snapshot-unavailable={e.GetBaseException().GetType().Name}"; }
        Log.Info($"AWAKE VISION: {bot.Profile?.Nickname} [{id}] {detail}");
        PerformanceJournal.Event("awake-vision", id, detail);
    }

    private static string SainSnapshot(BotOwner bot, string enemyId, float now)
    {
        if (!_resolved)
        {
            _resolved = true;
            _managerType = OptionalModTypes.Find("SAIN.Components.BotManagerComponent");
            _rayCheckType = OptionalModTypes.Find("SAIN.Models.Enums.ERaycastCheck");
        }
        if (_managerType == null) return "sain=absent";
        var world = Singleton<GameWorld>.Instance;
        var manager = world == null ? null : world.GetComponent(_managerType);
        if (Read(manager, "Bots") is not IDictionary bots) return "sain=unavailable registry";
        var component = bots[bot.ProfileId];
        if (!ReferenceEquals(Read(component, "BotOwner"), bot)) return "sain=unavailable owner";
        var controller = Read(component, "EnemyController");
        var enemies = Read(controller, "Enemies") as IDictionary;
        var target = enemies?[enemyId];
        if (target == null) return "sain=unavailable enemy";
        var vision = Read(target, "Vision");
        var angles = Read(vision, "Angles");
        var parts = Read(vision, "EnemyParts");
        var text = new StringBuilder($"sain=ready active={Format(Read(component, "BotActive"))}"
            + $" standby={Format(Read(component, "BotInStandBy"))} angle={Format(Read(angles, "AngleToEnemy"))}"
            + $" maxAngle={Format(Read(angles, "MaxVisionAngle"))} sector={Format(Read(angles, "CanBeSeen"))}"
            + $" partsVisible={Format(Read(parts, "CanBeSeen"))} partsLoS={Format(Read(parts, "LineOfSight"))}");
        if (Read(parts, "PartsArray") is not IEnumerable array) return text.Append(" parts=unavailable").ToString();
        var count = 0;
        foreach (var part in array)
        {
            if (++count > 8) break;
            text.Append(" part=").Append(Format(Read(part, "BodyPart")));
            if (Read(part, "RaycastResults") is IDictionary results)
            {
                var rays = 0;
                foreach (DictionaryEntry entry in results)
                {
                    if (++rays > 3) break;
                    AppendRay(text, entry.Key.ToString(), entry.Value, now);
                }
            }
            else if (Read(part, "_raycastResults") is Array compact)
                for (var i = 0; i < compact.Length && i < 3; i++)
                    AppendRay(text, _rayCheckType?.IsEnum == true ? Enum.GetName(_rayCheckType, i) ?? $"ray[{i}]" : $"ray[{i}]", compact.GetValue(i), now);
            else text.Append(" rays=unavailable");
        }
        return text.ToString();
    }

    private static void AppendRay(StringBuilder text, string name, object result, float now)
    {
        var checkedAt = Read(result, "TimeLastChecked");
        var age = checkedAt is float at && at > 0 ? Number(now - at) : "never";
        text.Append(' ').Append(name).Append("Age=").Append(age);
        if (Read(result, "LastRaycastHit") is RaycastHit hit)
            text.Append(" hit=").Append(ColliderPath(hit.collider)).Append(" dist=").Append(Number(hit.distance));
    }

    private static string ColliderPath(Collider collider)
    {
        if (collider == null) return "none";
        var path = collider.name;
        var parent = collider.transform.parent;
        for (var i = 0; parent != null && i < 6; i++, parent = parent.parent) path = parent.name + "/" + path;
        return $"{collider.gameObject.scene.name}:{path}[layer={collider.gameObject.layer},trigger={collider.isTrigger}]";
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
            _ => null,
        };
    }
    private static string Number(float value) => value.ToString("F2", CultureInfo.InvariantCulture);
    private static string Format(object value) => value is float number ? Number(number) : value?.ToString() ?? "unknown";
}
