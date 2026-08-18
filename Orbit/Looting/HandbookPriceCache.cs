using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace Orbit.Looting;

/// <summary>
/// Headless-safe item base-price cache. EFT's <c>Singleton&lt;EFT.HandBook.Handbook&gt;</c> is built only by the
/// main-menu / profile flow (EFT.HandBook.Handbook ctor → PreloaderUI.MenuTaskBar.InitHandbook), which a FIKA
/// headless client skips entirely — so on headless it stays null forever, every <see cref="ItemPriceLookup"/>
/// returns 0, and bots treat all loot as worthless (issue #5). This pulls the same handbook base prices
/// straight from the SPT server over HTTP at init (the <c>/client/handbook/templates</c> route EFT itself
/// uses, which is alive on a headless client since it still talks to the SPT server), with the on-disk
/// <c>handbook.json</c> as a fallback. Populated once; <see cref="ItemPriceLookup"/> only consults it when
/// EFT.HandBook.Handbook is absent, so normal clients are byte-for-byte unchanged.
/// </summary>
public static class HandbookPriceCache
{
    private static Dictionary<string, float> _prices;

    public static bool Loaded => _prices != null && _prices.Count > 0;
    public static int Count => _prices?.Count ?? 0;

    public static bool TryGet(string templateId, out float price)
    {
        if (_prices != null && templateId != null && _prices.TryGetValue(templateId, out price))
            return true;
        price = 0f;
        return false;
    }

    /// <summary>
    /// Fetch + cache base prices. HTTP first (layout-independent, works on headless), the on-disk
    /// handbook.json as a fallback. Tolerant parse (case-insensitive keys, optional {data:...} envelope)
    /// so a server-response shape we didn't anticipate degrades to the disk source instead of silently
    /// caching zeros. Call ONCE at init — never per lookup.
    /// </summary>
    public static void Init()
    {
        try
        {
            var http = LoadFromHttp();
            if (http != null && http.Count > 0)
            {
                _prices = http;
                Log.Info($"HandbookPriceCache: loaded {_prices.Count} base prices from server (/client/handbook/templates)");
                return;
            }
            Log.Warning("HandbookPriceCache: server handbook fetch returned nothing — trying disk");
        }
        catch (Exception e)
        {
            Log.Warning($"HandbookPriceCache: server handbook fetch failed ({e.Message}) — trying disk");
        }

        try
        {
            var disk = LoadFromDisk();
            if (disk != null && disk.Count > 0)
            {
                _prices = disk;
                Log.Info($"HandbookPriceCache: loaded {_prices.Count} base prices from disk handbook.json");
                return;
            }
            Log.Error("HandbookPriceCache: no prices from server or disk — loot value falls back to EFT.HandBook.Handbook if present, else 0");
        }
        catch (Exception e)
        {
            Log.Error($"HandbookPriceCache: disk handbook load failed ({e}) — loot value falls back to EFT.HandBook.Handbook if present, else 0");
        }
    }

    private static Dictionary<string, float> LoadFromHttp()
    {
        var body = RequestHandler.GetJson("/client/handbook/templates");
        return string.IsNullOrEmpty(body) ? null : ParseHandbook(JToken.Parse(body));
    }

    private static Dictionary<string, float> LoadFromDisk()
    {
        // SPT 4.0 nests the server data under an extra SPT/ segment: <GameRoot>/SPT/SPT_Data/database/templates.
        var path = Path.Combine(BepInEx.Paths.GameRootPath, "SPT", "SPT_Data", "database", "templates", "handbook.json");
        return File.Exists(path) ? ParseHandbook(JToken.Parse(File.ReadAllText(path))) : null;
    }

    /// <summary>
    /// Reads Items[].Id → Items[].Price. Accepts the raw handbook object or SPT's {err,errmsg,data:{...}}
    /// envelope (and a double-encoded string data payload), all case-insensitively — so we never bind to
    /// an empty/zeroed cache just because the server response cased a key differently than the on-disk file.
    /// </summary>
    private static Dictionary<string, float> ParseHandbook(JToken root)
    {
        if (root == null) return null;
        var data = root["data"] ?? root["Data"] ?? root;
        if (data.Type == JTokenType.String)
            data = JToken.Parse(data.Value<string>());

        if ((data["Items"] ?? data["items"]) is not JArray items) return null;

        var dict = new Dictionary<string, float>(items.Count);
        foreach (var it in items)
        {
            var id = (it["Id"] ?? it["id"] ?? it["_id"])?.Value<string>();
            var price = it["Price"] ?? it["price"];
            if (string.IsNullOrEmpty(id) || price == null) continue;
            dict[id] = price.Value<float>();
        }
        return dict;
    }
}
