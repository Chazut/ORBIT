using System;
using Orbit.Navigation;
using UnityEngine;

internal static class HermeticLeverTests
{
    internal static void Run(Action<bool, string> check)
    {
        var recovery = new HermeticSwitchRecovery();
        var actor = new object();
        var lever = new Vector3(-60.76f, -5.55f, 78.23f);
        var standing = new Vector3(-61.33f, -6.98f, 77.60f);
        var outside = new Vector3(-62.50f, -7.03f, 75.44f);
        var detour = new Vector3(-43.75f, -6.94f, 81.11f);
        bool Ready(Vector3 feet, float time, bool recorded, string op = "hermetic", string id = "autoId_00632_EXFIL")
            => recovery.Ready(actor, op, id, feet, lever, standing, time, recorded);

        check(!Ready(outside, 0, false), "ordinary approach tries the hut entrance first");
        check(!Ready(detour, 8, true), "recorded detour cannot activate the lever from afar");
        check(!Ready(outside, 14, true), "the recorded leg still allows time for normal entry");
        check(!Ready(detour, 24, true), "expired grace alone cannot operate the lever from a distance");
        check(Ready(outside, 28, true), "Olsenchiks can reach through the hut wall on the recorded return leg");
        check(!Ready(new Vector3(outside.x, -10, outside.z), 29, true), "cannot activate from the floor below");
        check(!Ready(outside, 30, true, "d2") && !Ready(outside, 30, true, id: "other"), "recorded rescue is exclusive to this lever");
        check(!Ready(outside, 31, false) && Ready(outside, 51, false), "a stationary local operator still has the existing timed rescue");
    }
}
