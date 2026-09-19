using Orbit.Server.Zones;
using Orbit.Server.Presets;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Models.Utils;
using System.Text.Json.Serialization;

namespace Orbit.Server.Routers;

/// <summary>
/// Serves the per-map advection zones to the client plugin as one JSON object keyed by map id.
/// Fetched alongside /orbit/config at boot and raid start; the client overrides its local
/// Config/Maps/Zones files with whatever this returns.
/// </summary>
[Injectable]
public sealed class ZonesRouter(JsonUtil jsonUtil, ZoneStoreService zoneStore, PresetService presets) : StaticRouter(jsonUtil, [
    new RouteAction<EmptyRequestData>(
        "/orbit/zones",
        (url, requestData, sessionId, output, cancellationToken) =>
            new ValueTask<string>(presets.ZonesForGame())
    ),
    new RouteAction<NativeFloorsRequest>(
        "/orbit/zones/native-floors",
        (url, requestData, sessionId, output, cancellationToken) =>
        {
            zoneStore.RecordNativeFloors(requestData.MapId, requestData.Floors);
            return new ValueTask<string>("{}");
        }
    )
])
{
}

public sealed class NativeFloorsRequest : IRequestData
{
    [JsonPropertyName("MapId")] public string MapId { get; set; } = "";
    [JsonPropertyName("Floors")] public Dictionary<string, string?> Floors { get; set; } = new();
}
