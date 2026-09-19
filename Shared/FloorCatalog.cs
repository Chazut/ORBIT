// Floor/render metadata from tarkov.dev, matching the SPT layouts used by RaidReview.
// Source: https://github.com/the-hideout/tarkov-dev/blob/main/src/data/maps.json
// Keep vanilla and rework entries separate when updating the catalogue.
#nullable disable
using System.Collections.Generic;

namespace Orbit.Zones;

public static class FloorCatalog
{
    public static readonly Dictionary<string, FloorMap> Maps = new Dictionary<string, FloorMap>
    {
        ["bigmap"] = new FloorMap
        {
            Rotation = 180.0f,
            Bounds = new float[] { 698.0f, -307.0f, -372.0f, 237.0f },
            Transform = new float[] { 0.239f, 168.65f, 0.239f, 136.35f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = "https://assets.tarkov.dev/maps/svg/Customs-Ground_Level.svg", TilePath = "https://assets.tarkov.dev/maps/customs_0.16/main/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 698.0f, -307.0f, -372.0f, 237.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-1000.0f, 1000.0f),
                    },
                },
                new MapFloor
                {
                    Id = "underground", Name = "Underground",
                    Svg = "https://assets.tarkov.dev/maps/svg/Customs-Underground_Level.svg", TilePath = "https://assets.tarkov.dev/maps/customs_0.16/underground/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 698.0f, -307.0f, -372.0f, 237.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-1000.0f, 0.5f, new float[] { 635.0f, -137.0f, 620.0f, -125.0f }, new float[] { 473.0f, -122.0f, 458.0f, -110.0f }, new float[] { 314.0f, -173.0f, 308.0f, -184.0f }, new float[] { 349.0f, -88.0f, 323.0f, -32.0f }, new float[] { 219.0f, -158.0f, 193.0f, -137.0f }),
                    },
                },
                new MapFloor
                {
                    Id = "2nd-floor", Name = "2nd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/Customs-Second_Floor.svg", TilePath = "https://assets.tarkov.dev/maps/customs_0.16/2nd/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 698.0f, -307.0f, -372.0f, 237.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(2.7f, 6.5f, new float[] { 243.0f, 190.0f, 165.0f, 125.0f }, new float[] { 116.0f, -83.0f, 72.0f, -170.0f }, new float[] { 356.0f, -30.0f, 341.0f, -84.0f }, new float[] { 334.0f, -52.0f, 321.0f, -59.0f }, new float[] { 589.0f, 10.0f, 577.0f, -1.0f }, new float[] { 580.0f, -104.0f, 532.0f, -134.0f }, new float[] { 625.0f, -120.0f, 599.0f, -139.0f }),
                        new FloorExtent(5.7f, 1000.0f, new float[] { 580.0f, -104.0f, 532.0f, -134.0f }, new float[] { -199.0f, -90.0f, -223.0f, -131.0f }, new float[] { 239.0f, 3.0f, 169.0f, -160.0f }, new float[] { 336.0f, -56.0f, 316.0f, -95.0f }, new float[] { 584.0f, -46.0f, 556.0f, -92.0f }, new float[] { 93.0f, 0.0f, 65.0f, -22.0f }),
                        new FloorExtent(14.0f, 15.0f, new float[] { 497.0f, -44.0f, 450.0f, -90.0f }),
                    },
                },
                new MapFloor
                {
                    Id = "3rd-floor", Name = "3rd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/Customs-Third_Floor.svg", TilePath = "https://assets.tarkov.dev/maps/customs_0.16/3rd/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 698.0f, -307.0f, -372.0f, 237.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(5.7f, 1000.0f, new float[] { 243.0f, 190.0f, 165.0f, 125.0f }),
                    },
                },
            },
        },
        ["factory4_day"] = new FloorMap
        {
            Rotation = 90.0f,
            Bounds = new float[] { 77.0f, -64.5f, -65.5f, 67.4f },
            Transform = new float[] { 1.629f, 119.9f, 1.629f, 139.3f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = "https://assets.tarkov.dev/maps/svg/Factory-Ground_Floor.svg", TilePath = "https://assets.tarkov.dev/maps/factory/main/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 77.0f, -64.5f, -65.5f, 67.4f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-1.0f, 3.0f),
                    },
                },
                new MapFloor
                {
                    Id = "2nd-floor", Name = "2nd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/Factory-Second_Floor.svg", TilePath = "https://assets.tarkov.dev/maps/factory/2nd/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 77.0f, -64.5f, -65.5f, 67.4f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(3.0f, 6.0f),
                    },
                },
                new MapFloor
                {
                    Id = "3rd-floor", Name = "3rd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/Factory-Third_Floor.svg", TilePath = "https://assets.tarkov.dev/maps/factory/3rd/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 77.0f, -64.5f, -65.5f, 67.4f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(6.0f, 10000.0f),
                    },
                },
                new MapFloor
                {
                    Id = "tunnels", Name = "Tunnels",
                    Svg = "https://assets.tarkov.dev/maps/svg/Factory-Basement.svg", TilePath = "https://assets.tarkov.dev/maps/factory/tunnels/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 77.0f, -64.5f, -65.5f, 67.4f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-10000.0f, -1.0f),
                    },
                },
            },
        },
        ["factory4_night"] = new FloorMap
        {
            Rotation = 90.0f,
            Bounds = new float[] { 77.0f, -64.5f, -65.5f, 67.4f },
            Transform = new float[] { 1.629f, 119.9f, 1.629f, 139.3f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = "https://assets.tarkov.dev/maps/svg/Factory-Ground_Floor.svg", TilePath = "https://assets.tarkov.dev/maps/factory/main/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 77.0f, -64.5f, -65.5f, 67.4f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-1.0f, 3.0f),
                    },
                },
                new MapFloor
                {
                    Id = "2nd-floor", Name = "2nd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/Factory-Second_Floor.svg", TilePath = "https://assets.tarkov.dev/maps/factory/2nd/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 77.0f, -64.5f, -65.5f, 67.4f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(3.0f, 6.0f),
                    },
                },
                new MapFloor
                {
                    Id = "3rd-floor", Name = "3rd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/Factory-Third_Floor.svg", TilePath = "https://assets.tarkov.dev/maps/factory/3rd/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 77.0f, -64.5f, -65.5f, 67.4f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(6.0f, 10000.0f),
                    },
                },
                new MapFloor
                {
                    Id = "tunnels", Name = "Tunnels",
                    Svg = "https://assets.tarkov.dev/maps/svg/Factory-Basement.svg", TilePath = "https://assets.tarkov.dev/maps/factory/tunnels/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 77.0f, -64.5f, -65.5f, 67.4f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-10000.0f, -1.0f),
                    },
                },
            },
        },
        ["Interchange"] = new FloorMap
        {
            Rotation = 180.0f,
            Bounds = new float[] { 530.0f, -439.0f, -364.0f, 452.0f },
            Transform = new float[] { 0.265f, 150.6f, 0.265f, 134.6f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = "https://assets.tarkov.dev/maps/svg/Interchange-Ground_Level.svg", TilePath = "https://assets.tarkov.dev/maps/interchange/main/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 530.0f, -439.0f, -364.0f, 452.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-10000.0f, 10000.0f),
                    },
                },
                new MapFloor
                {
                    Id = "2nd-floor", Name = "2nd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/Interchange-First_Floor.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 530.0f, -439.0f, -364.0f, 452.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(25.0f, 34.0f, new float[] { 120.0f, 218.0f, -222.0f, -327.0f }),
                    },
                },
                new MapFloor
                {
                    Id = "3rd-floor", Name = "3rd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/Interchange-Second_Floor.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 530.0f, -439.0f, -364.0f, 452.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(34.0f, 1000.0f, new float[] { 120.0f, 218.0f, -222.0f, -327.0f }),
                    },
                },
            },
        },
        ["Interchange@rework"] = new FloorMap
        {
            Rotation = 180.0f,
            Bounds = new float[] { 598.0f, -442.0f, -433.0f, 426.0f },
            Transform = new float[] { 0.265f, 150.6f, 0.265f, 134.6f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = "https://assets.tarkov.dev/maps/svg/Interchange.svg", TilePath = null,
                    HideLayers = new string[] { "First_Floor", "Second_Floor" }, ImageBounds = new float[] { 598.0f, -442.0f, -433.0f, 426.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-10000.0f, 10000.0f),
                    },
                },
                new MapFloor
                {
                    Id = "2nd-floor", Name = "2nd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/Interchange.svg", TilePath = null,
                    HideLayers = new string[] { "Ground_Level", "Second_Floor" }, ImageBounds = new float[] { 598.0f, -442.0f, -433.0f, 426.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(25.0f, 34.0f, new float[] { 120.0f, 218.0f, -222.0f, -327.0f }),
                    },
                },
                new MapFloor
                {
                    Id = "3rd-floor", Name = "3rd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/Interchange.svg", TilePath = null,
                    HideLayers = new string[] { "Ground_Level", "First_Floor" }, ImageBounds = new float[] { 598.0f, -442.0f, -433.0f, 426.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(34.0f, 1000.0f, new float[] { 120.0f, 218.0f, -222.0f, -327.0f }),
                    },
                },
            },
        },
        ["laboratory"] = new FloorMap
        {
            Rotation = 270.0f,
            Bounds = new float[] { -80.0f, -477.0f, -287.0f, -193.0f },
            Transform = new float[] { 0.575f, 281.2f, 0.575f, 193.7f },
            TileSize = 175, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = null, TilePath = "https://assets.tarkov.dev/maps/labs_v4/1st/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { -80.0f, -477.0f, -287.0f, -193.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-0.9f, 3.0f),
                    },
                },
                new MapFloor
                {
                    Id = "second-level", Name = "Second Level",
                    Svg = null, TilePath = "https://assets.tarkov.dev/maps/labs_v4/2nd/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { -80.0f, -477.0f, -287.0f, -193.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(3.0f, 10000.0f, new float[] { -101.0f, -422.0f, -271.0f, -270.0f }),
                    },
                },
                new MapFloor
                {
                    Id = "technical", Name = "Technical",
                    Svg = null, TilePath = "https://assets.tarkov.dev/maps/labs_v4/technical/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { -80.0f, -477.0f, -287.0f, -193.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-10000.0f, -0.9f),
                    },
                },
            },
        },
        ["Labyrinth"] = new FloorMap
        {
            Rotation = 270.0f,
            Bounds = new float[] { -52.0f, -37.0f, 53.0f, 76.0f },
            Transform = new float[] { 2.115f, 85.5f, 2.115f, 128.0f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = null, TilePath = "https://assets.tarkov.dev/maps/labyrinth/main/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { -52.0f, -37.0f, 53.0f, 76.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-10000.0f, 10000.0f),
                    },
                },
            },
        },
        ["Lighthouse"] = new FloorMap
        {
            Rotation = 180.0f,
            Bounds = new float[] { 515.0f, -998.0f, -545.0f, 725.0f },
            Transform = new float[] { 0.2f, 0.0f, 0.2f, 0.0f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = "https://assets.tarkov.dev/maps/svg/Lighthouse.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 515.0f, -998.0f, -545.0f, 725.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-10000.0f, 10000.0f),
                    },
                },
            },
        },
        ["Lighthouse@rework"] = new FloorMap
        {
            Rotation = 180.0f,
            Bounds = new float[] { 515.0f, -998.0f, -545.0f, 725.0f },
            Transform = new float[] { 0.2f, 0.0f, 0.2f, 0.0f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = "https://assets.tarkov.dev/maps/svg/Lighthouse.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 515.0f, -998.0f, -545.0f, 725.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-10000.0f, 10000.0f),
                    },
                },
            },
        },
        ["RezervBase"] = new FloorMap
        {
            Rotation = 180.0f,
            Bounds = new float[] { 289.0f, -293.0f, -303.0f, 244.0f },
            Transform = new float[] { 0.395f, 122.0f, 0.395f, 137.65f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = "https://assets.tarkov.dev/maps/svg/Reserve-Ground_Level.svg", TilePath = "https://assets.tarkov.dev/maps/reserve/main/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 289.0f, -274.0f, -303.0f, 272.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-7.0f, 10000.0f),
                    },
                },
                new MapFloor
                {
                    Id = "bunkers", Name = "Bunkers",
                    Svg = "https://assets.tarkov.dev/maps/svg/Reserve-Bunkers.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 289.0f, -274.0f, -303.0f, 272.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-10000.0f, -7.0f),
                    },
                },
            },
        },
        ["Sandbox"] = new FloorMap
        {
            Rotation = 180.0f,
            Bounds = new float[] { 249.0f, -124.0f, -99.0f, 364.0f },
            Transform = new float[] { 0.524f, 167.3f, 0.524f, 65.1f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = "https://assets.tarkov.dev/maps/svg/GroundZero-Ground_Level.svg", TilePath = "https://assets.tarkov.dev/maps/groundzero/main_summer/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 249.0f, -124.0f, -99.0f, 364.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-1000.0f, 28.0f),
                    },
                },
                new MapFloor
                {
                    Id = "garage", Name = "Garage",
                    Svg = "https://assets.tarkov.dev/maps/svg/GroundZero-Underground_Level.svg", TilePath = "https://assets.tarkov.dev/maps/groundzero/garage/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 249.0f, -124.0f, -99.0f, 364.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-1000.0f, 21.0f, new float[] { 117.0f, -100.0f, 43.0f, 190.0f }, new float[] { 143.0f, 49.0f, 117.0f, 80.0f }),
                    },
                },
                new MapFloor
                {
                    Id = "2nd-floor", Name = "2nd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/GroundZero-Second_Floor.svg", TilePath = "https://assets.tarkov.dev/maps/groundzero/2nd/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 249.0f, -124.0f, -99.0f, 364.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(28.0f, 32.3f),
                        new FloorExtent(26.0f, 31.0f, new float[] { 98.0f, 216.0f, 91.0f, 228.0f }),
                    },
                },
                new MapFloor
                {
                    Id = "3rd-floor", Name = "3rd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/GroundZero-Third_Floor.svg", TilePath = "https://assets.tarkov.dev/maps/groundzero/3rd/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 249.0f, -124.0f, -99.0f, 364.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(32.3f, 1000.0f),
                    },
                },
            },
        },
        ["Sandbox_high"] = new FloorMap
        {
            Rotation = 180.0f,
            Bounds = new float[] { 249.0f, -124.0f, -99.0f, 364.0f },
            Transform = new float[] { 0.524f, 167.3f, 0.524f, 65.1f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = "https://assets.tarkov.dev/maps/svg/GroundZero-Ground_Level.svg", TilePath = "https://assets.tarkov.dev/maps/groundzero/main_summer/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 249.0f, -124.0f, -99.0f, 364.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-1000.0f, 28.0f),
                    },
                },
                new MapFloor
                {
                    Id = "garage", Name = "Garage",
                    Svg = "https://assets.tarkov.dev/maps/svg/GroundZero-Underground_Level.svg", TilePath = "https://assets.tarkov.dev/maps/groundzero/garage/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 249.0f, -124.0f, -99.0f, 364.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-1000.0f, 21.0f, new float[] { 117.0f, -100.0f, 43.0f, 190.0f }, new float[] { 143.0f, 49.0f, 117.0f, 80.0f }),
                    },
                },
                new MapFloor
                {
                    Id = "2nd-floor", Name = "2nd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/GroundZero-Second_Floor.svg", TilePath = "https://assets.tarkov.dev/maps/groundzero/2nd/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 249.0f, -124.0f, -99.0f, 364.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(28.0f, 32.3f),
                        new FloorExtent(26.0f, 31.0f, new float[] { 98.0f, 216.0f, 91.0f, 228.0f }),
                    },
                },
                new MapFloor
                {
                    Id = "3rd-floor", Name = "3rd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/GroundZero-Third_Floor.svg", TilePath = "https://assets.tarkov.dev/maps/groundzero/3rd/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 249.0f, -124.0f, -99.0f, 364.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(32.3f, 1000.0f),
                    },
                },
            },
        },
        ["Shoreline"] = new FloorMap
        {
            Rotation = 180.0f,
            Bounds = new float[] { 504.0f, -415.0f, -1056.0f, 618.0f },
            Transform = new float[] { 0.16f, 83.2f, 0.16f, 111.1f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = "https://assets.tarkov.dev/maps/svg/Shoreline-Ground_Level.svg", TilePath = "https://assets.tarkov.dev/maps/shoreline/main_summer/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 504.0f, -415.0f, -1056.0f, 618.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-1000.0f, -1.0f),
                    },
                },
                new MapFloor
                {
                    Id = "underground", Name = "Underground",
                    Svg = "https://assets.tarkov.dev/maps/svg/Shoreline-Underground_Level.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 504.0f, -415.0f, -1056.0f, 618.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-1000.0f, -5.0f, new float[] { -137.0f, -68.0f, -237.0f, -104.0f }, new float[] { -234.0f, -134.0f, -268.0f, -163.0f }),
                    },
                },
                new MapFloor
                {
                    Id = "2nd-floor", Name = "2nd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/Shoreline-Second_Floor.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 504.0f, -415.0f, -1056.0f, 618.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-1.0f, 2.0f),
                    },
                },
                new MapFloor
                {
                    Id = "3rd-floor", Name = "3rd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/Shoreline-Third_Floor.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 504.0f, -415.0f, -1056.0f, 618.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(2.0f, 1000.0f),
                    },
                },
            },
        },
        ["TarkovStreets"] = new FloorMap
        {
            Rotation = 180.0f,
            Bounds = new float[] { 323.0f, -295.0f, -280.0f, 532.0f },
            Transform = new float[] { 0.38f, 0.0f, 0.38f, 0.0f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = "https://assets.tarkov.dev/maps/svg/StreetsOfTarkov-Ground_Level.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 323.0f, -295.0f, -280.0f, 532.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-6.0f, 10.0f),
                    },
                },
                new MapFloor
                {
                    Id = "underground", Name = "Underground",
                    Svg = "https://assets.tarkov.dev/maps/svg/StreetsOfTarkov-Underground_Level.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 323.0f, -295.0f, -280.0f, 532.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-10000.0f, -6.0f),
                    },
                },
                new MapFloor
                {
                    Id = "2nd-floor", Name = "2nd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/StreetsOfTarkov-Second_Floor.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 323.0f, -295.0f, -280.0f, 532.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(10.0f, 15.0f),
                    },
                },
                new MapFloor
                {
                    Id = "3rd-floor", Name = "3rd Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/StreetsOfTarkov-Third_Floor.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 323.0f, -295.0f, -280.0f, 532.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(15.0f, 20.0f),
                    },
                },
                new MapFloor
                {
                    Id = "4th-floor", Name = "4th Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/StreetsOfTarkov-Fourth_Floor.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 323.0f, -295.0f, -280.0f, 532.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(20.0f, 25.0f),
                    },
                },
                new MapFloor
                {
                    Id = "5th-floor", Name = "5th Floor",
                    Svg = "https://assets.tarkov.dev/maps/svg/StreetsOfTarkov-Fifth_Floor.svg", TilePath = null,
                    HideLayers = null, ImageBounds = new float[] { 323.0f, -295.0f, -280.0f, 532.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(25.0f, 10000.0f),
                    },
                },
            },
        },
        ["Woods"] = new FloorMap
        {
            Rotation = 180.0f,
            Bounds = new float[] { 646.0f, -914.0f, -761.0f, 442.0f },
            Transform = new float[] { 0.1855f, 112.95f, 0.1855f, 167.85f },
            TileSize = 256, TileZoom = 3,
            Floors = new MapFloor[]
            {
                new MapFloor
                {
                    Id = "base", Name = "Base",
                    Svg = "https://assets.tarkov.dev/maps/svg/Woods.svg", TilePath = "https://assets.tarkov.dev/maps/woods/main_0.16/{z}/{x}/{y}.png",
                    HideLayers = null, ImageBounds = new float[] { 646.0f, -914.0f, -761.0f, 442.0f },
                    Extents = new FloorExtent[]
                    {
                        new FloorExtent(-10000.0f, 10000.0f),
                    },
                },
            },
        },
    };

    public static FloorMap For(string mapId)
        => Maps.TryGetValue(mapId, out var map) ? map : null;

    public static bool Matches(string mapId, string floorId, float x, float y, float z)
        => string.IsNullOrEmpty(floorId) || (For(mapId)?.Matches(floorId, x, y, z) ?? false);
}
