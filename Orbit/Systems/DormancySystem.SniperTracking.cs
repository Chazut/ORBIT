using System;
using System.Collections.Generic;
using EFT;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

public partial class DormancySystem
{
    private readonly List<SniperContactRange> _sniperTracks = new();
    private int _sniperTrackCursor;

    private void ClearSniperTracking()
    {
        foreach (var contact in _sniperTracks) contact.Remove();
        _sniperTracks.Clear(); _sniperTrackCursor = 0;
    }

    private void TrackSniperContact(SniperContactRange contact)
    {
        for (var i = _sniperTracks.Count - 1; i >= 0; i--)
            if (_sniperTracks[i].Observer == contact.Observer)
            { _sniperTracks[i].Remove(); _sniperTracks.RemoveAt(i); }
        _sniperTracks.Add(contact);
        Log.Info($"SNIPER TRACK: {contact.Observer} state=started target={contact.Target.ProfileId} reach={contact.Reach:F1}m");
    }

    private void PumpSniperTracking()
    {
        if (_sniperTracks.Count == 0) return;
        if (_sniperTrackCursor >= _sniperTracks.Count) _sniperTrackCursor = 0;
        var index = _sniperTrackCursor++;
        var c = _sniperTracks[index];
        if (Time.time < c.NextPoll) return;
        c.NextPoll = Time.time + .25f;
        try
        {
            var bot = c.Observer.Bot;
            string reason = null;
            if (!ServerConfig.Rush.Enabled || c.Plan.ContactObserver != c.Observer
                || !c.Observer.Squad.Members.Contains(c.Observer) || c.Observer.IsDormant
                || bot.IsDead || bot.BotState != EBotState.Active || !bot.gameObject.activeInHierarchy)
                reason = "post-inactive";
            else if (!AwakeSniperTarget(c.Target, c.Observer) || !_gameWorld.AllAlivePlayersList.Contains(c.Target))
                reason = "target-inactive";
            else if (!ReferenceEquals(bot.Memory.GoalEnemy, c.Enemy)) reason = "target-changed";
            else if (bot.BotsGroup == null || !bot.BotsGroup.IsEnemy(c.Enemy.Person) || !SniperHostile(bot, c.Target.AIData.BotOwner))
                reason = "no-longer-hostile";
            else if (!SniperRange(c.Observer, c.Target, out _, out c.Reach)) reason = "out-of-range";
            if (reason != null) { EndSniperTrack(index, reason); return; }

            var sain = _sniperContactBridge.Read(_gameWorld, bot, c.Target.ProfileId);
            // A populated GoalEnemy is not proof of current visibility. Require fresh native checks
            // and, when managed by SAIN, its current visual/LOS result before renewing sight time.
            var fresh = c.NativeCheckAt >= 0 && Time.time - c.NativeCheckAt <= .75f;
            var seen = fresh && c.Enemy.IsVisible && (!sain.Managed || sain.Current && sain.Visible && sain.LineOfSight);
            if (seen) c.LastSeen = Time.time;
            if (Time.time - c.LastSeen > 10f) { EndSniperTrack(index, "visual-contact-timeout"); return; }
            c.Until = Time.time + 2f;
            var shooting = bot.ShootData?.Shooting == true;
            var shot = c.Enemy.PersonalLastShootTime >= c.Started;
            var state = (fresh ? 1 : 0) | (c.Enemy.IsVisible ? 2 : 0) | (c.Enemy.CanShoot ? 4 : 0)
                | (sain.Managed ? 8 : 0) | (sain.Current ? 16 : 0) | (sain.Visible ? 32 : 0)
                | (sain.CanShoot ? 64 : 0) | (sain.LineOfSight ? 128 : 0) | (shooting ? 256 : 0)
                | (sain.Known ? 512 : 0) | (shot ? 1024 : 0);
            if (Log.DebugEnabled && (Time.time >= c.NextReport || state != c.LastState && Time.time - c.LastReport >= 2f))
            {
                c.NextReport = Time.time + 15f; c.LastReport = Time.time; c.LastState = state;
                Log.Debug($"SNIPER TRACK: {c.Observer} state=observed target={c.Target.ProfileId} distance={Vector3.Distance(c.Observer.Position, c.Target.Position):F1}m reach={c.Reach:F1}m nativeRange={c.InputRange:F1}m effectiveRange={c.EffectiveRange:F1}m checkFresh={fresh} visible={c.Enemy.IsVisible} canShoot={c.Enemy.CanShoot} sainManaged={sain.Managed} sainCurrent={sain.Current} sainKnown={sain.Known} sainVisible={sain.Visible} sainLos={sain.LineOfSight} sainCanShoot={sain.CanShoot} shooting={shooting} hasShot={shot} shotAge={(shot ? Time.time - c.Enemy.PersonalLastShootTime : -1):F1}s sightAge={Time.time - c.LastSeen:F1}s");
            }
        }
        catch (Exception error)
        {
            Log.Warning($"SNIPER TRACK: {c.Observer} state=failed target={c.Target.ProfileId} detail={error.Message}");
            EndSniperTrack(index, "integration-error");
        }
    }

    private void EndSniperTrack(int index, string reason)
    {
        var c = _sniperTracks[index]; c.Remove(); _sniperTracks.RemoveAt(index);
        Log.Info($"SNIPER TRACK: {c.Observer} state=ended target={c.Target.ProfileId} reason={reason}");
    }
}
