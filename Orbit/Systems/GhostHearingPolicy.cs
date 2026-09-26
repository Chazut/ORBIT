using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using EFT;
using HarmonyLib;
using Orbit.Brain;
using Orbit.Helpers;

namespace Orbit.Systems;

internal enum GhostHearingCategory
{
    None, Pmc, PlayerScav, Scav, Goons, Bosses, Cultists, Raiders, Bloodhounds,
    OtherVanilla, UntarHunters, RuafHunters, RoguesVsRaiders, ArmyOfTwo, Isb, BlackDivision,
}

internal static class GhostHearingPolicy
{
    private sealed class ComponentFlag
    {
        private readonly Type _type;
        private readonly Func<object, bool> _read;
        private readonly bool _unknown;

        internal ComponentFlag(string typeName, string member, bool method, bool unknown)
        {
            _unknown = unknown;
            _type = AccessTools.TypeByName(typeName);
            if (_type == null) return;
            try
            {
                var instance = Expression.Parameter(typeof(object), "instance");
                var typed = Expression.Convert(instance, _type);
                Expression value = method ? Expression.Call(typed, AccessTools.Method(_type, member, Type.EmptyTypes))
                    : Expression.Field(typed, AccessTools.Field(_type, member));
                _read = Expression.Lambda<Func<object, bool>>(value, instance).Compile();
            }
            catch (Exception e) { Log.Warning($"GHOST HEARING: binding unavailable for {typeName}.{member}: {e.Message}"); }
        }

        internal bool Get(BotOwner bot)
        {
            if (_type == null) return false;
            var component = bot.GetComponent(_type);
            return component != null && (_read?.Invoke(component) ?? _unknown);
        }
    }

    private static ComponentFlag _hunt, _untarGuard, _ruafGuard;
    private static readonly Dictionary<WildSpawnType, string> RoleNames = new();
    private static bool _reportedFailure;

    internal static GhostHearingCategory Category(BotOwner bot)
    {
        try { return ReadCategory(bot); }
        catch (Exception e)
        {
            if (!_reportedFailure)
                Log.Warning($"GHOST HEARING: category unavailable: {e.GetType().Name}: {e.Message}");
            _reportedFailure = true;
            return GhostHearingCategory.None;
        }
    }

    private static GhostHearingCategory ReadCategory(BotOwner bot)
    {
        if (bot?.Profile?.Info?.Settings == null) return GhostHearingCategory.None;
        _hunt ??= new ComponentFlag("MoreBotsAPI.Components.BotHuntManager", "active", false, false);
        _untarGuard ??= new ComponentFlag("TacticalToasterUNTARGH.Components.BotUntarManager", "HasAssignedCheckpoint", true, true);
        _ruafGuard ??= new ComponentFlag("RUAFComeHome.Components.BotRuafManager", "HasAssignedCheckpoint", true, true);
        var role = bot.Profile.Info.Settings.Role;
        // Sniper scavs keep their firing position, including when another mod tags their group.
        if (role == WildSpawnType.marksman) return GhostHearingCategory.None;
        if (!RoleNames.TryGetValue(role, out var name)) RoleNames[role] = name = role.ToString();
        var spawn = bot.SpawnProfileData?.SpawnParams?.Id_spawn;
        var spawnHunt = spawn?.IndexOf("hunt", StringComparison.OrdinalIgnoreCase) >= 0;
        var owner = spawnHunt ? HuntFactionPolicy.Owner(spawn, OrbitBrainLayer.LegacyUntarHunts) : null;
        // An assigned checkpoint is authoritative even during a temporary patrol/combat transition.
        if (_untarGuard.Get(bot) || _ruafGuard.Get(bot)) return GhostHearingCategory.None;
        var hunter = spawnHunt || _hunt.Get(bot);
        if (Contains(name, "untar") || owner == "untar" || (int)role is >= 1170 and <= 1173)
            return hunter ? GhostHearingCategory.UntarHunters : GhostHearingCategory.None;
        if (Contains(name, "ruaf") || Contains(name, "remnant") || owner is "ruaf" or "remnant"
            || (int)role is >= 848400 and <= 848406)
            return hunter ? GhostHearingCategory.RuafHunters : GhostHearingCategory.None;
        if (NativeGhostAdapters.IsWarbandMember(bot)) return GhostHearingCategory.RoguesVsRaiders;
        if (name.Equals("bossRook", StringComparison.OrdinalIgnoreCase)
            || name.Equals("followerTombstone", StringComparison.OrdinalIgnoreCase)
            || (int)role is 658400 or 658401) return GhostHearingCategory.ArmyOfTwo;
        if (Contains(name, "blackDiv") || owner == "blackDiv" || (int)role is >= 848420 and <= 848424)
            return GhostHearingCategory.BlackDivision;
        if (name.StartsWith("ISB", StringComparison.OrdinalIgnoreCase) || owner == "ISB"
            || (int)role is >= 13700 and <= 13716) return GhostHearingCategory.Isb;
        // A generic raider role does not grant permission to an unidentified mod's hunt squad.
        if (spawnHunt && role == WildSpawnType.pmcBot) return GhostHearingCategory.None;
        if (role.IsPMC()) return GhostHearingCategory.Pmc;
        if (bot.Profile.WillBeAPlayerScav()) return GhostHearingCategory.PlayerScav;
        if (role.IsScav()) return GhostHearingCategory.Scav;
        if (role.IsGoon()) return GhostHearingCategory.Goons;
        if (role.IsCultist()) return GhostHearingCategory.Cultists;
        if (role.IsRaider()) return GhostHearingCategory.Raiders;
        if (role.IsBloodhound()) return GhostHearingCategory.Bloodhounds;
        if (BotTypeUtils.IsBoss(role) || (int)role < 200 && name.StartsWith("follower", StringComparison.Ordinal))
            return GhostHearingCategory.Bosses;
        return (int)role >= 0 && (int)role < 200 ? GhostHearingCategory.OtherVanilla : GhostHearingCategory.None;
    }

    private static bool Contains(string value, string part) => value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
}
