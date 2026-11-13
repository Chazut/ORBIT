namespace Orbit.Looting;
internal sealed class LootAccessBlockedException(string collider, float distance) : System.Exception("Loot target is obstructed")
{
    internal string Collider => collider;
    internal float Distance => distance;
}
