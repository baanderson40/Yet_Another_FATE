using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;

namespace YetAnotherFATE.Services.Fates;

public sealed class FateRewardTracker : IFateRewardTracker, IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private bool sessionActive;
    private bool disposed;
    // PostSetup is the reference reward signal. Count each addon lifecycle once;
    // addon addresses are reused, so release the key on finalize.
    private readonly HashSet<nint> openRewards = new();

    public int CompletedCount { get; private set; }
    public int? RunUntil { get; private set; }
    public int? Remaining => RunUntil is { } target ? Math.Max(0, target - CompletedCount) : null;
    public bool PendingStop { get; private set; }
    public event Action? ManualStopRequested;

    public FateRewardTracker(IAddonLifecycle addonLifecycle, Configuration configuration, IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.configuration = configuration;
        this.log = log;
        SynchronizeSession();
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "FateReward", OnReward);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FateReward", OnRewardFinalized);
    }

    public void SynchronizeSession()
    {
        if (sessionActive == configuration.AutomatorEnabled)
        {
            return;
        }

        sessionActive = configuration.AutomatorEnabled;
        RunUntil = null;
        PendingStop = false;
        if (sessionActive)
        {
            CompletedCount = 0;
        }
    }

    public void Start(int count)
    {
        configuration.AutomatorEnabled = true;
        SynchronizeSession();
        RunUntil = Math.Max(1, count);
        PendingStop = CompletedCount >= RunUntil;
        configuration.Save();
    }

    public void Start()
    {
        configuration.AutomatorEnabled = true;
        SynchronizeSession();
        RunUntil = null;
        PendingStop = false;
        configuration.Save();
    }

    public void Stop()
    {
        StopSession();
        ManualStopRequested?.Invoke();
    }

    private void StopSession()
    {
        configuration.AutomatorEnabled = false;
        SynchronizeSession();
        configuration.Save();
    }

    public void RequestStopWhenSafe()
    {
        SynchronizeSession();
        if (sessionActive) PendingStop = true;
    }

    public void CompleteSafeStop()
    {
        if (PendingStop)
        {
            StopSession();
        }
    }

    private void OnReward(AddonEvent type, AddonArgs args)
    {
        if (!openRewards.Add(args.Addon.Address))
            return;
        SynchronizeSession();
        if (disposed || !sessionActive)
        {
            return;
        }

        CompletedCount++;
        if (RunUntil is { } target && CompletedCount >= target)
        {
            PendingStop = true;
        }

        log.Debug("FateRewardTracker: completed={Completed} target={Target} pendingStop={PendingStop}", CompletedCount, RunUntil?.ToString() ?? "unlimited", PendingStop);
    }

    private void OnRewardFinalized(AddonEvent type, AddonArgs args)
        => openRewards.Remove(args.Addon.Address);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        addonLifecycle.UnregisterListener(OnReward);
        addonLifecycle.UnregisterListener(OnRewardFinalized);
    }
}
