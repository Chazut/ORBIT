using System;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using Orbit.Core;

namespace Orbit.Looting;

internal static class LootActorGuard
{
    internal static bool IsCurrent(BotOwner bot, Player player)
    {
        if (bot == null || player == null || bot.IsDead || bot.BotState == EBotState.Disposed
            || !ReferenceEquals(bot.GetPlayer, player) || player.InventoryController == null)
            return false;
        // Numeric bot IDs and body GameObjects can both be recycled. Ghost intentionally disables
        // the body, so activeInHierarchy and the native AI update state are not lifetime checks.
        var agent = Singleton<BotRoster>.Instance?.GetAgent(bot);
        return agent != null && ReferenceEquals(agent.Bot, bot) && ReferenceEquals(agent.Player, player);
    }

    internal static void RequireCurrent(BotOwner bot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsCurrent(bot, bot?.GetPlayer)) throw new OperationCanceledException(ct);
    }

    internal static Task<T> TrackTransaction<T>(BotOwner bot, Task<T> task)
    {
        var agent = Singleton<BotRoster>.Instance?.GetAgent(bot);
        if (agent != null && ReferenceEquals(agent.Bot, bot)) agent.LootHandler?.TrackInventoryTransaction(task);
        return task;
    }

    // Cancellation stops our continuation, not an inventory operation owned by EFT/Fika. Observe a
    // late fault without touching the old controller or fast-forwarding a replacement actor's hands.
    internal static async Task<T> AwaitTransaction<T>(Task<T> task, CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource<bool>();
        using (ct.Register(() => cancelled.TrySetResult(true)))
        {
            if (await Task.WhenAny(task, cancelled.Task) != task || ct.IsCancellationRequested)
            {
                _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                ct.ThrowIfCancellationRequested();
            }
            ct.ThrowIfCancellationRequested();
            return await task;
        }
    }
}
