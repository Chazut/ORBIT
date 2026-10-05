using EFT;
using UnityEngine;

namespace Orbit.Systems;

public partial class DormancySystem
{
    private readonly GhostVisibility _visibility = new();
    private const float SniperMinimumDetectRange = 200f;
    private static float SniperDetectionReach(BotOwner bot, float weaponReach)
        => bot?.Profile?.Info?.Settings?.Role == WildSpawnType.marksman
            ? Mathf.Clamp(weaponReach, SniperMinimumDetectRange, SkirmishReachCap) : 0f;
    private static BotOwner MemberBot(GhostUnit unit, int index)
        => index < unit.Agents.Count ? unit.Agents[index].Bot : unit.VanillaBots[index - unit.Agents.Count];
    private GhostVisibilityModel.Result MemberVisibility(BotOwner observer, BotOwner target, bool sniper = true)
    {
        var raw = UnitMemberReach(observer);
        if (sniper) raw = Mathf.Max(raw, SniperDetectionReach(observer, raw));
        return _visibility.Read(observer, target, raw, _darkness, HasNightVision(observer));
    }
    private bool TryFindGhostContact(GhostUnit a, GhostUnit b, out Vector3 closestA, out Vector3 closestB,
        out float distanceSqr, out float reach, out bool sniperDetection, out float acquisition)
    {
        distanceSqr = float.MaxValue; reach = acquisition = 0;
        closestA = closestB = default; sniperDetection = false;
        for (var i = 0; i < a.Count; i++)
        {
            var botA = MemberBot(a, i);
            if (botA == null || botA.IsDead || botA.GetPlayer == null) continue;
            var pa = botA.GetPlayer.Position;
            for (var j = 0; j < b.Count; j++)
            {
                var botB = MemberBot(b, j);
                if (botB == null || botB.IsDead || botB.GetPlayer == null) continue;
                var pb = botB.GetPlayer.Position;
                var va = MemberVisibility(botA, botB); var vb = MemberVisibility(botB, botA);
                var delta = pa - pb; var flat = delta.x * delta.x + delta.z * delta.z;
                var sniperA = SniperDetectionReach(botA, 0) > 0;
                var sniperB = SniperDetectionReach(botB, 0) > 0;
                var seesA = va.Reach > 0 && (sniperA ? flat : delta.sqrMagnitude) <= va.Reach * va.Reach;
                var seesB = vb.Reach > 0 && (sniperB ? flat : delta.sqrMagnitude) <= vb.Reach * vb.Reach;
                if (!seesA && !seesB) continue;
                var useSniper = seesA && sniperA || seesB && sniperB;
                var distance = useSniper ? flat : delta.sqrMagnitude;
                if (distance >= distanceSqr) continue;
                distanceSqr = distance; closestA = pa; closestB = pb;
                reach = Mathf.Max(seesA ? va.Reach : 0, seesB ? vb.Reach : 0);
                acquisition = Mathf.Max(seesA ? va.Acquisition : 0, seesB ? vb.Acquisition : 0);
                sniperDetection = useSniper;
            }
        }
        return distanceSqr < float.MaxValue;
    }
    private bool CanSeeGhostTarget(BotOwner observer, BotOwner target)
    {
        if (observer == null || target == null || observer.IsDead || target.IsDead
            || observer.GetPlayer == null || target.GetPlayer == null) return false;
        var reach = MemberVisibility(observer, target).Reach;
        var delta = observer.GetPlayer.Position - target.GetPlayer.Position;
        var distance = SniperDetectionReach(observer, 0) > 0 ? delta.x * delta.x + delta.z * delta.z : delta.sqrMagnitude;
        return reach > 0 && distance <= reach * reach && ClearFightLos(observer.GetPlayer.Position, target.GetPlayer.Position);
    }
    private bool HasVisibleAttacker(GhostUnit opposing, BotOwner target)
    {
        for (var i = 0; i < opposing.Count; i++)
        {
            var attacker = MemberBot(opposing, i);
            if (CanSeeGhostTarget(attacker, target)
                && Vector3.Distance(attacker.GetPlayer.Position, target.GetPlayer.Position) <= WeaponKillRange(attacker.GetPlayer) * 1.3f) return true;
        }
        return false;
    }
}
