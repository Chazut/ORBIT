using System;
using System.Collections.Generic;
using System.Threading;
using EFT;
using UnityEngine;

namespace Orbit.Core;

/// <summary>Raid-owned, identity-keyed exclusions, with deferred teardown outside brain callbacks.</summary>
internal sealed class BotExclusions
{
    private sealed class Entry
    {
        internal bool Released;
        internal bool ReleaseMover;
        internal float RetryAt;
        internal float ReportAt;
    }

    private sealed class Identity : IEqualityComparer<BotOwner>
    {
        public bool Equals(BotOwner a, BotOwner b) => ReferenceEquals(a, b);
        public int GetHashCode(BotOwner bot) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bot);
    }

    private readonly Dictionary<BotOwner, Entry> _entries = new(new Identity());
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

    private Orbit.Entities.Agent OwnedAgent(BotOwner bot)
    {
        var agent = _roster.GetAgent(bot);
        return agent != null && ReferenceEquals(agent.Bot, bot) ? agent : null;
    }

    internal bool Request(BotOwner bot, string source)
    {
        if (_disposed || Thread.CurrentThread.ManagedThreadId != _thread) return false;
        if (bot == null || bot.IsDead || bot.BotState == EBotState.Disposed
            || bot.GetPlayer?.AIData?.IsAI != true || bot.GetPlayer.IsYourPlayer) return false;
        if (_entries.ContainsKey(bot)) return true;
        var agent = OwnedAgent(bot);
        _entries.Add(bot, new Entry { ReleaseMover = agent?.IsActive == true || agent?.IsDormant == true });
        if (agent != null) agent.IsActive = false;
        source = string.IsNullOrWhiteSpace(source) ? "external-mod" : source.Replace('\r', ' ').Replace('\n', ' ');
        if (source.Length > 80) source = source.Substring(0, 80);
        Log.Info($"BOT EXCLUSION: requested bot={bot.ProfileId} source={source}");
        return true;
    }

    internal void Tick()
    {
        if (_disposed) return;
        _scratch.Clear();
        foreach (var pair in _entries) _scratch.Add(pair.Key);
        foreach (var bot in _scratch)
        {
            if (bot == null || bot.IsDead || bot.BotState == EBotState.Disposed)
            { Forget(bot); continue; }
            if (!_entries.TryGetValue(bot, out var entry) || entry.Released || Time.time < entry.RetryAt) continue;
            entry.RetryAt = Time.time + 0.25f;
            try
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
            catch (Exception e)
            {
                if (Time.time >= entry.ReportAt)
                {
                    entry.ReportAt = Time.time + 10f;
                    Log.Warning($"BOT EXCLUSION: release retry bot={bot.ProfileId} error={e.GetType().Name}: {e.Message}");
                }
            }
        }
        _scratch.Clear();
    }

    internal void Forget(BotOwner bot)
    { if (!ReferenceEquals(bot, null)) _entries.Remove(bot); }

    internal void Dispose()
    { _disposed = true; _entries.Clear(); _scratch.Clear(); }
}
