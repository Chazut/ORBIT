using Comfort.Common;
using EFT;
using Orbit.Core;

namespace Orbit.Api;

/// <summary>Opt a living AI out of ORBIT control and Ghost Mode for the rest of its life.</summary>
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

    /// <summary>Unity-thread query. True also while a safe wake is pending.</summary>
    public static bool IsBotExcluded(BotOwner bot)
        => Singleton<OrbitManager>.Instance?.BotExclusions.Contains(bot) == true;
}
