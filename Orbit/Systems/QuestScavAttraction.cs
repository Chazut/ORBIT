using System;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using EFT.Quests;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using UnityEngine;

namespace Orbit.Systems;

// Raid-local. The scene scan that already gathers quest POIs also indexes the kill triggers.
internal sealed class QuestScavAttraction(string map, float radius)
{
    private readonly float _radius = Mathf.Clamp(radius, 100f, 1000f);
    private readonly Dictionary<string, List<TriggerWithId>> _triggers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wanted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _current = new(StringComparer.Ordinal);
    private readonly List<Collider> _volumes = new();
    private readonly Dictionary<int, bool> _insideByWaypoint = new();
    private readonly HashSet<string> _reported = new();

    internal void Register(TriggerWithId trigger)
    {
        if (string.IsNullOrEmpty(trigger.Id)) return;
        if (!_triggers.TryGetValue(trigger.Id, out var triggers))
            _triggers.Add(trigger.Id, triggers = new());
        triggers.Add(trigger);
    }

    internal void Refresh(GameWorld world)
    {
        _wanted.Clear();
        if (world != null)
            foreach (var player in world.AllAlivePlayersList)
            {
                if (player == null || player.IsAI || player.Profile?.Side == EPlayerSide.Savage
                    || player.HealthController is not { IsAlive: true }) continue;
                var quests = player.QuestController?.Quests;
                if (quests == null) continue;
                foreach (var quest in quests)
                {
                    if (quest?.QuestStatus != EQuestStatus.Started) continue;
                    try { CollectObjectives(quest); }
                    catch (Exception error)
                    {
                        if (_reported.Add(quest.Id))
                            Log.Warning($"Quest scav attraction: could not read quest {quest.Id}: {error.Message}");
                    }
                }
            }

        if (_current.SetEquals(_wanted)) return;
        _current.Clear();
        _current.UnionWith(_wanted);
        _volumes.Clear();
        _insideByWaypoint.Clear();
        foreach (var id in _current)
            foreach (var trigger in _triggers[id])
                if (trigger != null)
                    foreach (var volume in trigger.GetComponents<Collider>())
                        // Unity ClosestPoint supports primitive and convex mesh colliders.
                        if (volume is BoxCollider or SphereCollider or CapsuleCollider or MeshCollider { convex: true })
                            _volumes.Add(volume);
        Log.Info($"Quest scav attraction: map={map} zones=[{string.Join(", ", _current)}] volumes={_volumes.Count}");
    }

    private void CollectObjectives(Quest quest)
    {
        if (quest.Template?.Conditions == null || quest.ProgressCheckers == null
            || !quest.Template.Conditions.TryGetValue(EQuestStatus.AvailableForFinish, out var objectives)) return;
        foreach (var objective in objectives)
        {
            if (objective is not ConditionCounterCreator counter || counter.Conditions == null
                || !CountsScavKills(counter) || !quest.ProgressCheckers.ContainsKey(counter)
                || !quest.CheckVisibilityStatus(counter) || quest.CompletedConditions?.Contains(counter.id) == true
                || quest.IsConditionDone(counter)) continue;
            foreach (var condition in counter.Conditions)
                if (condition is ConditionInZone { zoneIds: { } ids })
                    foreach (var id in ids)
                        if (id != null && _triggers.ContainsKey(id)) _wanted.Add(id);
        }
    }

    private bool CountsScavKills(ConditionCounterCreator counter)
    {
        var scavKill = false;
        var hasZoneOnMap = false;
        foreach (var condition in counter.Conditions)
        {
            if (condition is ConditionInZone { zoneIds: { } ids })
                foreach (var id in ids)
                    if (id != null && _triggers.ContainsKey(id)) { hasZoneOnMap = true; break; }
            if (condition is ConditionLocation location && !Contains(location.target, map) && !Contains(location.target, "Any"))
                return false;
            if (condition is not ConditionKills kill) continue;
            if (!string.Equals(kill.target, "Savage", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(kill.target, "Any", StringComparison.OrdinalIgnoreCase)) return false;
            // This attraction is for regular scavs. A task requiring a specific role has its own targets.
            if (kill.savageRole is { Length: > 0 }) return false;
            scavKill = true;
        }
        return scavKill && hasZoneOnMap;
    }

    private static bool Contains(string[] values, string match)
    {
        if (values != null)
            foreach (var value in values)
                if (string.Equals(value, match, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal bool AppliesTo(Squad squad)
    {
        if (_volumes.Count == 0 || squad == null || squad.ExtractRequested) return false;
        var profile = squad.Leader?.Bot?.Profile;
        return profile?.Info?.Settings != null && profile.Info.Settings.Role.IsScav() && !profile.WillBeAPlayerScav();
    }

    internal bool InRange(Vector3 feet)
    {
        var body = feet + Vector3.up * .75f;
        var limit = _radius * _radius;
        foreach (var volume in _volumes)
            if (volume != null && volume.enabled && volume.gameObject.activeInHierarchy
                && volume.bounds.SqrDistance(body) < limit && (volume.ClosestPoint(body) - body).sqrMagnitude < limit)
                return true;
        return false;
    }

    internal Vector2 Pull(Vector3 feet, out bool inZoneColumn)
    {
        inZoneColumn = false;
        var body = feet + Vector3.up * .75f;
        var best = _radius * _radius;
        var target = body;
        foreach (var volume in _volumes)
        {
            if (volume == null || !volume.enabled || !volume.gameObject.activeInHierarchy
                || volume.bounds.SqrDistance(body) >= best) continue;
            var near = volume.ClosestPoint(body);
            var distance = (near - body).sqrMagnitude;
            if (distance < .01f) { inZoneColumn = true; return Vector2.zero; }
            if (distance >= best) continue;
            best = distance;
            // A scav directly above a bunker can already be in the correct grid cell.
            inZoneColumn = new Vector2(near.x - body.x, near.z - body.z).sqrMagnitude < .01f;
            // Steer a little inside the closest volume. A long road can be entered anywhere
            // along its length, and two gas stations never produce a target halfway between them.
            target = Vector3.MoveTowards(near, volume.bounds.center, 25f);
        }
        var heading = new Vector2(target.x - body.x, target.z - body.z);
        return heading.normalized * (10f * (1f - Mathf.Sqrt(best) / _radius));
    }

    internal float PatrolWeight(Waypoint point)
    {
        if (_insideByWaypoint.TryGetValue(point.Id, out var inside)) return inside ? 10f : 1f;
        var body = point.Position + Vector3.up * .75f;
        foreach (var volume in _volumes)
            if (volume != null && volume.enabled && volume.gameObject.activeInHierarchy
                && volume.bounds.Contains(body) && QuestWaypointPlacement.Contains(volume, point.Position))
            { inside = true; break; }
        _insideByWaypoint[point.Id] = inside;
        return inside ? 10f : 1f;
    }
}
