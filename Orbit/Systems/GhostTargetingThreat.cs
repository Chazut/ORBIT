using UnityEngine;

namespace Orbit.Systems;

internal static class GhostTargetingThreat
{
    // GoalEnemy can be assigned by a group or retained indefinitely by a native sniper brain.
    // Use the active observer's actual perception, with a short grace for a lost sightline.
    // No distance ceiling: a sniper who really sees and can shoot remains a threat.
    internal static bool IsImmediate(EnemyInfo enemy)
    {
        if (enemy?.Person == null) return false;
        if (enemy.IsVisible && enemy.CanShoot) return true;
        var age = Time.time - enemy.PersonalLastSeenTime;
        return enemy.HaveSeenPersonal && enemy.PersonalLastSeenTime > 0 && age >= 0 && age <= 3f;
    }
}
