using EFT;
using UnityEngine;

namespace Orbit.Systems;

public partial class DormancySystem
{
    private const float SniperMinimumDetectRange = 200f;

    private static float SniperDetectionReach(BotOwner bot, float weaponReach)
        => bot?.Profile?.Info?.Settings?.Role == WildSpawnType.marksman
            ? Mathf.Clamp(weaponReach, SniperMinimumDetectRange, SkirmishReachCap) : 0f;

    private static BotOwner MemberBot(GhostUnit unit, int index)
        => index < unit.Agents.Count ? unit.Agents[index].Bot : unit.VanillaBots[index - unit.Agents.Count];

    private static bool TryFindGhostContact(GhostUnit a, GhostUnit b, out Vector3 closestA, out Vector3 closestB,
        out float distanceSqr, out float reach, out bool sniperDetection)
    {
        distanceSqr = float.MaxValue;
        closestA = closestB = default;
        reach = Mathf.Max(a.Reach, b.Reach);
        sniperDetection = false;
        var normalReach = reach;
        for (var i = 0; i < a.Count; i++)
        {
            var pa = i < a.Agents.Count ? a.Agents[i].Position : a.VanillaBots[i - a.Agents.Count].GetPlayer.Position;
            var sniperA = a.SniperReach > 0f && MemberBot(a, i).Profile.Info.Settings.Role == WildSpawnType.marksman
                ? a.SniperReach : 0f;
            for (var j = 0; j < b.Count; j++)
            {
                var pb = j < b.Agents.Count ? b.Agents[j].Position : b.VanillaBots[j - b.Agents.Count].GetPlayer.Position;
                var sniperB = b.SniperReach > 0f && MemberBot(b, j).Profile.Info.Settings.Role == WildSpawnType.marksman
                    ? b.SniperReach : 0f;
                var delta = pa - pb;
                var sniperReach = Mathf.Max(sniperA, sniperB);
                var horizontalSqr = sniperReach > 0f ? delta.x * delta.x + delta.z * delta.z : 0f;
                var useSniper = sniperReach > 0f && horizontalSqr <= sniperReach * sniperReach;
                var d = useSniper ? horizontalSqr : delta.sqrMagnitude;
                var candidateReach = useSniper ? sniperReach : normalReach;
                if (d > candidateReach * candidateReach || d >= distanceSqr) continue;
                distanceSqr = d;
                closestA = pa;
                closestB = pb;
                reach = candidateReach;
                sniperDetection = useSniper;
            }
        }
        // Heights are retained in the selected positions for physical sight lines and fight resolution.
        return distanceSqr < float.MaxValue;
    }
}
