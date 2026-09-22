#nullable disable
using System.Collections.Generic;

namespace Orbit.Server.Zones;

// Initial editor metadata from SPT 4.1 spawn positions (including the installed 1.0 backports).
// The client refreshes it with the loaded scene's spawn and patrol points at raid start.
public static class NativeFloorDefaults
{
    public static readonly Dictionary<string, Dictionary<string, string>> ByMap = new()
    {
        ["bigmap"] = new()
        {
            ["ZoneBlockPost"] = "base",
            ["ZoneBlockPostSniper"] = "2nd-floor",
            ["ZoneBrige"] = "base",
            ["ZoneCrossRoad"] = "base",
            ["ZoneCustoms"] = "base",
            ["ZoneDormitory"] = "base|2nd-floor",
            ["ZoneFactoryCenter"] = "base",
            ["ZoneFactorySide"] = "base",
            ["ZoneGasStation"] = "base",
            ["ZoneOldAZS"] = "base",
            ["ZoneScavBase"] = "base",
            ["ZoneSnipeBrige"] = "base",
            ["ZoneSnipeFactory"] = "2nd-floor",
            ["ZoneSnipeTower"] = "4th-floor",
            ["ZoneTankSquare"] = "base|3rd-floor",
            ["ZoneWade"] = "base",
        },
        ["factory4_day"] = new()
        {
            ["BotZone"] = "base|2nd-floor|3rd-floor|tunnels",
        },
        ["factory4_night"] = new()
        {
            ["BotZone"] = "base|2nd-floor|3rd-floor|tunnels",
        },
        ["Interchange"] = new()
        {
            ["ZoneCenter"] = "3rd-floor",
            ["ZoneCenterBot"] = "2nd-floor|3rd-floor",
            ["ZoneGoshan"] = "2nd-floor",
            ["ZoneIDEA"] = "2nd-floor",
            ["ZoneIDEAPark"] = "base",
            ["ZoneOLI"] = "base|2nd-floor",
            ["ZoneOLIPark"] = "base",
            ["ZonePowerStation"] = "base",
            ["ZoneRoad"] = "base",
            ["ZoneTrucks"] = "base|2nd-floor",
        },
        ["Interchange@rework"] = new()
        {
            ["ZoneBearCamp"] = "base",
            ["ZoneCenter"] = "3rd-floor",
            ["ZoneCenterBot"] = "2nd-floor|3rd-floor",
            ["ZoneGoshan"] = "2nd-floor",
            ["ZoneIDEA"] = "2nd-floor",
            ["ZoneIDEAPark"] = "base",
            ["ZoneOLI"] = "base|2nd-floor",
            ["ZoneOLIPark"] = "base",
            ["ZonePowerStation"] = "base",
            ["ZoneRoad"] = "base",
            ["ZoneTagilla"] = "base",
            ["ZoneTrucks"] = "base|2nd-floor",
        },
        ["laboratory"] = new()
        {
            ["BotZoneBasement"] = "technical",
            ["BotZoneFloor1"] = "base",
            ["BotZoneFloor2"] = "second-level",
            ["BotZoneGate1"] = "base",
            ["BotZoneGate2"] = "base",
        },
        ["Labyrinth"] = new()
        {
        },
        ["Lighthouse"] = new()
        {
            ["Zone_Blockpost"] = "base",
            ["Zone_Bridge"] = "base",
            ["Zone_Chalet"] = "base",
            ["Zone_Containers"] = "base",
            ["Zone_DestroyedHouse"] = "base",
            ["Zone_Hellicopter"] = "base",
            ["Zone_Island"] = "base",
            ["Zone_LongRoad"] = "base",
            ["Zone_Rocks"] = "base",
            ["Zone_RoofBeach"] = "base",
            ["Zone_RoofContainers"] = "base",
            ["Zone_RoofRocks"] = "base",
            ["Zone_SniperPeak"] = "base",
            ["Zone_TreatmentBeach"] = "base",
            ["Zone_TreatmentContainers"] = "base",
            ["Zone_TreatmentRocks"] = "base",
            ["Zone_Village"] = "base",
        },
        ["Lighthouse@rework"] = new()
        {
            ["Zone_Blockpost"] = "base",
            ["Zone_Bridge"] = "base",
            ["Zone_Chalet"] = "base",
            ["Zone_Containers"] = "base",
            ["Zone_DestroyedHouse"] = "base",
            ["Zone_Hellicopter"] = "base",
            ["Zone_Island"] = "base",
            ["Zone_LongRoad"] = "base",
            ["Zone_OldHouse"] = "base",
            ["Zone_Rocks"] = "base",
            ["Zone_TreatmentBeach"] = "base",
            ["Zone_TreatmentContainers"] = "base",
            ["Zone_TreatmentRocks"] = "base",
            ["Zone_Village"] = "base",
        },
        ["RezervBase"] = new()
        {
            ["ZoneBarrack"] = "base",
            ["ZoneBunkerStorage"] = "base|bunkers",
            ["ZonePTOR1"] = "base",
            ["ZonePTOR2"] = "base",
            ["ZoneRailStrorage"] = "base",
            ["ZoneSubCommand"] = "bunkers",
            ["ZoneSubStorage"] = "bunkers",
        },
        ["Sandbox"] = new()
        {
            ["ZoneSandSnipeCenter"] = "3rd-floor",
        },
        ["Sandbox_high"] = new()
        {
            ["ZoneSandSnipeCenter"] = "3rd-floor",
            ["ZoneSandSnipeCenter2"] = "3rd-floor",
            ["ZoneSandbox"] = "base|garage|2nd-floor",
        },
        ["Shoreline"] = new()
        {
            ["ZoneBunkeSniper"] = "base",
            ["ZoneBunker"] = "base",
            ["ZoneBusStation"] = "base",
            ["ZoneForestGasStation"] = "base",
            ["ZoneForestSpawn"] = "base",
            ["ZoneForestTruck"] = "base",
            ["ZoneGasStation"] = "base",
            ["ZoneGreenHouses"] = "base",
            ["ZoneIsland"] = "base",
            ["ZoneMeteoStation"] = "base",
            ["ZonePassClose"] = "base",
            ["ZonePort"] = "base",
            ["ZonePowerStation"] = "base",
            ["ZonePowerStationSniper"] = "base",
            ["ZoneRailWays"] = "base",
            ["ZoneSanatorium1"] = "base",
            ["ZoneSanatorium2"] = "base",
            ["ZoneSmuglers"] = "base",
            ["ZoneStartVillage"] = "base",
            ["ZoneTunnel"] = "base",
        },
        ["TarkovStreets"] = new()
        {
            ["ZoneCarShowroom"] = "base",
            ["ZoneCard1"] = "base",
            ["ZoneCinema"] = "base",
            ["ZoneClimova"] = "base",
            ["ZoneColumn"] = "base",
            ["ZoneConcordiaParking"] = "base",
            ["ZoneConcordia_1"] = "base",
            ["ZoneConstruction"] = "base",
            ["ZoneFactory"] = "base",
            ["ZoneHotel_1"] = "base",
            ["ZoneHotel_2"] = "base",
            ["ZoneMvd"] = "base",
            ["ZoneSW00"] = "base",
            ["ZoneSW01"] = "base",
            ["ZoneSnipeBuilding"] = "4th-floor",
            ["ZoneSnipeCarShowroom"] = "2nd-floor",
            ["ZoneSnipeCard"] = "5th-floor",
            ["ZoneSnipeCinema"] = "4th-floor",
            ["ZoneSnipeSW01"] = "5th-floor",
            ["ZoneSnipeStilo"] = "2nd-floor",
            ["ZoneStilo"] = "base",
        },
        ["Woods"] = new()
        {
            ["ZoneBigRocks"] = "base",
            ["ZoneBrokenVill"] = "base",
            ["ZoneClearVill"] = "base",
            ["ZoneDepo"] = "base",
            ["ZoneHighRocks"] = "base",
            ["ZoneHouse"] = "base",
            ["ZoneMiniHouse"] = "base",
            ["ZoneRedHouse"] = "base",
            ["ZoneRoad"] = "base",
            ["ZoneScavBase2"] = "base",
            ["ZoneStoneBunker"] = "base",
            ["ZoneUsecBase"] = "base",
            ["ZoneWoodCutter"] = "base",
        },
    };
}
