using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace YetAnotherFATE.Services.Combat.Wrath;

public sealed class WrathComboBridge : IWrathComboBridge, IDisposable
{
    private const string ReadyEndpoint = "WrathCombo.IPCReady";
    private const string ComboOptionsEndpoint = "WrathCombo.GetComboOptionNamesForJob";
    private const string RegisterLeaseEndpoint = "WrathCombo.RegisterForLease";
    private const string ReleaseLeaseEndpoint = "WrathCombo.ReleaseControl";
    private const string SetAutoRotationEndpoint = "WrathCombo.SetAutoRotationState";
    private const string SetJobReadyEndpoint = "WrathCombo.SetCurrentJobAutoRotationReady";
    private const string SetConfigEndpoint = "WrathCombo.SetAutoRotationConfigState";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;

    private ICallGateSubscriber<bool>? readySubscriber;
    private ICallGateSubscriber<uint, Dictionary<string, List<string>>?>? comboOptionsSubscriber;
    private ICallGateSubscriber<string, string, Guid?>? registerLeaseSubscriber;
    private ICallGateSubscriber<string, string, string?, Guid?>? registerLeaseCallbackSubscriber;
    private ICallGateSubscriber<Guid, object?>? releaseLeaseSubscriber;
    private ICallGateSubscriber<Guid, WrathSetResult>? setJobReadySubscriber;
    private ICallGateSubscriber<Guid, bool, WrathSetResult>? setAutoRotationResultSubscriber;
    private ICallGateSubscriber<Guid, object, object, WrathSetResult>? setConfigSubscriber;
    private ICallGateProvider<int, string, object?>? callbackProvider;
    private bool callbackRegistered;

    private Guid? activeLease;
    private uint? configuredJobId;
    private bool autoEnabled;
    private bool configApplied;
    private bool leaseReleased;

    public WrathComboBridge(
        IDalamudPluginInterface pluginInterface,
        IPlayerState playerState,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.playerState = playerState;
        this.log = log;
    }

    public bool TryGetComboOptions(out Dictionary<string, List<string>>? combos)
    {
        combos = null;

        if (!EnsureComboSubscribers() || !EnsureIpcReady())
        {
            return false;
        }

        if (!playerState.IsLoaded || !playerState.ClassJob.IsValid)
        {
            log.Debug("WrathComboBridge: player job info unavailable");
            return false;
        }

        var jobId = (uint)playerState.ClassJob.Value.RowId;

        try
        {
            combos = comboOptionsSubscriber?.InvokeFunc(jobId);
            if (combos == null)
            {
                log.Debug("WrathComboBridge: combo option list is null for job {JobId}", jobId);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "WrathComboBridge: GetComboOptionNamesForJob failed");
            return false;
        }
    }

    public bool ConfigureForFateAutomation()
    {
        if (!EnsureAllSubscribers() || !EnsureIpcReady())
        {
            return false;
        }

        if (!EnsureLease())
        {
            return false;
        }

        if (!EnsureJobReady())
        {
            return false;
        }

        if (!autoEnabled && !SetAutoRotationState(true))
        {
            return false;
        }

        if (!configApplied && !ApplyFateConfiguration())
        {
            return false;
        }

        return true;
    }

    public void NotifyAutomationStopped()
    {
        if (activeLease is not { } lease)
        {
            return;
        }

        var autoStopped = !autoEnabled || SetAutoRotationState(false);
        if (!leaseReleased)
        {
            try
            {
                releaseLeaseSubscriber?.InvokeAction(lease);
                leaseReleased = true;
            }
            catch (Exception ex)
            {
                log.Debug(ex, "WrathComboBridge: release lease failed; retaining ownership for retry");
            }
        }

        if (autoStopped && leaseReleased)
        {
            activeLease = null;
            autoEnabled = false;
            configuredJobId = null;
            configApplied = false;
            leaseReleased = false;
        }
    }

    public void Dispose()
    {
        NotifyAutomationStopped();
        if (callbackRegistered)
            callbackProvider?.UnregisterAction();
        callbackRegistered = false;
    }

