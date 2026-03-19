using System;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Chains;

namespace YetAnotherFATE.Modules.Automator;

public sealed class AutomatorModule : IDisposable
{
    private readonly IFramework framework;
    private readonly AutomatorChain automatorChain;
    private bool initialized;

    public AutomatorModule(IFramework framework, AutomatorChain automatorChain)
    {
        this.framework = framework;
        this.automatorChain = automatorChain;
    }

    public void Initialize()
    {
        if (initialized)
        {
            return;
        }

        framework.Update += OnFrameworkUpdate;
        initialized = true;
    }

    private void OnFrameworkUpdate(IFramework _) => automatorChain.Tick();

    public void Dispose()
    {
        if (!initialized)
        {
            return;
        }

        framework.Update -= OnFrameworkUpdate;
        initialized = false;
    }
}
