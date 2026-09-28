using UnityEngine;
using UnityEngine.AI;

namespace Orbit.Systems;

// Quest anchors are walkable ground samples, not loot interaction points. Validate a short walkable
// approach and geometry above the floor instead of casting down into the target's ground collider.
internal sealed class QuestArrival
{
    private Vector3 _from, _target;
    private float _nextCheck;
    private bool _known, _reachable;
    private string _reason;

    internal bool Check(Vector3 from, Vector3 target, out string reason)
    {
        if (!BotGroundPlacement.Finite(from) || !BotGroundPlacement.Finite(target))
        { reason = "quest-navmesh"; return false; }
        if (!_known || Time.time >= _nextCheck || (_from - from).sqrMagnitude > .25f * .25f
            || (_target - target).sqrMagnitude > .01f * .01f)
        {
            _known = true;
            _from = from;
            _target = target;
            _nextCheck = Time.time + .25f;
            _reachable = Evaluate(from, target, out _reason);
        }
        reason = _reason;
        return _reachable;
    }

    private static bool Evaluate(Vector3 from, Vector3 target, out string reason)
    {
        reason = "quest-navmesh";
        if (!NavMesh.SamplePosition(from, out var feet, .5f, NavMesh.AllAreas)
            || !NavMesh.SamplePosition(target, out var goal, .35f, NavMesh.AllAreas)) return false;
        reason = "quest-floor";
        if (Mathf.Abs(feet.position.y - from.y) > .5f || Mathf.Abs(goal.position.y - target.y) > .35f
            || Mathf.Abs(feet.position.y - goal.position.y) > 1.25f) return false;
        reason = "quest-route";
        if (NavMesh.Raycast(feet.position, goal.position, out _, NavMesh.AllAreas)) return false;
        reason = "quest-geometry";
        // Sampling must not move the geometry check across a thin wall beside an off-mesh bot.
        if (Physics.Linecast(from + Vector3.up * .75f, target + Vector3.up * .75f,
                LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore)) return false;
        reason = "reached";
        return true;
    }
}
