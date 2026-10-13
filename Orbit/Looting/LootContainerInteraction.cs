using EFT;
using EFT.Interactive;
using Orbit.Helpers;

namespace Orbit.Looting;

internal static class LootContainerInteraction
{
    internal static void Close(BotOwner bot, LootableContainer container, bool dormant)
    {
        if (container == null || !LootActorGuard.IsCurrent(bot, bot?.GetPlayer)) return;
        if (!dormant && bot.gameObject.activeInHierarchy)
            bot.LootOpener?.Interact(container, EInteractionType.Close);
        else if (container.DoorState == EDoorState.Open)
        {
            // No interaction animation can finish on an inactive body. Preserve Fika replication.
            if (FikaDetection.FikaLoaded)
                bot.GetPlayer.ExecuteInteraction(container, new InteractionResult(EInteractionType.Close));
            else container.Close();
        }
    }
}
