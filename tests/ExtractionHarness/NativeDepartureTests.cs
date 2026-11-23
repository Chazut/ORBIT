using System;
using EFT.Interactive;
using Orbit.Entities;
using UnityEngine;

static class NativeDepartureTests
{
    public static void Run(Action<bool, string> check)
    {
        // Extract--Rat: native raid time can reach departure before Unity time.
        foreach (var drift in new[] { 0.005f, 0.05f, 0.25f, 5f, 55f })
        foreach (var partySize in new[] { 0, 1, 3 })
        {
            var scenario = new Scenario();
            var members = new Agent[Math.Max(1, partySize)];
            for (var i = 0; i < members.Length; i++) members[i] = scenario.Add(partySize == 0);
            Time.deltaTime = 0.02f;
            scenario.Tick(0.02f);
            Time.time = 60f - drift;
            Time.deltaTime = 0.005f;
            scenario.Car.SetStatusLogged(EExfiltrationStatus.NotPresent, "native car departure");
            scenario.Tick(60.005f - drift);
            foreach (var member in members)
                check(member.Bot.LeaveData.Removals == 1,
                    $"present participant extracts at native departure with {drift}s clock difference, party={partySize}");
            scenario.Tick(61f);
            foreach (var member in members)
                check(member.Bot.LeaveData.Removals == 1, "native departure cannot despawn a participant twice");
        }

        // A departing car cannot wait for the rest of a gathering squad.
        {
            var scenario = new Scenario();
            var present = scenario.Add(false);
            var absent = scenario.Add(false, new(50, 0, 0), active: false);
            scenario.Tick(0);
            Time.time = 30;
            scenario.Car.SetStatusLogged(EExfiltrationStatus.NotPresent, "native departure during gathering");
            scenario.Tick(30.01f);
            check(present.Bot.LeaveData.Removals == 1, "present waiting member leaves with car during gathering");
            absent.IsActive = true;
            scenario.Tick(30.02f);
            check(absent.Bot.LeaveData.Removals == 0 && absent.Objective.Location == null,
                "absent gathering member selects another exit");
        }

        foreach (var cancelledStatus in new[] { EExfiltrationStatus.UncompleteRequirements, EExfiltrationStatus.Hidden, EExfiltrationStatus.AwaitsManualActivation })
        {
            var scenario = new Scenario();
            var member = scenario.Add(true);
            scenario.Tick(0);
            Time.time = 10;
            scenario.Car.SetStatusLogged(cancelledStatus, "countdown cancelled");
            scenario.Car.SetStatusLogged(EExfiltrationStatus.NotPresent, "unavailable without departure");
            scenario.Tick(10.01f);
            check(member.Bot.LeaveData.Removals == 0 && member.Objective.Location == null,
                "unavailability after cancellation is not a car departure");
        }

        {
            var scenario = new Scenario();
            scenario.Car.SetStatusLogged(EExfiltrationStatus.NotPresent, "car already left");
            var late = scenario.Add(true);
            scenario.Tick(1);
            check(late.Bot.LeaveData.Removals == 0 && late.Objective.Location == null,
                "new participant arriving after departure cannot extract");
        }

        foreach (var changedTarget in new[] { false, true })
        {
            var scenario = new Scenario();
            var member = scenario.Add(true);
            scenario.Tick(0);
            if (changedTarget) member.Objective.Location = Scenario.NewExit(Scenario.NewCar(), 200);
            else member.Objective.Status = ObjectiveStatus.Looting;
            Time.time = 10;
            scenario.Car.SetStatusLogged(EExfiltrationStatus.NotPresent, "native departure");
            scenario.Tick(10.01f);
            check(member.Bot.LeaveData.Removals == 0, "bot no longer waiting for this car cannot extract with it");
        }
    }
}
