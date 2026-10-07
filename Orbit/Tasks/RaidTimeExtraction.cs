using Comfort.Common;
using EFT;
using Orbit.Entities;
using Orbit.Helpers;
using Random = UnityEngine.Random;

namespace Orbit.Tasks;

internal static class RaidTimeExtraction
{
    // Read-only telemetry: never roll personality/random state while recording a snapshot.
    internal static float? SecondsUntilDeparture(Squad squad)
    {
        if (squad == null || float.IsNaN(squad.TimeExtractThresholdSeconds)) return null;
        var timer = Singleton<AbstractGame>.Instance?.GameTimer;
        if (timer?.SessionTime == null || timer.SessionTime.Value.TotalSeconds <= 0) return null;
        return System.Math.Max(0f, (float)(timer.SessionTime.Value.TotalSeconds - timer.PastTime.TotalSeconds)
            - squad.TimeExtractThresholdSeconds);
    }

    internal static void Check(Squad squad)
    {
        if (squad.ExtractRequested && squad.LootExtractSweep == null) return;
        var leaderBot = squad?.Leader?.Bot;
        if (leaderBot?.Profile?.Info?.Settings == null) return;
        var role = leaderBot.Profile.Info.Settings.Role;
        // Eligibility: same gate as the loot-value trigger , only factions permitted to extract bother to
        // roll a threshold.
        var allowed = ServerConfig.Loot.ExtractAllowedFor;
        var isPlayerScav = leaderBot.Profile.WillBeAPlayerScav();
        if (!(isPlayerScav ? (allowed & ExtractFaction.PlayerScav) != 0 : allowed.IsBotEnabled(role))) return;

        var gameTimer = Singleton<AbstractGame>.Instance?.GameTimer;
        if (gameTimer?.SessionTime == null || gameTimer.SessionTime.Value.TotalSeconds <= 0) return;

        // Lazy-roll the threshold the first time we evaluate this squad.
        if (float.IsNaN(squad.TimeExtractThresholdSeconds))
            squad.TimeExtractThresholdSeconds = RollExtractThreshold(leaderBot);

        var remaining = (float)(gameTimer.SessionTime.Value.TotalSeconds - gameTimer.PastTime.TotalSeconds);
        if (remaining > squad.TimeExtractThresholdSeconds) return;

        squad.ExtractRequested = true;
        squad.ExtractRequestedReason = $"raid time low ({remaining:F0}s left)";
        if (squad.MainObjectives != null)
            foreach (var main in squad.MainObjectives)
                if (main.Type == MainObjectiveType.ExtractCamp && !main.Completed && main.CampStartedAt > 0)
                {
                    main.Completed = true;
                    Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
                    Log.Info($"AMBUSH: {squad} extract camp completed at time-based departure hold={main.CampElapsed:F0}s");
                }
        Log.Info($"{squad}: raid time low ({remaining:F0}s remaining <= {squad.TimeExtractThresholdSeconds:F0}s threshold for role {role}) , squad will bee-line to nearest eligible exfil");
    }

    private static float RollExtractThreshold(BotOwner leaderBot)
    {
        var isPlayerScav = leaderBot?.Profile != null && leaderBot.Profile.WillBeAPlayerScav();
        var windowPct = isPlayerScav ? ServerConfig.PlayerScav.TimeExtractWindow : ServerConfig.MainObjectives.TimeExtractWindow;
        var totalRaidSeconds = (float)(Singleton<AbstractGame>.Instance?.GameTimer?.SessionTime?.TotalSeconds ?? 0d);
        if (totalRaidSeconds <= 0f) return 0f;
        return totalRaidSeconds * Random.Range(windowPct.x, windowPct.y) / 100f;
    }

}
