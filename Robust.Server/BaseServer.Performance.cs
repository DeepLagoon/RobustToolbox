using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Physics.Systems;

namespace Robust.Server;

internal sealed partial class BaseServer
{
    private void ConfigurePerformanceLogging()
    {
        _tickTiming.ConfigurePerformance(_config.GetCVar(CVars.PerformanceLogLevel),
            _config.GetCVar(CVars.PerformanceLogInterval), _config.GetCVar(CVars.PerformanceSlowInterval),
            _config.GetCVar(CVars.PerformanceTopCount));
        ConfigurePerformanceObservers();
    }

    private void ConfigurePerformanceObservers()
    {
        var systems = (EntitySystemManager)_entityManager.EntitySysManager;
        systems.UpdateTiming = _tickTiming.Enabled ? _tickTiming.RecordSystem : null;
        systems.UpdateAllocationTiming = _tickTiming.DetailLevel >= 2 ? _tickTiming.RecordSystemAllocation : null;
        ((EntityManager)_entityManager).DiagnosticWorkTiming = _tickTiming.DetailLevel > 0 ? _tickTiming.RecordDetail : null;
        _entityManager.System<SharedPhysicsSystem>().DiagnosticTiming =
            _tickTiming.DetailLevel > 0 ? _tickTiming.RecordDetail : null;
    }
}
