using System.Collections.Generic;
using System.Reflection;
using EFT;
using SPT.Reflection.Patching;

namespace Orbit.Patches;

/// <summary>
/// Shrink the open-state navmesh carvers BSG attaches to every door so an
/// opened door doesn't block its entire hallway from the navmesh. The
/// vanilla carvers are sized for the door frame; we keep ~37% of that and
/// rely on physics for the rest.
/// </summary>
public class DoorCarverShrinkPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(GameWorld).GetMethod(nameof(GameWorld.OnGameStarted));
    }

    [PatchPostfix]
    public static void Patch()
    {
        var processed = new HashSet<NavMeshDoorLink>();
        var doorsController = UnityEngine.Object.FindObjectOfType<BotDoorsController>();

        if (doorsController == null)
        {
            return;
        }

        Log.Debug($"Shrinking {doorsController._navMeshDoorLinks.Count} door navmesh cutters");

        for (var i = 0; i < doorsController._navMeshDoorLinks.Count; i++)
        {
            var doorLink = doorsController._navMeshDoorLinks[i];

            if (!processed.Add(doorLink))
            {
                continue;
            }

            doorLink.Carver_Opened.size = 0.375f * doorLink.Carver_Opened.size;
            doorLink.Carver_Closed.size = 0.375f * doorLink.Carver_Closed.size;
            doorLink.Carver_Breached.size = 0.375f * doorLink.Carver_Breached.size;
        }
    }
}
