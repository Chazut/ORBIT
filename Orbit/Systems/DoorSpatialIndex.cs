using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace Orbit.Systems;

// Door pivots are stationary during normal swings. Refresh their cell on state changes as well,
// so a relocated interactable does not retain a stale entry. Exact 3D/leaf tests stay with callers.
internal sealed class DoorSpatialIndex
{
    private const float CellSize = 8f;
    private readonly Dictionary<(int x, int z), List<Door>> _cells = new();
    private readonly Dictionary<Door, (int x, int z)> _locations = new();

    internal void Update(Door door)
    {
        var p = door.transform.position;
        var cell = (Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.z / CellSize));
        if (_locations.TryGetValue(door, out var previous))
        {
            if (previous == cell) return;
            _cells[previous].Remove(door);
        }
        if (!_cells.TryGetValue(cell, out var bucket)) _cells[cell] = bucket = new List<Door>();
        bucket.Add(door);
        _locations[door] = cell;
    }

    internal void Query(Vector3 position, float radius, List<Door> result)
    {
        result.Clear();
        var minX = Mathf.FloorToInt((position.x - radius) / CellSize);
        var maxX = Mathf.FloorToInt((position.x + radius) / CellSize);
        var minZ = Mathf.FloorToInt((position.z - radius) / CellSize);
        var maxZ = Mathf.FloorToInt((position.z + radius) / CellSize);
        for (var x = minX; x <= maxX; x++)
            for (var z = minZ; z <= maxZ; z++)
                if (_cells.TryGetValue((x, z), out var bucket)) result.AddRange(bucket);
    }
}
