using System.Reflection;
using Comfort.Common;
using EFT;
using Orbit.Core;
using Orbit.Navigation;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

/// <summary>
/// Hooks BSG's BotsController.Init — when the raid hands us a BotsController we carve the danger-zone navmesh
/// obstacles and bring up the singletons (OrbitManager + BotRoster) that the brain layer and patches depend
/// on.
/// </summary>
public class OrbitInitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BotsController).GetMethod(nameof(BotsController.Init));
    }

    [PatchPostfix]
    public static void Postfix(BotsController __instance)
    {
        Log.Debug("Initializing ORBIT runtime");

        DangerZoneCarver.AddNavmeshCutter();

        var botRoster = new BotRoster();
        var orbit = new OrbitManager(__instance, botRoster);

        Singleton<OrbitManager>.Create(orbit);
        Singleton<BotRoster>.Create(botRoster);
    }
}

/// <summary>
/// Per-frame driver. AICoreControllerClass.Update is where BSG ticks the bot layer + action machinery, which
/// is the right moment to evaluate dispatch state. Runs as a POSTFIX — running a prefix or replacing the
/// method nulls the in-flight ActualPath inside BSG's own code, causing path jobs to be resubmitted
/// needlessly when the brain layer gets deactivated mid-tick.
/// </summary>
public class OrbitTickPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(AICoreControllerClass).GetMethod(nameof(AICoreControllerClass.Update));
    }

    [PatchPostfix]
    public static void Postfix(AICoreControllerClass __instance)
    {
        // Bool_0 is BSG's IsActive flag — skip the tick when their controller hasn't enabled itself.
        if (!__instance.Bool_0)
            return;

        // The singleton can be absent here: released at raid end while a queued AICoreController tick
        // still fires, or a raid where ORBIT never initialised (FIKA host/client timing). Skip instead
        // of NRE-ing every frame.
        var orbit = Singleton<OrbitManager>.Instance;
        if (orbit == null)
            return;

        orbit.Update();
    }
}

/// <summary>
/// Releases the OrbitManager + BotRoster singletons when GameWorld disposes (raid end / map unload). Without
/// this, a second raid in the same client session would see stale state from the previous one.
/// </summary>
public class OrbitDisposePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(GameWorld).GetMethod(nameof(GameWorld.Dispose));
    }

    [PatchPostfix]
    public static void Postfix()
    {
        // This runs from GameWorld.Dispose at raid end and MUST NOT throw. Under FIKA, an exception
        // escaping here aborts the host's lobby/raid teardown and leaves FIKA in an undefined state
        // (issue #6: "ORBIT is unable to complete its dispose operation... Object reference not set").
        // The singletons may already be gone — a raid where ORBIT never initialised, a double dispose,
        // or FIKA timing — so null-guard each release and swallow anything that still slips through.
        // A failed cleanup is harmless (the next raid re-creates the singletons); a thrown one is not.
        try
        {
            Plugin.LogSource.LogInfo("Disposing ORBIT static + long-lived state");

            var orbit = Singleton<OrbitManager>.Instance;
            if (orbit != null) Singleton<OrbitManager>.Release(orbit);

            var roster = Singleton<BotRoster>.Instance;
            if (roster != null) Singleton<BotRoster>.Release(roster);

            Plugin.LogSource.LogInfo("Dispose complete");
        }
        catch (System.Exception e)
        {
            Plugin.LogSource.LogWarning($"ORBIT dispose hit an error (swallowed to protect raid/FIKA teardown): {e}");
        }
    }
}
