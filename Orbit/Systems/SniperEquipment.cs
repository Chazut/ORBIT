using EFT.InventoryLogic;
using Orbit.Entities;
using Orbit.Helpers;

namespace Orbit.Systems;

internal static class SniperEquipment
{
    internal static bool Eligible(Agent agent, float minimumZoom)
    {
        if (agent?.BotCategory != "PMC" || agent.Bot.Profile.WillBeAPlayerScav()) return false;
        var weapon = agent.IsDormant && agent.GhostBestWeapon != null
            ? agent.GhostBestWeapon : agent.Player?.HandsController?.Item as Weapon;
        if (weapon == null || weapon.WeapClass is not ("sniperRifle" or "marksmanRifle" or "assaultRifle" or "assaultCarbine" or "machinegun")) return false;
        foreach (var sight in weapon.GetItemComponentsInChildren<SightComponent>(false))
            if (sight.GetMaxOpticZoom() >= minimumZoom) return true;
        return false;
    }
}
