using UnityEngine;

namespace Orbit.Navigation;

// Bunker transit observations with loot branches and stops removed. Used only after autonomous routing fails.
internal static class D2TransitHints
{
    internal static readonly Vector3[][] Paths =
    {
        new Vector3[]
        {
            new(-90.95f, -14.47f, 36.12f),
            new(-79.68f, -14.49f, 42.25f),
            new(-74.00f, -11.95f, 57.85f),
            new(-72.80f, -11.71f, 67.28f),
            new(-70.80f, -13.00f, 69.03f),
            new(-74.20f, -14.57f, 68.37f),
            new(-69.79f, -15.63f, 67.76f),
            new(-74.00f, -17.52f, 69.22f),
            new(-74.22f, -17.53f, 71.05f),
            new(-71.05f, -18.84f, 73.05f),
            new(-68.66f, -18.77f, 88.55f),
            new(-62.67f, -19.23f, 90.87f),
            new(-59.42f, -19.88f, 97.16f),
            new(-69.68f, -19.68f, 103.94f),
            new(-83.32f, -19.68f, 111.68f),
            new(-87.42f, -16.03f, 122.94f),
            new(-86.36f, -15.84f, 130.69f),
            new(-81.55f, -15.84f, 140.44f),
            new(-92.16f, -16.19f, 152.72f),
            new(-91.87f, -16.59f, 156.57f),
            new(-92.85f, -19.48f, 153.24f),
            new(-98.44f, -18.34f, 156.60f),
            new(-107.47f, -18.30f, 160.11f),
            new(-113.85f, -18.31f, 166.55f),
            new(-116.84f, -18.31f, 170.03f),
        },
        new Vector3[]
        {
            new(-109.86f, -14.49f, 40.52f),
            new(-102.85f, -14.49f, 37.75f),
            new(-94.37f, -14.49f, 41.03f),
            new(-87.27f, -14.49f, 40.98f),
            new(-78.95f, -14.46f, 38.51f),
            new(-74.97f, -13.13f, 52.57f),
            new(-73.56f, -11.72f, 65.59f),
            new(-70.36f, -12.78f, 68.97f),
            new(-72.77f, -14.59f, 67.52f),
            new(-71.29f, -16.05f, 68.69f),
            new(-75.17f, -17.51f, 69.66f),
            new(-74.15f, -17.54f, 71.27f),
            new(-69.59f, -18.75f, 82.26f),
            new(-61.91f, -19.82f, 91.45f),
            new(-65.60f, -19.80f, 101.75f),
            new(-78.06f, -19.74f, 108.97f),
            new(-89.08f, -19.72f, 117.33f),
            new(-89.49f, -16.03f, 124.86f),
            new(-86.50f, -15.88f, 129.96f),
            new(-82.99f, -15.88f, 136.76f),
            new(-81.57f, -15.88f, 140.86f),
            new(-93.36f, -16.21f, 154.12f),
            new(-92.89f, -16.16f, 154.83f),
            new(-89.12f, -19.47f, 152.12f),
            new(-100.29f, -18.41f, 157.79f),
            new(-107.01f, -18.30f, 159.76f),
            new(-115.25f, -18.31f, 167.94f),
        },
    };
}
