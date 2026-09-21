using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace Orbit.Systems;

// Cache references, not open/closed verdicts: state changes remain visible without a new search.
// The margin covers movement between refreshes; leaving it invalidates the cache immediately.
internal sealed class NearbyDoorCache
{
    private readonly List<Door> _doors = new();
    private Vector3 _origin;
    private float _refreshAt = float.NegativeInfinity;

    internal List<Door> Get(DoorSystem system, Vector3 position)
    {
        if (Time.time >= _refreshAt || (position - _origin).sqrMagnitude >= 64f)
        {
            system.Nearby(position, 12f, _doors);
            _origin = position;
            _refreshAt = Time.time + 2f;
        }
        return _doors;
    }
}
