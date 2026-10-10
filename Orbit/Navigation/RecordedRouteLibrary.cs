using System;
using UnityEngine;

namespace Orbit.Navigation;

internal sealed class RecordedRoute(string key, string map, Vector3[] points)
{
    internal readonly string Key = key, Map = map;
    internal readonly Vector3[] Points = points;
}

internal static partial class RecordedRouteLibrary
{
    internal static bool Matches(RecordedRoute route, string map, string key)
        => route.Key == key && string.Equals(route.Map, map, StringComparison.OrdinalIgnoreCase);

    internal static bool Has(string map, string key)
    { foreach (var route in Routes) if (Matches(route, map, key)) return true; return false; }

    internal static string Sniper(string map, string id) => id switch
    {
        "ccd9c3c805594f2494b46a7ee91414cc" when Same(map, "interchange@rework") => "interchange-roof",
        "1455da6c776640439384c1d5aaee4f86" when Same(map, "rezervbase") => "dome",
        "73fd948d52b149b8a0dad648ae5f5b23" when Same(map, "shoreline") => "west",
        "3bc9877f14434b6b9d504c463a4dc0d0" when Same(map, "shoreline") => "east",
        "2f4305d88fee4c2995594b8cef3633ee" when Same(map, "bigmap") => "customs-roof",
        _ => null
    };
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Standing positions verified against the player's corresponding native switch event.
    internal static Vector3? Interaction(string map, string id) => id switch
    {
        "Shopping_Mall_DesignStuff_00058" when Same(map, "interchange@rework") => new(-47.587f, 36.570f, -54.480f),
        "Shopping_Mall_DesignStuff_00064" when Same(map, "interchange@rework") => new(-51.269f, 36.620f, -124.153f),
        "autoId_00000_D2_LEVER" when Same(map, "rezervbase") => new(-117.733f, -14.486f, 21.860f),
        "00453" when Same(map, "rezervbase") => new(-117.158f, -18.294f, 169.513f),
        "autoId_00632_EXFIL" when Same(map, "rezervbase") => new(-61.331f, -6.984f, 77.600f),
        "custom_DesignStuff_00034" when Same(map, "bigmap") => new(352.352f, 1.282f, -39.704f),
        _ => null
    };

    internal static Vector3 Post(string key, int candidate)
    {
        var posts = key switch
        {
            "interchange-roof" => InterchangePosts, "dome" => DomePosts,
            "west" => WestPosts, "east" => EastPosts, _ => CustomsPosts
        };
        if (candidate < posts.Length) return posts[candidate];
        // Prefer the observed firing stops, then sample the safe roof perimeter walked in the raid.
        // Every candidate still requires a local mesh point and a usable sightline at runtime.
        foreach (var route in Routes)
            if (route.Key == key + ":roof")
                return route.Points[(candidate - posts.Length) * route.Points.Length / (25 - posts.Length) % route.Points.Length];
        return posts[candidate % posts.Length];
    }
    private static readonly Vector3[] InterchangePosts = { new(-205.591f,30.526f,-346.176f), new(-195.351f,30.351f,-348.449f) };
    private static readonly Vector3[] DomePosts = { new(-6.645f,32.786f,166.940f), new(-9.627f,32.786f,167.219f),
        new(-12.015f,32.786f,168.356f), new(-10.097f,32.723f,167.671f), new(-6.956f,32.723f,167.132f) };
    private static readonly Vector3[] WestPosts = { new(-233.966f,6.884f,-85.530f), new(-202.747f,6.919f,-73.815f),
        new(-142.664f,6.924f,-72.800f), new(-141.546f,6.924f,-91.176f) };
    private static readonly Vector3[] EastPosts = { new(-269.400f,6.859f,-84.947f), new(-275.491f,6.859f,-104.056f),
        new(-306.746f,6.859f,-92.224f), new(-361.564f,6.859f,-92.655f), new(-300.891f,6.892f,-72.772f) };
    private static readonly Vector3[] CustomsPosts = { new(191.995f,5.894f,170.061f) };

    internal static Vector3 Departure(string map, string key, Vector3 origin)
    {
        var result = origin; var best = float.MaxValue;
        foreach (var route in Routes)
        {
            if (!Matches(route, map, key + ":down")) continue;
            var gap = (route.Points[0] - origin).sqrMagnitude;
            if (gap >= best) continue;
            best = gap; result = route.Points[route.Points.Length - 1];
        }
        return result;
    }
}
