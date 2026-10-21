using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Server.Presets;
using Orbit.Settings;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace Orbit.Server.Routers;

public sealed class DoorCaptureEnvelope : IRequestData
{
    [JsonPropertyName("Payload")] public MarkedDoorCapture? Payload { get; set; }
}

[Injectable]
public sealed class DoorCaptureRouter(JsonUtil json, PresetService presets) : StaticRouter(json, [
    new RouteAction<EmptyRequestData>("/orbit/tools/door/context",
        (url, request, session, output, cancellation) => Reply(presets.DoorCaptureContext)),
    new RouteAction<DoorCaptureEnvelope>("/orbit/tools/door/capture",
        (url, request, session, output, cancellation) => Reply(() => presets.CaptureMarkedDoor(
            request.Payload ?? throw new InvalidDataException("Missing door capture."))))
])
{
    private static ValueTask<string> Reply(Func<MarkedDoorCaptureReply> action)
    {
        MarkedDoorCaptureReply result;
        try { result = action(); }
        catch (Exception ex) { result = new() { Message = ex.Message }; }
        return new(JsonSerializer.Serialize(result));
    }
}
