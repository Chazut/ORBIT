using System.Collections.Generic;
using UnityEngine.Pool;

namespace Orbit.Inventory;

/// <summary>
/// Object pool for the per-tick scratch list of <see cref="LootRoutineAction"/>s
/// the inventory controller builds when deciding whether to equip / swap
/// / throw an incoming pickup. Pooling the list itself + auto-returning
/// every action on release keeps the hot loot path allocation-free.
/// </summary>
public static class ListActionPool
{
    private static readonly ObjectPool<List<LootRoutineAction>> _pool
        = new(Create, null, OnRelease, LogOnDestroyInstance, true, 2, 32);

    public static List<LootRoutineAction> Create() => [];

    /// <summary>Take a list from the pool.</summary>
    public static List<LootRoutineAction> Rent() => _pool.Get();

    /// <summary>Return a list to the pool (also returns each contained
    /// action to its own pool and clears the list).</summary>
    public static void Return(List<LootRoutineAction> list) => _pool.Release(list);

    /// <summary>Re-pool every action and clear the list — used when the
    /// caller wants to reuse the list before returning it.</summary>
    public static void Reset(List<LootRoutineAction> list) => OnRelease(list);

    private static void OnRelease(List<LootRoutineAction> lootingActions)
    {
        foreach (var action in lootingActions)
            action.Return();
        lootingActions.Clear();
    }

    public static void LogOnDestroyInstance<T>(T instance)
        => Log.Error($"ListActionPool: destroyed instance of {instance.GetType().FullName}");
}
