using EFT;
using EFT.Interactive;
using UnityEngine;

namespace Orbit.Looting;

public class OrbitLootHandler : MonoBehaviour, ILootHandler
{
    private BotOwner _bot;

    public LootStats Stats { get; } = new();
    public bool LootTaskRunning { get; private set; }

    public InteractableObject ActiveLoot { get; set; }
    public LootKind ActiveLootType { get; set; } = LootKind.None;
    public Vector3 Destination { get; set; }
    public Vector3 LootObjectPosition { get; set; }
    public bool ForceBrainEnabled { get; set; }

    public void Init(BotOwner bot)
    {
        _bot = bot;
    }

    public void StartLooting()
    {
        // Phase 1 stub — accept the call, mark task done immediately.
        LootTaskRunning = false;
        Stats.LastItemsTaken = false;
    }

    public void StopLooting()
    {
        LootTaskRunning = false;
    }

    public void Cancel()
    {
        LootTaskRunning = false;
    }
}
