using EFT;
using EFT.Interactive;
using UnityEngine;

namespace Orbit.Looting;

public interface ILootHandler
{
    LootStats Stats { get; }
    bool LootTaskRunning { get; }

    InteractableObject ActiveLoot { get; set; }
    LootKind ActiveLootType { get; set; }
    Vector3 Destination { get; set; }
    Vector3 LootObjectPosition { get; set; }
    bool ForceBrainEnabled { get; set; }

    void Init(BotOwner bot);
    void StartLooting();
    void StopLooting();
    void Cancel();
}
