using Orbit.Server.Config;
using Orbit.Server.Presets;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace Orbit.Server.Routers;

/// <summary>
/// Serves the saved active preset to the client at game boot and raid start, including headless.
/// </summary>
[Injectable]
public sealed class ConfigRouter(JsonUtil jsonUtil, PresetService presets) : StaticRouter(jsonUtil, [
    new RouteAction<EmptyRequestData>(
        "/orbit/config",
        (url, requestData, sessionId, output, cancellationToken) =>
            new ValueTask<string>(presets.ConfigForGame())
    )
])
{
}
