using UnityEngine;

namespace Orbit.Navigation;

// The small Hermetic hut can leave a reachable path just outside its entrance.
// Keep ordinary physical interaction first, then allow this one switch locally.
internal sealed class HermeticSwitchRecovery
{
    private object _actor;
    private float _since = -1f, _bestGap;

    internal void Reset() { _actor = null; _since = -1f; }

    internal bool Ready(object actor, string operation, string switchId, Vector3 feet,
        Vector3 target, Vector3? standingPoint, float activeTime)
    {
        if (actor == null || operation != "hermetic" || switchId != "autoId_00632_EXFIL"
            || standingPoint is not { } standing || Mathf.Abs(feet.y - standing.y) > 1f
            || (feet - target).sqrMagnitude > 25f)
        { Reset(); return false; }

        var gap = Vector3.Distance(feet, target);
        if (!ReferenceEquals(actor, _actor) || _since < 0f || activeTime < _since || gap < _bestGap - .5f)
        { _actor = actor; _since = activeTime; _bestGap = gap; }
        return activeTime - _since >= 20f;
    }
}
