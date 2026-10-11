using System;
using System.Threading;
using EFT;
using EFT.AssetsManager;
using UnityEngine;

namespace Orbit.Looting;

public partial class OrbitLootHandler
{
    private Player _player;
    private bool _retired;

    internal bool HasLiveOwner => !_retired && LootActorGuard.IsCurrent(_bot, _player);

    internal static OrbitLootHandler ForBot(BotOwner bot)
    {
        var player = bot.GetPlayer;
        var go = player.gameObject;
        foreach (var existing in go.GetComponents<OrbitLootHandler>())
        {
            if (!existing._retired && ReferenceEquals(existing._bot, bot)
                && ReferenceEquals(existing._player, player)) return existing;
            if (!existing._retired)
                Log.Debug($"OrbitLootHandler.Lifecycle({existing.Nick}): discarded handler from previous spawn");
            existing.Retire();
            Destroy(existing);
        }
        var handler = go.AddComponent<OrbitLootHandler>();
        handler.Init(bot);
        // Player.Create registers its Player component here too. ReturnToPool must remove our
        // component with that spawn, even if ORBIT receives no bot-removal event.
        go.GetComponent<PlayerPoolObject>()?.RegisteredComponentsToClean.Add(handler);
        return handler;
    }

    internal void Retire()
    {
        if (_retired) return;
        _retired = true;
        if (LootTaskRunning) Log.Debug($"OrbitLootHandler.Lifecycle({Nick}): retired active session");
        StopLooting();
        CurrentTarget = null;
        CurrentTargetKind = LootKind.None;
        ForceEnabled = false;
    }

    // Do not cancel in OnDisable: Ghost keeps inventory-only looting on an inactive body.
    private void OnDestroy() => Retire();

    private bool OwnsSession(CancellationToken ct) => _cts != null && ct == _cts.Token;

    private void CheckSession(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OwnsSession(ct) || !HasLiveOwner) throw new OperationCanceledException(ct);
    }

    private bool FinishSession(CancellationToken ct)
    {
        // An earlier cancelled task can complete after a new session has started on this bot.
        if (!OwnsSession(ct)) return false;
        if (HasLiveOwner && !DormantMode)
        {
            if (_swappedWeaponIds.Count > 0) SyncBotWeaponStateAtSessionEnd();
            UnfreezeBotAfterLootSession();
        }
        LootTaskRunning = false;
        _cts = null;
        return true;
    }
}
