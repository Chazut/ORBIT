using Orbit.Debugging;
using Orbit.Server.Debugging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace Orbit.Server.Routers;

[Injectable(InjectionType.Singleton)]
public sealed class DebugRelayService
{
    public DebugRelayStore Store { get; init; } = new(Path.Combine(
        Path.GetDirectoryName(typeof(DebugRelayService).Assembly.Location)!, "debug-cache"));
}

public sealed class DebugRelayEnvelope : IRequestData
{
    public DebugRelayRequest Payload { get; set; } = new();
}

[Injectable]
public sealed class DebugRelayRouter(JsonUtil json, DebugRelayService relay) : StaticRouter(json, [
    new RouteAction<DebugRelayEnvelope>(DebugRelayProtocol.Route,
        (url, request, session, output, cancellation) => new ValueTask<string>(
            json.Serialize(relay.Store.Handle(session.ToString(), request.Payload))!))
]);
