using Orbit.Server.Config;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace Orbit.Server.Routers;

/// <summary>
/// Serves the ORBIT server config to the client plugin. The client fetches this once at game
/// start (SAIN-style) so behaviour config applies wherever the bots run - including headless.
/// </summary>
[Injectable]
public sealed class ConfigRouter(JsonUtil jsonUtil, ConfigService configService) : StaticRouter(jsonUtil, [
    new RouteAction<EmptyRequestData>(
        "/orbit/config",
        (url, requestData, sessionId, output, cancellationToken) =>
            new ValueTask<string>(configService.ToJson())
    )
])
{
}
