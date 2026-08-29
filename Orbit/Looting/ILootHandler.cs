using EFT;
using EFT.Interactive;
using UnityEngine;

namespace Orbit.Looting;

public interface ILootHandler
{
    LootStats Stats { get; }
    bool LootTaskRunning { get; }

    InteractableObject CurrentTarget { get; set; }
    LootKind CurrentTargetKind { get; set; }
    Vector3 ApproachPosition { get; set; }
    Vector3 TargetWorldPosition { get; set; }
    bool ForceEnabled { get; set; }

    /// <summary>Dormant fast path (AI limiter): the body is a disabled GameObject, so the session skips
    /// every animation, delay, freeze and equip/swap — pure data transfers only.</summary>
    bool DormantMode { get; set; }

    /// <summary>True when the last session ended through a cancel rather than completing.</summary>
    bool LastSessionCancelled { get; }

    void Init(BotOwner bot);
    void StartLooting();
    void StopLooting();
    void Cancel();
}
