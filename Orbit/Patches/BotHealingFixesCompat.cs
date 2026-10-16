using System;
using System.Threading.Tasks;
using HarmonyLib;
using Orbit.Helpers;
using Orbit.Systems;

namespace Orbit.Patches;

// HarmonyX runs every prefix even after another prefix refuses the original.
// Guard the replacement's entry before it marks medicine in use or touches hands.
internal static class BotHealingFixesCompat
{
    private const string PatchId = "orbit.compat.bot-healing-fixes";
    private static Type _attempted;
    private static bool _present, _ready;
    internal static bool AllowsNativeSleep => !_present || _ready;

    internal static void Enable()
    {
        var helper = OptionalModTypes.Find("BotHealingFixes.Utils.MedUtils");
        var detected = helper ?? OptionalModTypes.Find("BotHealingFixes.BotHealingFixes");
        if (detected == null || _attempted == detected) return;
        _attempted = detected;
        _present = true;
        var harmony = new Harmony(PatchId);
        try
        {
            var method = helper == null ? null : AccessTools.DeclaredMethod(helper, "ApplyMedicineAsync",
                new[] { typeof(BotAbstractMedsToPart), typeof(TimeSpan), typeof(Action) });
            if (method == null || !method.IsStatic || method.ReturnType != typeof(Task))
                throw new MissingMethodException("Bot Healing Fixes ApplyMedicineAsync signature changed");
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(BotHealingFixesCompat), nameof(BeforeApply)));
            _ready = true;
            Log.Always("BOT HEALING FIXES: Ghost compatibility ready");
        }
        catch (Exception e)
        {
            harmony.UnpatchSelf();
            _ready = false;
            Log.Always($"BOT HEALING FIXES: compatibility unavailable; native bots stay awake: {e.Message}");
        }
    }

    private static bool BeforeApply(BotAbstractMedsToPart __0, ref Task __result)
    {
        if (!NativeGhostSystem.HasSleepers || __0?._owner == null) return true;
        var bot = __0._owner;
        if (!(__0 is BotFirstAid && NativeGhostSystem.RetainSimulatedFirstAid(bot))
            && !NativeGhostSystem.DeferBodyOperation(bot, "Bot Healing Fixes." + __0.GetType().Name)) return true;

        // Nothing started, so do not run a completion callback or mutate medicine state.
        // Normal native decisions will retry after the body has actually woken.
        __result = Task.CompletedTask;
        return false;
    }
}
