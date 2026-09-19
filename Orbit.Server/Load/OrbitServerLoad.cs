using Orbit.Server.Config;
using Orbit.Server.Zones;
using Orbit.Server.Presets;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace Orbit.Server.Load;

[Injectable(TypePriority = OnLoadOrder.Preload + 10)]
public sealed class OrbitServerLoad(ISptLogger<OrbitServerLoad> logger, PresetService presets) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        presets.Initialize();
        logger.Info("[ORBIT] Server mod loaded - config UI at /orbit");
        return Task.CompletedTask;
    }
}
