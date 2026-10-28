using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbit.Server.Presets;

public sealed partial class PresetService
{
    // Scan into a local list. Opening/cancelling a review must not refresh the active addon,
    // fork Default, save outgoing edits or change selection as a side effect.
    public PresetSelectionReview ReviewSelection(string id, PresetParts parts)
    {
        lock (_gate)
        {
            RequireReady();
            var addons = AddonDiscovery.Scan(AddonDirectory, zones).Addons;
            var addon = addons.FirstOrDefault(a => a.Id == id);
            ValidateSelection(id, parts, addon);
            var current = Capture();
            var target = SelectionTarget(id, parts, addon, current);
            var name = addon?.Name ?? (id == "default" ? "Default" : _library.ActiveAddon?.Id == id
                ? _library.ActiveAddon.Name : _library.Presets.First(p => p.Id == id).Name);
            return new(id, name, parts, addon != null, SelectionStamp(current), addon?.Revision, target, Defaults());
        }
    }

    public void ApplyReview(PresetSelectionReview review)
    {
        lock (_gate)
        {
            RequireReady();
            var current = Capture();
            if (review.StateStamp != SelectionStamp(current))
                throw new InvalidOperationException("Your setup changed while this review was open. Cancel and select the preset again to review the latest values.");
            var addon = AddonDiscovery.Scan(AddonDirectory, zones).Addons.FirstOrDefault(a => a.Id == review.Id);
            ValidateSelection(review.Id, review.Parts, addon);
            if (addon?.Revision != review.AddonRevision || !Same(SelectionTarget(review.Id, review.Parts, addon, current), review.Original))
                throw new InvalidOperationException("This preset changed while you were reviewing it. Cancel and select it again.");
            // Validate everything before writing or replacing either working service.
            var target = Normalize(review.Snapshot());
            if (!Same(target, review.Snapshot()))
                throw new InvalidDataException("Some values were not valid for this preset. Check your edits before applying.");
            var next = WithSavedEdits();
            if (ReadOnly && HasEdits())
                next.Presets.Add(NewPreset(ReviewCopyName(next), current));
            if (review.CreatesCopy)
            {
                var custom = NewPreset(ReviewCopyName(next), target);
                next.Presets.Add(custom); next.ActiveId = custom.Id; next.ActiveAddon = null;
            }
            else
            {
                next.ActiveId = review.Id;
                next.ActiveAddon = addon == null ? null : new UserPreset
                    { Id = addon.Id, Name = addon.Name, AddonRevision = addon.Revision, Snapshot = target };
                if (addon == null && _library.ActiveAddon?.Id == review.Id)
                    next.ActiveAddon = Clone(_library.ActiveAddon);
            }
            Persist(next);
            _library = next; _active = target;
            ApplyWorking(replace: true);
            WriteMirrors();
            logger.Info($"[ORBIT] PRESETS: selected {ActiveName}");
        }
        Changed?.Invoke();
    }

    private void ValidateSelection(string id, PresetParts parts, PresetAddon? addon)
    {
        if (!Enum.IsDefined(parts)) throw new InvalidDataException("Choose config, zones or both.");
        if (id != "default" && _library.Presets.All(p => p.Id != id) && addon == null && _library.ActiveAddon?.Id != id)
            throw new InvalidDataException("This preset is no longer available.");
        if (addon != null && (parts == PresetParts.ConfigOnly && !addon.Config.HasValue || parts == PresetParts.ZonesOnly && !addon.HasZones))
            throw new InvalidDataException("This addon does not contain the selected part.");
    }

    private PresetSnapshot SelectionTarget(string id, PresetParts parts, PresetAddon? addon, PresetSnapshot current)
    {
        var target = addon != null ? ApplyAddon(current, addon, parts != PresetParts.ConfigOnly)
            : id == _library.ActiveId && !ReadOnly ? Normalize(current) : Resolve(_library, id);
        if (parts == PresetParts.ConfigAndZones) return target;
        return Normalize(new PresetSnapshot
        {
            Config = parts == PresetParts.ConfigOnly
                ? AddonDiscovery.OverlayRushPoints(target.Config, AddonDiscovery.ReadRushPoints(current.Config))
                : AddonDiscovery.OverlayRushPoints(current.Config, AddonDiscovery.ReadRushPoints(target.Config)),
            Maps = parts == PresetParts.ZonesOnly ? target.Maps : current.Maps,
        });
    }

    private string SelectionStamp(PresetSnapshot current) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(_library, Json) + JsonSerializer.Serialize(current, Json))));
    private static string ReviewCopyName(PresetLibrary library)
    {
        var name = "Custom";
        for (int n = 2; library.Presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++) name = $"Custom {n}";
        return name;
    }
}
