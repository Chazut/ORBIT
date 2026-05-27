using System;
using System.Collections;
using System.Reflection;
using Comfort.Common;
using EFT;

namespace Orbit.Sain;

/// <summary>
/// Reflective lookup against the SAIN plugin to read a bot's currently-
/// assigned personality brain name (e.g. "Rat", "GigaChad", "Wreckless").
/// SAIN is a hard dependency, but the brain attachment is asynchronous —
/// the BotComponent lives in <c>SAIN.Components.BotManagerComponent.Bots</c>
/// (a dictionary keyed by Profile.Id, not a UnityEngine Component on the
/// BotOwner GameObject), and SAIN populates it 1-2 seconds after spawn.
/// Brain name resolution therefore returns null during that window —
/// callers must defer use of the result (see the deferred-personality
/// path in <see cref="Orbit.Core.SquadRegistry"/>).
/// </summary>
public static class SainPersonality
{
    private static bool _initAttempted;
    private static bool _typesResolved;
    private static Type _botManagerComponentType;
    private static Type _ePersonalityType;
    private static PropertyInfo _botsProperty;
    private static PropertyInfo _instanceProperty;
    private static object _cachedBotManager;

    public static bool IsSainAvailable => _typesResolved;

    public static void InitIfNeeded()
    {
        if (_initAttempted) return;
        _initAttempted = true;

        try
        {
            _botManagerComponentType = Type.GetType("SAIN.Components.BotManagerComponent, SAIN");
            _ePersonalityType = Type.GetType("SAIN.Models.Preset.Personalities.EPersonality, SAIN");
            if (_botManagerComponentType == null)
            {
                Log.Info("SainPersonality: SAIN.Components.BotManagerComponent not found — SAIN missing or version-mismatched. Every PMC will resolve to Average.");
                return;
            }
            if (_ePersonalityType == null)
            {
                Log.Info("SainPersonality: SAIN.Models.Preset.Personalities.EPersonality not found — SAIN missing or version-mismatched. Every PMC will resolve to Average.");
                return;
            }
            _botsProperty = _botManagerComponentType.GetProperty("Bots", BindingFlags.Public | BindingFlags.Instance);
            _instanceProperty = _botManagerComponentType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            _typesResolved = _botsProperty != null;
            Log.Info($"SainPersonality: reflection ready={_typesResolved} (BotManagerComponent={_botManagerComponentType.FullName}, EPersonality={_ePersonalityType.FullName}, BotsProp={_botsProperty != null}, InstanceProp={_instanceProperty != null})");
        }
        catch (Exception ex)
        {
            Log.Warning($"SainPersonality.InitIfNeeded threw: {ex.Message} — every PMC will resolve to Average");
            _typesResolved = false;
        }
    }

    private static object ResolveBotManager()
    {
        if (_cachedBotManager != null) return _cachedBotManager;
        // Prefer GameWorld.GetComponent (the canonical way SAIN attaches
        // the manager). Fall back to the static Instance property if
        // present.
        try
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld != null)
            {
                var component = gameWorld.GetComponent(_botManagerComponentType);
                if (component != null)
                {
                    _cachedBotManager = component;
                    return _cachedBotManager;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"SainPersonality.ResolveBotManager via GameWorld.GetComponent failed: {ex.Message}");
        }
        if (_instanceProperty != null)
        {
            try
            {
                _cachedBotManager = _instanceProperty.GetValue(null);
                return _cachedBotManager;
            }
            catch (Exception ex)
            {
                Log.Debug($"SainPersonality.ResolveBotManager via Instance property failed: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// Reads the SAIN-assigned brain name for the given bot. Returns
    /// null if SAIN isn't loaded, the BotComponent hasn't been attached
    /// yet (typical in the 1-2 seconds after spawn), or the per-bot
    /// Info / Personality field couldn't be read. Caller must be
    /// defensive against the null and retry later if appropriate.
    /// </summary>
    public static string GetBrainName(BotOwner bot)
    {
        if (bot == null) return null;
        InitIfNeeded();
        if (!_typesResolved) return null;

        try
        {
            var manager = ResolveBotManager();
            if (manager == null) return null;
            var bots = _botsProperty.GetValue(manager);
            if (bots == null) return null;

            // SAIN's Bots is a Dictionary<string, BotComponent> keyed by
            // Profile.Id (string). Use the indexer.
            var profileId = bot.Profile?.Id;
            if (string.IsNullOrEmpty(profileId)) return null;

            var dictType = bots.GetType();
            object botComponent = null;
            // ContainsKey + indexer is the fast path; otherwise iterate
            // Values (slower fallback for unusual dictionary impls).
            var containsKey = dictType.GetMethod("ContainsKey");
            var item = dictType.GetProperty("Item");
            if (containsKey != null && item != null)
            {
                var has = (bool)containsKey.Invoke(bots, new object[] { profileId });
                if (!has) return null;
                botComponent = item.GetValue(bots, new object[] { profileId });
            }
            else
            {
                var values = bots.GetType().GetProperty("Values")?.GetValue(bots) as IEnumerable;
                if (values == null) return null;
                foreach (var bc in values)
                {
                    var p = bc?.GetType().GetProperty("Player")?.GetValue(bc);
                    var pid = p?.GetType().GetProperty("ProfileId")?.GetValue(p) as string;
                    if (pid == profileId) { botComponent = bc; break; }
                }
            }
            if (botComponent == null) return null;

            var info = botComponent.GetType().GetProperty("Info")?.GetValue(botComponent);
            if (info == null) return null;
            var personality = info.GetType().GetProperty("Personality")?.GetValue(info);
            if (personality == null) return null;
            if (Enum.IsDefined(_ePersonalityType, personality))
                return Enum.GetName(_ePersonalityType, personality);
            return personality.ToString();
        }
        catch (Exception ex)
        {
            Log.Debug($"SainPersonality.GetBrainName({bot}) failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Maps a SAIN brain name to one of the five archetypes using the
    /// user's F12 mapping strings (comma-separated lists per archetype).
    /// Null / unknown brain names map to <see cref="PersonalityArchetype.Average"/>.
    /// </summary>
    public static PersonalityArchetype MapBrainToArchetype(string brainName)
    {
        if (string.IsNullOrWhiteSpace(brainName)) return PersonalityArchetype.Average;
        if (ContainsToken(Plugin.SainArchetypeTimmyBrains?.Value, brainName)) return PersonalityArchetype.Timmy;
        if (ContainsToken(Plugin.SainArchetypeCautiousBrains?.Value, brainName)) return PersonalityArchetype.Cautious;
        if (ContainsToken(Plugin.SainArchetypeAggressiveBrains?.Value, brainName)) return PersonalityArchetype.Aggressive;
        if (ContainsToken(Plugin.SainArchetypeVeryAggressiveBrains?.Value, brainName)) return PersonalityArchetype.VeryAggressive;
        // Explicit Average mapping (default "Normal") — keeps the
        // fallback semantic but lets the user surface specific brain
        // names in the F12 list. Unknown brains still fall through to
        // Average via the return below.
        if (ContainsToken(Plugin.SainArchetypeAverageBrains?.Value, brainName)) return PersonalityArchetype.Average;
        return PersonalityArchetype.Average;
    }

    private static bool ContainsToken(string commaSeparated, string token)
    {
        if (string.IsNullOrEmpty(commaSeparated) || string.IsNullOrEmpty(token)) return false;
        foreach (var part in commaSeparated.Split(','))
        {
            if (string.Equals(part.Trim(), token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
