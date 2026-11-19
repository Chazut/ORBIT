using System.Globalization;
using Orbit;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Sain;
using Orbit.Systems;
using SAIN;
using SAIN.Components;
using UnityEngine;
using UnityEngine.AI;

internal static class Program
{
    private static int _checks;
    private static readonly Vector3 Origin = new(-18, 0, 0);
    private static readonly Vector3 Target = new(50, 0, 0);

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        _checks++;
    }

    private static void Main()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        HandoffsAndDiagnostics();
        EpisodeBoundaries();
        RouteGeometry();
        SchedulingAndFallbacks();
        Console.WriteLine($"{_checks} grenade interruption and navigation checks passed");
    }

    private static void HandoffsAndDiagnostics()
    {
        var agent = new Agent();
        var bot = new BotComponent();
        var projectile = new EFT.Grenade(42) { transform = new(new(246, 8, -532)) };
        var tracker = new Tracker { Grenade = projectile, DangerPoint = new(244, 8, -535), TimeThrown = 900f };
        bot.Grenade.GrenadeReactionClass.DangerGrenade = tracker;
        SAINEnableClass.Bots[agent.Bot.ProfileId] = bot;
        const string avoid = "SAIN : Avoid Threat";
        GrenadeThreatReader.OnHandoff(agent, avoid);
        Check(agent.GrenadeInterruptions == null, "DogFight shares the layer but is not a grenade");
        bot.Decision.CurrentCombatDecision = CombatDecision.AvoidGrenade;
        GrenadeThreatReader.OnHandoff(agent, "SAIN : Combat Layer");
        agent.IsDormant = true;
        GrenadeThreatReader.OnHandoff(agent, avoid);
        agent.IsDormant = false;
        agent.Movement.Status = MovementStatus.Stopped;
        GrenadeThreatReader.OnHandoff(agent, avoid);
        agent.Movement.Status = MovementStatus.Moving;
        bot.Grenade.GrenadeReactionClass.DangerGrenade = null;
        GrenadeThreatReader.OnHandoff(agent, avoid);
        bot.Grenade.GrenadeReactionClass.DangerGrenade = tracker;
        var dangerPoint = tracker.DangerPoint;
        tracker.DangerPoint = new(float.NaN, 0, 0);
        GrenadeThreatReader.OnHandoff(agent, avoid);
        Check(agent.GrenadeInterruptions == null, "malformed hazard coordinates never arm a route");
        tracker.DangerPoint = dangerPoint;
        Check(agent.GrenadeInterruptions == null, "unrelated layers, ghosts, stopped orders and missing trackers are ignored");

        // Woods #480: recorded traveller positions, timestamps and original destination.
        // The old replay did not record the hazard, so its ID and position here are synthetic.
        var events = new (float time, Vector3 pos)[] {
            (929.929f, new(243.33f, 7.58f, -518.26f)), (932.928f, new(242.74f, 7.39f, -518.18f)),
            (935.112f, new(244.02f, 7.75f, -517.65f)), (937.240f, new(242.70f, 7.41f, -518.03f))
        };
        for (var i = 0; i < events.Length; i++)
        {
            Time.time = events[i].time;
            agent.Position = events[i].pos;
            // Replacing a projectile at the same spot must not reset a real travel loop.
            if (i == 1) tracker.Grenade = new EFT.Grenade(43) { transform = projectile.transform };
            GrenadeThreatReader.OnHandoff(agent, avoid);
            Check(agent.GrenadeInterruptions.Armed(Time.time) == (i >= 2), "arm on third genuine handoff");
        }
        Check(Log.Lines.Count == 1 && Log.Lines[0].Contains("projectile=43 type=smoke_grenade")
            && Log.Lines[0].Contains("template=test-template age=35.1s grenade=(246,8,-532) danger=(244,8,-535)"),
            "one diagnostic with grenade type, age, actual and predicted positions");
        var movement = new MovementSystem();
        var destination = new Vector3(244, 8.48f, -571.25f);
        var job = new NavJob(agent.Position, destination);
        Check(Finish(movement, agent, job), "Woods approach can bypass a persistent hazard");
        Check(job.Path[^1].Equals(destination) && GrenadeDetourPlan.Clears(agent.Position, job.Path, tracker.DangerPoint),
            "arrive at the original Woods objective without cutting back through the danger");
        var quiet = new Agent();
        var logged = Log.Lines.Count;
        Log.InfoEnabled = false;
        for (var i = 0; i < 3; i++) GrenadeThreatReader.OnHandoff(quiet, avoid);
        Check(quiet.GrenadeInterruptions.Armed(Time.time) && Log.Lines.Count == logged, "quiet logging still tracks the hazard");
        Log.InfoEnabled = true;
    }

    private static void EpisodeBoundaries()
    {
        var memory = new GrenadeInterruptions();
        memory.Observe(default, Origin, 0);
        memory.Observe(default, Origin, 2);
        Check(!memory.Observe(new(20, 0, 0), Origin, 3), "distinct danger locations never combine");
        Check(!memory.Observe(default, Origin, 14), "a long interval restarts the count");
        memory.Observe(default, Origin, 15);
        Check(!memory.Observe(new(0, 5, 0), Origin, 16), "a changed hazard restarts the count");
        memory.Observe(new(0, 5, 0), Origin, 17);
        Check(!memory.Observe(new(0, 5, 0), Origin + new Vector3(0, 7, 0), 18), "another floor is another spot");
        Check(!memory.Armed(18), "ordinary movement cannot inherit a distant episode");
        var gap = new GrenadeInterruptions();
        gap.Observe(default, Origin, 0);
        gap.Observe(default, Origin, 2);
        Check(!gap.Observe(default, Origin, 13) && !gap.Armed(13), "long gap at exactly the same place");
        var other = ArmedAgent();
        Check(other.GrenadeInterruptions.Armed(22) && !other.GrenadeInterruptions.Armed(22.01f),
            "memory ends twenty seconds after last interruption");
        Check(!memory.Armed(18), "travellers have independent memory");
        var creeping = new GrenadeInterruptions();
        creeping.Observe(default, Origin, 1);
        creeping.Observe(new(3, 0, 0), Origin, 2);
        Check(!creeping.Observe(new(6, 0, 0), Origin, 3), "a moving hazard cannot drift from the first position");
    }

    private static void RouteGeometry()
    {
        var far = new Vector3(-40, 0, 0);
        Check(!GrenadeDetourPlan.Clears(far, [far, Target], default), "check segments, not just endpoints");
        Check(!GrenadeDetourPlan.Clears(Origin, [new(-12, 0, 0), far], default), "no inward step during an escape");
        Check(GrenadeDetourPlan.Clears(Origin, [Origin, far], default), "allow an outward escape from just inside");
        Check(GrenadeDetourPlan.Clears(new(-40, 25, 0), [new(40, 25, 0)], default), "a route on a clear upper floor can proceed");
        var moving = new MovementSystem();
        var agent = ArmedAgent();
        var clear = new NavJob(Origin, far);
        var old = clear.Path;
        Check(moving.Resolve(agent, clear) && ReferenceEquals(old, clear.Path) && clear.Travel != null
            && moving._travelWorld.Queries == 0, "safe baseline keeps its route and travel style");
        var stairs = ArmedAgent();
        stairs.Position = new(-30, .7f, 0);
        var onMesh = new Vector3(-30, 0, 0);
        var stairJob = new NavJob(onMesh, Target);
        moving._travelWorld.CompletePath = (from, to) => Math.Abs(from.y) <= .3f ? [from, to] : null;
        Check(Finish(moving, stairs, stairJob) && stairJob.Path[0].Equals(onMesh), "reuse the mesh start under the traveller");
        var crossings = new NavJob(Origin, Target);
        moving._travelWorld.CompletePath = (a, b) => b.z > 0 ? [a, default, b] : [a, b];
        Check(Finish(moving, agent, crossings) && crossings.Path.Any(p => p.z < 0) && crossings.Travel == null,
            "unsafe curved leg retries on the other side");
        Check(crossings.Path.Length == 4 && crossings.Path[0].Equals(Origin) && crossings.Path[^1].Equals(Target),
            "one continuous route without duplicate pivots");
    }

    private static void SchedulingAndFallbacks()
    {
        var movement = new MovementSystem();
        var one = ArmedAgent();
        var two = ArmedAgent();
        var a = new NavJob(Origin, Target);
        var b = new NavJob(Origin, Target);
        Check(!movement.Resolve(one, a) && !movement.Resolve(two, b) && movement._travelWorld.Queries == 1,
            "all bots share one extra path query per frame");
        Check(one.GrenadeInterruptions.Waiting(1, Time.time) && !one.GrenadeInterruptions.Waiting(2, Time.time),
            "only the waiting order suspends rescues");
        var newer = new NavJob(Origin, new(-60, 0, 0));
        Check(movement.Resolve(one, newer, 2) && newer.Travel != null, "obsolete detour never replaces a newer safe order");
        Check(Finish(movement, two, b), "deferred second bot also completes");
        var blocked = new NavJob(Origin, Target);
        var waiting = ArmedAgent();
        movement._travelWorld.CompletePath = (_, _) => null; // Incomplete native paths have no usable corners.
        var before = movement._travelWorld.Queries;
        Check(!Finish(movement, waiting, blocked) && movement._travelWorld.Queries - before == 4,
            "unreachable pivots stop after both sides and both radii");
        before = movement._travelWorld.Queries;
        Check(!movement.Resolve(waiting, blocked) && movement._travelWorld.Queries == before, "no per-frame retry once exhausted");
        Time.time = 23;
        Check(movement.Resolve(waiting, blocked) && blocked.Travel != null && waiting.GrenadeInterruptions.Plan == null,
            "expired memory releases the same pending order");
        var inside = new NavJob(Origin, new(1, 0, 0));
        var traveller = ArmedAgent();
        Check(!movement.Resolve(traveller, inside) && movement._travelWorld.Queries == before, "a target inside the danger waits with zero probes");
        traveller.IsDormant = true;
        Check(movement.Resolve(traveller, inside), "ghost movement is independent");
        traveller.IsDormant = false;
        traveller.Squad = new Squad();
        Check(movement.Resolve(traveller, inside), "preserve constrained sniper-post movement");
        var invalid = new NavJob(Origin, Target) { Status = NavMeshPathStatus.PathInvalid };
        Check(movement.Resolve(ArmedAgent(), invalid), "native invalid-path handling is preserved");
        movement._travelWorld.CompletePath = (x, y) => [x, y];
        DangerZones.IsInside = p => p.z > 0;
        DirectionalMineSafety.Block = corners => corners[^1].z < 0;
        var mines = new NavJob(Origin, Target);
        Check(!Finish(movement, ArmedAgent(), mines) && mines.Travel != null, "danger-zone pivots and mined legs are never installed");
    }

    private static Agent ArmedAgent()
    {
        var agent = new Agent { Position = Origin, GrenadeInterruptions = new() };
        for (var i = 0; i < 3; i++) agent.GrenadeInterruptions.Observe(default, Origin, i);
        Time.time = 2;
        return agent;
    }

    private static bool Finish(MovementSystem movement, Agent agent, NavJob job)
    {
        for (var i = 0; i < 16; i++)
        {
            Time.frameCount++;
            if (movement.Resolve(agent, job)) return true;
        }
        return false;
    }
}
