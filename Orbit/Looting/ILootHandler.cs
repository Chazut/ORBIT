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

    void Init(BotOwner bot);
    void StartLooting();
    void StopLooting();
    void Cancel();
}
