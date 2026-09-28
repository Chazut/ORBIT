using EFT;

namespace Orbit.Systems;

// A disposed boss can remain in BotFollower.BossToFollow: native Dispose only clears a living boss.
// Repair that stale relationship before the sleeping brain reads its former leader's disposed mover.
internal static class NativeGhostFollowers
{
    internal static bool Reconcile(BotOwner bot)
    {
        var follower = bot.BotFollower;
        var former = follower?.BossToFollow;
        if (former == null || !former.IsAI || former.IsAlive) return false;

        follower.PatrolDataFollower.Dispose();
        follower.BossToFollow = null;
        // Dispose does not reset IsInited. A fresh controller must not keep the old follower action alive.
        follower.PatrolDataFollower = new PatrolDataFollower(bot, follower.Index);
        if (bot.Boss is { IamBoss: true })
            bot.Boss.SetPatrolMode();
        else
        {
            var chooser = PatrollingData.GetPointChooser(bot, PatrolMode.simple, bot.SpawnProfileData);
            bot.PatrollingData.SetMode(PatrolMode.simple, chooser);
            // Keep the leader elected by the game or faction. Never select an unrelated nearby boss.
            var group = bot.BotsGroup;
            for (var i = 0; group != null && i < group.MembersCount; i++)
            {
                var candidate = group.Member(i);
                if (candidate == null || candidate == bot || candidate.IsDead
                    || candidate.BotState != EBotState.Active || candidate.Boss is not { IamBoss: true }) continue;
                var index = candidate.Boss.Followers.IndexOf(bot);
                if (index >= 0) follower.SetToFollow(candidate.Boss, index, true);
                else candidate.Boss.OfferSelf(bot);
                if (follower.BossToFollow != null) break;
            }
        }
        Log.Info($"NATIVE GHOST FOLLOWER: {bot.Profile.Nickname} released dead leader; promoted={bot.Boss?.IamBoss == true} rejoined={follower.BossToFollow != null}");
        return true;
    }
}
