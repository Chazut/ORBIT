namespace Orbit.Helpers;

internal static class IsbRolePolicy
{
    // Sam's White Tusk roster, including the two original commanders.
    internal static bool IsWhiteTusk(int role)
        => role is 13702 or 13703 or 13704 or 13705 or 13706 or 13707 or 13708 or 13715 or 13716;
}
