using EFT.Interactive;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Systems;
using UnityEngine;

namespace Orbit.Looting;

internal struct CorpseLootFacing
{
    private Vector3? _direction;

    internal void Hold(Agent agent, InteractableObject corpse)
    {
        if (!_direction.HasValue)
        {
            // EFT's LookToPoint aims from the weapon root. When prone, that pivot can
            // swing past a nearby body and reverse the requested yaw on every turn.
            var heading = LootVisibility.BodyPosition(corpse) - agent.Position;
            heading.y = 0f;
            if (heading.sqrMagnitude < .1f * .1f)
            {
                heading = agent.Player.Transform.forward;
                heading.y = 0f;
            }
            heading = heading.sqrMagnitude > .0001f ? heading.normalized : new Vector3(0f, 0f, 1f);
            heading.y = -.35f;
            _direction = heading.normalized;
        }
        LookSystem.LookToDirection(agent, _direction.Value);
    }

    internal void Release(Agent agent)
    {
        if (_direction.HasValue && agent.Look.Type == LookType.Direction
            && agent.Look.Target is Vector3 target && target.Equals(_direction.Value))
            agent.Look.Target = null;
        _direction = null;
    }
}
