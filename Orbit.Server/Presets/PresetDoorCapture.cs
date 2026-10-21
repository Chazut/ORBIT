using Orbit.Server.Zones;
using Orbit.Settings;

namespace Orbit.Server.Presets;

public sealed partial class PresetService
{
    public MarkedDoorCaptureReply DoorCaptureContext()
    {
        lock (_gate)
        {
            RequireReady();
            return new() { Success = true, PresetId = ActiveId, PresetName = ActiveName };
        }
    }

    public MarkedDoorCaptureReply CaptureMarkedDoor(MarkedDoorCapture capture)
    {
        MarkedDoorCaptureReply reply;
        lock (_gate)
        {
            RequireReady();
            ArgumentNullException.ThrowIfNull(capture);
            if (capture.ExpectedPresetId != ActiveId)
                throw new InvalidOperationException("The active preset changed. Capture the door again.");
            var map = ZoneStoreService.MapIds.FirstOrDefault(id => id.Equals(capture.MapId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("This map is not supported by the Rush editor.");
            if (string.IsNullOrWhiteSpace(capture.DoorId) || capture.DoorId.Length > 200 || capture.DoorId.Any(char.IsControl))
                throw new InvalidDataException("Invalid scene door ID.");
            var point = new RushPoint
            {
                Id = "captured-" + Guid.NewGuid().ToString("N"),
                Name = "Captured room " + capture.DoorId[..Math.Min(65, capture.DoorId.Length)],
                Kind = "Marked", DoorId = capture.DoorId, Enabled = capture.PlacementConfirmed,
                X = capture.X, Y = capture.Y, Z = capture.Z,
                LootX = capture.LootX, LootY = capture.LootY, LootZ = capture.LootZ, Radius = capture.Radius,
            };
            point.Validate();
            if (Math.Abs(point.Y - point.LootY) > 2.5f
                || Math.Pow(point.X - point.LootX, 2) + Math.Pow(point.Z - point.LootZ, 2) > 100 * 100)
                throw new InvalidDataException("The room position must be near the door on the same floor.");
            if (!RushDefaults.IsMarkedRushTarget(map, point.DoorId))
                throw new InvalidDataException("This door is excluded from Marked Rush because its access is unsupported.");
            // Do not save unrelated browser edits or silently replace an editor's working copy.
            if (HasEdits())
                throw new InvalidOperationException("Save or discard pending edits in the ORBIT web UI, then capture again.");
            var points = configs.Config.Rush.Points(map);
            var existing = points.FirstOrDefault(p => p.Kind == "Marked" && p.DoorId == point.DoorId);
            if (existing != null)
                return new() { Success = true, PresetId = ActiveId, PresetName = ActiveName, PointId = existing.Id,
                    Message = "This door already has a Marked Rush point. Its settings were kept." };

            var snapshot = Normalize(_active);
            snapshot.Config = AddonDiscovery.OverlayRushPoints(snapshot.Config,
                new() { [map] = points.Select(p => p.Copy()).Append(point).ToList() });
            var next = Clone(_library);
            var copied = ReadOnly;
            if (copied)
            {
                var personal = NewPreset(UniqueName("Custom"), snapshot);
                next.Presets.Add(personal);
                next.ActiveId = personal.Id;
                next.ActiveAddon = null;
            }
            else next.Presets.First(p => p.Id == next.ActiveId).Snapshot = snapshot;
            Persist(next); // Only publish the new active snapshot after the atomic write succeeds.
            _library = next;
            _active = snapshot;
            ApplyWorking(replace: true);
            WriteMirrors();
            reply = new() { Success = true, Created = true, PresetId = ActiveId, PresetName = ActiveName,
                PointId = point.Id, NeedsReview = !point.Enabled,
                Message = (copied ? "Saved in a personal copy. " : "Saved. ")
                    + (point.Enabled ? "Applies next raid." : "Point disabled: check room coordinates and enable it in Rush objectives > Marked.")
                    + (MirrorWarning == null ? "" : " " + MirrorWarning) };
            logger.Info($"[ORBIT] DOOR CAPTURE: saved map={map} door={point.DoorId} preset={ActiveName} point={point.Id} enabled={point.Enabled}");
        }
        Changed?.Invoke();
        return reply;
    }
}
