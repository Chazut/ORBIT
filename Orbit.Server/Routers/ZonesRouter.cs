using Orbit.Server.Zones;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace Orbit.Server.Routers;

/// <summary>
/// Serves the per-map advection zones to the client plugin as one JSON object keyed by map id.
/// Fetched alongside /orbit/config at boot and raid start; the client overrides its local
/// Config/Maps/Zones files with whatever this returns.
/// </summary>
[Injectable]
public sealed class ZonesRouter(JsonUtil jsonUtil, ZoneStoreService zoneStore) : StaticRouter(jsonUtil, [
    new RouteAction<EmptyRequestData>(
        "/orbit/zones",
        (url, requestData, sessionId, output, cancellationToken) =>
            new ValueTask<string>(zoneStore.ToJsonAll())
    )
])
{
}
