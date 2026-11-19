using System;
using System.Collections.Generic;
using BepInEx.Logging;
using CommonAssets.Scripts.Game;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using HarmonyLib;
using Orbit.Api;
using UnityEngine;

namespace Orbit.Fika;

/// <summary>Replicates native exit changes and orders final bot poses before native death packets.</summary>
internal sealed class WorldSyncBridge : IDisposable
{
    internal const ulong Capability = 2;
    private sealed class ExitWatch
    {
        internal ExfiltrationPoint Point;
        internal byte Status;
        internal float Started;
        internal bool Sampled;
    }

    private readonly ManualLogSource _log;
    private readonly Harmony _harmony = new("com.chazut.orbit.fika.world");
    private readonly Dictionary<string, ExitWatch> _exits = new();
    private readonly HashSet<NetPeer> _peers = new();
    private readonly HashSet<NetPeer> _requests = new();
    private readonly WorldSyncState _received = new();
    private readonly Dictionary<string, float> _pending = new();
    private readonly Dictionary<string, float> _receivedAt = new();
    private readonly List<string> _remove = new();
    private IFikaNetworkManager _network;
    private string _session;
    private bool _host, _applying, _requestSent;
    private float _nextTick, _nextWarning;
    private ulong _revision;
    private static WorldSyncBridge _active;

    internal WorldSyncBridge(ManualLogSource log)
    {
        _log = log;
        _active = this;
        try
        {
            Patch(typeof(FikaPlayer), "SetupCorpseSyncPacket", nameof(BeforeSendDeath));
            Patch(typeof(ObservedPlayer), nameof(ObservedPlayer.OnDead), nameof(BeforeObservedDeath));
            // Names changed in Fika 2.4.3. Resolve both without referencing a version-specific packet.
            var countdown = AccessTools.TypeByName("Fika.Core.Networking.Packets.Generic.SubPackets.ExfilCountdownPacket")
                ?? AccessTools.TypeByName("Fika.Core.Networking.Packets.Generic.SubPackets.ExfilCountdown");
            Patch(countdown, "Execute", nameof(BeforeNativeCountdown));
            var response = AccessTools.TypeByName("Fika.Core.Networking.Packets.World.RequestSubPackets+ExfiltrationRequest");
            var handler = AccessTools.Method(response, "HandleResponse");
            if (handler == null) throw new MissingMethodException("Fika exfil snapshot receiver");
            _harmony.Patch(handler, postfix: new HarmonyMethod(typeof(WorldSyncBridge), nameof(AfterExitSnapshot)));
            OrbitExtractionEvents.Used += WatchExit;
            FikaEventDispatcher.OnFikaEvent += OnEvent;
        }
        catch
        {
            FikaPatchCleanup.Rollback(this, log);
            throw;
        }
    }

    private void Patch(Type type, string method, string prefix)
    {
        var target = type == null ? null : AccessTools.DeclaredMethod(type, method);
        if (target == null) throw new MissingMethodException(type?.FullName, method);
        _harmony.Patch(target, prefix: new HarmonyMethod(typeof(WorldSyncBridge), prefix) { priority = Priority.First });
    }

    internal void HostReady(NetPeer peer, string session, bool supported)
    {
        if (!_host || peer == null) return;
        _peers.Remove(peer);
        _requests.Remove(peer);
        if (!supported)
        {
            Warn("peer lacks world sync; update ORBIT on every participant");
            return;
        }
        _session = session;
        _peers.Add(peer);
    }

    internal void ClientReady(string session, bool supported)
    {
        if (_host) return;
        _session = supported ? session : null;
        _received.Clear();
        _pending.Clear();
        _receivedAt.Clear();
        _requestSent = false;
        if (!supported && session != null) Warn("host lacks world sync; update ORBIT on every participant");
    }

