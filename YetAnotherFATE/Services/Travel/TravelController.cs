using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Movement;

namespace YetAnotherFATE.Services.Travel;

public sealed class TravelController : ITravelController, IDisposable
{
    private readonly ITeleportExecutor teleportExecutor;
    private readonly INavmeshClient navmeshClient;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    private const uint MountActionId = 9;
    private const uint DismountActionId = 23;
    private const int MountMaxAttempts = 5;
    private const float DismountDistance = 20f;
    private const float DefaultArrivalRadius = 12f;
    private const int NavmeshMaxAttempts = 3;
    private static readonly ConditionFlag[] TeleportBlockingFlags =
    {
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
        ConditionFlag.Jumping,
        ConditionFlag.Jumping61,
        ConditionFlag.Occupied,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.InCombat,
        ConditionFlag.Casting,
    };
    private static readonly TimeSpan MountAttemptTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DismountAttemptTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DismountRetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan TeleportRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TeleportStabilityWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TeleportReadinessTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TeleportCompletionTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan WorldReadySettleDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WorldReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DismountTimeout = TimeSpan.FromSeconds(15);

    private readonly object jobGate = new();
    private readonly object dismountGate = new();
    private TravelJob? currentJob;
    private TravelPhase phase = TravelPhase.Idle;
    private string? phaseDetail;
    private CancellationTokenSource? jobCts;
    private Task? jobTask;
    private CancellationTokenSource? dismountCts;
    private Task? dismountMonitorTask;
    private Guid? navmeshJobId;

    public event EventHandler<TravelPhaseChangedEventArgs>? PhaseChanged;

    public TravelController(
        ITeleportExecutor teleportExecutor,
        INavmeshClient navmeshClient,
        IClientState clientState,
        ICondition condition,
        IObjectTable objectTable,
        IFramework framework,
        IGameGui gameGui,
        IPluginLog log)
    {
        this.teleportExecutor = teleportExecutor;
        this.navmeshClient = navmeshClient;
        this.clientState = clientState;
        this.condition = condition;
        this.objectTable = objectTable;
        this.framework = framework;
        this.gameGui = gameGui;
        this.log = log;
    }

    public TravelJob? CurrentJob
    {
        get
        {
            lock (jobGate)
            {
                return currentJob;
            }
        }
    }

    public TravelPhase Phase
    {
        get
        {
            lock (jobGate)
            {
                return phase;
            }
        }
    }

    public string? PhaseDetail
    {
        get
        {
            lock (jobGate)
            {
                return phaseDetail;
            }
        }
    }

    public void RequestTravel(FateSelection selection, TravelRequestKind kind = TravelRequestKind.Automatic, float? arrivalRadius = null)
    {
        lock (jobGate)
        {
            var requestedRadius = Math.Clamp(arrivalRadius ?? DefaultArrivalRadius, 2f, DefaultArrivalRadius);
            if (currentJob is { } existing && existing.Selection.Fate.FateId == selection.Fate.FateId && existing.Kind == kind &&
                Vector3.Distance(existing.Selection.Destination, selection.Destination) < 1f)
            {
                currentJob = existing with { Selection = selection, ArrivalRadius = requestedRadius };
                return;
            }

            if (currentJob is { } competing)
            {
                var existingPriority = GetPriority(competing.Kind);
                var incomingPriority = GetPriority(kind);
                if (incomingPriority < existingPriority)
                {
                    log.Debug(
                        "TravelController: ignoring {IncomingKind} travel request for fate {FateId} because higher priority job {JobId} ({ExistingKind}) is running",
                        kind,
                        selection.Fate.FateId,
                        competing.JobId,
                        competing.Kind);
                    return;
                }

                if (competing.Kind == TravelRequestKind.Automatic && kind == TravelRequestKind.Automatic &&
                    IsCommittedPhase(phase) && competing.Selection.Fate.FateId != selection.Fate.FateId)
                {
                    return;
                }

            }

            CancelJobLocked("superseded by new travel job", null);
            DetachCancelledJobLocked();

            var job = new TravelJob(Guid.NewGuid(), selection, kind, DateTimeOffset.UtcNow, requestedRadius);
            currentJob = job;

            if (IsAlreadyWithinArrivalRadius(selection, requestedRadius))
            {
                UpdatePhaseInternal(job.JobId, TravelPhase.Arrived, "Already within arrival radius");
                currentJob = null;
                return;
            }

            UpdatePhaseInternal(TravelPhase.Initializing, $"travel job {job.JobId} targeting {selection.Fate.Name}");

            jobCts = new CancellationTokenSource();
            var token = jobCts.Token;
            jobTask = Task.Run(() => ProcessJobAsync(job, token), token);
        }
    }

