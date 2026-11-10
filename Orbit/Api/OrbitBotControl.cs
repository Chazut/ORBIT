using Comfort.Common;
using EFT;
using Orbit.Core;

namespace Orbit.Api;

/// <summary>Temporarily opt a living AI out of ORBIT control and Ghost Mode.</summary>
public static class OrbitBotControl
{
    /// <summary>
    /// Call on the Unity thread on the AI authority (solo, host or headless), once per spawn.
    /// True means accepted, including duplicate requests. Wake/cleanup runs on the next ORBIT tick;
    /// an unsafe landing can delay physical activation. Does not respawn or revive a bot.
    /// Returns false outside a raid, on another thread, or for an invalid/dead/human bot.
    /// The exclusion belongs to this BotOwner instance, never to all bots of its type or squad.
    /// </summary>
    public static bool ExcludeBot(BotOwner bot, string source = "external-mod")
        => Singleton<OrbitManager>.Instance?.BotExclusions.Request(bot, source) == true;

    /// <summary>
    /// Remove this bot's API exclusion on the next ORBIT tick, after any pending safe wake/cleanup.
    /// Call on the Unity thread on the AI authority, after releasing your own control of the bot.
    /// True means accepted (also when already enabled), not that ORBIT is its active brain layer.
    /// Normal role, combat and Ghost settings still apply. Does not revive or respawn a bot.
    /// </summary>
    public static bool EnableBot(BotOwner bot, string source = "external-mod")
        => Singleton<OrbitManager>.Instance?.BotExclusions.Enable(bot, source) == true;

    /// <summary>Unity-thread query. True also while a safe wake is pending.</summary>
    public static bool IsBotExcluded(BotOwner bot)
        => Singleton<OrbitManager>.Instance?.BotExclusions.Contains(bot) == true;
}
