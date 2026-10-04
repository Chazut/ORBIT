using System;
using System.Collections.Generic;
using BepInEx.Logging;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Players;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using HarmonyLib;
using Orbit.Api;
using UnityEngine;

namespace Orbit.Fika;

/// <summary>
/// Replicates completed ORBIT door changes independently of bot bodies. Normal Fika interactions
/// retain their animation and ordered packets; snapshots only reconcile their terminal result.
/// </summary>
internal sealed class DoorSyncBridge : IDisposable
{
    private sealed class Watch
    {
        internal Door Door;
        internal float Expires;
        internal EDoorState PreviousState;
        internal float PreviousAngle;
        internal bool Sampled;
    }

    private struct Pending
    {
        internal OrbitDoorPacket Packet;
        internal float Expires;
    }

    private readonly ManualLogSource _log;
    private readonly Dictionary<string, Watch> _watches = new();
    private readonly Dictionary<string, Door> _known = new();
    private readonly HashSet<NetPeer> _peers = new();
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly List<string> _remove = new();
    private readonly DoorSyncOrder _order = new();
    private readonly Harmony _harmony = new("com.chazut.orbit.fika.doors");
    private IFikaNetworkManager _network;
    private GameWorld _world;
    private bool _host, _connected, _helloSent, _warned;
    private float _nextTick, _helloAt, _nextErrorLog;
    private string _session, _hello;
    private ulong _revision;
    private static DoorSyncBridge _active;

