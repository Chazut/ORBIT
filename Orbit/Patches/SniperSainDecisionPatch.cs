using System;
using System.Linq.Expressions;
using System.Reflection;
using EFT;
using HarmonyLib;
using Orbit.Systems;

namespace Orbit.Patches;

// A scoped, temporary decision bias. SAIN still owns perception, aim, firing and medical actions.
// Close, wounded, suppressed, retreating and grenade-avoiding bots retain their original decision.
internal static class SniperSainDecisionPatch
{
    private static Func<object, BotOwner> _owner;
    internal static bool Ready { get; private set; }
    internal static void Enable()
    {
        var harmony = new Harmony("orbit.sniper.sain-decision");
        try
        {
            var type = Type.GetType("SAIN.SAINComponent.Classes.Decision.BotDecisionManager, SAIN", false)
                ?? throw new TypeLoadException("SAIN decision manager unavailable");
            var target = AccessTools.Method(type, "SetDecisions") ?? throw new MissingMethodException("SetDecisions");
            var args = target.GetParameters();
            if (args.Length != 4 || !args[0].ParameterType.IsEnum || !args[1].ParameterType.IsEnum || !args[2].ParameterType.IsEnum)
                throw new InvalidOperationException("Unsupported SAIN decision signature");
            // Compile once at startup, never search assemblies or reflect through a bot on each tick.
            var value = Expression.Parameter(typeof(object), "value");
            _owner = Expression.Lambda<Func<object, BotOwner>>(Expression.Property(Expression.Convert(value, type), "BotOwner"), value).Compile();
            var prefix = typeof(SniperSainDecisionPatch).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(args[0].ParameterType, args[1].ParameterType, args[2].ParameterType);
            Enum.Parse(args[0].ParameterType, "StandAndShoot");
            SniperSainAlertPatch.Enable(harmony, type, args[3].ParameterType);
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Ready = true;
            Log.Info("SNIPER: SAIN distant shooting preference ready");
        }
        catch (Exception error)
        {
            harmony.UnpatchSelf(); Ready = false;
            Log.Warning($"SNIPER: SAIN preference unavailable, ordinary combat retained: {error.Message}");
        }
    }
    private static class Decision<T> where T : struct
    { internal static readonly T Stand = (T)Enum.Parse(typeof(T), "StandAndShoot"); }
    private static void Prefix<TSolo, TSquad, TSelf>(object __instance, ref TSolo __0, TSquad __1, TSelf __2, object __3)
        where TSolo : struct where TSquad : struct where TSelf : struct
    {
        try
        {
            if (!Ready) return;
            var bot = _owner(__instance);
            if (!SniperCombat.HasLease(bot)) return;
            var independent = __1.ToString() == "None" && __2.ToString() == "None";
            SniperSainAlertPatch.Observe(__instance, bot, __0.ToString(), independent, __3);
            if (!SniperCombat.CanHold(bot) || !independent || !SniperSainAlertPatch.SafeForDistantHold(__instance)) return;
            // Preserve all safety/self-care decisions and squad coordination.
            if (__0.ToString() is "Search" or "RushEnemy" or "StandAndShoot" or "ShootDistantEnemy")
                __0 = Decision<TSolo>.Stand;
        }
        catch (Exception error)
        {
            Ready = false;
            SniperSainAlertPatch.Fail(error);
            Log.Warning($"SNIPER: SAIN preference disabled, ordinary combat retained: {error.Message}");
        }
    }
}
