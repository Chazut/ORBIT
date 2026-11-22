namespace Orbit.Settings;

public static class CustomsBackport
{
    public const string BaseId = "bigmap";
    public const string Variant = "fums13";
    public const string Key = BaseId + "@" + Variant;

    // Both layouts use the same editor projection and initial floor catalogue.
    public static string ReferenceMap(string mapId) => mapId == Key ? BaseId : mapId;
}
