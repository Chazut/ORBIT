namespace Orbit.Systems;

internal static class NativeGhostPolicy
{
    public static bool Supports(string decision, string customLogic, bool customRole, bool huntAdapter,
        string checkpoint = null, bool warband = false, bool partisanTravel = false, bool coverTravel = false, bool isb = false,
        bool peacefulLay = false)
    {
        if (customLogic != null)
        {
            if (checkpoint == "ISB" && customLogic is
                "ISBSpecialForces.Behavior.Actions.GoToCheckpoint" or
                "ISBSpecialForces.Behavior.Actions.SitAtCheckpoint" or
                "ISBSpecialForces.Behavior.Actions.SwitchCheckpointCover") return true;
            if (isb && customLogic == "ISBSpecialForces.Behavior.BlackDivision.ISBTacticalMove") return true;
            if (checkpoint == "UNTAR" && customLogic is
                "TacticalToasterUNTARGH.Behavior.Actions.GoToCheckpoint" or
                "TacticalToasterUNTARGH.Behavior.Actions.SitAtCheckpoint" or
                "TacticalToasterUNTARGH.Behavior.Actions.SwitchCheckpointCover") return true;
            if (checkpoint == "RUAF" && customLogic is
                "RUAFComeHome.Behavior.Actions.GoToCheckpoint" or
                "RUAFComeHome.Behavior.Actions.SitAtCheckpoint" or
                "RUAFComeHome.Behavior.Actions.SwitchCheckpointCover") return true;
            if (warband && customLogic is
                "RoguesVRaiders.Objective.TravelLogic" or "RoguesVRaiders.Objective.PatrolLogic" or
                "RoguesVRaiders.Objective.LockdownLogic" or "RoguesVRaiders.Objective.HuntLogic") return true;
            return huntAdapter && customLogic is
                "MoreBotsAPI.Behavior.Actions.HuntTargetAction" or
                "MoreBotsAPI.Behavior.Actions.HuntRegroupAction" or
                "MoreBotsAPI.Behavior.Actions.SearchForTargetAction";
        }

        if (customRole && !huntAdapter && checkpoint == null && !isb) return false;
        if (peacefulLay && decision == "lay") return true;
        if (coverTravel && decision is "goToCoverPoint" or "runToCover") return true;
        if (partisanTravel && decision is "goToPointTactical" or "goToCoverPointTactical") return true;
        return decision is "simplePatrol" or "followerPatrol" or "alternativePatrol" or "holdPosition";
    }
}
