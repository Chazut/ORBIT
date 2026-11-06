using System;

namespace Orbit.Looting;

internal sealed class LootInventoryBusyException : Exception
{
    internal LootInventoryBusyException() : base("Inventory operation still pending") { }
}
