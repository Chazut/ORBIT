using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Settings;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    internal System.Func<Agent, Vector3, MainObjective, bool> TrySniperRelocation;
    internal System.Func<Agent, Vector3, bool> TrySniperTransit;
    private List<RushPoint> _rushPoints;
    private Dictionary<string, Door> _rushDoors;
    private readonly Dictionary<string, Audio.SpatialSystem.SpatialAudioPortal> _rushPortals = new();
    private readonly Dictionary<Corpse, string> _rushCorpses = new();
    internal static float RushRaidSeconds => (float)(Singleton<AbstractGame>.Instance?.GameTimer?.PastTime.TotalSeconds ?? double.PositiveInfinity);

    private void InitializeRush()
    {
        _rushPoints = ServerConfig.Rush.Points(_zoneKey);
        _rushDoors = new();
        _rawLockedDoors = new();
        // Reuse the gatherer's scene inventory where possible; this scan is once per raid, never per squad.
        foreach (var door in Object.FindObjectsOfType<Door>(true))
        {
            if (!string.IsNullOrEmpty(door.Id)) _rushDoors[door.Id] = door;
            if (door.DoorState == EDoorState.Locked) _rawLockedDoors.Add(door);
        }
        foreach (var portal in Object.FindObjectsOfType<Audio.SpatialSystem.SpatialAudioPortal>(true))
            if (!string.IsNullOrEmpty(portal.DoorID) && _rushDoors.TryGetValue(portal.DoorID, out var door)
                && (portal.transform.position - door.transform.position).sqrMagnitude < 225f)
                _rushPortals[portal.DoorID] = portal;
    }

    internal Door RushDoor(string id) => _rushDoors.TryGetValue(id, out var door) ? door : null;
    internal System.Func<Agent, Door, bool> OpenRushDoor;
    internal MarkedRoomScope MarkedRoom(RushPoint point)
    {
        SpatialAudioRoom selected = null;
        if (_rushPortals.TryGetValue(point.DoorId, out var portal) && portal != null)
        {
            // The dedicated room is smaller than its adjoining corridor or outdoor volume.
            foreach (var room in new[] { portal.FrontRoom, portal.BackRoom })
                if (room != null && !room.IsOutdoor && room.Bounds.size.sqrMagnitude > 0
                    && (selected == null || room.RoomSize < selected.RoomSize)) selected = room;
        }
        return new(new Vector3(point.LootX, point.LootY, point.LootZ), point.Radius, selected);
    }
    internal void RegisterRushCorpse(Corpse corpse, string role)
    {
        if (corpse != null && RushDefaults.IsBossRushTarget(_zoneKey, role)) _rushCorpses[corpse] = role;
    }

    internal MainObjective RollRushMain(Squad squad)
    {
        var cfg = ServerConfig.Rush;
        if (!cfg.Allows(squad.Leader?.BotCategory) || squad.Leader.Bot.Profile.WillBeAPlayerScav()) return null;
        var style = cfg.Style(squad.Personality?.Archetype.ToString());
        if (style.Chance <= 0 || style.Chance < 1 && Random.value >= style.Chance) return null;
        var pools = new Dictionary<string, List<RushPoint>>();
        var scoped = false;
        if (style.Weight("Sniper", RushRaidSeconds) > 0)
            foreach (var member in squad.Members)
                if (SniperEquipment.Eligible(member, cfg.SniperMinZoom)) { scoped = true; break; }
        foreach (var point in _rushPoints)
        {
            if (!point.Enabled || point.Weight <= 0 || style.Weight(point.Kind, RushRaidSeconds) <= 0) continue;
            if (point.Kind == "Sniper" && !scoped) continue;
            var position = new Vector3(point.X, point.Y, point.Z);
            if (point.Kind == "Spawn" && (position - squad.SpawnPosition).sqrMagnitude < Mathf.Pow(cfg.OwnSpawnExclusion + point.Radius, 2)) continue;
            if (point.Kind == "Boss" && !RushDefaults.IsBossRushTarget(_zoneKey, point.Boss)) continue;
            if (point.Kind == "Marked" && (!RushDefaults.IsMarkedRushTarget(_zoneKey, point.DoorId)
                || RushDoor(point.DoorId) == null || !MultiStepAccess.CanForceUnlock(RushDoor(point.DoorId)))) continue;
            if (!pools.TryGetValue(point.Kind, out var pool)) pools[point.Kind] = pool = new();
            pool.Add(point);
        }
        string kind = null;
        var total = 0f;
        foreach (var pair in pools)
        {
            var weight = style.Weight(pair.Key, RushRaidSeconds);
            total += weight;
            if (kind == null || Random.value * total < weight) kind = pair.Key;
        }
        if (kind == null) return null;
        var points = pools[kind];
        if (kind is "Marked" or "Custom" or "Boss" or "Sniper")
        {
            RushPoint chosen = null;
            total = 0;
            // One resident boss is selected; every configured site for that boss is searched.
            foreach (var p in points)
            { total += p.Weight; if (chosen == null || Random.value * total < p.Weight) chosen = p; }
            points = kind == "Boss" ? points.FindAll(p => p.Boss == chosen.Boss) : new() { chosen };
        }
        var plan = new RushPlan(kind, points, style.SearchScale, style);
        var first = points[0];
        var main = new MainObjective { Type = MainObjectiveType.Rush, Rush = plan,
            Position = new Vector3(first.X, first.Y, first.Z), ZoneFloorId = first.FloorId };
        main.CellCoords = WorldToCell(main.Position);
        plan.Main = main;
        return main;
    }

    internal bool TickRush(Squad squad, bool combat)
    {
        if (squad.Rush == null && squad.MainObjectives != null)
            foreach (var main in squad.MainObjectives)
                if (!main.Completed && main.Rush != null) { squad.Rush = main.Rush; break; }
        if (squad.Rush == null) return false;
        using var timing = PerformanceJournal.Measure(TransitionPhase.StrategyObjectives, "rush-objective", this, squad.Id);
        var result = squad.Rush.Tick(squad, this, combat);
        if (squad.Rush.Main.Completed)
        {
            var marked = squad.Rush.Kind == "Marked";
            squad.Rush = null;
            if (marked) Orbit.Tasks.Actions.LootContainerAction.ReevaluateAfterMarkedRoom(squad);
        }
        return result;
    }

    internal string PerceivedRushBoss(Squad squad, string role)
    {
        foreach (var member in squad.Members)
        {
            if (!member.IsActive) continue;
            var enemies = member.Bot.EnemiesController?.EnemyInfos;
            if (enemies != null)
                foreach (var enemy in enemies.Values)
                    if (enemy.IsVisible && enemy.Person?.Profile?.Info?.Settings?.Role.ToString() == role)
                        return "boss seen";
            foreach (var pair in _rushCorpses)
            {
                if (pair.Key == null || pair.Value != role) continue;
                var target = pair.Key.transform.position + Vector3.up * .4f;
                if ((target - member.Position).sqrMagnitude > 900f || !member.Bot.LookSensor.IsPointInVisibleSector(target)) continue;
                var head = member.Bot.LookSensor.HeadPoint;
                if (!Physics.Linecast(head, target, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
                    return "boss body found";
            }
        }
        return null;
    }
}
