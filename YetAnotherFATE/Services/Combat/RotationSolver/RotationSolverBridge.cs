using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Combat.RotationSolver;

public sealed class RotationSolverBridge : IRotationSolverBridge
{
    private const string AutodutyEndpoint = "RotationSolverReborn.AutodutyChangeOperatingMode";

    private const int StateOff = 0;
    private const int StateAuto = 1;
    private const int TargetNearest = 8;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    private ICallGateSubscriber<int, int, object?>? autodutySubscriber;
    private uint? engagedFateId;
    private bool autoEngaged;
    private DateTimeOffset nextEngageRefresh;

    public RotationSolverBridge(IDalamudPluginInterface pluginInterface, Dalamud.Plugin.Services.IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public void EngageNearestAuto(FateSelection selection)
    {
        if (engagedFateId == selection.Fate.FateId && DateTimeOffset.UtcNow < nextEngageRefresh)
        {
            return;
        }

        if (!TryInitializeIpc())
        {
            return;
        }

        try
        {
            autodutySubscriber?.InvokeAction(StateAuto, TargetNearest);
            engagedFateId = selection.Fate.FateId;
            autoEngaged = true;
            nextEngageRefresh = DateTimeOffset.UtcNow.AddSeconds(5);
            log.Debug("RotationSolverBridge: engaged auto targeting for fate #{FateId}", selection.Fate.FateId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "RotationSolverBridge: failed to send AutodutyChangeOperatingMode");
        }
    }

    public void EngageNearestAuto()
    {
        if (autoEngaged && DateTimeOffset.UtcNow < nextEngageRefresh)
            return;

        if (!TryInitializeIpc())
            return;

        try
        {
            autodutySubscriber?.InvokeAction(StateAuto, TargetNearest);
            engagedFateId = null;
            autoEngaged = true;
            nextEngageRefresh = DateTimeOffset.UtcNow.AddSeconds(5);
            log.Debug("RotationSolverBridge: engaged auto targeting for combat clearance");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "RotationSolverBridge: failed to engage auto targeting for combat clearance");
        }
    }

    public void EngageManualTarget()
    {
        if (!TryInitializeIpc())
            return;
        try
        {
            autodutySubscriber?.InvokeAction(3, TargetNearest);
            engagedFateId = null;
            autoEngaged = true;
            nextEngageRefresh = DateTimeOffset.UtcNow.AddSeconds(5);
            log.Debug("RotationSolverBridge: engaged manual-target rotation for combat clearance");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "RotationSolverBridge: failed to engage manual-target mode for combat clearance");
        }
    }

    public void NotifyFateEnded()
    {
        engagedFateId = null;
        if (!autoEngaged || !TryInitializeIpc())
        {
            autoEngaged = false;
            return;
        }

        try
        {
            autodutySubscriber?.InvokeAction(StateOff, TargetNearest);
            autoEngaged = false;
            log.Debug("RotationSolverBridge: disengaged auto mode");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "RotationSolverBridge: failed to disengage auto mode");
        }
    }

    private bool TryInitializeIpc()
    {
        if (autodutySubscriber != null)
        {
            return true;
        }

        try
        {
            autodutySubscriber = pluginInterface.GetIpcSubscriber<int, int, object?>(AutodutyEndpoint);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "RotationSolverBridge: failed to hook {Endpoint}", AutodutyEndpoint);
        }

        return autodutySubscriber != null;
    }
}
