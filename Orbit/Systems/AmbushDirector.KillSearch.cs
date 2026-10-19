using Orbit.Entities;
using Orbit.Helpers;
using Orbit.Settings;
using UnityEngine;

namespace Orbit.Systems;

internal sealed partial class AmbushDirector
{
    internal const int InitialKillAnchorLimit = 4;
    internal const float InitialKillSearchBudget = 30f;

    internal static bool IsInitialKillSearch(MainObjective main)
        => main != null && main.Type == MainObjectiveType.Kills && main.KillAmbush && !main.CampInitialSearchResolved;

    internal static float InitialKillSearchRemaining(MainObjective main)
        => Mathf.Max(0, Mathf.Min(InitialKillSearchBudget, ServerConfig.Ambush.TravelTimeout) - main.CampInitialSearchElapsed);

    private bool StartInitialKillSearch(Squad squad, MainObjective main)
    {
        if (main.CampSearchAttempt == 0) main.CampSearchAttempt = 1;
        if (InitialKillSearchRemaining(main) <= 0)
        {
            FailInitialKillSearchAttempt(squad, main, "initial search budget exhausted", "none", 0);
            return false;
        }
        if (main.CampSite != null && !waypoints.IsAmbushSiteAvailable(main.CampSite, squad.Leader.BotCategory))
            main.CampSite = null;
        main.CampSite ??= waypoints.CreateKillMainCampSite(main, main.CampRejectedAnchors);
        if (main.CampSite == null)
        {
            FailInitialKillSearch(squad, main, main.CampRejectedAnchors?.Count > 0
                ? "no alternate anchor in main zone and floor" : "no anchor in main zone and floor", "none");
            return false;
        }
        main.SetCampState("searching cover");
        Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
        main.CampRetryAt = Time.time + 5f;
        LogInitialKillSearch(squad, main, "started", "searching initial cover", "none", 0);
        return TryFormation(squad, main.CampSite, main);
    }

    private void CancelInitialKillSearch(Squad squad, CampFormationSearch search, string cancellationReason)
    {
        var main = search.Main;
        if (!main.CanPursue(squad.MainObjectives) || squad.MainObjectives?.Contains(main) != true) return;
        main.CampInitialSearchElapsed += search.Elapsed;
        var reason = cancellationReason ?? search.InvalidReason ?? "search cancelled";
        // Interruptions never count as exhausted covers. Keep the work already spent so
        // repeated movement invalidations cannot reset the initial-search budget forever.
        if (cancellationReason == null && search.TimedOut && Available(squad) && ServerConfig.Ambush.Allows(squad.Leader?.BotCategory)
            && ServerConfig.Ambush.Hotspots.Enabled)
        {
            FailInitialKillSearchAttempt(squad, main, "initial search budget exhausted", search.Rejections, 0);
            return;
        }
        main.CampRetryAt = Time.time + 1f;
        main.SetCampState("paused", reason);
        LogInitialKillSearch(squad, main, "paused", reason, search.Rejections, 0);
    }

    private void FailInitialKillSearchAttempt(Squad squad, MainObjective main, string reason, string rejected, float elapsed)
    {
        main.CampInitialSearchElapsed += elapsed;
        if (main.CampSearchAttempt >= InitialKillAnchorLimit)
        {
            FailInitialKillSearch(squad, main, reason, rejected);
            return;
        }
        LogInitialKillSearch(squad, main, "retry", reason, rejected, 0);
        if (main.CampSite != null)
            (main.CampRejectedAnchors ??= new(InitialKillAnchorLimit - 1)).Add(main.CampSite.Position);
        main.CampSite = null;
        main.CampSearchAttempt++;
        main.CampInitialSearchElapsed = 0;
        main.CampRetryAt = Time.time + 5f;
        main.SetCampState("searching cover", "retry at alternate anchor: " + reason);
        Orbit.Api.OrbitTelemetry.MainObjectivesRevision++;
    }

    private static void FailInitialKillSearch(Squad squad, MainObjective main, string reason, string rejected)
    {
        main.Completed = true;
        main.SetCampState("failed", reason);
        LogInitialKillSearch(squad, main, "failed", reason, rejected, 0);
        main.CampSite = null;
    }

    private static void LogInitialKillSearch(Squad squad, MainObjective main, string state, string reason, string rejected, float elapsed)
    {
        if (!Log.InfoEnabled) return;
        var anchor = main.CampSite != null ? main.CampSite.Position.ToString() : "none";
        Log.Info($"KILL AMBUSH SEARCH: {squad} state={state} attempt={main.CampSearchAttempt}/{InitialKillAnchorLimit} anchor={anchor} zone={main.KillZoneCenter} radius={main.KillZoneRadius:F1}m floor={main.ZoneFloorId ?? "all"} searchTime={main.CampInitialSearchElapsed + elapsed:F1}s reason={reason} rejected={rejected}");
    }
}