    public void Cancel(string reason, TravelRequestKind? onlyKind = null)
    {
        lock (jobGate)
        {
            CancelJobLocked(reason, onlyKind);
        }
    }

    private void CancelJobLocked(string reason, TravelRequestKind? onlyKind)
    {
        if (currentJob == null)
        {
            return;
        }

        if (onlyKind.HasValue && currentJob.Kind != onlyKind.Value)
        {
            return;
        }

        log.Debug("TravelController: cancelling active job {JobId} reason={Reason}", currentJob.JobId, reason);
        var jobId = currentJob.JobId;
        jobCts?.Cancel();
        StopNavmesh(jobId);
        UpdatePhaseInternal(jobId, TravelPhase.Cancelled, reason);
    }

    private async Task ProcessJobAsync(TravelJob job, CancellationToken token)
    {
        try
        {
            log.Debug("TravelController: job {JobId} started for fate {FateId} ({Name})", job.JobId, job.Selection.Fate.FateId, job.Selection.Fate.Name);

            if (job.Kind == TravelRequestKind.Automatic && job.Selection.ShouldTeleport && job.Selection.Teleport.AetheryteId.HasValue)
            {
                UpdatePhaseInternal(job.JobId, TravelPhase.TeleportQueued, $"teleport target {job.Selection.Teleport.AetheryteName ?? job.Selection.Teleport.AetheryteId.Value.ToString()}");
                var teleported = await ExecuteTeleportAsync(job.JobId, job.Selection, token).ConfigureAwait(false);
                if (!teleported)
                {
                    FailJob(job.JobId, "Teleport failed or was blocked");
                    return;
                }

                if (!await WaitForWorldReadyAsync(token).ConfigureAwait(false))
                {
                    FailJob(job.JobId, "World not ready after teleport");
                    return;
                }

                await Task.Delay(WorldReadySettleDelay, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            UpdatePhaseInternal(job.JobId, TravelPhase.Mounting, "Ensuring mount");
            if (!await EnsureMountedAsync(job.JobId, token).ConfigureAwait(false))
            {
                FailJob(job.JobId, "Failed to mount after retries");
                return;
            }

            if (!await WaitForWorldReadyAsync(token).ConfigureAwait(false))
            {
                FailJob(job.JobId, "World not ready after mounting");
                return;
            }

            await Task.Delay(WorldReadySettleDelay, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            UpdatePhaseInternal(job.JobId, TravelPhase.Moving, "Issuing navmesh move");
            if (!await TryNavmeshMoveAsync(job.JobId, job.Selection.Destination, token).ConfigureAwait(false))
            {
                FailJob(job.JobId, $"Navmesh move failed ({navmeshClient.Status})");
                return;
            }

            UpdatePhaseInternal(job.JobId, TravelPhase.Moving, "Navmesh move active");
            await MonitorArrivalAsync(job, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            UpdatePhaseInternal(job.JobId, TravelPhase.Arrived, "Arrival radius reached");
        }
        catch (TaskCanceledException)
        {
            log.Debug("TravelController: job {JobId} cancelled", job.JobId);
        }
        catch (Exception ex)
        {
            log.Error(ex, "TravelController: job {JobId} threw", job.JobId);
            FailJob(job.JobId, ex.Message);
        }
        finally
        {
            CleanupJob(job.JobId);
        }
    }

    private async Task<bool> ExecuteTeleportAsync(Guid jobId, FateSelection selection, CancellationToken token)
    {
        if (!selection.Teleport.AetheryteId.HasValue)
        {
            return false;
        }

        var attempts = 0;
        while (!token.IsCancellationRequested && attempts < 3)
        {
            if (!await WaitForTeleportReadinessAsync(token).ConfigureAwait(false))
            {
                log.Debug("TravelController: teleport readiness timeout before attempt {Attempt}", attempts + 1);
                return false;
            }

            if (teleportExecutor.State != TeleportState.Idle)
            {
                await Task.Delay(TeleportRetryDelay, token).ConfigureAwait(false);
                attempts++;
                continue;
            }

            var result = await RunForJobOnFrameworkThreadAsync(
                jobId,
                () => teleportExecutor.RequestTeleport(selection.Teleport),
                token).ConfigureAwait(false);
            log.Debug("TravelController: teleport request attempt {Attempt} => {Result}", attempts + 1, result);
            switch (result)
            {
                case TeleportRequestResult.Issued:
                    UpdatePhaseInternal(jobId, TravelPhase.Teleporting, "Teleport in progress");
                    var completed = await WaitForTeleportCompletionAsync(token).ConfigureAwait(false);
                    if (!completed)
                    {
                        log.Debug("TravelController: teleport was issued but did not complete successfully");
                        attempts++;
                        if (attempts < 3)
                        {
                            await Task.Delay(TeleportRetryDelay, token).ConfigureAwait(false);
                            continue;
                        }
                    }
                    return completed;
                case TeleportRequestResult.Busy:
                    log.Debug("TravelController: teleport executor busy, retrying shortly");
                    await Task.Delay(TeleportRetryDelay, token).ConfigureAwait(false);
                    attempts++;
                    break;
                case TeleportRequestResult.Blocked:
                    log.Debug("TravelController: teleport blocked by current conditions, will retry");
                    await Task.Delay(TeleportRetryDelay, token).ConfigureAwait(false);
                    attempts++;
                    break;
                case TeleportRequestResult.InvalidTarget:
                    log.Debug("TravelController: teleport invalid target (missing aetheryte id)");
                    return false;
                case TeleportRequestResult.ClientUnavailable:
                    log.Debug("TravelController: teleport client unavailable (not logged in?)");
                    return false;
                case TeleportRequestResult.Failed:
                    log.Debug("TravelController: teleport executor reported failure immediately");
                    return false;
            }
        }

        log.Debug("TravelController: teleport attempts exhausted without success");
        return false;
    }

    private async Task<bool> WaitForTeleportCompletionAsync(CancellationToken token)
    {
        var elapsed = TimeSpan.Zero;
        var observedLoading = false;
        while (!token.IsCancellationRequested && elapsed < TeleportCompletionTimeout)
        {
            var state = teleportExecutor.State;
            switch (state)
            {
                case TeleportState.Completed:
                    return true;
                case TeleportState.Loading:
                    observedLoading = true;
                    break;
                case TeleportState.Failed:
                    log.Debug("TravelController: teleport executor entered Failed state");
                    return false;
                case TeleportState.Idle:
                {
                    if (!observedLoading)
                    {
                        log.Debug("TravelController: teleport returned to Idle before loading began");
                        return false;
                    }

                    var betweenAreas = await IsConditionActiveAsync(ConditionFlag.BetweenAreas, token).ConfigureAwait(false);
                    var betweenAreas51 = await IsConditionActiveAsync(ConditionFlag.BetweenAreas51, token).ConfigureAwait(false);
                    if (observedLoading && !betweenAreas && !betweenAreas51)
                    {
                        return true;
                    }

                    break;
                }
            }

            await Task.Delay(PollInterval, token).ConfigureAwait(false);
            elapsed += PollInterval;
        }

        log.Debug("TravelController: teleport completion timeout (state={State})", teleportExecutor.State);
        return false;
    }

    private async Task<bool> WaitForTeleportReadinessAsync(CancellationToken token)
    {
        var startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset? stableSince = null;
        ConditionFlag? lastBlockingFlag = null;

        while (!token.IsCancellationRequested && DateTimeOffset.UtcNow - startedAt < TeleportReadinessTimeout)
        {
            var blockingFlag = await RunOnFrameworkThreadAsync(GetTeleportBlockingFlag, token).ConfigureAwait(false);
            if (blockingFlag is { } activeFlag)
            {
                stableSince = null;
                if (lastBlockingFlag != activeFlag)
                {
                    log.Debug("TravelController: waiting to teleport; blocking condition={Flag}", activeFlag);
                    lastBlockingFlag = activeFlag;
                }
            }
            else
            {
                stableSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - stableSince.Value >= TeleportStabilityWindow)
                {
                    return true;
                }

                lastBlockingFlag = null;
            }

            await Task.Delay(PollInterval, token).ConfigureAwait(false);
        }

        return false;
    }

    private ConditionFlag? GetTeleportBlockingFlag()
    {
        foreach (var flag in TeleportBlockingFlags)
        {
            if (condition[flag])
            {
                return flag;
            }
        }

        return null;
    }

    private async Task<bool> EnsureMountedAsync(Guid jobId, CancellationToken token)
    {
        if (await IsConditionActiveAsync(ConditionFlag.Mounted, token).ConfigureAwait(false))
        {
            return true;
        }

        for (var attempt = 0; attempt < MountMaxAttempts && !token.IsCancellationRequested; attempt++)
        {
            log.Debug("TravelController: issuing mount attempt {Attempt}/{Max}", attempt + 1, MountMaxAttempts);
            await TryUseGeneralActionAsync(jobId, MountActionId, token).ConfigureAwait(false);

            if (await WaitForConditionAsync(() => condition[ConditionFlag.Mounted], MountAttemptTimeout, token).ConfigureAwait(false))
            {
                log.Debug("TravelController: mount confirmed");
                return true;
            }

            log.Debug("TravelController: mount attempt {Attempt} timed out", attempt + 1);
        }

        return await IsConditionActiveAsync(ConditionFlag.Mounted, token).ConfigureAwait(false);
    }

    private async Task<bool> TryNavmeshMoveAsync(Guid jobId, Vector3 destination, CancellationToken token)
    {
        if (!navmeshClient.IsAvailable)
        {
            log.Debug("TravelController: navmesh unavailable");
            return false;
        }

        for (var attempt = 0; attempt < NavmeshMaxAttempts && !token.IsCancellationRequested; attempt++)
        {
            var ready = await RunForJobOnFrameworkThreadAsync(jobId, navmeshClient.EnsureReady, token).ConfigureAwait(false);
            if (!ready)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                continue;
            }

            var moveIssued = await RunForJobOnFrameworkThreadAsync(
                jobId,
                () =>
                {
                    var issued = navmeshClient.SimpleMoveTo(destination);
                    if (issued)
                    {
                        navmeshJobId = jobId;
                    }

                    return issued;
                },
                token).ConfigureAwait(false);
            if (moveIssued)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }

        return false;
    }

    private void StartDismountMonitor(Guid jobId, Vector3 destination, CancellationToken token)
    {
        lock (dismountGate)
        {
            dismountCts?.Cancel();
            dismountCts?.Dispose();
            dismountCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var monitorToken = dismountCts.Token;
            dismountMonitorTask = Task.Run(() => MonitorDismountAsync(jobId, destination, monitorToken), monitorToken);
        }
    }

    private async Task MonitorArrivalAsync(TravelJob job, CancellationToken token)
    {
        var navmeshStopped = false;
        while (!token.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow - job.CreatedAt > TravelTimeout)
            {
                throw new TimeoutException("Travel timed out before reaching the destination");
            }

            var position = await TryGetPlayerPositionAsync(token).ConfigureAwait(false);
            if (position is not Vector3 playerPosition)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                continue;
            }

            var distance = Vector3.Distance(playerPosition, job.Selection.Destination);
            if (distance <= job.ArrivalRadius)
            {
                if (!navmeshStopped)
                {
                    StopNavmesh(job.JobId);
                    navmeshStopped = true;
                }
                UpdatePhaseInternal(job.JobId, TravelPhase.Arriving, $"within {distance:F1}y, waiting for dismount");
                if (await DismountAtDestinationAsync(job.JobId, token).ConfigureAwait(false))
                {
                    return;
                }
            }

            await Task.Delay(PollInterval, token).ConfigureAwait(false);
        }
    }

