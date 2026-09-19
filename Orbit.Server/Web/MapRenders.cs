using System.Collections.Concurrent;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Orbit.Server.Web;

/// <summary>
/// Isolated images for renders that ship several floors in one SVG. Preserve the complete SVG
/// document and its namespaces while hiding the other floors. Each image owns its styles and IDs,
/// so one floor cannot hide another floor in the surrounding page.
/// </summary>
public static class MapRenders
{
    public sealed record InlineRender(string ImageUrl);

    private static readonly ConcurrentDictionary<string, Task<InlineRender?>> _cache = new();
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>A standalone image, or null when no filtering is needed or the fetch failed.</summary>
    public static async Task<InlineRender?> InlineSvgAsync(string mapId, MapView view)
    {
        if (view.Svg == null || view.HideLayers is not { Length: > 0 }) return null;
        var key = mapId + "|" + view.Svg + "|" + string.Join(",", view.HideLayers);
        var task = _cache.GetOrAdd(key, _ => FetchAsync(view));
        var render = await task;
        // Reopening a map can retry a transient download failure without restarting the server.
        if (render == null) _cache.TryRemove(new KeyValuePair<string, Task<InlineRender?>>(key, task));
        return render;
    }

    internal static InlineRender PrepareSvg(string text, string[] hiddenLayers)
    {
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader).Root;
        XNamespace svg = "http://www.w3.org/2000/svg";
        if (root?.Name != svg + "svg") throw new InvalidDataException("Expected an SVG document.");
        var hidden = hiddenLayers.ToHashSet(StringComparer.Ordinal);
        foreach (var layer in root.Descendants().Where(e => hidden.Contains((string?)e.Attribute("id") ?? "")))
            layer.SetAttributeValue("style", ((string?)layer.Attribute("style"))?.TrimEnd(';') + ";display:none");
        // Retain root attributes, especially xmlns:xlink used by references inside Interchange.svg.
        return new InlineRender("data:image/svg+xml;base64," + Convert.ToBase64String(
            Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting))));
    }

    private static async Task<InlineRender?> FetchAsync(MapView view)
    {
        try
        {
            var text = await _http.GetStringAsync(view.Svg!);
            return PrepareSvg(text, view.HideLayers!);
        }
        catch
        {
            // Keep the last error local to this request; a later map selection can retry.
            return null;
        }
    }
}
