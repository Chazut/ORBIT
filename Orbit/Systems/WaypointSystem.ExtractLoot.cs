using System.Collections.Generic;
using Comfort.Common;
using EFT.Interactive;
using Orbit.Core;
using Orbit.Looting;
using Orbit.Tasks;
using Orbit.Tasks.Actions;
using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

public partial class WaypointSystem
{
    private readonly List<Door> _extractLootDoors = new();
    // Runs only between pickups, using the spatial index and the fixed origin of this departure sweep.
    internal Navigation.Waypoint FindNearbyExtractLoot(LootExtractSweep sweep)
    {
        var agent = sweep.Owner;
        var handler = agent.Player?.gameObject?.GetComponent<OrbitLootHandler>();
        if (handler == null) return null;
        var center = WorldToCell(sweep.Center);
        var minPerSlot = LootContainerAction.GetOrResolveAgentMiniLootThreshold(agent);
        Navigation.Waypoint best = null;
        var bestValue = 0f;
        var extent = Mathf.CeilToInt(LootExtractSweep.Radius / _cellSize);
        for (var x = -extent; x <= extent; x++)
        for (var y = -extent; y <= extent; y++)
        {
            var cell = center + new Vector2Int(x, y);
            if (!IsValidCell(cell)) continue;
            var locations = _cells[cell.x, cell.y].Waypoints;
            for (var i = 0; i < locations.Count; i++)
            {
                var loc = locations[i];
                if (loc.Category != Navigation.WaypointCategory.LooseLoot
                    || loc.Target is not LootItem loot || loot == null || loot.Item == null
                    || sweep.Attempted.Contains(loc.Id) || _claims.ContainsKey(loc.Id)
                    || sweep.Squad.CompletedPoiIds.Contains(loc.Id) || agent.ValueSkippedPoiIds.Contains(loc.Id)
                    || (loc.Position - sweep.Center).sqrMagnitude > LootExtractSweep.Radius * LootExtractSweep.Radius
                    || Mathf.Abs(loc.Position.y - sweep.Center.y) > LootExtractSweep.FloorTolerance) continue;
                var price = ItemPriceLookup.GetPrice(loot.Item);
                if (price < LootExtractSweep.MinimumValue || price <= bestValue
                    || ItemPriceLookup.GetPricePerSlot(loot.Item) < minPerSlot) continue;
                // Recheck from inside the room: a cached partial path from outside its locked door is stale.
                if (!ShortOpenLootPath(agent.Position, loc.Position) || !handler.HasSpaceForLooseLoot(loot)) continue;
                best = loc;
                bestValue = price;
            }
        }
        return best;
    }

    private bool ShortOpenLootPath(Vector3 from, Vector3 to)
    {
        if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, _reachabilityScratchPath)
            || _reachabilityScratchPath.status != NavMeshPathStatus.PathComplete) return false;
        var corners = _reachabilityScratchPath.corners;
        var doors = Singleton<OrbitManager>.Instance?.DoorSystem;
        if (doors == null) return false;
        doors.Nearby(from, LootExtractSweep.MaxPathLength, _extractLootDoors);
        var length = 0f;
        var previous = from;
        for (var i = 0; i < corners.Length; i++)
        {
            length += Vector3.Distance(previous, corners[i]);
            if (length > LootExtractSweep.MaxPathLength
                || Mathf.Abs(corners[i].y - from.y) > LootExtractSweep.FloorTolerance) return false;
            var direction = corners[i] - previous;
            if (direction.sqrMagnitude > 0.0001f)
            {
                var ray = new Ray(previous + Vector3.up * 0.8f, direction.normalized);
                for (var d = 0; d < _extractLootDoors.Count; d++)
                {
                    var door = _extractLootDoors[d];
                    if (door == null || door.Collider == null || door.DoorState == EDoorState.Open) continue;
                    var bounds = door.Collider.bounds;
                    bounds.Expand(0.2f);
                    if (bounds.Contains(ray.origin) || bounds.IntersectRay(ray, out var hit) && hit <= direction.magnitude)
                        return false;
                }
            }
            previous = corners[i];
        }
        return corners.Length > 0 && Vector3.Distance(previous, to) <= 1.5f;
    }
}
