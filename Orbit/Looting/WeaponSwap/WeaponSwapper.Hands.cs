using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using Orbit.Helpers;
namespace Orbit.Looting.WeaponSwap;
public static partial class WeaponSwapper
{
    // Wait for the hands controller's IsChangingWeapon flag to clear before the next op. Polled instead of
    // blind-delayed so the typical sub-1s case doesn't hold up the bot.
    private static async Task<bool> WaitForIsChangingWeaponAsync(BotOwner bot, string nick, CancellationToken ct)
    {
        LootActorGuard.RequireCurrent(bot, ct);
        const int maxWaitMs = 5000;
        const int pollIntervalMs = 100;
        var ic = bot?.GetPlayer?.InventoryController;
        if (ic == null) return false;
        var elapsed = 0;
        while (elapsed < maxWaitMs)
        {
            bool changing;
            try { changing = ic.IsChangingWeapon || bot.WeaponManager?.Selector?.IsChanging == true; }
            catch { return false; }
            if (!changing) return true;
            await Task.Delay(pollIntervalMs, ct);
            LootActorGuard.RequireCurrent(bot, ct);
            elapsed += pollIntervalMs;
        }
        Log.Warning($"WeaponSwap.WaitForIsChangingWeapon({nick}): hit {maxWaitMs}ms cap; next weapon operation deferred");
        return false;
    }

    private static bool MainWeaponReady(BotOwner bot)
    {
        var selector = bot?.WeaponManager?.Selector;
        var player = bot?.GetPlayer;
        var target = selector == null ? null : player?.Inventory?.Equipment?.GetSlot(selector._mainWeapon)?.ContainedItem;
        return target is Weapon && player.HandsController is Player.FirearmController
            && ReferenceEquals(target, player.HandsController.Item) && ReferenceEquals(target, bot.WeaponManager.CurrentWeapon)
            && selector.IsWeaponReady && !selector.IsChanging && !player.InventoryController.IsChangingWeapon;
    }

    // Refresh loadout metadata, then draw only if the actual main weapon is not already ready.
    private static async Task FinalizeWeaponSwapAsync(BotOwner bot, string nick, CancellationToken ct)
    {
        LootActorGuard.RequireCurrent(bot, ct);
        try
        {
            if (!await WaitForIsChangingWeaponAsync(bot, nick, ct)) return;
            LootActorGuard.RequireCurrent(bot, ct);
            var selector = bot?.WeaponManager?.Selector;
            if (selector == null) return;
            selector.UpdateWeaponsList();
            try { bot.AIData?.CalcPower(); } catch { }
            // Acquiring a secondary must not start another draw of the weapon already in hands.
            if (MainWeaponReady(bot))
            { Log.Debug($"WeaponSwap.Finalize({nick}): main weapon already ready; redraw skipped"); return; }
            bool callback = false, succeeded = false;
            bool accepted = selector.TryChangeToSlot(selector._mainWeapon, true, result => { callback = true; succeeded = result.Succeed; });
            for (int elapsed = 0; accepted && elapsed < 5000; elapsed += 100)
            {
                LootActorGuard.RequireCurrent(bot, ct);
                if (callback && !succeeded) break;
                if (callback && MainWeaponReady(bot))
                { Log.Debug($"WeaponSwap.Finalize({nick}): main weapon ready confirmed"); return; }
                await Task.Delay(100, ct);
            }
            Log.Warning($"WeaponSwap.Finalize({nick}): main weapon return unconfirmed accepted={accepted} callback={callback} succeeded={succeeded}");
        }
        catch (System.OperationCanceledException) { throw; }
        catch (System.Exception e)
        {
            Log.Warning($"WeaponSwap.Finalize({nick}): THREW {e.Message}");
        }
    }

}
