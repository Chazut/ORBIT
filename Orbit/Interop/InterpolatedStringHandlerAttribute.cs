// netstandard2.1 predates this .NET 6 BCL attribute; the C# compiler only needs a type with this exact
// namespace + name to exist to enable custom interpolated-string handlers (see Orbit.DebugMessageHandler).
namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
internal sealed class InterpolatedStringHandlerAttribute : Attribute
{
}
