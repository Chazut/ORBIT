using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using BepInEx.Bootstrap;
using Comfort.Common;
using EFT;

namespace Orbit.Helpers;

// Optional Fika accessors are compiled once per raid. No dependency on the companion addon,
// per-poll reflection, temporary player lists or per-player boxing.
internal sealed class GhostSpectatorPlayers
{
    private readonly bool _fika;
    private readonly Func<object> _handler;
    private readonly Func<object, IReadOnlyList<Player>> _humans;
    private readonly Func<object, List<int>> _extracted;
    private readonly Func<Player, int> _netId;
    private readonly Func<bool> _headless;
    private bool _reportedError;

    internal GhostSpectatorPlayers()
    {
        _fika = Chainloader.PluginInfos.TryGetValue("com.fika.core", out var plugin);
        if (!_fika) return;
        try
        {
            var assembly = plugin.Instance.GetType().Assembly;
            var network = assembly.GetType("Fika.Core.Networking.IFikaNetworkManager", throwOnError: true);
            var handler = assembly.GetType("Fika.Core.Main.Components.CoopHandler", throwOnError: true);
            var player = assembly.GetType("Fika.Core.Main.Players.FikaPlayer", throwOnError: true);
            var backend = assembly.GetType("Fika.Core.Main.Utils.FikaBackendUtils", throwOnError: true);
            var instance = Expression.Property(null, typeof(Singleton<>).MakeGenericType(network), "Instance");
            _handler = Expression.Lambda<Func<object>>(Expression.Condition(
                Expression.Equal(instance, Expression.Constant(null, network)), Expression.Constant(null, typeof(object)),
                Expression.Convert(Expression.Property(instance, "CoopHandler"), typeof(object)))).Compile();
            var obj = Expression.Parameter(typeof(object));
            _humans = Expression.Lambda<Func<object, IReadOnlyList<Player>>>(Expression.Convert(
                Expression.Property(Expression.Convert(obj, handler), "HumanPlayers"), typeof(IReadOnlyList<Player>)), obj).Compile();
            _extracted = Expression.Lambda<Func<object, List<int>>>(
                Expression.Property(Expression.Convert(obj, handler), "ExtractedPlayers"), obj).Compile();
            var bot = Expression.Parameter(typeof(Player));
            _netId = Expression.Lambda<Func<Player, int>>(Expression.Field(Expression.Convert(bot, player), "NetId"), bot).Compile();
            _headless = Expression.Lambda<Func<bool>>(Expression.Property(null, backend, "IsHeadless")).Compile();
        }
        catch (Exception e) { ReportError(e); }
    }

    internal bool TryRead(GameWorld world, out bool observedHuman, out bool activeHuman)
    {
        observedHuman = activeHuman = false;
        if (world == null) return false;
        try
        {
            IReadOnlyList<Player> players = world.AllAlivePlayersList;
            List<int> extracted = null;
            var headless = false;
            if (_fika)
            {
                if (_handler == null || _humans == null || _extracted == null || _netId == null || _headless == null) return false;
                var handler = _handler();
                if (handler == null) return false;
                players = _humans(handler);
                extracted = _extracted(handler);
                headless = _headless();
                if (players == null || extracted == null) return false;
            }
            // Handles death before the first poll without arming on an empty headless lobby.
            if (!headless && world.MainPlayer != null && !world.MainPlayer.IsAI) observedHuman = true;
            if (players == null) return false;
            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player == null || player.IsAI || headless && player.IsYourPlayer) continue;
                observedHuman = true;
                if (extracted != null && extracted.Contains(_netId(player))) continue;
                if (player.HealthController == null) return false;
                if (player.HealthController.IsAlive) { activeHuman = true; return true; }
            }
            return true;
        }
        catch (Exception e) { ReportError(e); return false; }
    }

    private void ReportError(Exception e)
    {
        if (_reportedError) return;
        _reportedError = true;
        Log.Warning($"GHOST SPECTATOR: player state unavailable; keeping current Ghost state: {e.Message}");
    }
}
