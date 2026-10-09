using System.Collections.Generic;
using EFT;
using EFT.CameraControl;
using Orbit.Interop;
using UnityEngine;

namespace Orbit.Api;

/// <summary>PiP-only view data. Remote entries are supplied by the optional Fika addon.</summary>
public struct OrbitScopeView
{
    public string SightId;
    public float Zoom;
    public float FieldOfView;
    public bool Valid => !string.IsNullOrEmpty(SightId) && SightId.Length <= 64
        && Zoom >= 1f && Zoom <= 100f && FieldOfView >= 0.1f && FieldOfView < 179f;
}

public static class OrbitScopeViews
{
    private struct Remote
    {
        internal OrbitScopeView View;
        internal float Expires;
    }

    private static readonly Dictionary<string, Remote> Views = new();

    public static bool TryReadLocal(Player player, out OrbitScopeView view)
    {
        view = default;
        if (!PipDisablerCompat.TryRead(player, out var zoom)) return false;
        try
        {
            var camera = CameraManager.Exist ? CameraManager.Instance?.Camera : null;
            var sight = player.ProceduralWeaponAnimation?.CurrentScope?.Mod;
            if (camera == null || sight?.Item == null) return false;
            view = new OrbitScopeView
            {
                SightId = sight.Item.Id.ToString(), Zoom = zoom, FieldOfView = camera.fieldOfView,
            };
            return view.Valid;
        }
        catch { return false; }
    }

    public static void SetRemote(string profileId, OrbitScopeView view)
    {
        if (string.IsNullOrEmpty(profileId) || profileId.Length > 64 || !view.Valid) return;
        if (Views.Count >= 64 && !Views.ContainsKey(profileId)) return;
        Views[profileId] = new Remote { View = view, Expires = Time.time + 1.5f };
    }

    public static void RemoveRemote(string profileId)
    {
        if (profileId != null) Views.Remove(profileId);
    }

    public static void ClearRemote() => Views.Clear();

    internal static bool TryReadRemote(Player player, out OrbitScopeView view)
    {
        view = default;
        if (player == null || player.IsYourPlayer || string.IsNullOrEmpty(player.ProfileId)
            || !Views.TryGetValue(player.ProfileId, out var remote)) return false;
        if (Time.time >= remote.Expires)
        {
            Views.Remove(player.ProfileId);
            return false;
        }
        // Never reuse a report for a dead player, a different weapon/sight or a secondary sight.
        if (player.HealthController is not { IsAlive: true }
            || player.HandsController is not Player.FirearmController { IsAiming: true }) return false;
        var scope = player.ProceduralWeaponAnimation?.CurrentScope;
        if (scope == null || !scope.IsOptic || scope.Mod?.Item == null
            || scope.Mod.Item.Id.ToString() != remote.View.SightId) return false;
        view = remote.View;
        return true;
    }
}
