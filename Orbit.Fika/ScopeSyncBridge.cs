using System;
using System.Collections.Generic;
using BepInEx.Logging;
using EFT;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Orbit.Api;
using UnityEngine;

namespace Orbit.Fika;

/// <summary>Client PiP views, negotiated through the existing addon handshake.</summary>
internal sealed class ScopeSyncBridge : IDisposable
{
    internal const ulong Capability = 1;
    private sealed class PeerState
    {
        internal string ProfileId;
        internal ulong Sequence;
    }

    private readonly ManualLogSource _log;
    private readonly Dictionary<NetPeer, PeerState> _peers = new();
    private IFikaNetworkManager _network;
    private GameWorld _world;
    private string _session, _lastProfile;
    private ulong _sequence;
    private bool _host, _lastActive;
    private float _nextSend, _nextError;

    internal ScopeSyncBridge(ManualLogSource log)
    {
        _log = log;
        FikaEventDispatcher.OnFikaEvent += OnEvent;
    }

    internal void HostReady(NetPeer peer, string session, bool supported)
    {
        if (!_host || peer == null) return;
        if (_peers.TryGetValue(peer, out var previous)) OrbitScopeViews.RemoveRemote(previous.ProfileId);
        _peers.Remove(peer);
        if (!supported) return;
        _session = session;
        _peers[peer] = new PeerState();
    }

    internal void ClientReady(string session, bool supported)
    {
        if (_host) return;
        _session = supported ? session : null;
        _lastActive = false;
        _lastProfile = null;
        _nextSend = 0;
    }

    private void OnEvent(FikaEvent e)
    {
        switch (e)
        {
            case FikaNetworkManagerCreatedEvent created:
                Reset();
                _network = created.Manager;
                _host = _network is FikaServer;
                if (_host) _network.RegisterPacket<OrbitScopePacket, NetPeer>(Receive);
                break;
            case FikaNetworkManagerDestroyedEvent destroyed when destroyed.Manager == _network:
                Reset();
                break;
            case GameWorldStartedEvent started:
                _world = started.GameWorld;
                OrbitScopeViews.ClearRemote();
                _nextSend = 0;
                break;
            case PeerDisconnectedEvent disconnected when disconnected.NetworkManager == _network:
                if (_peers.TryGetValue(disconnected.Peer, out var peer))
                    OrbitScopeViews.RemoveRemote(peer.ProfileId);
                _peers.Remove(disconnected.Peer);
                if (!_host) ClientReady(null, false);
                break;
            case PeerConnectedEvent connected when connected.NetworkManager == _network && !_host:
                ClientReady(null, false);
                break;
        }
    }

    internal void Tick()
    {
        if (_network == null || _host || _session == null || Time.time < _nextSend) return;
        _nextSend = Time.time + 0.25f;
        try
        {
            var player = _world?.MainPlayer;
            var active = OrbitScopeViews.TryReadLocal(player, out var view);
            if (!active && !_lastActive) return;
            var profile = active ? player.ProfileId : _lastProfile;
            if (string.IsNullOrEmpty(profile)) return;
            var packet = new OrbitScopePacket
            {
                Protocol = OrbitScopePacket.CurrentProtocol, Session = _session,
                ProfileId = profile, Sequence = ++_sequence, Active = active,
                SightId = view.SightId, Zoom = view.Zoom, FieldOfView = view.FieldOfView,
            };
            _network.SendData(ref packet, active ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
            _lastActive = active;
            _lastProfile = profile;
        }
        catch (Exception e) { Report(e); }
    }

    private void Receive(OrbitScopePacket packet, NetPeer peer)
    {
        try
        {
            if (!_host || _world == null || peer == null || !_peers.TryGetValue(peer, out var state)
                || packet.Protocol != OrbitScopePacket.CurrentProtocol || packet.Session != _session
                || packet.Sequence <= state.Sequence || string.IsNullOrEmpty(packet.ProfileId)) return;
            if (state.ProfileId != null && state.ProfileId != packet.ProfileId) return;
            // Fika associates the connection with MainProfileNickname during profile loading.
            // Bind the report to that human, never to an AI, the host, or another connection's player.
            Player owner = null;
            foreach (var player in _world.AllAlivePlayersList)
                if (player != null && !player.IsYourPlayer && !player.AIData.IsAI
                    && player.ProfileId == packet.ProfileId
                    && peer.Tag is string nickname && player.Profile.Info.MainProfileNickname == nickname)
                { owner = player; break; }
            if (owner == null) return;
            var view = new OrbitScopeView { SightId = packet.SightId, Zoom = packet.Zoom, FieldOfView = packet.FieldOfView };
            if (packet.Active && !view.Valid) return;
            state.ProfileId = packet.ProfileId;
            state.Sequence = packet.Sequence;
            if (packet.Active) OrbitScopeViews.SetRemote(packet.ProfileId, view);
            else OrbitScopeViews.RemoveRemote(packet.ProfileId);
        }
        catch (Exception e) { Report(e); }
    }

    private void Report(Exception e)
    {
        if (Time.time < _nextError) return;
        _nextError = Time.time + 10f;
        _log.LogWarning($"SCOPE SYNC: update skipped: {e.GetType().Name}");
    }

    private void Reset()
    {
        if (_host) _network?.UnregisterPacket<OrbitScopePacket>();
        _network = null;
        _world = null;
        _session = _lastProfile = null;
        _sequence = 0;
        _lastActive = _host = false;
        _nextSend = _nextError = 0;
        _peers.Clear();
        OrbitScopeViews.ClearRemote();
    }

    public void Dispose()
    {
        FikaEventDispatcher.OnFikaEvent -= OnEvent;
        Reset();
    }
}
