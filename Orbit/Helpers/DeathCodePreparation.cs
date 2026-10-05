using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;

namespace Orbit.Helpers;

internal static class DeathCodePreparation
{
    private const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly HashSet<Type> PlayerTypes = new();
    private static readonly FieldInfo[] DeathEvents = FindDeathEvents();
    private static readonly string[] PlayerMethods = { "OnDead", "CreateCorpse", "ApplyDamageInfo", "ManageAggressor",
        "OnBeenKilledByAggressor", "ApplyCorpseImpulse", "PlayDeathSound", "OnAudioHealthDied", "OnPlayerVisualDied" };
    private static readonly (string Type, string[] Methods)[] OptionalRoots =
    {
        ("SAIN.Components.PlayerComponentSpace.PlayerSpawnTracker", new[] { "RemovePerson", "TryRemove" }),
        ("SAIN.Components.PlayerComponentSpace.PlayerComponent", new[] { "Dispose" }),
        ("SAIN.BotController.Classes.Squad", new[] { "memberWasKilled" }),
        ("SAIN.SAINComponent.Classes.EnemyClasses.EnemyControllerEvents", new[] { "enemyKilled" }),
        ("SAIN.SAINEnableClass", new[] { "ClearBot" }),
        ("Audio.SpatialSystem.SpatialAudioSystem", new[] { "OnPlayerDeadOrUnspawn" })
    };

    internal static void Prepare(IEnumerable<Player> players)
    {
        // Called only during BotsController.Init. Optional bindings use exact names and cached misses.
        try
        {
            IncludePlayerType(typeof(Player));
            IncludePlayerType(typeof(LocalPlayer));
            IncludeNamed(typeof(BotOwner), "OnDied");
            IncludeNamed(typeof(Brain.OrbitBrainLayer), "OnDead");
            foreach (var entry in OptionalRoots)
            {
                try { IncludeNamed(OptionalModTypes.Find(entry.Type), entry.Methods); }
                catch (Exception e) { Unavailable(entry.Type, e); }
            }
            IncludeEventCallbacks(null, true);
            if (players != null)
                foreach (var player in players)
                {
                    if (player == null) continue;
                    IncludePlayerType(player.GetType());
                    IncludeEventCallbacks(player, false);
                }
        }
        catch (Exception e) { Unavailable("players", e); }
        RuntimeCodePreparation.Prepare();
    }

    private static void IncludePlayerType(Type type)
    {
        for (var current = type; current != null && typeof(Player).IsAssignableFrom(current); current = current.BaseType)
            if (PlayerTypes.Add(current)) IncludeNamed(current, PlayerMethods);
    }

    private static void IncludeNamed(Type type, params string[] names)
    {
        if (type == null) return;
        try
        {
            foreach (var method in type.GetMethods(Methods))
                if (Array.IndexOf(names, method.Name) >= 0) RuntimeCodePreparation.IncludeCallback(method);
        }
        catch (Exception e) { Unavailable(type.FullName, e); }
    }

    private static FieldInfo[] FindDeathEvents()
    {
        var fields = new List<FieldInfo>();
        foreach (var name in new[] { "OnPlayerDead", "OnPlayerDeadStatic", "OnPlayerDeadOrUnspawn", "OnIPlayerDeadOrUnspawn" })
        {
            var field = typeof(Player).GetField(name, Methods);
            if (field != null && typeof(Delegate).IsAssignableFrom(field.FieldType)) fields.Add(field);
        }
        return fields.ToArray();
    }

    private static void IncludeEventCallbacks(Player player, bool statics)
    {
        foreach (var field in DeathEvents)
        {
            if (field.IsStatic != statics) continue;
            try
            {
                if (field.GetValue(player) is not Delegate handlers) continue;
                foreach (var callback in handlers.GetInvocationList()) RuntimeCodePreparation.IncludeCallback(callback.Method);
            }
            catch (Exception e) { Unavailable(field.Name, e); }
        }
    }

    private static void Unavailable(string binding, Exception e)
        => Log.Warning($"Death code preparation bindings unavailable: {binding}: {e.GetType().Name}: {e.Message}");
}
