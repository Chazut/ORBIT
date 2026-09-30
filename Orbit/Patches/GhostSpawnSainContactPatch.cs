using System;
using System.Linq.Expressions;
using System.Reflection;
using Comfort.Common;
using HarmonyLib;
using Orbit.Core;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

// SAIN selects its own GoalEnemy before assigning BotMemory.GoalEnemy. Guard that
// first selection too, so declining the BSG assignment cannot leave SAIN fighting.
public class GhostSpawnSainContactPatch : ModulePatch
{
    private static Func<object, EnemyInfo> _enemyInfo;
    private static Func<object, object> _currentEnemy;

    protected override MethodBase GetTargetMethod()
    {
        var controller = AccessTools.TypeByName("SAIN.SAINComponent.Classes.EnemyClasses.SAINEnemyController")
            ?? throw new TypeLoadException("SAIN enemy controller unavailable");
        var goal = AccessTools.Property(controller, "GoalEnemy")
            ?? throw new MissingMemberException(controller.FullName, "GoalEnemy");
        var info = AccessTools.Property(goal.PropertyType, "EnemyInfo")
            ?? throw new MissingMemberException(goal.PropertyType.FullName, "EnemyInfo");
        var instance = Expression.Parameter(typeof(object), "instance");
        _enemyInfo = Expression.Lambda<Func<object, EnemyInfo>>(
            Expression.Property(Expression.Convert(instance, goal.PropertyType), info), instance).Compile();
        _currentEnemy = Expression.Lambda<Func<object, object>>(
            Expression.Convert(Expression.Property(Expression.Convert(instance, controller), goal), typeof(object)), instance).Compile();
        return goal.GetSetMethod(true) ?? throw new MissingMethodException(controller.FullName, "set_GoalEnemy");
    }

    [PatchPrefix]
    public static bool Prefix(object __instance, object value)
    {
        if (value == null || _currentEnemy(__instance) != null) return true;
        return Singleton<OrbitManager>.Instance?.DormancySystem
            .DeferSpawnContact(_enemyInfo(value), source: "sain-goal") != true;
    }
}