    private bool ApplyFateConfiguration()
    {
        if (activeLease is not { } lease)
        {
            return false;
        }

        var success = true;
        success &= InvokeConfig(lease, WrathAutoConfigOption.FatePriority, true);
        success &= InvokeConfig(lease, WrathAutoConfigOption.DpsRotationMode, (int)WrathDpsRotationMode.Nearest);

        if (success)
        {
            configApplied = true;
        }

        return success;
    }

    private bool InvokeConfig(Guid lease, WrathAutoConfigOption option, object value)
    {
        try
        {
            var result = setConfigSubscriber?.InvokeFunc(lease, (int)option, value) ?? WrathSetResult.InvalidConfiguration;
            if (result == WrathSetResult.InvalidLease) HandleInvalidLease();
            if (!IsSuccess(result))
                return false;
            log.Debug("WrathComboBridge: set config {Option} => {Value}", option, value);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "WrathComboBridge: failed to set config {Option}", option);
            return false;
        }
    }

    private bool EnsureJobReady()
    {
        if (activeLease is not { } lease)
        {
            return false;
        }

        if (!playerState.IsLoaded || !playerState.ClassJob.IsValid)
        {
            log.Debug("WrathComboBridge: unable to ensure job ready (player not loaded)");
            return false;
        }

        var jobId = (uint)playerState.ClassJob.Value.RowId;
        if (configuredJobId == jobId)
        {
            return true;
        }

        try
        {
            var result = setJobReadySubscriber?.InvokeFunc(lease) ?? WrathSetResult.InvalidLease;
            if (result == WrathSetResult.InvalidLease) HandleInvalidLease();
            if (!IsSuccess(result))
            {
                log.Debug("WrathComboBridge: job-ready request rejected with result {Result}", result);
                return false;
            }
            log.Debug("WrathComboBridge: requested auto-rotation setup for job {JobId}", jobId);
            configuredJobId = jobId;
            configApplied = false;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "WrathComboBridge: failed to set job ready");
            return false;
        }
    }

    private bool SetAutoRotationState(bool enabled)
    {
        if (activeLease is not { } lease)
        {
            return false;
        }

        try
        {
            var result = setAutoRotationResultSubscriber?.InvokeFunc(lease, enabled) ?? WrathSetResult.InvalidLease;
            if (result == WrathSetResult.InvalidLease) HandleInvalidLease();
            if (!IsSuccess(result))
            {
                log.Debug("WrathComboBridge: auto-rotation request rejected with result {Result}", result);
                return false;
            }
            autoEnabled = enabled;
            log.Debug("WrathComboBridge: auto rotation => {State}", enabled);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "WrathComboBridge: failed to toggle auto rotation");
            return false;
        }
    }

    private bool EnsureLease()
    {
        if (activeLease.HasValue)
        {
            return true;
        }

        try
        {
            var lease = registerLeaseCallbackSubscriber?.HasFunction == true
                ? registerLeaseCallbackSubscriber.InvokeFunc("YetAnotherFATE", "YetAnotherFATE", "YetAnotherFATE")
                : registerLeaseSubscriber?.InvokeFunc("YetAnotherFATE", "YetAnotherFATE");
            if (lease is { } guid)
            {
                activeLease = guid;
                leaseReleased = false;
                log.Debug("WrathComboBridge: obtained lease {Lease}", guid);
                return true;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "WrathComboBridge: failed to register lease");
        }

        return false;
    }

    private bool EnsureIpcReady()
    {
        try
        {
            if (readySubscriber == null)
            {
                return false;
            }

            return readySubscriber.InvokeFunc();
        }
        catch (IpcNotReadyError)
        {
            return false;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "WrathComboBridge: IPCReady call failed");
            return false;
        }
    }

    private bool EnsureComboSubscribers()
    {
        if (readySubscriber != null && comboOptionsSubscriber != null)
        {
            return true;
        }

        try
        {
            readySubscriber ??= pluginInterface.GetIpcSubscriber<bool>(ReadyEndpoint);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "WrathComboBridge: failed to hook {Endpoint}", ReadyEndpoint);
        }

        try
        {
            comboOptionsSubscriber ??= pluginInterface.GetIpcSubscriber<uint, Dictionary<string, List<string>>?>(ComboOptionsEndpoint);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "WrathComboBridge: failed to hook {Endpoint}", ComboOptionsEndpoint);
        }

        return readySubscriber != null && comboOptionsSubscriber != null;
    }

    private bool EnsureAllSubscribers()
    {
        if (!EnsureComboSubscribers())
        {
            return false;
        }

        try
        {
            registerLeaseSubscriber ??= pluginInterface.GetIpcSubscriber<string, string, Guid?>(RegisterLeaseEndpoint);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "WrathComboBridge: failed to hook {Endpoint}", RegisterLeaseEndpoint);
        }

        try
        {
            registerLeaseCallbackSubscriber ??= pluginInterface.GetIpcSubscriber<string, string, string?, Guid?>("WrathCombo.RegisterForLeaseWithCallback");
            callbackProvider ??= pluginInterface.GetIpcProvider<int, string, object?>("YetAnotherFATE.WrathComboCallback");
            if (!callbackRegistered)
            {
                callbackProvider.RegisterAction(OnLeaseCancelled);
                callbackRegistered = true;
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "WrathComboBridge: callback lease IPC unavailable; using non-callback lease");
        }
        try
        {
            releaseLeaseSubscriber ??= pluginInterface.GetIpcSubscriber<Guid, object?>(ReleaseLeaseEndpoint);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "WrathComboBridge: failed to hook {Endpoint}", ReleaseLeaseEndpoint);
        }

        try
        {
            setAutoRotationResultSubscriber ??= pluginInterface.GetIpcSubscriber<Guid, bool, WrathSetResult>(SetAutoRotationEndpoint);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "WrathComboBridge: failed to hook {Endpoint}", SetAutoRotationEndpoint);
        }

        try
        {
            setJobReadySubscriber ??= pluginInterface.GetIpcSubscriber<Guid, WrathSetResult>(SetJobReadyEndpoint);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "WrathComboBridge: failed to hook {Endpoint}", SetJobReadyEndpoint);
        }

        try
        {
            setConfigSubscriber ??= pluginInterface.GetIpcSubscriber<Guid, object, object, WrathSetResult>(SetConfigEndpoint);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "WrathComboBridge: failed to hook {Endpoint}", SetConfigEndpoint);
        }

        return registerLeaseSubscriber != null && releaseLeaseSubscriber != null &&
               setAutoRotationResultSubscriber != null && setJobReadySubscriber != null &&
               setConfigSubscriber != null;
    }

    private void OnLeaseCancelled(int _, string reason)
    {
        if (activeLease == null)
            return;
        log.Warning("WrathComboBridge: lease cancelled by provider ({Reason}); reacquiring on next automation tick", reason);
        activeLease = null;
        leaseReleased = false;
        autoEnabled = false;
        configuredJobId = null;
        configApplied = false;
    }

    private void HandleInvalidLease()
    {
        activeLease = null;
        leaseReleased = false;
        autoEnabled = false;
        configuredJobId = null;
        configApplied = false;
    }

    private static bool IsSuccess(WrathSetResult result) => result is WrathSetResult.Okay or WrathSetResult.OkayWorking;

    private enum WrathSetResult
    {
        Okay = 0,
        OkayWorking = 1,
        IPCDisabled = 10,
        InvalidLease = 11,
        BlacklistedLease = 12,
        Duplicate = 13,
        PlayerNotAvailable = 14,
        InvalidConfiguration = 15,
        InvalidValue = 16,
    }

    private enum WrathAutoConfigOption
    {
        InCombatOnly = 0,
        DpsRotationMode = 1,
        HealerRotationMode = 2,
        FatePriority = 3,
    }

    private enum WrathDpsRotationMode
    {
        Manual = 0,
        HighestMax = 1,
        LowestMax = 2,
        HighestCurrent = 3,
        LowestCurrent = 4,
        TankTarget = 5,
        Nearest = 6,
        Furthest = 7,
    }
}
