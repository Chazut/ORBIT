using EFT;
using Orbit.Entities;

namespace Orbit.Systems;

public partial class DormancySystem
{
    /// <summary>Direct, single-bot wake: no distance gate or normal wake queue, but retain landing safety.</summary>
    internal bool WakeForExternalControl(BotOwner bot, Agent agent)
        => WakeForExternalPolicy(bot, agent, "external control exclusion");

    internal bool WakeForSleepRestriction(BotOwner bot, Agent agent)
        => WakeForExternalPolicy(bot, agent, "external sleep restriction");

    private bool WakeForExternalPolicy(BotOwner bot, Agent agent, string detail)
    {
        ForgetStagedBot(bot);
        CancelGhostShooter(bot.ProfileId);
        var reason = new GhostWakeReason(GhostWakeCause.GroupChanged, detail);
        if (agent?.IsDormant == true) WakeAgentWithReason(agent, reason);
        if (_vanillaDormant.Contains(bot)) WakeVanillaBotWithReason(bot, reason);
        return agent?.IsDormant != true && !_vanillaDormant.Contains(bot)
            && bot.gameObject.activeSelf && bot.BotState == EBotState.Active;
    }
}
