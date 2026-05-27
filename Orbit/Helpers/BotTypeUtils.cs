using System;
using EFT;
using Orbit.Inventory;

namespace Orbit.Helpers;

// ╔══════════════════════════════════════════════════════════════════╗
// ║ Bot-type classification used by the inventory/loot subsystem and  ║
// ║ by the waypoint dispatch to gate features per faction. Three      ║
// ║ flag enums share the same bit layout so a plain cast is enough    ║
// ║ to route a restricted-UI enum back through the full BotType       ║
// ║ resolver.                                                         ║
// ╚══════════════════════════════════════════════════════════════════╝

/// <summary>
/// Full bot-type bitmask used internally by the gating predicates. Not
/// directly surfaced in F12; the user-facing toggles use the restricted
/// <see cref="LootingFaction"/> and <see cref="ExtractFaction"/> subsets.
/// </summary>
[Flags]
public enum BotType
{
    Scav = 1,
    Pmc = 2,
    PlayerScav = 4,
    Raider = 8,
    Cultist = 16,
    Boss = 32,
    Follower = 64,
    Bloodhound = 128,
    // Goons (Knight + Big Pipe + Bird Eye) as a unit — all three brains
    // go through the same inventory pipeline, splitting across Boss +
    // Follower in the F12 mask would be confusing.
    Goon = 256,

    None = 0,
    All = Scav | Pmc | PlayerScav | Raider | Cultist | Boss | Follower | Bloodhound | Goon,
}

/// <summary>
/// Restricted subset surfaced in "Enable corpse / container / loose item
/// looting" F12 toggles. Same bit positions as <see cref="BotType"/> so
/// a plain cast forwards into the full resolver, but the dropdown only
/// shows the factions actually modified AND likely to loot.
/// </summary>
[Flags]
public enum LootingFaction
{
    None = 0,
    Scav = 1,
    Pmc = 2,
    PlayerScav = 4,
    Goon = 256,
    All = Scav | Pmc | PlayerScav | Goon,
}

/// <summary>
/// Restricted subset surfaced in the "Extract allowed for" F12 toggle.
/// Only PMC and PlayerScav can route to an exfil.
/// </summary>
[Flags]
public enum ExtractFaction
{
    None = 0,
    Pmc = 2,
    PlayerScav = 4,
    All = Pmc | PlayerScav,
}

public static class BotTypeUtils
{
    // ── BotType flag accessors ──────────────────────────────────────
    public static bool HasScav(this BotType botType) => (botType & BotType.Scav) != 0;
    public static bool HasPmc(this BotType botType) => (botType & BotType.Pmc) != 0;
    public static bool HasPlayerScav(this BotType botType) => (botType & BotType.PlayerScav) != 0;
    public static bool HasRaider(this BotType botType) => (botType & BotType.Raider) != 0;
    public static bool HasCultist(this BotType botType) => (botType & BotType.Cultist) != 0;
    public static bool HasBoss(this BotType botType) => (botType & BotType.Boss) != 0;
    public static bool HasFollower(this BotType botType) => (botType & BotType.Follower) != 0;
    public static bool HasBloodhound(this BotType botType) => (botType & BotType.Bloodhound) != 0;
    public static bool HasGoon(this BotType botType) => (botType & BotType.Goon) != 0;

    // ── Restricted-enum dispatch ────────────────────────────────────
    // Same bit layout as BotType, so a plain cast forwards to the
    // per-WildSpawnType resolver.
    public static bool IsBotEnabled(this LootingFaction enabledTypes, WildSpawnType botType)
        => ((BotType)enabledTypes).IsBotEnabled(botType);

    public static bool IsBotEnabled(this ExtractFaction enabledTypes, WildSpawnType botType)
        => ((BotType)enabledTypes).IsBotEnabled(botType);

    /// <summary>
    /// Convenience overload for the loot-brain context: PlayerScavs
    /// resolve via <see cref="LootBrain.IsPlayerScav"/> (so the
    /// PlayerScav flag wins over the assault WildSpawnType the brain's
    /// bot was originally spawned as), everything else falls through to
    /// the WildSpawnType resolver.
    /// </summary>
    public static bool IsBotEnabledForBrain(this LootingFaction enabledTypes, LootBrain brain)
        => ((BotType)enabledTypes).IsBotEnabledForBrain(brain);

