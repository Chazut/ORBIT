using System;
using System.Linq;
using EFT.Interactive;
using Orbit;
using Orbit.Entities;
using UnityEngine;
using UnityEngine.AI;
static class RetainedExitRecoveryTests
{
    internal static void Run(Action<bool,string> check)
    {
        foreach(var solo in new[]{true,false})
        foreach(var ghost in new[]{true,false})
        {
            var s=new Scenario();s.Car.Settings.ExfiltrationType=EExfiltrationType.Individual;s.Car.Status=EExfiltrationStatus.RegularMode;
            var bot=s.Add(solo,new(226,0,0),ObjectiveStatus.None);bot.IsDormant=ghost;
            if(solo)s.Squad.ExtractRequested=false;
            s.Squad.Objective.Location=s.Exit;
            NavMesh.Sampler=(p,r)=>new(p.x,0,p.z);NavMesh.Connected=true;NavMesh.PathQueries=0;
            int lastOrders=0;float rescuedAt=-1;bool preserved=true;
            for(int tick=0;tick<650 && bot.Bot.LeaveData.Removals==0;tick++)
            {
                var now=tick*.1f;Time.time=now;
                // The strategy retries Failed at its 0.5s cadence. Goto/action and recovery below
                // run the production approach, stall detection and path reset.
                if(tick%5==0 && bot.Objective.Status==ObjectiveStatus.Failed)bot.Objective.Status=ObjectiveStatus.None;
                s.Tick(now);
                if(s.Movement.Orders!=lastOrders)
                {
                    lastOrders=s.Movement.Orders;
                    if(bot.Player.Teleports==0){bot.Movement.Status=MovementStatus.Failed;bot.Movement.HasPath=false;}
                }
                s.Movement.RescueTick(bot);
                if(bot.Player.Teleports>0)
                {
                    if(rescuedAt<0)rescuedAt=now;
                    s.Movement.Step(bot,.8f);
                }
                preserved &= bot.Objective.Location==s.Exit && !s.Squad.CompletedPoiIds.Contains(s.Exit.Id)
                             && (!solo || bot.SoloExtractTarget==s.Exit);
            }
            check(preserved,"solo/group route stays pinned through fast failures and rescue");
            check(rescuedAt>=25 && rescuedAt<27 && bot.Player.Teleports==1,"one bounded local rescue runs after actual 25s physical stall");
            check(bot.Bot.LeaveData.Removals==1,"bot walks remaining route and extracts at the original exit");
            check(s.Movement.RecoveryPathQueries>0,"rescue verifies an onward path to the retained exit");
            check(Log.Messages.Count(x=>x.Contains("local escape rescue: teleported"))==1,"a single landing emits one rescue notice through repeated approach failures");
            check(bot.IsDormant==ghost,"retained-exit recovery does not trigger wake or sleep");
        }
        {
            var s=new Scenario();s.Car.Settings.ExfiltrationType=EExfiltrationType.Individual;s.Car.Status=EExfiltrationStatus.RegularMode;
            var bot=s.Add(true,new(226,0,0),ObjectiveStatus.None);
            NavMesh.Sampler=(p,r)=>new(p.x,0,p.z);NavMesh.Connected=false;NavMesh.PathQueries=0;
            for(int i=0;i<1200;i++)
            {
                Time.time=i*.1f;
                if(i%5==0 && bot.Objective.Status==ObjectiveStatus.Failed)bot.Objective.Status=ObjectiveStatus.None;
                s.Tick(Time.time);bot.Movement.Status=MovementStatus.Failed;
                s.Movement.RescueTick(bot);
            }
            check(bot.SoloExtractTarget==s.Exit && bot.Objective.Location==s.Exit && !s.Squad.CompletedPoiIds.Contains(s.Exit.Id),"failed local probes never abandon the original available exit");
            check(bot.Player.Teleports==0 && bot.Bot.LeaveData.Removals==0,"no valid local path means no unsafe teleport or distant despawn");
            check(s.Movement.RecoveryPathQueries<=4*(32+40),$"failed local searches remain paced: at most four nearby32 plus wider40 passes, actual={s.Movement.RecoveryPathQueries}");
        }
    }
}
