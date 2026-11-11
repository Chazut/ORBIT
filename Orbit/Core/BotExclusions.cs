using System;
using System.Collections.Generic;
using System.Threading;
using EFT;
using UnityEngine;

namespace Orbit.Core;

/// <summary>Raid-owned, identity-keyed exclusions, with deferred teardown outside brain callbacks.</summary>
internal sealed partial class BotExclusions
{
    private sealed class Entry
    {
        internal bool Released;
        internal bool ReleaseMover;
        internal bool EnableRequested;
        internal bool Restoring;
        internal float RetryAt;
        internal float ReportAt;
    }

    private sealed class Identity : IEqualityComparer<BotOwner>
    {
        public bool Equals(BotOwner a, BotOwner b) => ReferenceEquals(a, b);
        public int GetHashCode(BotOwner bot) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bot);
    }

    private readonly Dictionary<BotOwner, Entry> _entries = new(new Identity());
    private readonly Dictionary<BotOwner, Action> _restorers = new(new Identity());
    private readonly List<BotOwner> _scratch = new();
    private readonly OrbitManager _manager;
    private readonly BotRoster _roster;
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private bool _disposed;

    internal BotExclusions(OrbitManager manager, BotRoster roster)
    { _manager = manager; _roster = roster; }

    internal bool Contains(BotOwner bot)
        => !_disposed && Thread.CurrentThread.ManagedThreadId == _thread
            && !ReferenceEquals(bot, null) && _entries.ContainsKey(bot);

    // Only the current eligible brain layer may reattach. Retain its callback while excluded,
    // including when the external mod opted out before the layer was constructed.
    internal void RegisterLayer(BotOwner bot, Action restore)
    { if (!_disposed) _restorers[bot] = restore; }
    internal void UnregisterLayer(BotOwner bot) => _restorers.Remove(bot);
    internal bool IsCurrentLayer(BotOwner bot, Action restore)
        => !_disposed && _restorers.TryGetValue(bot, out var current) && current == restore;
    internal bool BlocksRegistration(BotOwner bot)
        => Contains(bot) && !_entries[bot].Restoring;

    private bool CanRequest(BotOwner bot)
    {
        // Check the thread before touching Unity-backed properties.
        if (_disposed || Thread.CurrentThread.ManagedThreadId != _thread) return false;
        return bot != null && !bot.IsDead && bot.BotState != EBotState.Disposed
            && bot.GetPlayer?.AIData?.IsAI == true && !bot.GetPlayer.IsYourPlayer;
    }

    private static string Source(string source)
    {
        source = string.IsNullOrWhiteSpace(source) ? "external-mod" : source.Replace('\r', ' ').Replace('\n', ' ');
        return source.Length > 80 ? source.Substring(0, 80) : source;
    }

    private Orbit.Entities.Agent OwnedAgent(BotOwner bot)
    {
        var agent = _roster.GetAgent(bot);
        return agent != null && ReferenceEquals(agent.Bot, bot) ? agent : null;
    }

    internal bool Request(BotOwner bot, string source)
    {
        if (!CanRequest(bot)) return false;
        if (_entries.TryGetValue(bot, out var pending))
        {
            if (!pending.EnableRequested) return true;
            pending.EnableRequested = false;
            pending.RetryAt = 0f;
            // A callback may request exclusion again while attaching. Last request wins.
            if (pending.Restoring)
            {
                pending.Released = false;
                if (OwnedAgent(bot) is { } attaching)
                {
                    pending.ReleaseMover |= attaching.IsActive;
                    attaching.IsActive = false;
                }
            }
            Log.Info($"BOT EXCLUSION: requested bot={bot.ProfileId} source={Source(source)}");
            return true;
        }
        var agent = OwnedAgent(bot);
        _entries.Add(bot, new Entry { ReleaseMover = agent?.IsActive == true || agent?.IsDormant == true });
        if (agent != null) agent.IsActive = false;
        Log.Info($"BOT EXCLUSION: requested bot={bot.ProfileId} source={Source(source)}");
        return true;
    }

    internal bool Enable(BotOwner bot, string source)
    {
        if (!CanRequest(bot)) return false;
        if (!_entries.TryGetValue(bot, out var entry) || entry.EnableRequested) return true;
        entry.EnableRequested = true;
        entry.RetryAt = 0f;
        Log.Info($"BOT EXCLUSION: enable requested bot={bot.ProfileId} source={Source(source)}");
        return true;
    }

    internal void Tick()
    {
        if (_disposed) return;
        TickSleepRestrictions();
        _scratch.Clear();
        foreach (var pair in _entries) _scratch.Add(pair.Key);
        foreach (var bot in _scratch)
        {
            if (bot == null || bot.IsDead || bot.BotState == EBotState.Disposed)
            { Forget(bot); continue; }
            if (!_entries.TryGetValue(bot, out var entry)
                || entry.Released && !entry.EnableRequested || Time.time < entry.RetryAt) continue;
            entry.RetryAt = Time.time + 0.25f;
            try
            {
                if (!entry.Released)
                {
                    var agent = OwnedAgent(bot);
                    if (agent != null)
                    {
                        agent.IsActive = false;
                        agent.LootHandler?.Retire();
                    }
                    if (!_manager.DormancySystem.WakeForExternalControl(bot, agent))
                    {
                        if (Time.time >= entry.ReportAt)
                        {
                            entry.ReportAt = Time.time + 10f;
                            Log.Warning($"BOT EXCLUSION: wake pending bot={bot.ProfileId} reason=safe-activation");
                        }
                        continue;
                    }
                    if (agent != null) _manager.RemoveAgent(agent, detachedAlive: true);
                    if (entry.ReleaseMover)
                    {
                        bot.Mover.Pause = false;
                        bot.PatrollingData.Unpause();
                    }
                    _manager.DoorSystem.UnregisterBot(bot.GetPlayer.CharacterController.GetCollider());
                    // Preserve the existing ORBIT spawn guarantee against BSG standby while far away.
                    bot.StandBy.CanDoStandBy = false;
                    bot.StandBy.Activate();
                    entry.Released = true;
                    Log.Info($"BOT EXCLUSION: released bot={bot.ProfileId} awake=True control=False");
                }
                if (!entry.EnableRequested) continue;
                // Reattach outside brain callbacks, with the exclusion still guarding control/sleep.
                entry.Restoring = true;
                if (_restorers.TryGetValue(bot, out var restore)) restore();
                if (!entry.EnableRequested || _disposed || !_entries.TryGetValue(bot, out var current)
                    || !ReferenceEquals(current, entry)) continue;
                _entries.Remove(bot);
                Log.Info($"BOT EXCLUSION: enabled bot={bot.ProfileId} policy=normal");
            }
            catch (Exception e)
            {
                if (Time.time >= entry.ReportAt)
                {
                    entry.ReportAt = Time.time + 10f;
                    Log.Warning($"BOT EXCLUSION: {(entry.Restoring ? "enable retry" : "release retry")} bot={bot.ProfileId} error={e.GetType().Name}: {e.Message}");
                }
                // If attachment failed part-way, clean up the new agent before retrying.
                if (entry.Restoring)
                {
                    entry.Released = false;
                    if (OwnedAgent(bot) is { } attaching)
                    {
                        entry.ReleaseMover |= attaching.IsActive;
                        attaching.IsActive = false;
                    }
                }
            }
            finally { entry.Restoring = false; }
        }
        _scratch.Clear();
    }

    internal void Forget(BotOwner bot)
    {
        if (ReferenceEquals(bot, null)) return;
        _entries.Remove(bot);
        _restorers.Remove(bot);
        _sleepRestrictions.Remove(bot);
    }

    internal void Dispose()
    {
        _disposed = true;
        _entries.Clear(); _restorers.Clear(); _scratch.Clear();
        _sleepRestrictions.Clear(); _sleepScratch.Clear();
    }
}
