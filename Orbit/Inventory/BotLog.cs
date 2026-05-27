using System;
using EFT;

namespace Orbit.Inventory;

/// <summary>
/// Per-bot logger wrapper. Prefixes every message with the bot's nickname
/// so a trace of one bot's loot pipeline can be greppped out of the
/// shared BepInEx log file. Severity gates exist so call sites can use
/// <c>if (log.DebugEnabled)</c> to skip expensive
/// <c>$"{item.Name.Localized()}"</c> formatting when debug logs aren't
/// wanted. Debug is gated on the Debug build configuration so production
/// users don't get the verbose loot trace.
/// </summary>
public sealed class BotLog
{
    private readonly string _prefix;

    public BotLog(BotOwner bot)
    {
        var nickname = bot?.Profile?.Nickname;
        _prefix = string.IsNullOrEmpty(nickname) ? string.Empty : $"[{nickname}] ";
    }

    public bool InfoEnabled => true;
    public bool WarningEnabled => true;
    public bool ErrorEnabled => true;

#if DEBUG
    public bool DebugEnabled => true;
#else
    public bool DebugEnabled => false;
#endif

    public void LogDebug(string message) => Log.Debug(_prefix + message);
    public void LogInfo(string message) => Log.Info(_prefix + message);
    public void LogWarning(string message) => Log.Warning(_prefix + message);
    public void LogError(string message) => Log.Error(_prefix + message);
    public void LogError(Exception e) => Log.Error(_prefix + e);
}
