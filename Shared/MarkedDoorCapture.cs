#nullable disable
namespace Orbit.Settings;

// Plain wire data shared by the in-game authoring tool and the preset service.
public sealed class MarkedDoorCapture
{
    public string ExpectedPresetId { get; set; } = "";
    public string MapId { get; set; } = "";
    public string DoorId { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float LootX { get; set; }
    public float LootY { get; set; }
    public float LootZ { get; set; }
    public float Radius { get; set; } = 5;
    public bool PlacementConfirmed { get; set; }
}

public sealed class MarkedDoorCaptureReply
{
    public const string FormatName = "orbit-door-capture/1";
    public string Format { get; set; } = FormatName;
    public bool Success { get; set; }
    public string PresetId { get; set; } = "";
    public string PresetName { get; set; } = "";
    public string PointId { get; set; } = "";
    public bool Created { get; set; }
    public bool NeedsReview { get; set; }
    public string Message { get; set; } = "";
}
