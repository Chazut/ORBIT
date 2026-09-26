using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;

namespace Orbit.Patches;

// Sanitar's reserve patrol scripts manufacture an injury to perform an ambient medical animation.
// Keep the patrol, including item drops, but leave treatment to his normal medical decisions.
internal static class SanitarPatrolMedicinePatch
{
    private const string PatchId = "orbit.sanitar.patrol-medicine";
    private static readonly HashSet<string> Reported = new();
    internal static bool Ready { get; private set; }

    internal static void Enable()
    {
        if (Ready) return;
        var harmony = new Harmony(PatchId);
        try
        {
            foreach (var type in new[] { typeof(DropItemAndHealReservWay), typeof(UseSurgeKitReservWay) })
                harmony.Patch(AccessTools.Method(type, "ManualUpdate", new[] { typeof(BotOwner) }),
                    transpiler: new HarmonyMethod(typeof(SanitarPatrolMedicinePatch), nameof(GuardMedicine)));
            Ready = true;
            Log.Info("SANITAR PATROL: scripted medicine guard ready");
        }
        catch (Exception e)
        {
            harmony.UnpatchSelf();
            Log.Warning($"SANITAR PATROL: scripted medicine guard unavailable: {e.Message}");
        }
    }

    internal static void Clear() => Reported.Clear();

    private static IEnumerable<CodeInstruction> GuardMedicine(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var replacements = new Dictionary<MethodInfo, MethodInfo>();
        void Replace(Type type, string name, Type[] args, string wrapper)
        {
            var source = AccessTools.Method(type, name, args);
            if (source == null) throw new MissingMethodException(type.FullName, name);
            replacements.Add(source, AccessTools.Method(typeof(SanitarPatrolMedicinePatch), wrapper));
        }
        Replace(typeof(BotSurgicalKit), "SetRandomPartToHeal", Type.EmptyTypes, nameof(PrepareSurgery));
        Replace(typeof(BotSurgicalKit), "ApplyToCurrentPart", new[] { typeof(Action) }, nameof(ApplySurgery));
        if (original.DeclaringType == typeof(UseSurgeKitReservWay))
        {
            Replace(typeof(BotFirstAid), "SetRandomPartToHeal", Type.EmptyTypes, nameof(PrepareFirstAid));
            Replace(typeof(BotFirstAid), "TryApplyToCurrentPart", new[] { typeof(int?), typeof(Action) }, nameof(ApplyFirstAid));
            Replace(typeof(BotStimulators), "TryApply", new[] { typeof(bool), typeof(int?), typeof(Action<bool>) }, nameof(ApplyStimulator));
        }

        var code = instructions.ToList();
        foreach (var method in replacements.Keys)
            if (code.Count(instruction => instruction.Calls(method)) != 1)
                throw new InvalidOperationException($"Sanitar patrol medicine shape changed: {original.DeclaringType.Name}.{method.Name}");

        var result = new List<CodeInstruction>(code.Count + replacements.Count);
        foreach (var instruction in code)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && instruction.operand is MethodInfo method && replacements.TryGetValue(method, out var wrapper))
            {
                // Pass the patrol's bot after the original call arguments, including optional callbacks.
                // Retain branch labels on the inserted argument load.
                instruction.opcode = OpCodes.Ldarg_1;
                instruction.operand = null;
                result.Add(instruction);
                result.Add(new CodeInstruction(OpCodes.Call, wrapper));
                continue;
            }
            result.Add(instruction);
        }
        return result;
    }

    private static bool Skip(BotOwner bot)
    {
        if (bot?.Profile?.Info?.Settings?.Role != WildSpawnType.bossSanitar) return false;
        if (Reported.Add(bot.ProfileId))
            Log.Info($"SANITAR PATROL: {bot.Profile.Nickname} ({bot.ProfileId}) skipped scripted medicine; normal medical treatment preserved");
        return true;
    }

    private static void PrepareSurgery(BotSurgicalKit kit, BotOwner bot)
    {
        if (!Skip(bot)) kit.SetRandomPartToHeal();
    }

    private static void ApplySurgery(BotSurgicalKit kit, Action callback, BotOwner bot)
    {
        if (!Skip(bot)) kit.ApplyToCurrentPart(callback);
    }

    private static void PrepareFirstAid(BotFirstAid kit, BotOwner bot)
    {
        if (!Skip(bot)) kit.SetRandomPartToHeal();
    }

    private static void ApplyFirstAid(BotFirstAid kit, int? animation, Action callback, BotOwner bot)
    {
        if (!Skip(bot)) kit.TryApplyToCurrentPart(animation, callback);
    }

    private static void ApplyStimulator(BotStimulators kit, bool noCheckDelay, int? animation, Action<bool> callback, BotOwner bot)
    {
        if (!Skip(bot)) kit.TryApply(noCheckDelay, animation, callback);
    }
}