    private async Task<bool> DismountAtDestinationAsync(Guid jobId, CancellationToken token)
    {
        if (!await IsConditionActiveAsync(ConditionFlag.Mounted, token).ConfigureAwait(false))
        {
            return true;
        }

        var startedAt = DateTimeOffset.UtcNow;
        while (!token.IsCancellationRequested && DateTimeOffset.UtcNow - startedAt < DismountTimeout)
        {
            log.Debug("TravelController: issuing dismount request");
            await TryUseGeneralActionAsync(jobId, DismountActionId, token).ConfigureAwait(false);
            if (await WaitForConditionAsync(() => !condition[ConditionFlag.Mounted], DismountAttemptTimeout, token).ConfigureAwait(false))
            {
                log.Debug("TravelController: dismount confirmed");
                return true;
            }

            await Task.Delay(DismountRetryDelay, token).ConfigureAwait(false);
        }

        log.Debug("TravelController: dismount timed out near the destination");
        return !await IsConditionActiveAsync(ConditionFlag.Mounted, token).ConfigureAwait(false);
    }

    private async Task<bool> WaitForWorldReadyAsync(CancellationToken token)
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (!token.IsCancellationRequested && DateTimeOffset.UtcNow - startedAt < WorldReadyTimeout)
        {
            var ready = await RunOnFrameworkThreadAsync(
                () => !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] && IsScreenReady(),
                token).ConfigureAwait(false);
            if (ready)
            {
                var position = await TryGetPlayerPositionAsync(token).ConfigureAwait(false);
                if (position.HasValue)
                {
                    return true;
                }
            }

