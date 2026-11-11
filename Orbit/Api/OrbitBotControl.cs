using Comfort.Common;
using EFT;
using Orbit.Core;

namespace Orbit.Api;

/// <summary>Per-bot control and Ghost sleep policies for external mods.</summary>
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

    /// <summary>
    /// Allow or prevent Ghost sleep without changing ORBIT control, objectives or role eligibility.
    /// False requests a safe wake on the next update if already asleep; true removes only this
    /// sleep restriction and does not force sleep or undo ExcludeBot. Unity thread / AI authority
    /// only, once per spawned BotOwner instance. Source is diagnostic, not a separate owner lock.
    /// True means accepted, not that physical activation has already completed.
    /// </summary>
    public static bool SetBotSleepAllowed(BotOwner bot, bool allowed, string source = "external-mod")
        => Singleton<OrbitManager>.Instance?.BotExclusions.SetSleepAllowed(bot, allowed, source) == true;

    /// <summary>Unity-thread query: sleep is blocked by the API (sleep restriction or full exclusion).
    /// False means normal Ghost eligibility applies, not that the bot is necessarily sleeping.</summary>
    public static bool IsBotSleepBlocked(BotOwner bot)
        => Singleton<OrbitManager>.Instance?.BotExclusions.BlocksSleep(bot) == true;
}
