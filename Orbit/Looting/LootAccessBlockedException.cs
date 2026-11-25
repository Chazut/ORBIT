namespace Orbit.Looting;
internal sealed class LootAccessBlockedException(string collider, float distance, bool wall = false) : System.Exception("Loot target is obstructed")
{
    internal bool Wall => wall;
    internal string Collider => collider;
    internal float Distance => distance;
}
