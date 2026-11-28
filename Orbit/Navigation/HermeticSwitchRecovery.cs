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
        Vector3 target, Vector3? standingPoint, float activeTime, bool recordedRescue = false)
    {
        if (actor == null || operation != "hermetic" || switchId != "autoId_00632_EXFIL"
            || standingPoint is not { } standing || Mathf.Abs(feet.y - standing.y) > 1f)
        { Reset(); return false; }

        if ((feet - target).sqrMagnitude > 25f)
        {
            // Rescue legs can lead the same operator away from the hut and back.
            // Preserve the failed local attempt, but never operate the switch from here.
            if (!recordedRescue || !ReferenceEquals(actor, _actor) || activeTime < _since) Reset();
            return false;
        }

        var gap = Vector3.Distance(feet, target);
        if (!ReferenceEquals(actor, _actor) || _since < 0f || activeTime < _since || (!recordedRescue && gap < _bestGap - .5f))
        { _actor = actor; _since = activeTime; _bestGap = gap; }
        return activeTime - _since >= 20f;
    }
}
