using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Web;
using SemVerVersion = SemanticVersioning.Version;
using SemVerRange = SemanticVersioning.Range;

namespace Orbit.Server;

public record OrbitServerMetadata : IModMetadata, IModBlazorMetadata
{
    public string ModGuid { get; init; } = "com.chazut.orbit.server";
    public string Name { get; init; } = "ORBIT Server";
    public string Author { get; init; } = "Chazut";
    public List<string>? Contributors { get; init; }
    public SemVerVersion Version { get; init; } = new("2.0.0");
    public SemVerRange SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemVerRange>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/Chazut/ORBIT";
    public string License { get; init; } = "MIT";

    // Blazor web UI integration: the config page shows up in the server UI's mod links.
    public string? WWWRootUrl { get; init; }
    public string? HomePage { get; init; } = "/orbit";
    public string? HomePageDescription { get; init; } = "Configure ORBIT";
}