    private void OnEvent(FikaEvent e)
    {
        switch (e)
        {
            case FikaNetworkManagerCreatedEvent created:
                Reset();
                _network = created.Manager;
                _host = _network is FikaServer;
                _network.RegisterPacket<OrbitWorldPacket, NetPeer>(Receive);
                break;
            case FikaNetworkManagerDestroyedEvent destroyed when destroyed.Manager == _network:
                Reset();
                break;
            case PeerDisconnectedEvent disconnected when disconnected.NetworkManager == _network:
                _peers.Remove(disconnected.Peer);
                _requests.Remove(disconnected.Peer);
                if (!_host) ClientReady(null, false);
                break;
            case PeerConnectedEvent connected when connected.NetworkManager == _network && !_host:
                ClientReady(null, false);
                break;
        }
    }

    private static float RaidTime => Singleton<IFikaGame>.Instance?.GameController?.GameInstance?.PastTime ?? 0f;

    private OrbitWorldPacket Packet(WorldMessage kind, string id = null)
        => new() { Protocol = OrbitWorldPacket.Version, Kind = kind, Id = id, Session = _session, Revision = ++_revision };

    private void Send(OrbitWorldPacket packet, NetPeer peer = null)
    {
        // The native death uses ReliableOrdered channel 0 too. Never send the final pose as a movement snapshot.
        if (_host)
        {
            if (peer != null) _network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
            else foreach (var target in _peers) _network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, target);
        }
        else _network.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }

    private void WatchExit(ExfiltrationPoint point)
    {
        if (!_host || point?.Settings?.ExfiltrationType != EExfiltrationType.SharedTimer) return;
        var id = point.Settings.Name;
        if (string.IsNullOrEmpty(id)) return;
        if (_exits.TryGetValue(id, out var old))
        {
            if (ReferenceEquals(old.Point, point)) return;
            old.Point.OnStatusChanged -= ExitChanged;
        }
        _exits[id] = new ExitWatch { Point = point };
        point.OnStatusChanged += ExitChanged;
        // Capture the current state as well: a late subscriber can first see an already departed car.
        PublishExit(_exits[id]);
    }

    private void ExitChanged(ExfiltrationPoint point, EExfiltrationStatus previous)
    {
        try
        {
            if (_host && _exits.TryGetValue(point.Settings.Name, out var watch))
            {
                // Fika leaves -100 after cancellation. Its next Countdown transition adds the
                // timer back without initializing that sentinel, so a repaid car can leave at once.
                if (point.Status == EExfiltrationStatus.Countdown && point.ExfiltrationStartTime <= -90f)
                    point.ExfiltrationStartTime = RaidTime;
                PublishExit(watch);
            }
        }
        catch (Exception e) { Warn("exit update failed: " + e.Message); }
    }

    private OrbitWorldPacket ExitPacket(ExfiltrationPoint point)
    {
        var packet = Packet(WorldMessage.Exit, point.Settings.Name);
        packet.Status = (byte)point.Status;
        packet.Elapsed = point.Status == EExfiltrationStatus.Countdown
            ? Mathf.Max(0f, RaidTime - point.ExfiltrationStartTime) : 0f;
        return packet;
    }

    private void PublishExit(ExitWatch watch)
    {
        var point = watch.Point;
        if (point == null) return;
        var status = (byte)point.Status;
        var start = point.Status == EExfiltrationStatus.Countdown ? point.ExfiltrationStartTime : 0f;
        if (watch.Sampled && watch.Status == status && watch.Started == start) return;
        if (_session == null) return;
        var packet = ExitPacket(point);
        Send(packet);
        watch.Status = status;
        watch.Started = start;
        watch.Sampled = true;
        _log.LogDebug($"WORLD SYNC: exit sent id={packet.Id} status={point.Status} revision={packet.Revision} peers={_peers.Count}");
    }

    private void Receive(OrbitWorldPacket packet, NetPeer peer)
    {
        try
        {
            if (_network == null || _session == null || packet.Session != _session || !WorldSyncState.Valid(packet)) return;
            if (_host)
            {
                if (packet.Kind == WorldMessage.Request && peer != null && _peers.Contains(peer)) _requests.Add(peer);
                return; // A client never supplies authoritative state.
            }
            if (!_received.Receive(packet)) return;
            if (packet.Kind == WorldMessage.Exit)
            {
                _pending[packet.Id] = Time.time;
                _receivedAt[packet.Id] = Time.time;
                if (ApplyExit(packet.Id)) _pending.Remove(packet.Id);
            }
            else _log.LogDebug($"WORLD SYNC: death pose received id={packet.Id} net={packet.NetId} revision={packet.Revision}");
        }
        catch (Exception e) { Warn("packet rejected: " + e.Message); }
    }

    private bool ApplyExit(string id)
    {
        if (!_received.Exits.TryGetValue(id, out var packet)) return true;
        var point = FindExit(id);
        if (point == null) return false;
        var status = (EExfiltrationStatus)packet.Status;
        _applying = true;
        try
        {
            var start = RaidTime - packet.Elapsed - (Time.time - _receivedAt[id]);
            // A reconnect can have a different clock origin. Suppress Fika's native start broadcast
            // while it registers this countdown, then restore the actual local start (possibly negative).
            if (status == EExfiltrationStatus.Countdown)
                point.ExfiltrationStartTime = start > 0 ? start : -100f;
            point.Status = status;
            if (status == EExfiltrationStatus.Countdown) point.ExfiltrationStartTime = start;
            _log.LogDebug($"WORLD SYNC: exit applied id={id} status={status} revision={packet.Revision}");
            return true;
        }
        finally { _applying = false; }
    }

    private static ExfiltrationPoint FindExit(string id)
    {
        var points = ExfiltrationController.Instance?.ExfiltrationPoints;
        if (points != null)
            foreach (var point in points)
                if (point?.Settings?.Name == id && point.Settings.ExfiltrationType == EExfiltrationType.SharedTimer) return point;
        return null;
    }

    private static void BeforeSendDeath(FikaPlayer __instance)
    {
        var bridge = _active;
        if (bridge?._host != true || bridge._session == null || bridge._peers.Count == 0 || __instance is not FikaBot) return;
        try
        {
            var packet = bridge.Packet(WorldMessage.DeathPose, __instance.ProfileId);
            packet.NetId = __instance.NetId;
            packet.Position = __instance.Position;
            packet.Rotation = __instance.Transform.rotation;
            if (!WorldSyncState.Valid(packet)) { bridge.Warn("invalid death pose id=" + packet.Id); return; }
            bridge.Send(packet);
            bridge._log.LogDebug($"WORLD SYNC: death pose sent id={packet.Id} net={packet.NetId} revision={packet.Revision} peers={bridge._peers.Count}");
        }
        catch (Exception e) { bridge.Warn("death pose send failed: " + e.Message); }
    }

    private static void BeforeObservedDeath(ObservedPlayer __instance)
    {
        var bridge = _active;
        if (bridge == null || bridge._host || bridge._session == null
            || !bridge._received.TakePose(__instance.ProfileId, __instance.NetId, out var packet)) return;
        try
        {
            var distance = Vector3.Distance(__instance.Position, packet.Position);
            if (!__instance.gameObject.activeSelf) __instance.gameObject.SetActive(true);
            // Deliberately apply at OnDead, not on receipt: interpolation may still run before death is processed.
            __instance.Snapshotter.Clear();
            __instance.Teleport(packet.Position);
            __instance.Transform.rotation = packet.Rotation;
            bridge._log.LogDebug($"WORLD SYNC: death pose applied id={packet.Id} net={packet.NetId} correction={distance:F2}m");
        }
        catch (Exception e) { bridge.Warn("death pose apply failed: " + e.Message); }
    }

    // Fika 2.4.2 uses a class and 2.4.6 a readonly struct. Both expose the same string field.
    private static bool BeforeNativeCountdown(string ___ExfilName)
    {
        var bridge = _active;
        if (bridge == null || bridge._host || bridge._applying || bridge._session == null) return true;
        // A delayed native start cannot resurrect a car the host has already consumed.
        return ___ExfilName == null || !bridge._received.Exits.TryGetValue(___ExfilName, out var state)
            || state.Status != (byte)EExfiltrationStatus.NotPresent;
    }

    private static void AfterExitSnapshot(byte[] ___Data)
    {
        var bridge = _active;
        if (bridge == null || bridge._host) return;
        try
        {
            // Fika 2.4.2-2.4.6 writes this field as Single but reads it as Int32. Repair only
            // countdown timestamps after its own snapshot handler; retain native availability/eligibility.
            if (___Data == null) return;
            var reader = new NetDataReader(___Data);
            var count = reader.GetInt();
            if (count < 0 || count > 256) return;
            for (var i = 0; i < count; i++)
            {
                var name = reader.GetString(256);
                var status = reader.GetEnum<EExfiltrationStatus>();
                reader.GetInt(); // Settings.StartTime
                if (status != EExfiltrationStatus.Countdown) continue;
                var start = reader.GetFloat();
                var point = FindExit(name);
                if (point?.Status == EExfiltrationStatus.Countdown && WorldSyncState.Finite(start) && start >= 0)
                    point.ExfiltrationStartTime = start;
            }
            foreach (var id in bridge._received.Exits.Keys)
                bridge._pending.TryAdd(id, Time.time);
            bridge._requestSent = false;
            bridge._log.LogDebug("WORLD SYNC: native exit snapshot reconciled");
        }
        catch (Exception e) { bridge.Warn("exit snapshot rejected: " + e.Message); }
    }

    internal void Tick()
    {
        if (_network == null || Time.time < _nextTick) return;
        _nextTick = Time.time + .25f;
        try
        {
            if (_host)
            {
                if (_session == null || ExfiltrationController.Instance?.ExfiltrationPoints == null) return;
                foreach (var watch in _exits.Values) PublishExit(watch);
                foreach (var peer in _requests)
                    foreach (var watch in _exits.Values)
                        if (watch.Point != null) Send(ExitPacket(watch.Point), peer);
                _requests.Clear();
            }
            else if (_session != null)
            {
                if (!_requestSent && ExfiltrationController.Instance?.ExfiltrationPoints != null)
                {
                    Send(Packet(WorldMessage.Request));
                    _requestSent = true;
                    _log.LogInfo("WORLD SYNC: host handshake complete");
                }
                _remove.Clear();
                foreach (var entry in _pending)
                    if (ApplyExit(entry.Key)) _remove.Add(entry.Key);
                    else if (Time.time - entry.Value > 30f)
                    {
                        Warn("exit unavailable id=" + entry.Key);
                        _remove.Add(entry.Key);
                    }
                foreach (var id in _remove) _pending.Remove(id);
            }
        }
        catch (Exception e) { Warn("update failed: " + e.Message); }
    }

    private void Warn(string text)
    {
        if (Time.time < _nextWarning) return;
        _nextWarning = Time.time + 5f;
        _log.LogWarning("WORLD SYNC: " + text);
    }

    private void Reset()
    {
        try { _network?.UnregisterPacket<OrbitWorldPacket>(); }
        catch (Exception e) { Warn("receiver cleanup failed: " + e.Message); }
        foreach (var watch in _exits.Values)
            if (watch.Point != null) watch.Point.OnStatusChanged -= ExitChanged;
        _exits.Clear();
        _peers.Clear();
        _requests.Clear();
        _received.Clear();
        _pending.Clear();
        _receivedAt.Clear();
        _network = null;
        _session = null;
        _revision = 0;
        _host = _applying = _requestSent = false;
        _nextTick = _nextWarning = 0;
    }

    public void Dispose()
    {
        if (_active == this) _active = null;
        OrbitExtractionEvents.Used -= WatchExit;
        FikaEventDispatcher.OnFikaEvent -= OnEvent;
        try { FikaPatchCleanup.Unpatch(_harmony, _log); }
        finally { Reset(); }
    }
}
