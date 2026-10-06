using System;
using System.Collections.Generic;
using EFT;
using Orbit.Helpers;
using UnityEngine;

namespace Orbit.Systems;

// Held firearms live outside the player's hierarchy. Hide their visuals without disabling
// the controller, animator, inventory operations or Ghost combat sound player.
internal static class GhostWeaponVisuals
{
    private sealed class Mask
    {
        internal Player Player;
        internal GameObject Root;
        internal float NextScan;
        internal readonly Dictionary<Renderer, bool> Renderers = new();
        internal readonly Dictionary<Light, bool> Lights = new();
        internal readonly List<Renderer> RendererScratch = new();
        internal readonly List<Light> LightScratch = new();

        internal void Hide()
        {
            if (Time.time >= NextScan)
            {
                NextScan = Time.time + 2f;
                Root.GetComponentsInChildren(true, RendererScratch);
                foreach (var renderer in RendererScratch)
                    if (renderer != null && !Renderers.ContainsKey(renderer))
                        Renderers.Add(renderer, renderer.forceRenderingOff);
                Root.GetComponentsInChildren(true, LightScratch);
                foreach (var light in LightScratch)
                    if (light != null && !Lights.ContainsKey(light)) Lights.Add(light, light.enabled);
            }
            foreach (var pair in Renderers)
                if (Belongs(pair.Key)) pair.Key.forceRenderingOff = true;
            foreach (var pair in Lights)
                if (Belongs(pair.Key)) pair.Key.enabled = false;
        }

        internal void Restore()
        {
            foreach (var pair in Renderers)
                if (Belongs(pair.Key)) pair.Key.forceRenderingOff = pair.Value;
            foreach (var pair in Lights)
                if (Belongs(pair.Key)) pair.Key.enabled = pair.Value;
        }

        private bool Belongs(Component component) => component != null && Root != null
            && (component.gameObject == Root || component.transform.IsChildOf(Root.transform));
    }

    private static readonly Dictionary<GameObject, Mask> ByRoot = new();
    private static readonly List<Mask> Active = new();
    private static int _cursor, _errors;

    internal static void Sleep(Player player)
    {
        try { Hide(player, player?.HandsController?.ControllerGameObject); }
        catch (Exception error) { Failure(error); }
    }

    internal static void Hide(Player player, GameObject root)
    {
        try
        {
            if (player == null || root == null || root.GetComponent<WeaponPrefab>() == null) return;
            if (ByRoot.TryGetValue(root, out var previous))
            {
                if (previous.Player == player) { previous.Hide(); return; }
                Release(root);
            }
            // Parent/ReturnToPool patches release the old lease before a prefab is reused.
            Release(root);
            var mask = new Mask { Player = player, Root = root };
            ByRoot.Add(root, mask); Active.Add(mask);
            mask.Hide();
            Report(mask, "hidden");
        }
        catch (Exception error) { Failure(error); }
    }

    internal static void Wake(Player player)
    {
        if (ReferenceEquals(player, null)) return;
        for (var i = Active.Count - 1; i >= 0; i--)
            if (ReferenceEquals(Active[i].Player, player)) Release(Active[i].Root);
    }

    internal static void Release(GameObject root)
    {
        if (ReferenceEquals(root, null) || !ByRoot.TryGetValue(root, out var mask)) return;
        ByRoot.Remove(root); Active.Remove(mask);
        try { mask.Restore(); Report(mask, "restored"); }
        catch (Exception error) { Failure(error); }
    }

    internal static void Tick()
    {
        // One tracked weapon per frame. Rescan each hierarchy at most every two seconds,
        // reusing component lists; new hands are hidden immediately by WeaponPrefab.Parent.
        if (Active.Count == 0) return;
        if (_cursor >= Active.Count) _cursor = 0;
        var mask = Active[_cursor++];
        try
        {
            if (mask.Player == null || mask.Root == null || !DormancySystem.IsDormantProfile(mask.Player.ProfileId)
                || mask.Player.HealthController is not { IsAlive: true })
            { Release(mask.Root); return; }
            // Parent can run before HandsController is assigned. Keep this lease until
            // wake, death or ReturnToPool rather than exposing the incoming weapon mid-swap.
            mask.Hide();
        }
        catch (Exception error) { Release(mask.Root); Failure(error); }
    }

    internal static void Clear()
    {
        while (Active.Count > 0) Release(Active[Active.Count - 1].Root);
        _cursor = _errors = 0;
    }

    private static void Report(Mask mask, string action)
    {
        if (!Log.DebugEnabled && !PerformanceJournal.Enabled) return;
        var detail = $"{action} renderers={mask.Renderers.Count} lights={mask.Lights.Count}";
        Log.Debug($"GHOST WEAPON: {mask.Player?.ProfileId} {detail}");
        PerformanceJournal.Event("ghost-weapon", mask.Player?.ProfileId, detail);
    }

    private static void Failure(Exception error)
    {
        if (++_errors <= 3) Log.Warning($"GHOST WEAPON: visual state failed: {error}");
    }
}
