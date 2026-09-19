using System;
using System.Collections.Generic;
using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Navigation;
using Orbit.Zones;
using UnityEngine;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private void LogZoneScopes()
    {
        var types = 0;
        var floors = 0;
        foreach (var zone in _zones)
        {
            if (zone.Scope?.BotTypes != null) types++;
            if (string.IsNullOrEmpty(zone.Scope?.FloorId)) continue;
            floors++;
            if (!FloorSelection.IsKnown(_zoneKey, zone.Scope.FloorId))
                Log.Warning($"ZONE SCOPE: unknown floor '{zone.Scope.FloorId}' on {_zoneKey}; zone has no eligible destination");
        }
        Log.Info($"ZONE SCOPE: loaded map={_zoneKey} zones={_zones.Count} typeFilters={types} floorFilters={floors}");
    }

    public bool MatchesZoneFloor(string floorId, Vector3 position)
        => FloorCatalog.Matches(_zoneKey, floorId, position.x, position.y, position.z);

    public bool HasReachedZoneFloor(Squad squad, Waypoint target, Vector3 position)
    {
        if (target == null || target.Category == WaypointCategory.Quest || target.Category == WaypointCategory.Exfil) return true;
        var selection = ZoneFloorForCell(squad, WorldToCell(target.Position));
        if (string.IsNullOrEmpty(selection)) return true;
        var targetFloor = FloorCatalog.For(_zoneKey)?.Resolve(target.Position.x, target.Position.y, target.Position.z);
        // Several native floors are allowed for routing, but arrival must reach this target's floor.
        return targetFloor != null && FloorSelection.Contains(selection, targetFloor) && MatchesZoneFloor(targetFloor, position);
    }

    private float ZoneStrength(Zone zone, Vector2 coords)
    {
        var radius = zone.Radius * ServerConfig.Zones.ZoneRadiusScale;
        if (radius <= 0f) return 0f;
        var fraction = Mathf.Clamp01(1f - Vector2.Distance(zone.Coords, coords) * _cellSize / radius);
        return Mathf.Pow(fraction, zone.Decay * ServerConfig.Zones.ZoneFalloffScale)
            * zone.Force * ServerConfig.Zones.ZoneForceScale;
    }

    private Vector2 ScopedAttraction(Squad squad, Vector2Int coords)
    {
        if (squad?.Leader == null || squad.ExtractRequested) return Vector2.zero;
        var kind = ZoneBotType.For(squad.Leader.Bot);
        var force = Vector2.zero;
        foreach (var zone in _zones)
        {
            if (!zone.IsScoped || !zone.Scope.Allows(kind)) continue;
            // Attract toward an upstairs destination even while the bot is downstairs.
            // Repulsion is local to the configured floor, so it cannot push the floor below away.
            if (zone.Force < 0f && !MatchesZoneFloor(zone.Scope.FloorId, squad.Leader.Position)) continue;
            if (!FloorSelection.IsKnown(_zoneKey, zone.Scope.FloorId)) continue;
            force += ZoneStrength(zone, coords) * (zone.Coords - (Vector2)coords).normalized;
        }
        return force;
    }

    // A floor applies to a destination cell, never to the bot's starting height. Main objectives
    // keep their chosen floor; ordinary routing uses the strongest positive scoped hotspot here.
    private string ZoneFloorForCell(Squad squad, Vector2Int coords)
    {
        if (squad?.Leader == null || squad.ExtractRequested) return null;
        if (squad.MainObjectives != null)
            foreach (var main in squad.MainObjectives)
                if (!main.Completed && main.Type == MainObjectiveType.Kills && main.CellCoords == coords
                    && !string.IsNullOrEmpty(main.ZoneFloorId)) return main.ZoneFloorId;
        var kind = ZoneBotType.For(squad.Leader.Bot);
        var best = 0f;
        string floor = null;
        foreach (var zone in _zones)
        {
            if (string.IsNullOrEmpty(zone.Scope?.FloorId) || !zone.Scope.Allows(kind)) continue;
            // Use the closest point in this cell: a small zone near a cell corner still has a floor.
            var closest = new Vector2(Mathf.Clamp(zone.Coords.x, coords.x - .5f, coords.x + .5f),
                Mathf.Clamp(zone.Coords.y, coords.y - .5f, coords.y + .5f));
            var strength = ZoneStrength(zone, closest);
            if (strength <= best) continue;
            best = strength;
            floor = zone.Scope.FloorId;
        }
        return floor;
    }

    private bool MatchesDestinationFloor(string floor, Waypoint waypoint)
        => waypoint.Category == WaypointCategory.Quest || waypoint.Category == WaypointCategory.Exfil
            || MatchesZoneFloor(floor, waypoint.Position);

    private float ScopedWaypointWeight(Squad squad, Waypoint waypoint)
    {
        if (squad?.Leader == null || squad.ExtractRequested) return 1f;
        if (waypoint.Category == WaypointCategory.Quest || waypoint.Category == WaypointCategory.Exfil) return 1f;
        var kind = ZoneBotType.For(squad.Leader.Bot);
        var coords = WorldToCellCentered(new Vector2(waypoint.Position.x, waypoint.Position.z));
        var strength = 0f;
        foreach (var zone in _zones)
            if (zone.IsScoped && zone.Scope.Allows(kind) && MatchesZoneFloor(zone.Scope.FloorId, waypoint.Position))
                strength += ZoneStrength(zone, coords);
        // A repeller changes preference, it is not a wall or a permanent exclusion.
        return Mathf.Exp(Mathf.Clamp(strength, -5f, 5f));
    }

    private void LogScopedPick(Squad squad, Vector2Int coords, Waypoint pick)
    {
        var floor = ZoneFloorForCell(squad, coords);
        if (!string.IsNullOrEmpty(floor))
            Log.Debug($"ZONE SCOPE: {squad} type={ZoneBotType.For(squad.Leader.Bot)} floor={floor} picked {pick} Y={pick.Position.y:F2}");
    }

    public readonly struct ZoneAnchor(Vector3 position, string floorId)
    {
        public readonly Vector3 Position = position;
        public readonly string FloorId = floorId;
    }

    private readonly List<Waypoint> _zoneAnchorCandidates = new();

    public List<ZoneAnchor> GetPositiveForceZoneAnchors(Squad squad)
    {
        var result = new List<ZoneAnchor>();
        var kind = ZoneBotType.For(squad.Leader?.Bot);
        foreach (var zone in _zones)
        {
            if (!zone.KillMains || (zone.Scope != null && !zone.Scope.Allows(kind))) continue;
            var floor = zone.Scope?.FloorId;
            if (string.IsNullOrEmpty(floor))
            {
                result.Add(new ZoneAnchor(zone.WorldPosition, null));
                continue;
            }
            // Keep the actual floor's reachable POI as anchor. Sampling Y=0 can pick the wrong storey.
            _zoneAnchorCandidates.Clear();
            var radius = zone.Radius * ServerConfig.Zones.ZoneRadiusScale;
            var center = WorldToCell(zone.WorldPosition);
            var window = Mathf.CeilToInt(radius / _cellSize);
            for (var x = Math.Max(0, center.x - window); x <= Math.Min(_gridSize.x - 1, center.x + window); x++)
            for (var y = Math.Max(0, center.y - window); y <= Math.Min(_gridSize.y - 1, center.y + window); y++)
                foreach (var point in _cells[x, y].Waypoints)
                {
                    if (point.Category == WaypointCategory.Quest || point.Category == WaypointCategory.Exfil) continue;
                    if (!MatchesZoneFloor(floor, point.Position)) continue;
                    if (XzDistanceSqr(zone.WorldPosition, point.Position) > radius * radius) continue;
                    if (!SquadCanUseWaypoint(squad, squad.Leader.Bot.Profile.Info.Settings.Role.IsPMC(), point)) continue;
                    _zoneAnchorCandidates.Add(point);
                }
            _zoneAnchorCandidates.Sort((a, b) => XzDistanceSqr(a.Position, zone.WorldPosition).CompareTo(XzDistanceSqr(b.Position, zone.WorldPosition)));
            var found = false;
            for (var i = 0; i < Math.Min(8, _zoneAnchorCandidates.Count); i++)
            {
                var point = _zoneAnchorCandidates[i];
                if (!IsWaypointReachable(point, squad)) continue;
                var anchorFloor = FloorCatalog.For(_zoneKey)?.Resolve(point.Position.x, point.Position.y, point.Position.z);
                result.Add(new ZoneAnchor(point.Position, anchorFloor));
                found = true;
                break;
            }
            if (!found) Log.Debug($"ZONE SCOPE: {squad} floor={floor} has no reachable kill anchor near {zone.WorldPosition}");
        }
        return result;
    }
}
