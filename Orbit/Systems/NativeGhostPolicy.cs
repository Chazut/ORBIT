namespace Orbit.Systems;

internal static class NativeGhostPolicy
{
    public static bool Supports(string decision, string customLogic, bool customRole, bool huntAdapter)
    {
        if (customRole && !huntAdapter) return false;
        if (customLogic != null)
            return huntAdapter && customLogic is
                "MoreBotsAPI.Behavior.Actions.HuntTargetAction" or
                "MoreBotsAPI.Behavior.Actions.HuntRegroupAction" or
                "MoreBotsAPI.Behavior.Actions.SearchForTargetAction";

        return decision is "simplePatrol" or "followerPatrol" or "alternativePatrol" or "holdPosition";
    }
}
