using System;
using EFT;

namespace Orbit.Helpers;

public static class ZoneBotType
{
    public static string For(BotOwner bot)
    {
        var profile = bot?.Profile;
        if (profile?.Info?.Settings == null) return "Other";
        var role = profile.Info.Settings.Role;
        // Same role-family names as the existing faction takeover switches.
        var name = role.ToString();
        if (name.IndexOf("untar", StringComparison.OrdinalIgnoreCase) >= 0) return "UNTAR";
        if (name.IndexOf("ruaf", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("remnant", StringComparison.OrdinalIgnoreCase) >= 0) return "RUAF";
        if (name.IndexOf("blackDiv", StringComparison.OrdinalIgnoreCase) >= 0) return "BlackDivision";
        if (name.IndexOf("ISB", StringComparison.OrdinalIgnoreCase) >= 0) return "ISB";
        if (name.IndexOf("Combine", StringComparison.OrdinalIgnoreCase) >= 0) return "Combine";
        if (role.IsPMC()) return "PMC";
        if (profile.WillBeAPlayerScav()) return "PlayerScav";
        if (role.IsScav()) return "Scav";
        if (role.IsGoon()) return "Goons";
        if (role.IsCultist()) return "Cultist";
        if (role.IsBloodhound()) return "Bloodhound";
        if (role == WildSpawnType.exUsec) return "Rogue";
        if (role == WildSpawnType.pmcBot) return "Raider";
        if (BotTypeUtils.IsBoss(role)) return "Boss";
        if (BotType.Follower.IsBotEnabled(role)) return "Follower";
        return "Other";
    }
}