            await Task.Delay(PollInterval, token).ConfigureAwait(false);
        }

        return false;
    }

    private bool IsAlreadyWithinArrivalRadius(FateSelection selection, float arrivalRadius)
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        var distance = Vector3.Distance(player.Position, selection.Destination);
        if (distance > arrivalRadius)
        {
            return false;
        }

        log.Debug("TravelController: already within arrival radius ({Distance:F1}y), skipping travel job", distance);

        if (condition[ConditionFlag.Mounted])
        {
            TryUseGeneralAction(DismountActionId);
        }

        return true;
    }

    private static int GetPriority(TravelRequestKind kind) => kind switch
    {
        TravelRequestKind.DebugForceRun => 10,
        _ => 0,
    };

    private static bool IsCommittedPhase(TravelPhase currentPhase) => currentPhase is TravelPhase.Teleporting
        or TravelPhase.Mounting
        or TravelPhase.Moving
        or TravelPhase.Arriving;

    private unsafe bool IsScreenReady()
    {
        return !IsAddonVisible("NowLoading")
            && !IsAddonVisible("FadeMiddle")
            && !IsAddonVisible("FadeBack");
    }

    private unsafe bool IsAddonVisible(string name)
    {
        var addonPtr = gameGui.GetAddonByName(name, 0);
        if (addonPtr.Address != IntPtr.Zero)
        {
            var addon = (AtkUnitBase*)addonPtr.Address;
            return addon != null && addon->IsVisible;
        }

        return false;
    }

    private async Task MonitorDismountAsync(Guid jobId, Vector3 destination, CancellationToken token)
    {
        try
        {
            DateTimeOffset? dismountStartedAt = null;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);

                if (!await IsConditionActiveAsync(ConditionFlag.Mounted, token).ConfigureAwait(false))
                {
                    return;
                }

                var position = await TryGetPlayerPositionAsync(token).ConfigureAwait(false);
                if (position is not Vector3 playerPosition)
                {
                    continue;
                }

                var distance = Vector3.Distance(playerPosition, destination);
                if (distance > DismountDistance)
                {
                    continue;
                }

                log.Debug("TravelController: within dismount range ({Distance:F1}y)", distance);

                while (!token.IsCancellationRequested && await IsConditionActiveAsync(ConditionFlag.Mounted, token).ConfigureAwait(false))
                {
                    dismountStartedAt ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - dismountStartedAt.Value > DismountTimeout)
                    {
                        log.Warning("TravelController: dismount timed out near the destination");
                        return;
                    }

                    log.Debug("TravelController: issuing dismount request");
                    await TryUseGeneralActionAsync(jobId, DismountActionId, token).ConfigureAwait(false);

                    if (await WaitForConditionAsync(() => !condition[ConditionFlag.Mounted], DismountAttemptTimeout, token).ConfigureAwait(false))
                    {
                        log.Debug("TravelController: dismount confirmed");
                        return;
                    }

                    await Task.Delay(DismountRetryDelay, token).ConfigureAwait(false);
                }
            }
        }
        catch (TaskCanceledException)
        {
        }
    }

    private async Task<Vector3?> TryGetPlayerPositionAsync(CancellationToken token)
    {
        try
        {
            return await RunOnFrameworkThreadAsync(() => objectTable.LocalPlayer?.Position, token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "TravelController: failed to capture player position");
            return null;
        }
    }

    private async Task<bool> WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken token)
    {
        var elapsed = TimeSpan.Zero;
        while (elapsed < timeout && !token.IsCancellationRequested)
        {
            if (await RunOnFrameworkThreadAsync(predicate, token).ConfigureAwait(false))
            {
                return true;
            }

            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            elapsed += PollInterval;
        }

        return !token.IsCancellationRequested && await RunOnFrameworkThreadAsync(predicate, token).ConfigureAwait(false);
    }

    private async Task<bool> TryUseGeneralActionAsync(Guid jobId, uint actionId, CancellationToken token)
    {
        return await RunForJobOnFrameworkThreadAsync(jobId, () => TryUseGeneralAction(actionId), token).ConfigureAwait(false);
    }

    private async Task<bool> IsConditionActiveAsync(ConditionFlag flag, CancellationToken token)
    {
        return await RunOnFrameworkThreadAsync(() => condition[flag], token).ConfigureAwait(false);
    }

    private unsafe bool TryUseGeneralAction(uint actionId)
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager == null)
            {
                log.Debug("TravelController: ActionManager instance missing");
                return false;
            }

            var result = manager->UseAction(ActionType.GeneralAction, actionId);
            log.Debug("TravelController: UseAction(ActionType.GeneralAction, {ActionId}) => {Result}", actionId, result);
            return result;
        }
        catch (Exception ex)
        {
            log.Error(ex, "TravelController: UseAction threw");
            return false;
        }
    }

    private void FailJob(Guid jobId, string reason)
    {
        log.Debug("TravelController: job {JobId} failed: {Reason}", jobId, reason);
        UpdatePhaseInternal(jobId, TravelPhase.Failed, reason);
        StopNavmesh(jobId);
    }

    private void CleanupJob(Guid jobId)
    {
        lock (jobGate)
        {
            if (currentJob?.JobId != jobId)
            {
                return;
            }

            StopNavmesh(jobId);
            currentJob = null;
            jobCts?.Dispose();
            jobCts = null;
            jobTask = null;
            phase = TravelPhase.Idle;
            phaseDetail = null;
        }

        lock (dismountGate)
        {
            dismountCts?.Cancel();
            dismountCts?.Dispose();
            dismountCts = null;
            dismountMonitorTask = null;
        }
    }

    private void DetachCancelledJobLocked()
    {
        var cancelledCts = jobCts;
        var cancelledTask = jobTask;
        jobCts = null;
        jobTask = null;

        if (cancelledCts == null)
        {
            return;
        }

        if (cancelledTask == null || cancelledTask.IsCompleted)
        {
            cancelledCts.Dispose();
            return;
        }

        _ = cancelledTask.ContinueWith(
            _ => cancelledCts.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void UpdatePhaseInternal(TravelPhase newPhase, string detail)
    {
        UpdatePhaseInternal(null, newPhase, detail);
    }

    private bool UpdatePhaseInternal(Guid? jobId, TravelPhase newPhase, string detail)
    {
        TravelJob? snapshot;
        lock (jobGate)
        {
            if (jobId.HasValue && currentJob?.JobId != jobId.Value)
            {
                log.Debug("TravelController: ignored stale phase {Phase} from job {JobId}", newPhase, jobId.Value);
                return false;
            }

            phase = newPhase;
            phaseDetail = detail;
            snapshot = currentJob;
            if (snapshot is { } job)
            {
                currentJob = job with { Status = detail };
            }
        }

        log.Debug("TravelController: phase => {Phase} ({Detail})", newPhase, detail);
        PhaseChanged?.Invoke(this, new TravelPhaseChangedEventArgs(newPhase, detail, snapshot));
        return true;
    }

    private void StopNavmesh(Guid jobId)
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            lock (jobGate)
            {
                if (navmeshJobId != jobId)
                {
                    return;
                }

                navmeshJobId = null;
            }

            navmeshClient.Stop();
        });
    }

    private async Task<T> RunOnFrameworkThreadAsync<T>(Func<T> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => tcs.TrySetCanceled(token));

        try
        {
            _ = framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<T> RunForJobOnFrameworkThreadAsync<T>(Guid jobId, Func<T> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => tcs.TrySetCanceled(token));

        try
        {
            _ = framework.RunOnFrameworkThread(() =>
            {
                if (token.IsCancellationRequested || !IsCurrentJob(jobId))
                {
                    tcs.TrySetCanceled();
                    return;
                }

                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private bool IsCurrentJob(Guid jobId)
    {
        lock (jobGate)
        {
            return currentJob?.JobId == jobId;
        }
    }

    public void Dispose()
    {
        Cancel("plugin disposing");
        lock (dismountGate)
        {
            dismountCts?.Cancel();
            dismountCts?.Dispose();
            dismountCts = null;
        }
    }
}
