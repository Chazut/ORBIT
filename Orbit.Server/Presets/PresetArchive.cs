using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orbit.Server.Zones;

namespace Orbit.Server.Presets;

public sealed record PresetExport(string FileName, byte[] Bytes);

public static class PresetArchive
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string? NameError(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return "Enter a preset name before exporting.";
        if (name.Length > 60) return "Use a preset name of 60 characters or fewer.";
        if (name.EnumerateRunes().Any(Rune.IsNumber))
            return "Do not include numbers or version numbers. Keep the same addon folder name for updates.";
        // Validate Windows paths even if the server runs on another operating system.
        if (name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) || name.EndsWith('.'))
            return "Use a valid folder name: no path separators, special path characters or trailing dot.";
        var stem = name.Split('.')[0].TrimEnd();
        if (new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" }
            .Contains(stem, StringComparer.OrdinalIgnoreCase))
            return "This folder name is reserved by Windows. Choose another preset name.";
        return null;
    }

    public static string? VersionError(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        return Regex.IsMatch(version.Trim(), @"\A[A-Za-z0-9][A-Za-z0-9.+_-]{0,39}\z") ? null
            : "Use up to 40 letters, digits, dots, hyphens, underscores or plus signs, starting with a letter or digit.";
    }

    public static PresetExport Create(string name, PresetSnapshot snapshot,
        PresetParts parts = PresetParts.ConfigAndZones, string? version = null)
    {
        if (NameError(name) is { } error) throw new InvalidDataException(error);
        if (VersionError(version) is { } versionError) throw new InvalidDataException(versionError);
        if (!Enum.IsDefined(parts)) throw new InvalidDataException("Choose config, zones or both.");
        name = name.Trim();
        var directory = $"SPT_Runtime/user/mods/ORBIT/addon/{name}/";
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Stable filenames let extracting a later release replace both files in place.
            if (parts != PresetParts.ZonesOnly) Write("config.json", snapshot.Config);
            if (parts != PresetParts.ConfigOnly) Write("zones.json", new ZonePackModel
            {
                Name = name,
                OrbitVersion = typeof(PresetArchive).Assembly.GetName().Version?.ToString(3) ?? "",
                Maps = snapshot.Maps,
            });

            void Write<T>(string fileName, T value)
            {
                using var entry = archive.CreateEntry(directory + fileName, CompressionLevel.Optimal).Open();
                JsonSerializer.Serialize(entry, value, Json);
            }
        }
        var suffix = string.IsNullOrWhiteSpace(version) ? "" : "-" + version.Trim();
        return new PresetExport(name + suffix + ".zip", output.ToArray());
    }
}
