using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Movement;
using YetAnotherFATE.Services.Combat.BossMod;
using YetAnotherFATE.Services.Fates.CombatHelper.Targeter;

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
    private readonly IBossModObstacleMap obstacleMap;
    private readonly AutomaticTravelPolicy automaticTravel;

    private const uint MountActionId = 9;
    private const uint DismountActionId = 23;
    private const int MountMaxAttempts = 5;
    private const float DismountDistance = 20f;
    private const float DefaultArrivalRadius = 12f;
    private const float FateApproachArrivalRadius = 5f;
    private const float LandingMaxVerticalDelta = 12f;
    private const float LandingMaxProjectionDrift = 8f;
    private const float LandingMaxEntityDistance = 20f;
    private const float LandingReplanMinShift = 9f;
    private const float ShortCorrectionDistance = 30f;
    private const float FateFallbackBoundaryPadding = 5f;
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
    private long navmeshMoveVersion;
    private Guid? obstacleMapJobId;

    public event EventHandler<TravelPhaseChangedEventArgs>? PhaseChanged;

    public TravelController(
        ITeleportExecutor teleportExecutor,
        INavmeshClient navmeshClient,
        IClientState clientState,
        ICondition condition,
        IObjectTable objectTable,
        IFramework framework,
        IGameGui gameGui,
        IBossModObstacleMap obstacleMap,
        AutomaticTravelPolicy automaticTravel,
        IPluginLog log)
    {
        this.teleportExecutor = teleportExecutor;
        this.navmeshClient = navmeshClient;
        this.clientState = clientState;
        this.condition = condition;
        this.objectTable = objectTable;
        this.framework = framework;
        this.gameGui = gameGui;
        this.obstacleMap = obstacleMap;
        this.automaticTravel = automaticTravel;
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
        if (kind == TravelRequestKind.Automatic && !automaticTravel.CanTeleport(selection.Fate))
            selection = selection with { ShouldTeleport = false };
        lock (jobGate)
        {
            var requestedRadius = Math.Clamp(arrivalRadius ?? DefaultArrivalRadius, 2f, DefaultArrivalRadius);
            if (currentJob is { } existing && phase is not (TravelPhase.Failed or TravelPhase.Cancelled or TravelPhase.Arrived) && existing.Selection.Fate.FateId == selection.Fate.FateId && existing.Kind == kind &&
                existing.ArrivalRadius == requestedRadius &&
                Vector3.Distance(existing.Selection.Destination, selection.Destination) < 1f)
            {
                currentJob = existing with { Selection = selection, ArrivalRadius = requestedRadius };
                return;
            }

            if (currentJob is { } competing && phase is not (TravelPhase.Failed or TravelPhase.Cancelled or TravelPhase.Arrived))
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
            var mapJobId = obstacleMapJobId;
            _ = framework.RunOnFrameworkThread(() =>
            {
                lock (jobGate)
                {
                    if (obstacleMapJobId != mapJobId) return;
                    obstacleMap.ClearTemporaryMap();
                    obstacleMapJobId = null;
                }
            });
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
        currentJob = null;
        DetachCancelledJobLocked();
        ClearObstacleMapForJob(jobId);
    }

    private async Task ProcessJobAsync(TravelJob job, CancellationToken token)
    {
        try
        {
            log.Debug("TravelController: job {JobId} started for fate {FateId} ({Name})", job.JobId, job.Selection.Fate.FateId, job.Selection.Fate.Name);

            if (job.Kind == TravelRequestKind.Automatic && job.Selection.ShouldTeleport && job.Selection.Teleport.AetheryteId.HasValue)
            {
                if (!await WaitForCombatClearAsync(token).ConfigureAwait(false))
                {
                    if (!token.IsCancellationRequested)
                        FailJob(job.JobId, "Combat remained active before teleporting");
                    return;
                }

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

            if (!await WaitForCombatClearAsync(token).ConfigureAwait(false))
            {
                if (!token.IsCancellationRequested)
                    FailJob(job.JobId, "Combat remained active before mounting");
                return;
            }

            if (job.Kind != TravelRequestKind.EscortPositioning)
            {
                UpdatePhaseInternal(job.JobId, TravelPhase.Mounting, "Ensuring mount");
                var travelDistance = await RunForJobOnFrameworkThreadAsync(job.JobId, () => GetTravelDistance(job.Selection), token).ConfigureAwait(false);
                if (travelDistance <= ShortCorrectionDistance)
                {
                    log.Debug("TravelController: short correction ({Distance:F1}y); skipping mount", travelDistance);
                }
                else if (!await EnsureMountedAsync(job.JobId, token).ConfigureAwait(false))
                {
                    FailJob(job.JobId, "Failed to mount after retries");
                    return;
                }
            }

            if (!await WaitForWorldReadyAsync(token).ConfigureAwait(false))
            {
                FailJob(job.JobId, "World not ready after mounting");
                return;
            }

            await Task.Delay(WorldReadySettleDelay, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            if (job.Kind != TravelRequestKind.EscortPositioning)
                await PrepareObstacleMapAsync(job, token).ConfigureAwait(false);

            if (!await WaitForCombatClearAsync(token).ConfigureAwait(false))
            {
                if (!token.IsCancellationRequested)
                    FailJob(job.JobId, "Combat resumed before movement");
                return;
            }

            UpdatePhaseInternal(job.JobId, TravelPhase.Moving, "Issuing navmesh move");
            if (!await TryReachDestinationAsync(job, token).ConfigureAwait(false))
            {
                FailJob(job.JobId, $"Navmesh move failed ({navmeshClient.Status})");
                return;
            }

            token.ThrowIfCancellationRequested();
            UpdatePhaseInternal(job.JobId, TravelPhase.Arrived, "Arrival radius reached");
        }
        catch (OperationCanceledException)
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

    private async Task PrepareObstacleMapAsync(TravelJob job, CancellationToken token)
    {
        var readyDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!await RunForJobOnFrameworkThreadAsync(job.JobId, navmeshClient.EnsureReady, token).ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow >= readyDeadline) return;
            await Task.Delay(PollInterval, token).ConfigureAwait(false);
        }
        var mapOrigin = await RunForJobOnFrameworkThreadAsync(
            job.JobId,
            () => navmeshClient.NearestPointReachable(job.Selection.Fate.Center) ?? job.Selection.Fate.Center,
            token).ConfigureAwait(false);
        var radius = job.Selection.Fate.Radius + Vector3.Distance(mapOrigin, job.Selection.Fate.Center);
        var started = await RunForJobOnFrameworkThreadAsync(
            job.JobId,
            () =>
            {
                var accepted = obstacleMap.BeginForFate(job.Selection.Fate.FateId, mapOrigin, radius);
                if (accepted) obstacleMapJobId = job.JobId;
                return accepted;
            },
            token).ConfigureAwait(false);
        if (!started)
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await RunForJobOnFrameworkThreadAsync(job.JobId, obstacleMap.GetGenerationStatus, token).ConfigureAwait(false);
            if (status is TaskStatus.RanToCompletion or TaskStatus.Faulted or TaskStatus.Canceled)
            {
                if (status != TaskStatus.RanToCompletion)
                {
                    await RunForJobOnFrameworkThreadAsync(job.JobId, () => obstacleMap.ClearTemporaryMap(), token).ConfigureAwait(false);
                    if (status == TaskStatus.Faulted) obstacleMap.Blacklist(job.Selection.Fate.FateId);
                    return;
                }

                var badMap = await RunForJobOnFrameworkThreadAsync(
                    job.JobId,
                    () => obstacleMap.IsCurrentMapBad(out var details) ? details : null,
                    token).ConfigureAwait(false);
                if (badMap is not null)
                {
                    log.Warning("TravelController: rejecting poor BossMod obstacle map for FATE {FateId}: {Details}", job.Selection.Fate.FateId, badMap);
                    await RunForJobOnFrameworkThreadAsync(job.JobId, () => obstacleMap.ClearTemporaryMap(), token).ConfigureAwait(false);
                    obstacleMap.Blacklist(job.Selection.Fate.FateId);
                }

                return;
            }

            await Task.Delay(PollInterval, token).ConfigureAwait(false);
        }

        log.Warning("TravelController: BossMod obstacle-map generation timed out for FATE {FateId}; continuing", job.Selection.Fate.FateId);
        await RunForJobOnFrameworkThreadAsync(job.JobId, () => obstacleMap.ClearTemporaryMap(), token).ConfigureAwait(false);
    }

    private async Task<bool> TryReachDestinationAsync(TravelJob job, CancellationToken token)
    {
        var strictDestination = job.ArrivalRadius < DefaultArrivalRadius;
        var fly = job.Kind != TravelRequestKind.EscortPositioning &&
                  await RunForJobOnFrameworkThreadAsync(job.JobId, CanFlyForMovement, token).ConfigureAwait(false);
        var useFlightApproach = ShouldUseFlightApproach(strictDestination, fly);
        var destinations = useFlightApproach
            ? await ResolveFateLandingDestinationsAsync(job, token).ConfigureAwait(false)
            : new List<Vector3> { job.Selection.Destination };
        if (destinations.Count == 0)
        {
            log.Warning("TravelController: no landable approach point found for FATE {FateId}", job.Selection.Fate.FateId);
            return false;
        }

        for (var attempt = 0; attempt < destinations.Count; attempt++)
        {
            if (!await TryNavmeshMoveAsync(job.JobId, destinations[attempt], fly, token).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                var attemptJob = job with { Selection = job.Selection with { Destination = destinations[attempt] } };
                await MonitorArrivalAsync(attemptJob, fly, useFlightApproach, token).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException) when (attempt + 1 < destinations.Count)
            {
                StopNavmesh(job.JobId);
                log.Warning("TravelController: movement stalled for FATE {FateId}; retrying alternate destination", job.Selection.Fate.FateId);
            }
        }

        return false;
    }

    private async Task<List<Vector3>> ResolveFateLandingDestinationsAsync(TravelJob job, CancellationToken token)
    {
        return await RunForJobOnFrameworkThreadAsync(job.JobId, () =>
        {
            var fate = job.Selection.Fate;
            var player = objectTable.LocalPlayer;
            if (player == null)
                return new List<Vector3>();

            var anchors = objectTable
                .Cast<IGameObject>()
                .OfType<IBattleNpc>()
                .Where(npc => FateTargetPolicy.IsValidTravelAnchor(npc, fate))
                .OrderBy(npc => Vector3.DistanceSquared(player.Position, npc.Position))
                .Take(3)
                .Select(npc => npc.Position)
                .ToList();

            var destinations = new List<Vector3>();
            foreach (var anchor in anchors)
            {
                AddProjectedApproachPoints(destinations, fate, anchor, player.Position, LandingMaxEntityDistance);
            }

            if (destinations.Count == 0)
            {
                AddProjectedFateFallbacks(destinations, fate, player.Position);
            }

            if (destinations.Count > 0)
            {
                log.Debug("TravelController: resolved {Count} landable approach points for FATE {FateId} using {AnchorCount} live entities",
                    destinations.Count,
                    fate.FateId,
                    anchors.Count);
            }

            return destinations;
        }, token).ConfigureAwait(false);
    }

    private async Task<Vector3?> ResolveFateLandingDestinationAsync(TravelJob job, CancellationToken token)
    {
        var destinations = await ResolveFateLandingDestinationsAsync(job, token).ConfigureAwait(false);
        return destinations.Count > 0 ? destinations[0] : null;
    }

    private void AddProjectedApproachPoints(List<Vector3> destinations, ActiveFate fate, Vector3 anchor, Vector3 playerPosition, float maxAnchorDistance)
    {
        var direction = new Vector2(playerPosition.X - anchor.X, playerPosition.Z - anchor.Z);
        var baseAngle = direction.LengthSquared() > 0.01f ? MathF.Atan2(direction.Y, direction.X) : 0f;
        var angles = new[] { 0f, MathF.PI / 4f, -MathF.PI / 4f, MathF.PI, MathF.PI / 2f, -MathF.PI / 2f };
        var radii = new[] { 8f, 12f, 16f };

        foreach (var radius in radii)
        {
            foreach (var angleOffset in angles)
            {
                var requested = anchor + new Vector3(
                    MathF.Cos(baseAngle + angleOffset) * radius,
                    0,
                    MathF.Sin(baseAngle + angleOffset) * radius);
                var projected = navmeshClient.PointOnFloor(requested, allowUnlandable: false, halfExtentXZ: 5f);
                if (projected is not { } point || !IsValidLandingPoint(point, requested, anchor, fate, maxAnchorDistance))
                    continue;
                if (!destinations.Exists(existing => Vector3.DistanceSquared(existing, point) < 9f))
                    destinations.Add(point);
            }
        }
    }

    private void AddProjectedFateFallbacks(List<Vector3> destinations, ActiveFate fate, Vector3 playerPosition)
    {
        var points = new List<Vector3> { fate.Center };
        var radius = MathF.Max(2f, fate.Radius * 0.6f);
        for (var i = 0; i < 8; i++)
        {
            var angle = i * MathF.PI / 4f;
            points.Add(fate.Center + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius));
        }

        foreach (var requested in points.OrderBy(point => Vector3.DistanceSquared(playerPosition, point)))
        {
            var projected = navmeshClient.PointOnFloor(requested, allowUnlandable: false, halfExtentXZ: 5f);
            if (projected is not { } point || FlatDistance(point, fate.Center) > MathF.Max(1f, fate.Radius - FateFallbackBoundaryPadding) ||
                MathF.Abs(point.Y - requested.Y) > LandingMaxVerticalDelta)
                continue;
            if (!destinations.Exists(existing => Vector3.DistanceSquared(existing, point) < 9f))
                destinations.Add(point);
        }
    }

    private static bool IsValidLandingPoint(Vector3 projected, Vector3 requested, Vector3 anchor, ActiveFate fate, float maxAnchorDistance)
        => FlatDistance(projected, anchor) <= maxAnchorDistance &&
           MathF.Abs(projected.Y - anchor.Y) <= LandingMaxVerticalDelta &&
           FlatDistance(projected, requested) <= LandingMaxProjectionDrift;

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
                () => automaticTravel.CanTeleport(currentJob?.Selection.Fate ?? selection.Fate)
                    ? teleportExecutor.RequestTeleport(selection.Teleport)
                    : TeleportRequestResult.Blocked,
                token).ConfigureAwait(false);
            log.Debug("TravelController: teleport request attempt {Attempt} => {Result}", attempts + 1, result);
            switch (result)
            {
                case TeleportRequestResult.Issued:
                    UpdatePhaseInternal(jobId, TravelPhase.Teleporting, "Teleport in progress");
                    var completed = await WaitForTeleportCompletionAsync(DateTimeOffset.UtcNow, token).ConfigureAwait(false);
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

    private async Task<bool> WaitForTeleportCompletionAsync(DateTimeOffset requestStartedAt, CancellationToken token)
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
                    if (teleportExecutor.LastCompletedAt is { } completedAt && completedAt >= requestStartedAt)
                        return true;
                    if (teleportExecutor.LastFailedAt is { } failedAt && failedAt >= requestStartedAt)
                        return false;
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

    private async Task<bool> WaitForCombatClearAsync(CancellationToken token)
    {
        DateTimeOffset clearSince = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested)
        {
            if (await IsConditionActiveAsync(ConditionFlag.InCombat, token).ConfigureAwait(false))
            {
                clearSince = DateTimeOffset.MinValue;
            }
            else
            {
                clearSince = clearSince == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : clearSince;
                if (DateTimeOffset.UtcNow - clearSince >= TimeSpan.FromSeconds(1))
                    return true;
            }

            await Task.Delay(PollInterval, token).ConfigureAwait(false);
        }

        return !token.IsCancellationRequested;
    }

    private async Task<bool> TryNavmeshMoveAsync(Guid jobId, Vector3 destination, bool fly, CancellationToken token)
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
                      log.Debug("TravelController: issuing navmesh move mode={Mode}", fly ? "flight" : "ground");
                     var issued = navmeshClient.SimpleMoveTo(destination, fly);
                    if (issued)
                    {
                        navmeshJobId = jobId;
                        navmeshMoveVersion++;
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

    private unsafe bool CanFlyForMovement()
    {
        if (!condition[ConditionFlag.Mounted])
            return false;
        if (condition[ConditionFlag.InFlight])
            return true;

        try
        {
            // Control.CanFly includes current mount and zone flight unlock state.
            return Control.CanFly;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "TravelController: flight permission unavailable; using ground movement");
            return false;
        }
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

    private async Task MonitorArrivalAsync(TravelJob job, bool fly, bool useFlightApproach, CancellationToken token)
    {
        var navmeshStopped = false;
        Vector3? lastPosition = null;
        var lastProgressAt = DateTimeOffset.UtcNow;
        var nextLandingReplanAt = DateTimeOffset.MinValue;
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
            var freshJob = CurrentJob;
            var fate = freshJob?.Selection.Fate ?? job.Selection.Fate;

            if (useFlightApproach && fate.State == Dalamud.Game.ClientState.Fates.FateState.Running &&
                DateTimeOffset.UtcNow >= nextLandingReplanAt &&
                await IsConditionActiveAsync(ConditionFlag.Mounted, token).ConfigureAwait(false))
            {
                nextLandingReplanAt = DateTimeOffset.UtcNow.AddSeconds(2);
                var liveLandingPoint = await ResolveFateLandingDestinationAsync(job, token).ConfigureAwait(false);
                if (liveLandingPoint is { } point && FlatDistance(point, job.Selection.Destination) > LandingReplanMinShift)
                {
                    StopNavmesh(job.JobId);
                    job = job with { Selection = job.Selection with { Destination = point } };
                    navmeshStopped = false;
                    lastPosition = null;
                    lastProgressAt = DateTimeOffset.UtcNow;
                    log.Debug("TravelController: retargeting mounted FATE travel to live-entity landing point {Point}", point);
                    if (!await TryNavmeshMoveAsync(job.JobId, point, fly, token).ConfigureAwait(false))
                        throw new TimeoutException("Failed to restart movement toward live-entity landing point");
                    continue;
                }
            }

            var arrivalTolerance = GetArrivalTolerance(job, fate, useFlightApproach);
            var arrived = distance <= arrivalTolerance;
            if (lastPosition is not { } previous || Vector3.Distance(previous, playerPosition) >= 1.5f)
            {
                lastPosition = playerPosition;
                lastProgressAt = DateTimeOffset.UtcNow;
            }
            else if (!arrived && DateTimeOffset.UtcNow - lastProgressAt > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException($"Navmesh movement stalled at {distance:F1}y from destination");
            }

            if (arrived)
            {
                if (!navmeshStopped)
                {
                    StopNavmesh(job.JobId);
                    navmeshStopped = true;
                }
                UpdatePhaseInternal(job.JobId, TravelPhase.Arriving, $"within {distance:F1}y, waiting for dismount");
                if (await DismountAtDestinationAsync(job.JobId, token).ConfigureAwait(false))
                {
                    var stillArrived = await RunForJobOnFrameworkThreadAsync(job.JobId, () =>
                    {
                        var latest = currentJob!;
                        var player = objectTable.LocalPlayer;
                        if (player == null || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]) return false;
                        return Vector3.Distance(player.Position, job.Selection.Destination) <= GetArrivalTolerance(job, fate, useFlightApproach);
                    }, token).ConfigureAwait(false);
                    if (stillArrived) return;
                    throw new TimeoutException("Player left arrival radius while dismounting");
                }
                throw new TimeoutException("Dismount timed out near the destination");
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
        if (player == null || condition[ConditionFlag.Mounted] || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || !IsScreenReady())
        {
            return false;
        }

        var distance = Vector3.Distance(player.Position, selection.Destination);
        var withinRadius = arrivalRadius < DefaultArrivalRadius && distance <= arrivalRadius;
        if (!withinRadius)
        {
            return false;
        }

        log.Debug("TravelController: already within arrival radius ({Distance:F1}y), skipping travel job", distance);

        return true;
    }

    private static int GetPriority(TravelRequestKind kind) => kind switch
    {
        TravelRequestKind.DebugForceRun => 10,
        TravelRequestKind.EscortPositioning => -10,
        _ => 0,
    };

    private static float FlatDistance(Vector3 left, Vector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private float GetTravelDistance(FateSelection selection)
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
            return float.MaxValue;

        var distance = Vector3.Distance(player.Position, selection.Destination);
        if (selection.Fate.State != Dalamud.Game.ClientState.Fates.FateState.Running)
            return distance;

        var nearestAnchor = objectTable
            .Cast<IGameObject>()
            .OfType<IBattleNpc>()
            .Where(npc => FateTargetPolicy.IsValidTravelAnchor(npc, selection.Fate))
            .Select(npc => Vector3.Distance(player.Position, npc.Position))
            .DefaultIfEmpty(float.MaxValue)
            .Min();
        return MathF.Min(distance, nearestAnchor);
    }

    private static float GetArrivalTolerance(TravelJob job, ActiveFate fate, bool useFlightApproach)
        => useFlightApproach && fate.State == Dalamud.Game.ClientState.Fates.FateState.Running
            ? FateApproachArrivalRadius
            : job.ArrivalRadius;

    private static bool ShouldUseFlightApproach(bool strictDestination, bool fly)
        => fly && !strictDestination;

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
        ClearObstacleMapForJob(jobId);
    }

    private void ClearObstacleMapForJob(Guid jobId)
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            lock (jobGate)
            {
                if (obstacleMapJobId != jobId) return;
                obstacleMap.ClearTemporaryMap();
                obstacleMapJobId = null;
            }
        });
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
            // Serialize notifications with job replacement; stale jobs must never overwrite observers.
            log.Debug("TravelController: phase => {Phase} ({Detail})", newPhase, detail);
            PhaseChanged?.Invoke(this, new TravelPhaseChangedEventArgs(newPhase, detail, snapshot));
        }
        return true;
    }

    private void StopNavmesh(Guid jobId)
    {
        long version;
        lock (jobGate)
        {
            if (navmeshJobId != jobId) return;
            version = navmeshMoveVersion;
        }
        _ = framework.RunOnFrameworkThread(() =>
        {
            lock (jobGate)
            {
                if (navmeshJobId != jobId || navmeshMoveVersion != version) return;
                navmeshClient.Stop();
            }
            // A completed SimpleMove task can still publish its path next update.
            // Keep ownership until the provider drains it; never stop a replacement job.
            _ = framework.RunOnTick(() => DrainStoppedMove(jobId, version), delayTicks: 2);
        });
    }

    private void DrainStoppedMove(Guid jobId, long version)
    {
        lock (jobGate)
        {
            if (navmeshJobId != jobId || navmeshMoveVersion != version) return;
            navmeshClient.Stop();
            if (!navmeshClient.IsPathfinding)
            {
                navmeshJobId = null;
                return;
            }
        }
        _ = framework.RunOnTick(() => DrainStoppedMove(jobId, version), delayTicks: 2);
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
                if (token.IsCancellationRequested) { tcs.TrySetCanceled(token); return; }
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
                lock (jobGate)
                {
                    if (token.IsCancellationRequested || !IsCurrentJob(jobId))
                    {
                        tcs.TrySetCanceled();
                        return;
                    }
                    try { tcs.TrySetResult(action()); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
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