    internal DoorSyncBridge(ManualLogSource log)
    {
        _log = log;
        if (!DoorStateReceiver.Supported)
            throw new NotSupportedException("Door interaction fields do not match this SPT version");
        _active = this;
        try
        {
            var postfix = new HarmonyMethod(typeof(DoorSyncBridge), nameof(AfterInteraction));
            _harmony.Patch(AccessTools.DeclaredMethod(typeof(FikaPlayer), nameof(FikaPlayer.ExecuteInteraction)), postfix: postfix);
            _harmony.Patch(AccessTools.DeclaredMethod(typeof(ObservedPlayer), nameof(ObservedPlayer.ExecuteInteraction)), postfix: postfix);
            var startPostfix = new HarmonyMethod(typeof(DoorSyncBridge), nameof(AfterStartInteraction));
            _harmony.Patch(AccessTools.DeclaredMethod(typeof(FikaPlayer), nameof(FikaPlayer.StartInteraction)), postfix: startPostfix);
            _harmony.Patch(AccessTools.DeclaredMethod(typeof(ObservedPlayer), nameof(ObservedPlayer.StartInteraction)), postfix: startPostfix);
            // Use the dispatcher event directly: the current Fika Subscribe/Unsubscribe helpers
            // allocate different wrapper delegates, so they cannot remove the original handler.
            FikaEventDispatcher.OnFikaEvent += OnFikaEvent;
            OrbitDoorEvents.Changed += OnDoorChanged;
            OrbitInteractionEvents.Changed += OnSwitchChanged;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void OnFikaEvent(FikaEvent e)
    {
        switch (e)
        {
            case FikaNetworkManagerCreatedEvent created:
                Reset();
                _network = created.Manager;
                _host = _network is FikaServer;
                _connected = true;
                _hello = Guid.NewGuid().ToString("N");
                _session = _host ? Guid.NewGuid().ToString("N") : null;
                _network.RegisterPacket<OrbitDoorPacket, NetPeer>(Receive);
                break;
            case FikaNetworkManagerDestroyedEvent destroyed when destroyed.Manager == _network:
                Reset();
                break;
            case GameWorldStartedEvent started:
                _world = started.GameWorld;
                break;
            case PeerDisconnectedEvent disconnected when disconnected.NetworkManager == _network:
                _peers.Remove(disconnected.Peer);
                if (!_host)
                {
                    _connected = false;
                    ResetClientHandshake();
                }
                break;
            case PeerConnectedEvent connected when connected.NetworkManager == _network && !_host:
                _connected = true;
                ResetClientHandshake();
                break;
        }
    }

    private void ResetClientHandshake()
    {
        _session = null;
        _hello = Guid.NewGuid().ToString("N");
        _helloSent = _warned = false;
        _pending.Clear();
        _order.Clear();
    }

    private void Reset()
    {
        _network?.UnregisterPacket<OrbitDoorPacket>();
        _network = null;
        _connected = false;
        _world = null;
        _session = _hello = null;
        _revision = 0;
        _helloSent = _warned = false;
        _nextTick = _nextErrorLog = 0;
        _peers.Clear();
        _watches.Clear();
        _known.Clear();
        _pending.Clear();
        _order.Clear();
    }

    private OrbitDoorPacket Packet(DoorMessage kind, string door = null)
        => new() { Protocol = OrbitDoorPacket.CurrentProtocol, Kind = kind, Session = _session,
            DoorId = door, Revision = ++_revision };

    private void Send(OrbitDoorPacket packet, NetPeer peer = null)
    {
        try
        {
            // Fika's native CommonPacket also uses ReliableOrdered on channel 0. A barrier/fence
            // follows its interaction on that same stream, including interactions sent by humans.
            if (_host)
            {
                if (peer != null) _network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
                else foreach (var target in _peers)
                    _network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, target);
            }
            else _network.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }
        catch (Exception e) { _log.LogDebug($"DOOR SYNC: send interrupted: {e.Message}"); }
    }

    private void Receive(OrbitDoorPacket packet, NetPeer peer)
    {
        try { ReceiveChecked(packet, peer); }
        catch (Exception e) { _log.LogWarning($"DOOR SYNC: packet rejected: {e.Message}"); }
    }

    private void ReceiveChecked(OrbitDoorPacket packet, NetPeer peer)
    {
        if (_network == null || packet.Protocol != OrbitDoorPacket.CurrentProtocol) return;
        if (_host)
        {
            if (packet.Kind == DoorMessage.Hello && peer != null && packet.Session?.Length == 32)
            {
                _peers.Add(peer);
                var welcome = Packet(DoorMessage.Welcome, packet.Session);
                Send(welcome, peer);
                // Resolve current objects, never replay cached Open snapshots over a later human close.
                foreach (var entry in _known)
                    if (entry.Value != null && Settled(entry.Value)) Send(Snapshot(entry.Value), peer);
                _log.LogInfo("DOOR SYNC: compatible client ready");
            }
            else if (packet.Kind == DoorMessage.Fence && _peers.Contains(peer)
                && packet.Session == _session && !string.IsNullOrEmpty(packet.DoorId))
            {
                var ack = Packet(DoorMessage.Acknowledge, packet.DoorId);
                ack.Token = packet.Token;
                Send(ack, peer);
            }
            return; // Clients never provide authoritative door states.
        }
        if (packet.Kind == DoorMessage.Welcome)
        {
            if (_session == null && packet.DoorId == _hello && packet.Session?.Length == 32)
            {
                _session = packet.Session;
                _log.LogInfo("DOOR SYNC: host handshake complete");
            }
            return;
        }
        if (_session == null || packet.Session != _session || string.IsNullOrEmpty(packet.DoorId)) return;
        if (packet.Kind == DoorMessage.SwitchInteraction)
        {
            if (packet.State != (byte)EInteractionType.Open || !_order.Receive(packet.DoorId, packet.Revision)) return;
            if (_world?.FindDoor(packet.DoorId) is Switch target && target.DoorState == EDoorState.Shut)
            {
                target.LockForInteraction();
                target.Interact(new InteractionResult(EInteractionType.Open));
            }
            return;
        }
        if (packet.Kind == DoorMessage.Acknowledge)
        {
            _order.Acknowledge(packet.DoorId, packet.Revision, packet.Token);
            return;
        }
        if (packet.Kind is not (DoorMessage.State or DoorMessage.Barrier)) return;
        if (packet.Kind == DoorMessage.State && !DoorStateReceiver.Valid(packet)) return;
        if (!_order.Receive(packet.DoorId, packet.Revision)) return;
        _pending.Remove(packet.DoorId);
        if (packet.Kind == DoorMessage.State)
        {
            if (!Apply(packet) && _pending.Count < 256)
                _pending[packet.DoorId] = new Pending { Packet = packet, Expires = Time.time + 3f };
        }
    }

    private bool Apply(OrbitDoorPacket packet)
    {
        if (!_order.IsCurrent(packet.DoorId, packet.Revision)) return true;
        var door = _world?.FindDoor(packet.DoorId) as Door;
        if (door == null || !door.isActiveAndEnabled) return false;
        if (door.ForceLocalInteraction) return true;
        if (!DoorStateReceiver.Apply(door, packet)) return false;
        _log.LogDebug($"DOOR SYNC: applied id={door.Id} state={door.DoorState} angle={door.CurrentAngle:F1} revision={packet.Revision}");
        return true;
    }

    private void OnDoorChanged(Door door, OrbitDoorEvents.Operation operation)
    {
        if (_network == null || !_host || door == null || door.ForceLocalInteraction || string.IsNullOrEmpty(door.Id)) return;
        // A completion belonging to an old operation must not replace a newer native interaction.
        if (operation == OrbitDoorEvents.Operation.Finalize && !_watches.ContainsKey(door.Id)) return;
        _known[door.Id] = door;
        _watches[door.Id] = new Watch { Door = door, Expires = Time.time + 16f };
    }

    private void OnSwitchChanged(WorldInteractiveObject target, EInteractionType interaction)
    {
        if (_network == null || !_host || target is not Switch || target.ForceLocalInteraction
            || interaction != EInteractionType.Open || string.IsNullOrEmpty(target.Id)) return;
        var packet = Packet(DoorMessage.SwitchInteraction, target.Id);
        packet.State = (byte)interaction;
        Send(packet);
    }

    private static bool Settled(Door door)
        => DoorStateReceiver.Terminal(door.DoorState)
            && Mathf.Abs(Mathf.DeltaAngle(door.CurrentAngle, door.GetAngle(door.DoorState))) <= 1f;

    private OrbitDoorPacket Snapshot(Door door)
    {
        var packet = Packet(DoorMessage.State, door.Id);
        packet.State = (byte)door.DoorState;
        packet.Angle = door.CurrentAngle;
        packet.Broken = door.IsBroken;
        return packet;
    }

    private static void AfterInteraction(FikaPlayer __instance, WorldInteractiveObject door)
    {
        try { _active?.OnNativeInteraction(__instance, door as Door); }
        catch (Exception e) { _active?._log.LogWarning($"DOOR SYNC: interaction notification failed: {e.Message}"); }
    }

    private static void AfterStartInteraction(FikaPlayer __instance, WorldInteractiveObject interactiveObject)
        => AfterInteraction(__instance, interactiveObject);

    private void OnNativeInteraction(FikaPlayer player, Door door)
    {
        if (_network == null || door == null || door.ForceLocalInteraction || string.IsNullOrEmpty(door.Id)) return;
        if (_host)
        {
            OrbitDoorEvents.NotifyExternalInteraction(door);
            if (!_known.ContainsKey(door.Id)) return;
            _watches.Remove(door.Id);
            Send(Packet(DoorMessage.Barrier, door.Id));
        }
        else
        {
            _pending.Remove(door.Id);
            // Remote native interactions already share the host stream. A local human action also
            // needs an acknowledgement so a snapshot sent BEFORE that action cannot undo it in flight.
            if (player is ObservedPlayer || _session == null) return;
            var fence = Packet(DoorMessage.Fence, door.Id);
            fence.Token = _order.Fence(door.Id);
            Send(fence);
        }
    }

    internal void Tick()
    {
        try { TickChecked(); }
        catch (Exception e)
        {
            if (Time.time < _nextErrorLog) return;
            _nextErrorLog = Time.time + 5f;
            _log.LogWarning($"DOOR SYNC: tick failed: {e.Message}");
        }
    }

    private void TickChecked()
    {
        if (_network == null || !_connected || Time.time < _nextTick) return;
        _nextTick = Time.time + 0.1f;
        if (!_host && !_helloSent && _world != null)
        {
            _helloSent = true;
            _helloAt = Time.time;
            var hello = Packet(DoorMessage.Hello);
            hello.Session = _hello;
            Send(hello);
        }
        if (!_host && _helloSent && _session == null && !_warned && Time.time > _helloAt + 10f)
        {
            _warned = true;
            _log.LogWarning("DOOR SYNC: no compatible host response; install the same ORBIT Fika addon on every machine");
        }
        if (_host) TickHost();
        else TickClient();
    }

    private void TickHost()
    {
        _remove.Clear();
        foreach (var entry in _watches)
        {
            var watch = entry.Value;
            var door = watch.Door;
            if (door == null) { _remove.Add(entry.Key); continue; }
            if (Settled(door) && watch.Sampled && watch.PreviousState == door.DoorState
                && Mathf.Abs(Mathf.DeltaAngle(watch.PreviousAngle, door.CurrentAngle)) < 0.1f)
            {
                var packet = Snapshot(door);
                Send(packet);
                _log.LogDebug($"DOOR SYNC: sent id={door.Id} state={door.DoorState} angle={door.CurrentAngle:F1} revision={packet.Revision}");
                _remove.Add(entry.Key);
            }
            else if (Time.time >= watch.Expires)
            {
                _log.LogWarning($"DOOR SYNC: host door did not settle id={door.Id} state={door.DoorState} angle={door.CurrentAngle:F1}");
                _remove.Add(entry.Key);
            }
            else
            {
                watch.Sampled = true;
                watch.PreviousState = door.DoorState;
                watch.PreviousAngle = door.CurrentAngle;
            }
        }
        foreach (var id in _remove) _watches.Remove(id);
    }

    private void TickClient()
    {
        _remove.Clear();
        foreach (var entry in _pending)
        {
            if (Apply(entry.Value.Packet)) _remove.Add(entry.Key);
            else if (Time.time >= entry.Value.Expires)
            {
                _log.LogWarning($"DOOR SYNC: client door unavailable id={entry.Key}");
                _remove.Add(entry.Key);
            }
        }
        foreach (var id in _remove) _pending.Remove(id);
    }

    public void Dispose()
    {
        OrbitDoorEvents.Changed -= OnDoorChanged;
        OrbitInteractionEvents.Changed -= OnSwitchChanged;
        FikaEventDispatcher.OnFikaEvent -= OnFikaEvent;
        _harmony.UnpatchSelf();
        Reset();
        if (_active == this) _active = null;
    }
}
