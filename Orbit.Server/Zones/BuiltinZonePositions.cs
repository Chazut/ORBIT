namespace Orbit.Server.Zones;

/// <summary>
/// Baked positions of the BSG BotZones (average of each zone's spawn points, extracted from the
/// SPT location database) so the zone editor can draw built-in zones on the map. The client keeps
/// resolving the REAL position at runtime (BotZone.CenterOfSpawnPoints) — this table is display-only.
/// </summary>
public static class BuiltinZonePositions
{
    public static readonly Dictionary<string, Dictionary<string, (float X, float Z)>> ByMap = new()
    {
        ["bigmap"] = new()
        {
            ["ZoneBlockPost"] = (547.5f, 40.3f),
            ["ZoneBlockPostSniper"] = (581.8f, 0.9f),
            ["ZoneBrige"] = (14.5f, 41.7f),
            ["ZoneCrossRoad"] = (202.3f, 51.7f),
            ["ZoneCustoms"] = (-236.3f, -132.1f),
            ["ZoneDormitory"] = (172.1f, 166.2f),
            ["ZoneFactoryCenter"] = (392.6f, -89.3f),
            ["ZoneFactorySide"] = (597.2f, -96.4f),
            ["ZoneGasStation"] = (433.0f, 56.2f),
            ["ZoneOldAZS"] = (310.6f, -177.0f),
            ["ZoneScavBase"] = (201.2f, -100.2f),
            ["ZoneSnipeBrige"] = (107.1f, -47.9f),
            ["ZoneSnipeFactory"] = (476.1f, -71.9f),
            ["ZoneSnipeTower"] = (253.8f, -19.4f),
            ["ZoneTankSquare"] = (85.2f, -50.5f),
            ["ZoneWade"] = (7.0f, -109.2f),
        },
        ["factory4_day"] = new()
        {
            ["BotZone"] = (9.6f, 8.4f),
        },
        ["factory4_night"] = new()
        {
            ["BotZone"] = (9.6f, 8.4f),
        },
        ["Interchange"] = new()
        {
            ["ZoneCenter"] = (47.4f, -68.9f),
            ["ZoneCenterBot"] = (9.9f, -26.5f),
            ["ZoneGoshan"] = (-128.7f, -60.3f),
            ["ZoneIDEA"] = (-48.9f, -237.5f),
            ["ZoneIDEAPark"] = (175.7f, -262.6f),
            ["ZoneOLI"] = (-37.4f, 120.7f),
            ["ZoneOLIPark"] = (-27.1f, 77.3f),
            ["ZonePowerStation"] = (-219.1f, -268.6f),
            ["ZoneRoad"] = (264.5f, 27.7f),
            ["ZoneTrucks"] = (-157.0f, 155.7f),
        },
        ["laboratory"] = new()
        {
            ["BotZoneBasement"] = (-166.2f, -384.4f),
            ["BotZoneFloor1"] = (-213.6f, -353.4f),
            ["BotZoneFloor2"] = (-189.4f, -334.7f),
            ["BotZoneGate1"] = (-170.7f, -224.9f),
            ["BotZoneGate2"] = (-233.5f, -450.3f),
        },
        ["Lighthouse"] = new()
        {
            ["Zone_Blockpost"] = (6.2f, -453.2f),
            ["Zone_Bridge"] = (-19.7f, -279.1f),
            ["Zone_Chalet"] = (-103.8f, -55.1f),
            ["Zone_Containers"] = (-61.7f, -833.2f),
            ["Zone_DestroyedHouse"] = (56.0f, 313.5f),
            ["Zone_Hellicopter"] = (-87.1f, -590.4f),
            ["Zone_Island"] = (357.9f, 543.5f),
            ["Zone_LongRoad"] = (37.7f, -15.6f),
            ["Zone_Rocks"] = (-118.6f, 164.0f),
            ["Zone_RoofBeach"] = (29.9f, -612.1f),
            ["Zone_RoofContainers"] = (-95.6f, -736.4f),
            ["Zone_RoofRocks"] = (-186.0f, -664.4f),
            ["Zone_SniperPeak"] = (-64.9f, 455.2f),
            ["Zone_TreatmentBeach"] = (63.8f, -614.8f),
            ["Zone_TreatmentContainers"] = (-101.4f, -747.1f),
            ["Zone_TreatmentRocks"] = (-188.6f, -663.2f),
            ["Zone_Village"] = (-152.1f, -224.6f),
        },
        ["RezervBase"] = new()
        {
            ["ZoneBarrack"] = (-125.6f, 33.7f),
            ["ZoneBunkerStorage"] = (207.5f, 6.7f),
            ["ZonePTOR1"] = (11.1f, -10.4f),
            ["ZonePTOR2"] = (89.1f, -19.9f),
            ["ZoneRailStrorage"] = (120.4f, -173.3f),
            ["ZoneSubCommand"] = (-99.3f, 26.9f),
            ["ZoneSubStorage"] = (58.7f, -126.0f),
        },
        ["Sandbox"] = new()
        {
            ["ZoneSandSnipeCenter"] = (104.5f, 107.5f),
        },
        ["Sandbox_high"] = new()
        {
            ["ZoneSandSnipeCenter"] = (104.5f, 107.5f),
            ["ZoneSandSnipeCenter2"] = (61.6f, 133.8f),
            ["ZoneSandbox"] = (74.2f, 86.1f),
        },
        ["Shoreline"] = new()
        {
            ["ZoneBunkeSniper"] = (-152.6f, -269.0f),
            ["ZoneBunker"] = (-134.7f, -351.1f),
            ["ZoneBusStation"] = (-106.8f, -0.2f),
            ["ZoneForestGasStation"] = (-96.9f, 243.0f),
            ["ZoneForestSpawn"] = (154.3f, -259.7f),
            ["ZoneForestTruck"] = (37.9f, -177.3f),
            ["ZoneGasStation"] = (-193.0f, 394.2f),
            ["ZoneGreenHouses"] = (153.3f, 120.0f),
            ["ZoneIsland"] = (215.9f, 438.8f),
            ["ZoneMeteoStation"] = (-499.9f, 231.2f),
            ["ZonePassClose"] = (-844.7f, 63.9f),
            ["ZonePort"] = (-326.5f, 506.1f),
            ["ZonePowerStation"] = (-247.0f, 183.6f),
            ["ZonePowerStationSniper"] = (-226.8f, 191.3f),
            ["ZoneRailWays"] = (-656.4f, 463.8f),
            ["ZoneSanatorium1"] = (-181.4f, -118.4f),
            ["ZoneSanatorium2"] = (-316.2f, -116.6f),
            ["ZoneSmuglers"] = (-621.2f, -193.9f),
            ["ZoneStartVillage"] = (406.4f, 154.3f),
            ["ZoneTunnel"] = (391.1f, 284.0f),
        },
        ["TarkovStreets"] = new()
        {
            ["ZoneCarShowroom"] = (98.4f, 328.1f),
            ["ZoneCard1"] = (92.0f, -47.6f),
            ["ZoneCinema"] = (-148.2f, 409.5f),
            ["ZoneClimova"] = (-137.9f, -53.3f),
            ["ZoneColumn"] = (6.0f, 222.6f),
            ["ZoneConcordiaParking"] = (246.3f, 367.7f),
            ["ZoneConcordia_1"] = (189.1f, 376.8f),
            ["ZoneConstruction"] = (198.8f, 298.4f),
            ["ZoneFactory"] = (-144.3f, 278.1f),
            ["ZoneHotel_1"] = (-73.4f, 164.9f),
            ["ZoneHotel_2"] = (-123.0f, 109.4f),
            ["ZoneMvd"] = (-255.5f, 138.6f),
            ["ZoneSW00"] = (207.3f, 119.0f),
            ["ZoneSW01"] = (85.4f, 146.3f),
            ["ZoneSnipeBuilding"] = (-24.7f, 228.1f),
            ["ZoneSnipeCarShowroom"] = (56.8f, 303.6f),
            ["ZoneSnipeCard"] = (50.2f, -17.9f),
            ["ZoneSnipeCinema"] = (-166.4f, 400.6f),
            ["ZoneSnipeSW01"] = (89.3f, 101.3f),
            ["ZoneSnipeStilo"] = (-140.4f, -8.5f),
            ["ZoneStilo"] = (-61.7f, -43.0f),
        },
        ["Woods"] = new()
        {
            ["ZoneBigRocks"] = (-196.8f, -193.6f),
            ["ZoneBrokenVill"] = (-95.1f, -699.8f),
            ["ZoneClearVill"] = (-471.2f, -344.8f),
            ["ZoneDepo"] = (-669.0f, 130.4f),
            ["ZoneHighRocks"] = (-140.8f, -206.8f),
            ["ZoneHouse"] = (395.1f, 181.7f),
            ["ZoneMiniHouse"] = (314.0f, -131.7f),
            ["ZoneRedHouse"] = (-492.4f, 134.0f),
            ["ZoneRoad"] = (-214.6f, 314.8f),
            ["ZoneScavBase2"] = (197.8f, -719.2f),
            ["ZoneStoneBunker"] = (-285.1f, -428.9f),
            ["ZoneUsecBase"] = (298.0f, -490.7f),
            ["ZoneWoodCutter"] = (50.7f, -113.3f),
        },
    };

    public static bool TryGet(string mapId, string zoneName, out (float X, float Z) pos)
    {
        pos = default;
        return ByMap.TryGetValue(mapId, out var zones) && zones.TryGetValue(zoneName, out pos);
    }
}
