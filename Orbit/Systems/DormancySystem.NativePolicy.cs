using System.Collections.Generic;
using EFT;
using Orbit.Helpers;

namespace Orbit.Systems;

public partial class DormancySystem
{
    // Policy and driver are independent: an AI using a non-ORBIT brain still participates.
    // Ordinary toggled-off native types remain untouched; standard types use the normal ring/floor.
    private static bool IsStandardDormantType(BotOwner bot)
    {
        var role = bot?.Profile?.Info?.Settings?.Role;
        return role.HasValue && (role.Value.IsPMC()
            || role.Value.IsScav() && bot.Profile.WillBeAPlayerScav());
    }

    private bool IsNativeGhostEligible(BotOwner bot)
        => IsStandardDormantType(bot) || IsDefaultDormant(bot);

    private int NativeStandardCount(List<BotOwner> group)
    {
        var count = 0;
        foreach (var bot in group)
            if (!IsDefaultDormant(bot)) count++;
        return count;
    }

    private void CountNativeStandard(ref int total, ref int awake)
    {
        foreach (var group in _vanillaGroups.Values)
            foreach (var bot in group)
            {
                if (IsDefaultDormant(bot)) continue;
                total++;
                if (!_vanillaDormant.Contains(bot)) awake++;
            }
    }
}
