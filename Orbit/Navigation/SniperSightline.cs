using EFT;
using UnityEngine;

namespace Orbit.Navigation;

// A watch pitch is a preferred direction, not a requirement to stare into nearby ground.
// Every accepted alternative is ray-tested from the same eye position and is also the
// direction the bot will actually watch. Walls and blocked horizons still reject the post.
internal static class SniperSightline
{
    internal struct Result
    {
        internal Vector3 Direction;
        internal RaycastHit PreferredHit, LevelHit;
        internal bool PreferredBlocked, LevelTried, LevelBlocked, Usable;
        internal float ClearDistance;
        internal string Outcome => Usable ? LevelTried ? "level-after-ground" : "authored"
            : LevelTried ? "level-blocked" : "blocked";
    }

    internal static Result Probe(Vector3 head, Vector3 preferred, float distance, float required)
    {
        required = Mathf.Min(distance, required);
        var result = new Result { Direction = preferred };
        result.PreferredBlocked = Physics.Raycast(head, preferred, out result.PreferredHit, distance,
            LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore);
        result.ClearDistance = result.PreferredBlocked ? result.PreferredHit.distance : distance;
        if (result.ClearDistance >= required) { result.Usable = true; return result; }

        // Retry only a downward view hitting an upward-facing surface. Never ignore the
        // collider, retry through a wall, or lift the eye position over an obstruction.
        if (preferred.y >= -.001f || result.PreferredHit.normal.y < .65f) return result;
        var level = new Vector3(preferred.x, 0, preferred.z);
        if (level.sqrMagnitude < .001f) return result;
        result.LevelTried = true;
        result.Direction = level.normalized;
        result.LevelBlocked = Physics.Raycast(head, result.Direction, out result.LevelHit, distance,
            LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore);
        result.ClearDistance = result.LevelBlocked ? result.LevelHit.distance : distance;
        result.Usable = result.ClearDistance >= required;
        return result;
    }
}
