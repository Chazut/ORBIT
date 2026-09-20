using System;
using System.Linq.Expressions;
using System.Reflection;
using EFT;
using HarmonyLib;
using Orbit.Systems;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

/// <summary>
/// SAIN's manager keeps checking inactive bots. Its repeated SetActive(false) clears the BSG
/// path through SAINMover.Stop, even when a native Ghost is still following that path.
/// Scope the protection to that check; SAIN's other cleanup and genuine brain stops still run.
/// </summary>
public class NativeGhostSainCleanupPatch : ModulePatch
{
    [ThreadStatic] private static BotOwner _cleanupBot;
    private static Func<object, BotOwner> _owner;
    private static Func<object, bool> _gameEnding;
    private static Func<object, bool> _botActive;

    protected override MethodBase GetTargetMethod()
    {
        var type = AccessTools.TypeByName("SAIN.SAINComponent.Classes.SAINActivationClass")
            ?? throw new TypeLoadException("SAIN activation type unavailable");
        _owner = Getter<BotOwner>(type, "BotOwner");
        _gameEnding = Getter<bool>(type, "GameEnding");
        _botActive = Getter<bool>(type, "BotActive");
        return AccessTools.Method(type, "CheckBotActive", Type.EmptyTypes)
            ?? throw new MissingMethodException(type.FullName, "CheckBotActive");
    }

    private static Func<object, T> Getter<T>(Type type, string name)
    {
        var property = AccessTools.Property(type, name)
            ?? throw new MissingMemberException(type.FullName, name);
        var instance = Expression.Parameter(typeof(object), "instance");
        return Expression.Lambda<Func<object, T>>(
            Expression.Property(Expression.Convert(instance, type), property), instance).Compile();
    }

    [PatchPrefix]
    public static void Prefix(object __instance, out BotOwner __state)
    {
        __state = _cleanupBot;
        _cleanupBot = null;
        if (!NativeGhostSystem.HasSleepers) return;
        try
        {
            var bot = _owner(__instance);
            // Let the initial deactivation and end-of-raid cleanup run normally.
            if (NativeGhostSystem.OwnsInactiveMovement(bot) && !_botActive(__instance) && !_gameEnding(__instance))
                _cleanupBot = bot;
        }
        catch (Exception e)
        {
            NativeGhostSystem.FailSainCleanupBridge(e);
        }
    }

    [PatchFinalizer]
    public static Exception Finalizer(BotOwner __state, Exception __exception)
    {
        _cleanupBot = __state;
        return __exception;
    }

    internal static bool PreservePath(BotMover mover)
        => NativeGhostSystem.PreservePathDuringSainCleanup(_cleanupBot, mover);
}

public class NativeGhostSainStopPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(BotMover), nameof(BotMover.Stop), Type.EmptyTypes);

    [PatchPrefix]
    public static bool Prefix(BotMover __instance)
    {
        if (NativeGhostSainCleanupPatch.PreservePath(__instance)) return false;
        NativeGhostSystem.CancelMoveOrder(__instance);
        return true;
    }
}
