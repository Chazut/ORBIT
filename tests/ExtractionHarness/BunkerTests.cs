using System;
using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using UnityEngine;
using UnityEngine.AI;

static class BunkerTests
{
    public static void Run(Action<bool,string> check)
    {
        foreach (var ghost in new[] { false, true })
            KeyedBunkerExtraction(check, ghost);

        // These generic individual exits retain the normal local fallback.
        Scenario Bunker()
        {
            var scenario = new Scenario();
            scenario.Car.Settings.ExfiltrationType = EExfiltrationType.Individual;
            scenario.Car.Status = EExfiltrationStatus.RegularMode;
            return scenario;
        }
        {
            var s = Bunker(); var bot = s.Add(false,new(0,7,0),ObjectiveStatus.None);
            s.Exit.ExfilInteriorPosition=new(0,-1,0);
            s.Tick(0); s.Tick(.2f); s.Tick(15.3f); s.Tick(15.5f); s.Tick(20.6f);
            check(s.Movement.Destination.Equals(new Vector3(0,-1,0)),"Goto aims at interior target while retaining original approach anchor");
            check(bot.Bot.LeaveData.Removals == 1,"unreachable basement still despawns from surface");
            check(bot.Position.y == 7 && !s.Squad.CompletedPoiIds.Contains(s.Exit.Id),"surface fallback never blacklists or redirects the exit");
        }
        {
            var s = Bunker(); var bot = s.Add(false,new(0,7,0),ObjectiveStatus.None);
            s.Tick(0); s.Tick(.2f);
            for (int i=1;i<=5;i++)
            {
                bot.Position = new(i%2==0 ? 0 : 2,7-i*.7f,0);
                s.Tick(i*10);
                check(bot.Bot.LeaveData.Removals==0 && bot.Objective.Status==ObjectiveStatus.Moving,"active descent may last over 15 seconds");
            }
            bot.Position=default;s.Tick(51);s.Tick(51.2f);s.Tick(56.3f);
            check(bot.Bot.LeaveData.Removals==1,"normal extraction completes after reaching basement");
            check(!s.Squad.CompletedPoiIds.Contains(s.Exit.Id),"descent keeps the same exit");
        }
        {
            var s = Bunker(); var bot=s.Add(false,new(0,7,0),ObjectiveStatus.None);
            s.Tick(0);s.Tick(.2f);bot.Position=new(.1f,7,0);s.Tick(16);s.Tick(16.2f);s.Tick(22);
            check(bot.Bot.LeaveData.Removals==1,"tiny position jitter cannot postpone the local fallback forever");
        }
        {
            var s=Bunker();var bot=s.Add(false,new(0,7,0),ObjectiveStatus.None);
            s.Exit.ExfilInteriorPosition=new(0,-10,0);
            var fail=typeof(Orbit.Tasks.Actions.GotoObjectiveAction).GetMethod("TrackArrivalFailure",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            for(int i=0;i<3;i++)fail.Invoke(s.Goto,new object[]{bot,s.Exit});
            check(bot.Objective.Status==ObjectiveStatus.Extracting && bot.Objective.Location==s.Exit,"three failed foot-exit arrivals keep local extraction fallback");
        }
        {
            var exfil=Scenario.NewCar();var collider=exfil.GetComponent<Collider>();
            collider.bounds=new(){Spatial=true,min=new(-5,-4,-5),max=new(5,-1,5)};
            NavMesh.Queries=0;NavMesh.Sampler=(p,r)=>{ check(r==1,"interior probes use bounded one metre radius");return new(p.x,-3.7f,p.z); };
            check(ExfilNavigation.TryInteriorPoint(exfil,out var target) && target.y==-3.7f && NavMesh.Queries==1,"lower interior surface selected before roof");
            NavMesh.Queries=0;NavMesh.Sampler=(p,r)=>new(p.x,4,p.z);
            check(!ExfilNavigation.TryInteriorPoint(exfil,out _) && NavMesh.Queries==27,"all roof samples rejected with bounded work");
            NavMesh.Queries=0;NavMesh.Sampler=(p,r)=>NavMesh.Queries<3?new(p.x,4,p.z):new(p.x,-3.7f,p.z);
            check(ExfilNavigation.TryInteriorPoint(exfil,out target) && target.y<0 && NavMesh.Queries==3,"scan continues past invalid roof samples");
            collider.Closest=p=>p+Vector3.one;
            check(!ExfilNavigation.TryInteriorPoint(exfil,out _),"bounds overlap alone cannot accept point outside actual trigger shape");
            collider.Closest=null;NavMesh.Sampler=(p,r)=>null;
            check(!ExfilNavigation.TryInteriorPoint(exfil,out _),"no interior navmesh retains caller's approach fallback");
            NavMesh.Queries=0;
            check(!ExfilNavigation.TryInteriorPoint(Scenario.NewCar(false),out _) && NavMesh.Queries==0,"missing trigger makes no navmesh queries");
            collider.bounds=default;
            check(!ExfilNavigation.TryInteriorPoint(exfil,out _) && NavMesh.Queries==0,"disabled empty trigger uses existing fallback");
        }
    }

    private static void KeyedBunkerExtraction(Action<bool, string> check, bool ghost)
    {
        var scenario = new Scenario();
        var exit = scenario.Exit;
        scenario.Car.Settings.Name = "ZB-014";
        scenario.Car.Settings.ExfiltrationType = EExfiltrationType.Individual;
        scenario.Car.Settings.ExfiltrationTime = 7;
        scenario.Car.Status = EExfiltrationStatus.RegularMode;
        var gate = new Door { DoorState = EDoorState.Locked };
        gate.transform.position = new(-6, 0, 0);
        exit.ExfilGate = gate;
        exit.ExfilInteriorPosition = default(Vector3);
        var bot = scenario.Add(true, new(-12, 0, 0), ObjectiveStatus.None);
        bot.IsDormant = ghost;
        check(WoodsBunkerAccess.Choose(scenario.Squad, bot, exit, true), "fixture selects the keyed exit");
        NavMesh.Sampler = (position, _) => position;
        NavMesh.Connected = true;

        for (var second = 0; second <= 17; second++)
            scenario.Tick(second);
        var handle = new Vector3(-7, 0, 0);
        check(bot.Objective.Location == exit && scenario.Movement.Destination.Equals(handle)
              && gate.Unlocks == 0 && bot.Bot.LeaveData.Removals == 0,
              "the PMC keeps its approach to the outside handle beyond the generic 15s timeout");

        bot.Position = handle;
        scenario.Tick(18);
        check(gate.DoorState == EDoorState.Shut && gate.Unlocks == 1, "the approaching PMC unlocks the gate");
        scenario.Tick(19);
        scenario.Tick(20);
        check(gate.Opens == 1 && scenario.Movement.Destination.Equals(exit.ExfilInteriorPosition.Value),
              "the PMC opens the gate and receives an order into the exit room");
        scenario.Tick(28);
        check(bot.Bot.LeaveData.Removals == 0 && bot.Objective.Status == ObjectiveStatus.Moving,
              "opening the door cannot start extraction from the outside");

        bot.Position = exit.ExfilInteriorPosition.Value;
        scenario.Tick(29);
        scenario.Tick(30);
        scenario.Tick(36.9f);
        check(bot.Objective.Status == ObjectiveStatus.Extracting && bot.Bot.LeaveData.Removals == 0,
              "the full seven-second countdown runs inside the room");
        scenario.Tick(37);
        scenario.Tick(38);
        check(bot.Bot.LeaveData.Removals == 1, "the task manager completes extraction exactly once");
    }
}
