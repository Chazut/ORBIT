using Microsoft.AspNetCore.Components;
using Orbit.Server.Config;

namespace Orbit.Server.Web;

/// <summary>
/// Base class for every config page. Provides the ConfigService + Cfg accessor and re-renders the
/// page when the config object is swapped out from under it (the AppBar's "Discard all"): the page's
/// bindings otherwise keep showing values from the discarded instance, since Blazor does not re-render
/// a child whose parameters did not change.
/// </summary>
public abstract class OrbitConfigPage : ComponentBase, IDisposable
{
    [Inject] protected ConfigService Configs { get; set; } = default!;

    protected OrbitServerConfig Cfg => Configs.Config;

    protected override void OnInitialized() => Configs.ConfigReplaced += OnConfigReplaced;

    private void OnConfigReplaced() => InvokeAsync(StateHasChanged);

    public void Dispose() => Configs.ConfigReplaced -= OnConfigReplaced;
}
