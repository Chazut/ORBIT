using System;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

public partial class DormancySystem
{
    private GhostSpectatorPlayers _spectatorPlayers;
    private float _nextSpectatorPoll;
    private float _spectatorEmptySince = -1f;
    private bool _spectatorObservedHuman, _spectatorSuspended;
    private int _spectatorWakeErrors;

    private bool UpdateSpectatorMode()
    {
        if (!_cfg.WakeForSpectators) return false;
        if (Time.time < _nextSpectatorPoll) return _spectatorSuspended;
        _nextSpectatorPoll = Time.time + 0.5f;
        _spectatorPlayers ??= new GhostSpectatorPlayers();
        if (!_spectatorPlayers.TryRead(_gameWorld, out var observed, out var active))
        {
            _spectatorEmptySince = -1f;
            return _spectatorSuspended;
        }
        _spectatorObservedHuman |= observed;
        if (active)
        {
            _spectatorEmptySince = -1f;
            if (_spectatorSuspended)
                Log.Info("GHOST SPECTATOR: human player active again, Ghost Mode resumed");
            _spectatorSuspended = false;
            return false;
        }
        // Never wake the map while the host is still waiting for its first player to load.
        if (!_spectatorObservedHuman) return false;
        if (_spectatorEmptySince < 0f) _spectatorEmptySince = Time.time;
        if (Time.time - _spectatorEmptySince < 1f) return _spectatorSuspended;
        if (!_spectatorSuspended)
        {
            _spectatorSuspended = true;
            for (var i = 0; i < _activeFights.Count; i++)
            {
                var fight = _activeFights[i];
                try { ReleaseFightPins(fight); }
                catch (Exception e)
                {
                    // A vanished native body must not leave the other squad waiting on this fight.
                    if (fight.Winner.Squad != null) fight.Winner.Squad.GhostFightUntil = -999f;
                    if (fight.Loser.Squad != null) fight.Loser.Squad.GhostFightUntil = -999f;
                    ReportSpectatorWakeError(e);
                }
            }
            _activeFights.Clear();
            _unitFightingUntil.Clear();
            _pendingShots.Clear();
            _noises.Clear();
            Log.Info($"GHOST SPECTATOR: no active human players, Ghost Mode suspended; waking {_dormantAgents.Count} ORBIT and {_vanillaDormant.Count} native bots");
        }
        for (var i = _dormantAgents.Count - 1; i >= 0; i--)
        {
            try { WakeAgent(_dormantAgents[i]); }
            catch (Exception e) { ReportSpectatorWakeError(e); }
        }
        _nativeMoveScratch.Clear();
        _nativeMoveScratch.AddRange(_vanillaDormant);
        for (var i = 0; i < _nativeMoveScratch.Count; i++)
        {
            try { WakeVanillaBot(_nativeMoveScratch[i]); }
            catch (Exception e) { ReportSpectatorWakeError(e); }
        }
        return true;
    }

    private void ReportSpectatorWakeError(Exception e)
    {
        _spectatorWakeErrors++;
        if (_spectatorWakeErrors <= 5 || _spectatorWakeErrors % 100 == 0)
            Log.Warning($"GHOST SPECTATOR: wake failed (#{_spectatorWakeErrors}): {e.Message}");
    }
}
