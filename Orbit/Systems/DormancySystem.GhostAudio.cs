using Orbit.Api;

namespace Orbit.Systems;

public partial class DormancySystem
{
    private bool GhostShooterCanPlay(string profileId)
    {
        if (string.IsNullOrEmpty(profileId) || !IsDormantProfile(profileId)) return false;
        try { return _gameWorld.GetAlivePlayerByProfileID(profileId)?.HealthController?.IsAlive == true; }
        catch { return false; }
    }

    private static bool HasLivingFightMember(GhostUnit unit)
    {
        foreach (var agent in unit.Agents)
            if (agent?.Player?.HealthController?.IsAlive == true) return true;
        foreach (var bot in unit.VanillaBots)
            if (bot != null && !bot.IsDead && bot.GetPlayer?.HealthController?.IsAlive == true) return true;
        return false;
    }

    private void CancelGhostShooter(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return;
        foreach (var fight in _activeFights)
            if (fight.AudioShooters.Remove(profileId)) StopFightSounds(fight.AudioId, profileId);
    }

    private void StopFightSounds(string fightId, string profileId = null)
    {
        if (string.IsNullOrEmpty(fightId)) return;
        for (var i = _pendingShots.Count - 1; i >= 0; i--)
        {
            var shot = _pendingShots[i];
            if (shot.FightId != fightId || profileId != null && shot.ProfileId != profileId) continue;
            if (shot.IsTail)
                try { shot.LoopSource?.Stop(0.02f); } catch { }
            _pendingShots.RemoveAt(i);
        }
        Log.Debug($"GHOST AUDIO STOP: fight={fightId} shooter={profileId ?? "all"}");
        OrbitEvents.StopGhostFightSounds(fightId, profileId);
    }
}
