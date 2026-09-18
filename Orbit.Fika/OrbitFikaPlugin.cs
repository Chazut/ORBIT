using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Orbit.Api;
using UnityEngine;

namespace Orbit.Fika;

/// <summary>
/// Optional ORBIT companion for Fika co-op: replicates Ghost Mode's simulated ghost-fight
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
        public int Rounds;
        public bool IsTail;
        public BetterSource LoopSource;
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
            Shooters = ToWire(fight.Shooters),
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

        // 2.1+ host: every member fires its own gun from its own spot, same shape as the limiter's
        // local playback (real fire mode and rates from the weapon in hands).
        if (packet.Shooters != null && packet.Shooters.Count > 0)
        {
            var queued = 0;
            var engagementDistance = Vector3.Distance(packet.PosA, packet.PosB);
            // Same per-side stagger as the host: sides are told apart by the nearer fight position.
            int shootersA = 0, shootersB = 0;
            for (var i = 0; i < packet.Shooters.Count; i++)
            {
                if (OnSideA(packet, packet.Shooters[i].Position)) shootersA++; else shootersB++;
            }
            int indexA = 0, indexB = 0;
            for (var i = 0; i < packet.Shooters.Count; i++)
            {
                var shooter = packet.Shooters[i];
                var sideA = OnSideA(packet, shooter.Position);
                var (startOffset, pauseScale) = sideA
                    ? Orbit.Api.GhostShotScheduler.SideStagger(indexA++, shootersA)
                    : Orbit.Api.GhostShotScheduler.SideStagger(indexB++, shootersB);
                var sound = WeaponSoundFromProfile(gameWorld, shooter.ProfileId);
                if (sound == null) continue;
                var weapon = Orbit.Api.GhostWeaponProfile.From(WeaponFromProfile(gameWorld, shooter.ProfileId), sound, engagementDistance);
                QueueShooterShots(sound, weapon, shooter.Position, shooter.Shots, packet.Duration, startOffset, pauseScale);
                queued++;
            }
            if (queued == 0)
            {
                _log.LogInfo($"{PluginName}: ghost fight received but no weapon sound player resolved, burst dropped");
                return;
            }
            _log.LogInfo($"{PluginName}: replaying ghost fight, {packet.Shots} shots from {queued} shooter(s) over {packet.Duration:F1}s at {Mathf.Min(distA, distB):F0}m");
            return;
        }

        // Pre-2.1 host: one weapon per side, generic rates.
        var soundA = WeaponSoundFromProfile(gameWorld, packet.ProfileA);
        var soundB = WeaponSoundFromProfile(gameWorld, packet.ProfileB);
        if (soundA == null && soundB == null)
        {
            _log.LogInfo($"{PluginName}: ghost fight received but no weapon sound player resolved, burst dropped");
            return;
        }

        _log.LogInfo($"{PluginName}: replaying ghost fight, {packet.Shots} shots over {packet.Duration:F1}s at {Mathf.Min(distA, distB):F0}m");
        var budgetA = packet.Shots / 2;
        var budgetB = packet.Shots - budgetA;
        if (soundA == null) { budgetB = packet.Shots; budgetA = 0; }
        if (soundB == null) { budgetA = packet.Shots; budgetB = 0; }
        if (soundA != null) QueueShooterShots(soundA, Orbit.Api.GhostWeaponProfile.Default(soundA.IsAutoWeapon), packet.PosA, budgetA, packet.Duration);
        if (soundB != null) QueueShooterShots(soundB, Orbit.Api.GhostWeaponProfile.Default(soundB.IsAutoWeapon), packet.PosB, budgetB, packet.Duration);
    }

    private static List<OrbitGhostShooter> ToWire(List<OrbitEvents.GhostShooter> shooters)
    {
        var wire = new List<OrbitGhostShooter>(shooters?.Count ?? 0);
        if (shooters == null) return wire;
        for (var i = 0; i < shooters.Count; i++)
            wire.Add(new OrbitGhostShooter { ProfileId = shooters[i].ProfileId, Position = shooters[i].Position, Shots = shooters[i].Shots });
        return wire;
    }

    private static bool OnSideA(OrbitGhostFightPacket packet, Vector3 position)
        => (position - packet.PosA).sqrMagnitude <= (position - packet.PosB).sqrMagnitude;

    private static void QueueShooterShots(WeaponSoundPlayer sound, Orbit.Api.GhostWeaponProfile weapon, Vector3 pos, int budget, float duration, float startOffset = 0f, float pauseScale = 1f)
    {
        if (sound == null || budget <= 0) return;
        var times = Orbit.Api.GhostShotScheduler.Schedule(weapon, budget, duration, startOffset, pauseScale);
        // One entry per trigger pull: an automatic weapon's Body clip is a 16-round loop (GhostShotPlayback).
        var pulls = Orbit.Api.GhostShotPlayback.GroupTriggerPulls(times, weapon, sound.IsAutoWeapon);
        for (var i = 0; i < pulls.Count; i++)
        {
            _pending.Add(new PendingShot
            {
                At = Time.time + pulls[i].At,
                Pos = pos + new Vector3(Random.Range(-1.5f, 1.5f), 0f, Random.Range(-1.5f, 1.5f)),
                Sound = sound,
                Rounds = pulls[i].Rounds,
            });
        }
    }

    private static Weapon WeaponFromProfile(GameWorld gameWorld, string profileId)
    {
        try
        {
            return string.IsNullOrEmpty(profileId)
                ? null
                : gameWorld.GetAlivePlayerByProfileID(profileId)?.HandsController?.Item as Weapon;
        }
        catch
        {
            return null;
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
                var listenerDist = Vector3.Distance(listenerPos, shot.Pos);
                if (shot.IsTail)
                {
                    Orbit.Api.GhostShotPlayback.PlayTail(audio, shot.Sound, shot.LoopSource, shot.Pos, listenerDist);
                    continue;
                }
                var source = Orbit.Api.GhostShotPlayback.Play(audio, shot.Sound, shot.Pos, listenerDist, Mathf.Max(1, shot.Rounds), out var tailDelay);
                if (source != null && tailDelay > 0f)
                {
                    _pending.Add(new PendingShot
                    {
                        At = Time.time + tailDelay, Pos = shot.Pos, Sound = shot.Sound, IsTail = true, LoopSource = source,
                    });
                }
            }
            catch
            {
                // Despawned weapon mid-burst, drop the shot.
            }
        }
    }
}
