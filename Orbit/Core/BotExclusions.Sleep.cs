using System;
using System.Collections.Generic;
using System.Threading;
using EFT;
using UnityEngine;

namespace Orbit.Core;

internal sealed partial class BotExclusions
{
    private sealed class SleepRestriction
    {
        internal bool Awake;
        internal float RetryAt;
        internal float ReportAt;
    }

    private readonly Dictionary<BotOwner, SleepRestriction> _sleepRestrictions = new(new Identity());
    private readonly List<BotOwner> _sleepScratch = new();

    internal bool BlocksSleep(BotOwner bot)
        => !_disposed && Thread.CurrentThread.ManagedThreadId == _thread
            && !ReferenceEquals(bot, null) && (_entries.ContainsKey(bot) || _sleepRestrictions.ContainsKey(bot));

    internal bool SetSleepAllowed(BotOwner bot, bool allowed, string source)
    {
        if (!CanRequest(bot)) return false;
        if (allowed)
        {
            if (_sleepRestrictions.Remove(bot))
                Log.Info($"BOT SLEEP: allowed bot={bot.ProfileId} source={Source(source)} policy=normal");
            return true;
        }
        if (_sleepRestrictions.ContainsKey(bot)) return true;
        _sleepRestrictions.Add(bot, new SleepRestriction());
        Log.Info($"BOT SLEEP: blocked bot={bot.ProfileId} source={Source(source)} control=unchanged");
        return true;
    }

    private void TickSleepRestrictions()
    {
        // Snapshot keys because activation can invoke another mod's callbacks. Reuse capacity.
        _sleepScratch.Clear();
        foreach (var pair in _sleepRestrictions) _sleepScratch.Add(pair.Key);
        for (var i = 0; i < _sleepScratch.Count; i++)
        {
            var bot = _sleepScratch[i];
            if (bot == null || bot.IsDead || bot.BotState == EBotState.Disposed)
            { Forget(bot); continue; }
            if (!_sleepRestrictions.TryGetValue(bot, out var entry) || entry.Awake
                || Time.time < entry.RetryAt || _entries.ContainsKey(bot)) continue;
            entry.RetryAt = Time.time + 0.25f;
            try
            {
                // Keep agent, squad, objectives, loot and the normal mover-resume path.
                // The full-exclusion path, if requested, owns its own wake and teardown.
                if (!_manager.DormancySystem.WakeForSleepRestriction(bot, OwnedAgent(bot)))
                {
                    if (Time.time >= entry.ReportAt)
                    {
                        entry.ReportAt = Time.time + 10f;
                        Log.Warning($"BOT SLEEP: wake pending bot={bot.ProfileId} reason=safe-activation");
                    }
                    continue;
                }
                if (_disposed || !_sleepRestrictions.TryGetValue(bot, out var current)
                    || !ReferenceEquals(current, entry) || _entries.ContainsKey(bot)) continue;
                entry.Awake = true;
                Log.Info($"BOT SLEEP: awake bot={bot.ProfileId} control=unchanged");
            }
            catch (Exception e)
            {
                if (Time.time >= entry.ReportAt)
                {
                    entry.ReportAt = Time.time + 10f;
                    Log.Warning($"BOT SLEEP: wake retry bot={bot.ProfileId} error={e.GetType().Name}: {e.Message}");
                }
            }
        }
        _sleepScratch.Clear();
    }
}