    public static bool IsBotEnabledForBrain(this BotType enabledTypes, LootBrain brain)
    {
        if (brain.IsPlayerScav)
            return enabledTypes.HasPlayerScav();
        var role = brain.BotOwner.Profile.Info.Settings.Role;
        return enabledTypes.IsBotEnabled(role);
    }

    public static bool IsBotEnabled(this BotType enabledTypes, WildSpawnType botType)
    {
        if (botType.IsPMC())
            return enabledTypes.HasPmc();

        // Goons route to their own flag (Knight is also a Boss in BSG's
        // taxonomy but we treat the trio as a single faction).
        if (botType.IsGoon())
            return enabledTypes.HasGoon();

        if (IsBoss(botType))
            return enabledTypes.HasBoss();

        switch (botType)
        {
            case WildSpawnType.assault:
            case WildSpawnType.assaultGroup:
                return enabledTypes.HasScav();
            case WildSpawnType.followerBully:
            case WildSpawnType.followerGluharAssault:
            case WildSpawnType.followerGluharScout:
            case WildSpawnType.followerGluharSecurity:
            case WildSpawnType.followerGluharSnipe:
            case WildSpawnType.followerKojaniy:
            case WildSpawnType.followerSanitar:
            case WildSpawnType.followerTagilla:
            case WildSpawnType.followerTest:
            case WildSpawnType.followerZryachiy:
            case WildSpawnType.followerKolontayAssault:
            case WildSpawnType.followerKolontaySecurity:
            case WildSpawnType.bossBoarSniper:
            case WildSpawnType.followerBoarClose1:
            case WildSpawnType.followerBoarClose2:
            case WildSpawnType.followerBoar:
                return enabledTypes.HasFollower();
            case WildSpawnType.exUsec:
            case WildSpawnType.pmcBot:
                return enabledTypes.HasRaider();
            case WildSpawnType.sectantPriest:
            case WildSpawnType.sectantWarrior:
            case WildSpawnType.cursedAssault:
                return enabledTypes.HasCultist();
            case WildSpawnType.arenaFighter:
            case WildSpawnType.arenaFighterEvent:
            case WildSpawnType.crazyAssaultEvent:
                return enabledTypes.HasBloodhound();
            default:
                return false;
        }
    }

    public static bool IsPMC(this WildSpawnType wildSpawnType)
        => wildSpawnType is WildSpawnType.pmcBEAR or WildSpawnType.pmcUSEC;

    public static bool IsScav(this WildSpawnType wildSpawnType)
        => wildSpawnType is WildSpawnType.assault or WildSpawnType.assaultGroup;

    /// <summary>
    /// The Goons trio: Knight (boss) + Big Pipe + Bird Eye (followers).
    /// Treated as a unit by dispatcher rules — same roaming behaviour,
    /// same local-pin opt-in.
    /// </summary>
    public static bool IsGoon(this WildSpawnType wildSpawnType)
        => wildSpawnType is WildSpawnType.bossKnight
            or WildSpawnType.followerBigPipe
            or WildSpawnType.followerBirdEye;

    public static bool IsBoss(WildSpawnType wildSpawnType)
    {
        switch (wildSpawnType)
        {
            case WildSpawnType.bossBully:
            case WildSpawnType.bossGluhar:
            case WildSpawnType.bossKilla:
            case WildSpawnType.bossKnight:
            case WildSpawnType.bossKojaniy:
            case WildSpawnType.bossSanitar:
            case WildSpawnType.bossTagilla:
            case WildSpawnType.bossTest:
            case WildSpawnType.bossZryachiy:
            case WildSpawnType.bossBoar:
            case WildSpawnType.bossKolontay:
            case WildSpawnType.bossPartisan:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// True when this profile will be controlled by a human player as a
    /// PlayerScav. Shares WildSpawnType.assault with bot scavs but needs
    /// distinct dispatch — PlayerScavs follow main objectives like PMCs,
    /// not the home-pinning bot-scav pattern.
    /// </summary>
    public static bool WillBeAPlayerScav(this Profile profile)
    {
        // Legacy player-scav creation path: nickname contains " (".
        if (profile.Info.Nickname.Contains(" ("))
            return true;

        // SPT player-scav: assault role + non-empty main-profile nickname.
        return profile.Info.Settings.Role == WildSpawnType.assault
            && !string.IsNullOrEmpty(profile.Info.MainProfileNickname);
    }
}
