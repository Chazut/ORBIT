using System;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Settings;
using Orbit.Systems;
using Orbit.Tasks.Actions;
using UnityEngine;
using UnityEngine.AI;

internal static class CampWatchTests
{
    internal static void Run(Action<bool, string> check)
    {
        var savedSampler = NavMesh.Sampler;
        var savedLine = Physics.LineBlock;
        var savedTime = Time.time;
        try
        {
            foreach (CampSiteKind kind in Enum.GetValues<CampSiteKind>())
            {
                var bot = new Agent { IsDormant = true };
                var post = new Waypoint(1, WaypointCategory.Synthetic, "ambush", default, 2f, new(), new(), null);
                bot.Squad.Camp.Enabled = true;
                bot.Squad.Camp.Site.Kind = kind;
                bot.Squad.Camp.Site.Position = new Vector3(35, 0, 0);
                bot.Objective.Location = post;
                bot.Objective.Status = ObjectiveStatus.Finished;
                bot.Guard.CoverPoint = new CoverPoint(default, Vector3.up, CoverCategory.Hard, CoverLevel.Stay);
                bot.Guard.Status = GuardStatus.Moving;
                bot.Movement.Status = MovementStatus.Stopped;
                using var guard = new GuardAction(new AgentData(), new MovementSystem(), 0f);
                guard.Activate(bot);

                int rays = 0;
                Physics.LineBlock = (_, _) => { rays++; return false; };
                guard.Update();
                check(bot.Movement.Pose == 0 && bot.Guard.Status == GuardStatus.Watch && rays == 0,
                    $"{kind}: a camper arriving in Ghost crouches without physics work");
                bot.IsDormant = false;
                foreach (bool looting in new[]{false, true})
                {
                    bot.Movement.Pose = 1f;
                    bot.Squad.Camp.Looter = looting ? bot : null;
                    GuardAction.RestoreCampOnWake(bot);
                    check(bot.Movement.Pose == (looting ? 1 : 0), $"{kind}: crouch belongs to the camper only");
                }
                bot.Squad.Camp.Looter = null;
                bot.Movement.Status = MovementStatus.Moving;
                GuardAction.RestoreCampOnWake(bot);
                check(bot.Movement.Pose == 1, $"{kind}: a bot still travelling keeps its gait");
                bot.Movement.Status = MovementStatus.Stopped;
                bot.Squad.Camp.Enabled = false;
                GuardAction.RestoreCampOnWake(bot);
                check(bot.Movement.Pose == 1, $"{kind}: released slot keeps its gait");
                if (kind != CampSiteKind.Extract) continue;

                bot.Squad.Camp.Enabled = true;
                bot.Player.PlayerBones.Head.position = Vector3.up;
                NavMesh.Sampler = (p, _) => p;
                Physics.LineBlock = (eye, target) => { rays++; return target.x > 1; };
                GuardAction.RestoreCampOnWake(bot);
                for (var step = 0; step < ExtractAmbushWatch.WatchProbeCount && bot.Look.Target == null; step++)
                {
                    var before = rays;
                    guard.Update();
                    check(rays - before == 1, "one line of sight per guard tick");
                }
                check(bot.Look.Target is Vector3 lane && Math.Abs(lane.x) < .1f && Math.Abs(lane.z) >= 4,
                    "Vaught watches a navigable lane alongside the wall");
                var queries = rays;
                guard.Update();
                check(queries == rays, "holding an angle makes no probes before the next watch interval");

                Time.time = bot.Guard.WatchTimeout + 1;
                Physics.LineBlock = (_, _) => true;
                for (var i = 0; i < ExtractAmbushWatch.WatchProbeCount; i++) guard.Update();
                check(bot.Objective.Status == ObjectiveStatus.Failed && bot.Look.Target == null,
                    "a newly enclosed post returns to formation planning");

                Physics.LineBlock = (_, _) => false;
                NavMesh.Sampler = (p, _) => null;
                check(!ExtractAmbushWatch.TryWatchApproach(default,Vector3.up,bot.Squad.Camp.Site.Position,0,out _),
                    "an empty line above unwalkable ground is not an approach");
                NavMesh.Sampler = (p, _) => p;
                check(ExtractAmbushWatch.TryWatchApproach(default,Vector3.up,bot.Squad.Camp.Site.Position,0,out var clear)
                    && clear.x == 8 && clear.z == 0, "a clear lane towards the exit stays preferred");
            }
        }
        finally
        {
            NavMesh.Sampler = savedSampler;
            Physics.LineBlock = savedLine;
            Time.time = savedTime;
        }
    }
}
