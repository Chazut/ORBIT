using System;
using System.Reflection;
using System.Linq;
using Orbit;
using EFT.Interactive;
using Orbit.Entities;
using UnityEngine;
static class FailedExitTests
{
    internal static void Run(Action<bool,string> check)
    {
        var fail=typeof(Orbit.Tasks.Actions.GotoObjectiveAction).GetMethod("TrackArrivalFailure",BindingFlags.NonPublic|BindingFlags.Instance);
        foreach(var solo in new[]{true,false})
        {
            var s=new Scenario();s.Car.Settings.ExfiltrationType=EExfiltrationType.Individual;s.Car.Status=EExfiltrationStatus.RegularMode;
            var bot=s.Add(solo,new(226,0,0),ObjectiveStatus.Moving);
            if(solo){bot.SoloExtractReason="loot";s.Squad.ExtractRequested=false;}
            s.Squad.Objective.Location=s.Exit;
            for(int i=0;i<6;i++)fail.Invoke(s.Goto,new object[]{bot,s.Exit});
            check(Log.Messages.Count(x => x.Contains("awaiting local unsticking")) == 1, "repeated arrival failures emit a single recovery notice");
            check(!solo || bot.SoloExtractTarget==s.Exit,"blocked solo bot retains the same available foot exit");
            check(s.Squad.NearestExfilCached==s.Exit,"local blockage does not invalidate the selected exit cache");
            check(!s.Squad.CompletedPoiIds.Contains(s.Exit.Id) && s.Squad.Objective.Location==s.Exit && bot.Objective.Location==s.Exit,"no blacklist or target release for a valid foot exit");
            check(bot.Objective.Status==ObjectiveStatus.None,"same route is rearmed while physical-stall recovery accumulates");
            check((solo?bot.SoloExtractRequested:s.Squad.ExtractRequested) && bot.Bot.LeaveData.Removals==0,"extraction intent retained without mid-map despawn");
            if(solo)check(bot.SoloExtractReason=="loot","solo departure reason retained");
        }
        var progress=new Scenario();progress.Car.Settings.ExfiltrationType=EExfiltrationType.Individual;
        var p=progress.Add(true,new(500,0,0),ObjectiveStatus.Moving);
        foreach(var distance in new[]{500,400,250}){p.Position=new(distance,0,0);fail.Invoke(progress.Goto,new object[]{p,progress.Exit});}
        check(!progress.Squad.CompletedPoiIds.Contains(progress.Exit.Id) && p.SoloExtractTarget==progress.Exit,"successive partial paths with progress retain exit");
        foreach(var unavailable in new[]{EExfiltrationStatus.NotPresent,EExfiltrationStatus.Hidden})
        {
            var s=new Scenario();s.Car.Settings.ExfiltrationType=EExfiltrationType.Individual;s.Car.Status=unavailable;
            var bot=s.Add(true,new(226,0,0),ObjectiveStatus.Moving);
            for(int i=0;i<3;i++)fail.Invoke(s.Goto,new object[]{bot,s.Exit});
            check(bot.SoloExtractTarget==null && s.Squad.NearestExfilCached==null && s.Squad.CompletedPoiIds.Contains(s.Exit.Id),"genuinely unavailable exit still clears stale pins and cache");
        }
    }
}
