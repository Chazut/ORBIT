using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Orbit.Server.Web;

/// <summary>
/// Inline copies of the tarkov.dev renders that ship several floors in one SVG (the reworked
/// Interchange: Ground_Level, First_Floor and Second_Floor as sibling groups). Drawn through an
/// &lt;image&gt; the floors stack and bury the ground level under the mall, so those renders are fetched
/// once, re-emitted as a nested &lt;svg&gt; with the upper floors display:none, and cached for the server's
/// lifetime. Renders without <see cref="MapView.HideLayers"/> keep the plain &lt;image&gt; path and never
/// touch the network; a failed fetch leaves the page on that path too.
/// </summary>
public static class MapRenders
{
    public sealed record InlineRender(string ViewBox, string Content);

    private static readonly ConcurrentDictionary<string, Task<InlineRender?>> _cache = new();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Regex RootTag = new(@"<svg\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ViewBoxAttr = new(@"viewBox\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WidthAttr = new(@"\swidth\s*=\s*""([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeightAttr = new(@"\sheight\s*=\s*""([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The render's inner markup, or null when the view has no hidden layers or the fetch failed.</summary>
    public static Task<InlineRender?> InlineSvgAsync(string mapId, MapView view)
    {
        if (view.Svg == null || view.HideLayers is not { Length: > 0 }) return Task.FromResult<InlineRender?>(null);
        return _cache.GetOrAdd(mapId, _ => FetchAsync(view));
    }

    /// <summary>Nested svg covering the same 1000 x vh box the &lt;image&gt; path uses.</summary>
    public static string Wrap(InlineRender render, float vh)
    {
        var h = vh.ToString("0.##", CultureInfo.InvariantCulture);
        return $"<svg x=\"0\" y=\"0\" width=\"1000\" height=\"{h}\" viewBox=\"{render.ViewBox}\" preserveAspectRatio=\"none\" opacity=\"0.85\">{render.Content}</svg>";
    }

    private static async Task<InlineRender?> FetchAsync(MapView view)
    {
        try
        {
            var text = await _http.GetStringAsync(view.Svg!);
            var root = RootTag.Match(text);
            if (!root.Success) return null;
            var closing = text.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
            if (closing <= root.Index) return null;
            var inner = text.Substring(root.Index + root.Length, closing - root.Index - root.Length);

            string viewBox;
            var vb = ViewBoxAttr.Match(root.Value);
            if (vb.Success)
            {
                viewBox = vb.Groups[1].Value;
            }
            else
            {
                var w = WidthAttr.Match(root.Value);
                var h = HeightAttr.Match(root.Value);
                if (!w.Success || !h.Success) return null;
                viewBox = $"0 0 {w.Groups[1].Value} {h.Groups[1].Value}";
            }

            var selector = string.Join(",", view.HideLayers!.Select(id => "#" + id));
            var style = "<style>" + selector + "{display:none}</style>";
            return new InlineRender(viewBox, style + inner);
        }
        catch
        {
            // Offline server or a reshaped file: the <image> fallback stays.
            return null;
        }
    }
}
