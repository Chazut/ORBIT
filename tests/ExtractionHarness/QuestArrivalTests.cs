using System;
using System.Collections.Generic;
using Orbit;
using Orbit.Api;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Systems;
using Orbit.Tasks;
using UnityEngine;
using UnityEngine.AI;

static class QuestArrivalTests
{
    internal static void Run(Action<bool,string> check)
    {
        Waypoint Quest(int id=1001,string name="quest_city_trotil1") => new(id,WaypointCategory.Quest,name,new(0,2.404079f,0),1,new(),new(),null);
        MainObjective Main(string name="quest_city_trotil1") => new(){Type=MainObjectiveType.Quest,QuestTriggerId=name};
        // yo-gangy, Woods: a pending quest pulled a solo PMC out of its entered loot area eight times.
        foreach (var kind in new[] { MainObjectiveType.LootValue, MainObjectiveType.Kills })
        {
            var quest = Main("kill_in_forest_woods");
            var area = new MainObjective { Type = kind, LootValueEnteredAt = 106, KillsRoamStartedAt = 106 };
            var mains = new List<MainObjective> { quest, area };
            for (int attempt = 0; attempt < 8; attempt++)
            {
                area.LootValueInterrupted = attempt % 2 == 1;
                check(!quest.CanPursue(mains) && area.CanPursue(mains), "entered area owns dispatch until completion");
            }
            area.AreaReturnDeferred = true;
            check(quest.CanPursue(mains), "unreachable return can yield to a different main");
            area.AreaReturnDeferred = false;
            area.Completed = true;
            check(quest.CanPursue(mains), "quest can start after local work finishes");
        }
        foreach(bool ghost in new[]{false,true})
        foreach(float distance in new[]{.1f,.7f,2f})
        {
            var s=new Scenario();s.Squad.ExtractRequested=false;s.Squad.MainObjectives=new(){Main()};
            var bot=s.Add(false,new(distance,2.404079f,0),ObjectiveStatus.Moving,location:Quest());bot.IsDormant=ghost;
            bot.Movement.Status=MovementStatus.Stopped;bot.Objective.DispatchTime=-3;
            NavMesh.Sampler=(p,r)=>p;NavMesh.Connected=true;
            Physics.Blocked=true; // Old head-to-ground ray hits the quest anchor's floor.
            Physics.LineBlock=(a,b)=>Math.Min(a.y,b.y)<2.5f;
            s.Tick(1);
            check(bot.Objective.Status==ObjectiveStatus.Finished,$"matt ground anchor reached at {distance}m, ghost={ghost}");
            check(!s.Squad.CompletedPoiIds.Contains(1001),"valid arrival does not blacklist quest");
            check(s.Squad.MainObjectives.Count==1 && !s.Squad.MainObjectives[0].Completed,"strategy remains responsible for successful quest completion");
        }
        foreach(string obstruction in new[]{"wall","navmesh","floor"})
        foreach(bool ghost in new[]{false,true})
        {
            var s=new Scenario();s.Squad.ExtractRequested=false;var main=Main();var other=Main("another");
            s.Squad.MainObjectives=new(){main,other};
            var bot=s.Add(false,new(.7f,2.404079f,0),ObjectiveStatus.Moving,location:Quest());bot.IsDormant=ghost;
            bot.Movement.Status=MovementStatus.Moving;
            NavMesh.Sampler=(p,r)=>obstruction=="floor"?p+Vector3.up*3:p;
            NavMesh.Connected=obstruction!="navmesh";Physics.LineBlock=(a,b)=>obstruction=="wall";
            s.Tick(1);
            check(bot.Objective.Status!=ObjectiveStatus.Finished,$"{obstruction} cannot validate quest, ghost={ghost}");
            var rev=OrbitTelemetry.MainObjectivesRevision;s.Tick(11.1f);
            check(s.Squad.CompletedPoiIds.Contains(1001) && bot.Objective.Location==null && s.Squad.Objective.Location==null,"blocked arrival clears both pins and blacklists target");
            check(s.Squad.MainObjectives.Count==1 && s.Squad.MainObjectives[0]==other && !main.Completed,"failed quest loses attraction without marking success or removing other quest");
            check(OrbitTelemetry.MainObjectivesRevision==rev+1 && !s.Squad.ExtractRequested,"retirement refreshes telemetry and retains other main work");
        }
        {
            var s=new Scenario();s.Squad.ExtractRequested=false;var main=Main();s.Squad.MainObjectives=new(){main};
            var bot=s.Add(false,new(2,2.404079f,0),ObjectiveStatus.Moving,location:Quest());bot.Movement.Status=MovementStatus.Stopped;
            NavMesh.Sampler=(p,r)=>p;NavMesh.Connected=false;Physics.LineBlock=(a,b)=>false;
            for(int i=0;i<3;i++){bot.Objective.Status=ObjectiveStatus.Moving;bot.Objective.DispatchTime=-10;s.Tick(i+1);}
            check(bot.Objective.Location==null && s.Squad.MainObjectives.Count==0 && !main.Completed,"repeated nav-snap failures retire inaccessible quest");
            check(s.Squad.ExtractRequested && s.Squad.ExtractRequestedReason=="no reachable mains","last failed main permits extraction instead of endless attraction");
        }
        {
            var s=new Scenario();s.Squad.ExtractRequested=false;s.Squad.MainObjectives=new(){Main()};
            var bot=s.Add(false,new(100,2.404079f,0),ObjectiveStatus.Moving,location:Quest());bot.Movement.Status=MovementStatus.Moving;
            s.Tick(1);s.Tick(32);
            check(s.Squad.MainObjectives.Count==0 && s.Squad.CompletedPoiIds.Contains(1001),"stalled en-route quest also removes attraction");
        }
        foreach(bool enabled in new[]{false,true})
        {
            ServerConfig.MainObjectives.ExtractOnAllCompleted=enabled;
            var s=new Squad{MainObjectives=new(){Main()}};
            QuestObjectiveRecovery.Retire(s,Quest(),"test");
            check(s.ExtractRequested==enabled,"retiring last quest respects configured extraction policy");
            var rev=OrbitTelemetry.MainObjectivesRevision;QuestObjectiveRecovery.Retire(s,Quest(),"test");
            check(OrbitTelemetry.MainObjectivesRevision==rev,"retirement is idempotent");
        }
        ServerConfig.MainObjectives.ExtractOnAllCompleted=true;
        {
            var done=Main();done.Completed=true;var pending=Main("other");
            var s=new Squad{MainObjectives=new(){done,pending}};
            QuestObjectiveRecovery.Retire(s,Quest(),"test");
            check(s.MainObjectives.Count==2,"completed quest history retained");
            QuestObjectiveRecovery.Retire(s,new(50,WaypointCategory.LooseLoot,"other",default,1,new(),new(),null),"test");
            check(s.MainObjectives.Count==2,"failed loot never retires quest with same name");
        }
        {
            var arrival=new QuestArrival();Time.time=100;NavMesh.Sampler=(p,r)=>p;NavMesh.Connected=true;Physics.LineBlock=(a,b)=>false;
            Physics.QuestQueries=0;
            check(arrival.Check(default,new(1,0,0),out _),"clear short route arrives");
            check(!arrival.Check(new(float.NaN,0,0),new(1,0,0),out _),"invalid position cannot reuse a successful cached arrival");
            for(int i=0;i<1000;i++)arrival.Check(default,new(1,0,0),out _);
            check(Physics.QuestQueries==1,"stationary arrival geometry cached across 1000 ticks");
            Physics.LineBlock=(a,b)=>true;Time.time+=.26f;
            check(!arrival.Check(default,new(1,0,0),out var reason)&&reason=="quest-geometry","closed door invalidates cached arrival after 250ms");
            Physics.LineBlock=(a,b)=>false;
            check(arrival.Check(new(.3f,0,0),new(1,0,0),out _),"movement invalidates cached rejection immediately");
            NavMesh.Sampler=(p,r)=>null;Time.time+=.26f;
            check(!arrival.Check(default,new(1,0,0),out reason)&&reason=="quest-navmesh","off-mesh endpoints cannot validate arrival");
            check(!arrival.Check(new(float.NaN,0,0),default,out _),"invalid position rejected");
        }
        {
            var arrival=new QuestArrival();
            NavMesh.Sampler=(p,r)=>new(.1f,p.y,p.z);
            Physics.LineBlock=(a,b)=>a.x<0 && b.x>0;
            check(!arrival.Check(new(-.1f,0,0),new(.1f,0,0),out var reason) && reason=="quest-geometry",
                "NavMesh projection across a thin wall cannot validate arrival");
        }
        Physics.LineBlock=null;Physics.Blocked=false;NavMesh.Connected=true;NavMesh.Sampler=null;
    }
}
