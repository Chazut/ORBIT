using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Orbit.Api;
using UnityEngine;

namespace Orbit.Fika;

/// <summary>
/// Optional ORBIT companion for Fika co-op: replicates the AI limiter's simulated ghost-fight
/// gunfire to every player. The machine that owns the bots (host or headless) resolves the fights
/// and broadcasts one packet per fight; each client replays the burst through its own BetterAudio
/// with its own listener distance, so the whole party hears the off-screen action correctly
/// positioned and attenuated. Without this DLL the limiter works identically, the sounds are just
/// host-only. Ships separately from the main RC as the Fika addon.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("com.fika.core")]
[BepInDependency(Plugin.PluginGuid)]
public class OrbitFikaPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.chazut.orbit.fika";
    public const string PluginName = "ORBIT Fika Bridge";
    public const string PluginVersion = "1.0.0";

    // Mirrors the limiter's own earshot gate, judged here against the LOCAL listener.
    private const float EarshotMeters = 1500f;

    private struct PendingShot
    {
        public float At;
        public Vector3 Pos;
        public WeaponSoundPlayer Sound;
    }

    private static ManualLogSource _log;
    private static IFikaNetworkManager _network;
    private static bool _isHost;
    private static readonly List<PendingShot> _pending = new();

    private void Awake()
    {
        _log = Logger;
        FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnNetworkManagerCreated);
        FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerDestroyedEvent>(OnNetworkManagerDestroyed);
        OrbitEvents.GhostFightSoundsResolved += OnGhostFightResolved;
        _log.LogInfo($"{PluginName} {PluginVersion} loaded");
    }

    private static void OnNetworkManagerCreated(FikaNetworkManagerCreatedEvent e)
    {
        _network = e.Manager;
        _isHost = e.Manager is FikaServer;
        _pending.Clear();
        // Only clients replay received fights; the host already plays its own burst via the limiter.
        if (!_isHost)
            e.Manager.RegisterPacket<OrbitGhostFightPacket>(OnGhostFightPacket);
        _log.LogInfo($"{PluginName}: network manager ready (host={_isHost})");
    }

    private static void OnNetworkManagerDestroyed(FikaNetworkManagerDestroyedEvent e)
    {
        _network = null;
        _pending.Clear();
    }

    // ORBIT resolves ghost fights on the machine that owns the bots, so this only ever fires
    // where _isHost is true. Raised before the limiter's own earshot gate on purpose.
    private static void OnGhostFightResolved(OrbitEvents.GhostFightSounds fight)
    {
        if (_network == null || !_isHost) return;
        var packet = new OrbitGhostFightPacket
        {
            PosA = fight.PosA,
            PosB = fight.PosB,
            ProfileA = fight.ProfileA,
            ProfileB = fight.ProfileB,
            Shots = fight.Shots,
            Duration = fight.Duration,
        };
        try
        {
            _network.SendData(ref packet, DeliveryMethod.ReliableUnordered, true);
        }
        catch
        {
            // Raid-teardown race: losing one burst is fine.
        }
    }

    private static void OnGhostFightPacket(OrbitGhostFightPacket packet)
    {
        var gameWorld = Singleton<GameWorld>.Instance;
        var listener = gameWorld?.MainPlayer;
        if (listener == null) return;

        var listenerPos = listener.Position;
        var distA = Vector3.Distance(listenerPos, packet.PosA);
        var distB = Vector3.Distance(listenerPos, packet.PosB);
        if (Mathf.Min(distA, distB) > EarshotMeters) return;

        var soundA = WeaponSoundFromProfile(gameWorld, packet.ProfileA);
        var soundB = WeaponSoundFromProfile(gameWorld, packet.ProfileB);
        if (soundA == null && soundB == null)
        {
            _log.LogInfo($"{PluginName}: ghost fight received but no weapon sound player resolved, burst dropped");
            return;
        }

        _log.LogInfo($"{PluginName}: replaying ghost fight, {packet.Shots} shots over {packet.Duration:F1}s at {Mathf.Min(distA, distB):F0}m");

        // Same burst shape as the limiter's local playback: shots alternate sides with positional
        // jitter, spread over the fight's duration.
        for (var i = 0; i < packet.Shots; i++)
        {
            var sideA = Random.value < 0.5f;
            var sound = sideA ? soundA : soundB;
            if (sound == null) sound = sideA ? soundB : soundA;
            _pending.Add(new PendingShot
            {
                At = Time.time + Random.Range(0.1f, packet.Duration),
                Pos = (sideA ? packet.PosA : packet.PosB) + new Vector3(Random.Range(-3f, 3f), 0f, Random.Range(-3f, 3f)),
                Sound = sound,
            });
        }
    }

    private static WeaponSoundPlayer WeaponSoundFromProfile(GameWorld gameWorld, string profileId)
    {
        try
        {
            return string.IsNullOrEmpty(profileId)
                ? null
                : gameWorld.GetAlivePlayerBridgeByProfileID(profileId)?.WeaponSoundPlayer;
        }
        catch
        {
            return null;
        }
    }

    private void Update()
    {
        if (_pending.Count == 0) return;

        var gameWorld = Singleton<GameWorld>.Instance;
        var audio = Singleton<BetterAudio>.Instance;
        if (gameWorld?.MainPlayer == null || audio == null)
        {
            _pending.Clear();
            return;
        }

        var listenerPos = gameWorld.MainPlayer.Position;
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var shot = _pending[i];
            if (Time.time < shot.At) continue;
            _pending.RemoveAt(i);
            try
            {
                // The tail bank IS what a distant gunshot sounds like in EFT; body as fallback.
                var bank = shot.Sound.Tail != null ? shot.Sound.Tail : shot.Sound.Body;
                if (bank == null) continue;
                audio.PlayAtPointDistant(shot.Pos, bank, Vector3.Distance(listenerPos, shot.Pos), 1f);
            }
            catch
            {
                // Despawned weapon mid-burst, drop the shot.
            }
        }
    }
}
