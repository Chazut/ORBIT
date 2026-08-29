using Orbit.Server.Config;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace Orbit.Server.Load;

[Injectable(TypePriority = OnLoadOrder.Preload + 10)]
public sealed class OrbitServerLoad(ISptLogger<OrbitServerLoad> logger, ConfigService configService) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        configService.Load();
        logger.Info("[ORBIT] Server mod loaded - config UI at /orbit");
        return Task.CompletedTask;
    }
}
