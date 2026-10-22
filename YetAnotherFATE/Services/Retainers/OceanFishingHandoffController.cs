using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Chains;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Retainers;
using YetAnotherFATE.Services.Travel;

namespace YetAnotherFATE.Services.Retainers;

/// <summary>Owns scheduled, single-voyage handoffs to WahTools Feesh.</summary>
public sealed class OceanFishingHandoffController : IDisposable
{
    private static readonly TimeSpan CharacterTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DutyStartTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan VoyageTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan AmbiguousLaunchTimeout = TimeSpan.FromMinutes(2);
    private const float CleanupPositionTolerance = 0.1f;
    private const uint OceanFishingTerritoryOne = 900;
    private const uint OceanFishingTerritoryTwo = 1163;

    private readonly Configuration configuration;
    private readonly IAutoRetainerClient autoRetainer;
    private readonly AutoRetainerHandoffController autoRetainerHandoff;
    private readonly IWahToolsFeeshClient feesh;
    private readonly ILifestreamCharacterClient lifestream;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly IDutyState dutyState;
    private readonly IFramework framework;
    private readonly YesAlreadyLease yesAlreadyLease;
    private readonly IPluginLog log;
    private readonly NearestAetheryteReturnRoute safeLocationRoute;
    private readonly TerritoryReturnRoute territoryReturnRoute;
    private DateTimeOffset nextCharacterAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset nextRosterScan = DateTimeOffset.MinValue;
    private IReadOnlyList<AutoRetainerCharacter> rosterCache = Array.Empty<AutoRetainerCharacter>();
    private bool rosterAvailable;
    private DateTime nextStatusRefreshUtc = DateTime.MinValue;
    private WahToolsFeeshStatus? feeshStatus;
    private DateTime? cleanupStableSinceUtc;
    private Vector3? cleanupStablePosition;
    private uint cleanupStableTerritory;
    private ulong cleanupStableContentId;
    private long? cleanupStableGeneration;
    private OceanCleanupConfirmationSource cleanupSource;
    private bool feeshSessionObservedThisRuntime;
    private bool disposed;

    public OceanFishingHandoffController(Configuration configuration, IAutoRetainerClient autoRetainer,
        AutoRetainerHandoffController autoRetainerHandoff,
        IWahToolsFeeshClient feesh, ILifestreamCharacterClient lifestream, IClientState clientState,
        IPlayerState playerState, IObjectTable objectTable, IGameGui gameGui, ICondition condition, IDutyState dutyState, IFramework framework,
        YesAlreadyLease yesAlreadyLease,
        IPluginLog log, NearestAetheryteReturnRoute safeLocationRoute, TerritoryReturnRoute territoryReturnRoute)
    {
        this.configuration = configuration;
        this.autoRetainer = autoRetainer;
        this.autoRetainerHandoff = autoRetainerHandoff;
        this.feesh = feesh;
        this.lifestream = lifestream;
        this.clientState = clientState;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.condition = condition;
        this.dutyState = dutyState;
        this.framework = framework;
        this.yesAlreadyLease = yesAlreadyLease;
        this.log = log;
        this.safeLocationRoute = safeLocationRoute;
        this.territoryReturnRoute = territoryReturnRoute;
        dutyState.DutyStarted += OnDutyStarted;
        dutyState.DutyCompleted += OnDutyCompleted;

        if (configuration.OceanFishingHandoff is { } restored)
        {
            if (restored.YesAlreadyLeaseAcquired)
            {
                var lease = yesAlreadyLease.TryAcquire(out _);
                if (lease != YesAlreadyLeaseResult.Acquired)
                {
                    restored.YesAlreadyLeaseAcquired = false;
                    configuration.Save();
                }
            }
            if (restored.Phase == AutoRetainerHandoffPhase.Failed)
            {
                ReleaseYesAlreadyLease(restored);
                Status = restored.LastError ?? "Ocean Fishing recovery requires manual intervention";
            }
            else if (restored.Phase is AutoRetainerHandoffPhase.ReturningOriginalCharacter or
                AutoRetainerHandoffPhase.WaitingForOriginalCharacter or
                AutoRetainerHandoffPhase.ReturningOriginalTerritory or
                AutoRetainerHandoffPhase.Resuming or
                AutoRetainerHandoffPhase.ChainingAutoRetainer)
            {
                if (restored.Phase == AutoRetainerHandoffPhase.ReturningOriginalTerritory)
                    territoryReturnRoute.Reset();
                Status = $"Recovered Ocean Fishing handoff at {restored.Phase}; continuing safe return";
            }
            else if (IsOceanTerritory(clientState.TerritoryType) &&
                     (dutyState.IsDutyStarted || condition[ConditionFlag.BoundByDuty]))
            {
                restored.DutyStarted = true;
                restored.OceanTerritoryEntered = true;
                restored.Phase = AutoRetainerHandoffPhase.WaitingForOceanVoyage;
                ReleaseYesAlreadyLease(restored);
                Status = "Recovered active Ocean Fishing voyage; observing only";
            }
            else if (restored.DutyCompleted)
            {
                restored.Phase = AutoRetainerHandoffPhase.WaitingForOceanVoyage;
                Status = "Recovered completed voyage; confirming safe exit";
            }
            else if (restored.LaunchAmbiguous)
            {
                Fail(restored, "Reload interrupted an ambiguous WahTools launch; confirm Feesh stopped before manual recovery");
            }
            else
            {
                restored.OceanFishingLegSkipped = true;
                restored.WindowConsumed = true;
                configuration.LastHandledOceanFishingWindowUtc = restored.WindowStartUtc;
                ReleaseYesAlreadyLease(restored);
                BeginReturnToOrigin(restored);
                Status = "Reload interrupted before a confirmed voyage; returning without stopping unowned Feesh work";
            }
        }
    }

    public string Status { get; private set; } = "Disabled";
    public AutoRetainerHandoffPhase Phase => configuration.OceanFishingHandoff?.Phase ?? AutoRetainerHandoffPhase.Idle;
    public bool HasSession => configuration.OceanFishingHandoff is not null;
    public bool ShouldDispatchChainedRetainer => configuration.OceanFishingHandoff is
        { Phase: AutoRetainerHandoffPhase.ChainingAutoRetainer, RetainerLegRequested: true, RetainerLegObserved: false };
    public DateTime? NextWindowUtc => OceanFishingSchedule.GetState(DateTime.UtcNow,
        TimeSpan.FromMinutes(configuration.OceanFishingLeadMinutes)).Window.StartUtc;

    public bool Tick(AutomatorChain chain, bool retainerSessionActive)
    {
        if (disposed) return false;
        if (configuration.OceanFishingHandoff is { } session)
            return TickSession(chain, session);
        if (retainerSessionActive || !configuration.OceanFishingEnabled || !configuration.AutomatorEnabled || !clientState.IsLoggedIn)
            return false;

        var schedule = OceanFishingSchedule.GetState(DateTime.UtcNow, TimeSpan.FromMinutes(configuration.OceanFishingLeadMinutes));
        if (!schedule.LaunchEligible)
        {
            Status = $"Next Ocean Fishing registration {schedule.Window.StartUtc:HH:mm} UTC";
            return false;
        }
        if (OceanFishingSchedule.IsHandled(configuration.LastHandledOceanFishingWindowUtc, schedule.Window.StartUtc))
            return false;

        if (!autoRetainer.TryGetState(out var autoBusy, out var multiActive) || autoBusy || multiActive)
        {
            Status = "Ocean Fishing pending; waiting for AutoRetainer to become idle";
            return false;
        }
        if (!lifestream.TryIsBusy(out var lifestreamBusy) || lifestreamBusy)
        {
            Status = "Ocean Fishing pending; waiting for Lifestream to become idle";
            return false;
        }
        if (!IsPlayerSafe())
        {
            Status = "Ocean Fishing pending; waiting for safe player state";
            return false;
        }

        if (!feesh.IsAvailable)
        {
            Status = "WahTools is unavailable; waiting without claiming the window";
            return false;
        }
        AutoRetainerCharacter? target;
        if (configuration.OceanFishingCharacterMode == OceanFishingCharacterMode.CurrentCharacter)
        {
            var player = objectTable.LocalPlayer;
            var name = player?.Name.ToString() ?? string.Empty;
            var world = player?.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty;
            target = player is not null && playerState.ContentId != 0
                ? new AutoRetainerCharacter(playerState.ContentId, name, world)
                : null;
        }
        else
        {
            if (DateTimeOffset.UtcNow >= nextRosterScan)
            {
                nextRosterScan = DateTimeOffset.UtcNow.AddSeconds(30);
                rosterAvailable = autoRetainer.TryGetRegisteredCharacters(out rosterCache);
            }
            if (!rosterAvailable)
            {
                Status = "Waiting for AutoRetainer character roster";
                return false;
            }
            target = rosterCache.FirstOrDefault(c => c.ContentId == configuration.OceanFishingCharacterContentId);
        }
        if (target is null || string.IsNullOrWhiteSpace(target.Name) || string.IsNullOrWhiteSpace(target.World))
        {
            Status = "Selected Ocean Fishing character is not available in AutoRetainer roster";
            return false;
        }
        if (!chain.CanStartOceanFishingHandoff())
        {
            Status = "Ocean Fishing pending; waiting for safe FATE boundary";
            return false;
        }
        StartSession(chain, schedule.Window, target);
        return true;
    }

    public bool ConfirmFeeshStoppedAndReturn()
    {
        var session = configuration.OceanFishingHandoff;
        if (session?.Phase != AutoRetainerHandoffPhase.Failed) return false;
        if (!IsPlayerSafe() || IsOceanTerritory(clientState.TerritoryType))
        {
            Status = "Manual return blocked until duty, combat, and player activity have ended";
            return false;
        }
        if (!autoRetainer.TryGetState(out var autoBusy, out var multiActive) || autoBusy || multiActive ||
            !lifestream.TryIsBusy(out var lifestreamBusy) || lifestreamBusy)
        {
            Status = "Manual return blocked until AutoRetainer and Lifestream are available and idle";
            return false;
        }
        var recoveryStatus = feesh.ReadStatus();
        if (recoveryStatus.IsAvailable && WahToolsFeeshStatusPolicy.GetCleanupBlockers(recoveryStatus).Count > 0)
        {
            Status = "Manual return blocked: WahTools still reports active trip or cleanup";
            return false;
        }
        session.LastError = null;
        BeginReturnToOrigin(session);
        Status = "Manual recovery confirmed; returning to farming character";
        return true;
    }

    public bool AbandonFailedSession()
    {
        if (configuration.OceanFishingHandoff?.Phase != AutoRetainerHandoffPhase.Failed) return false;
        configuration.OceanFishingHandoff = null;
        configuration.Save();
        Status = "Ocean Fishing recovery discarded";
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        dutyState.DutyStarted -= OnDutyStarted;
        dutyState.DutyCompleted -= OnDutyCompleted;
        yesAlreadyLease.Release();
    }

    private bool TickSession(AutomatorChain chain, OceanFishingHandoffSession session)
    {
        switch (session.Phase)
        {
            case AutoRetainerHandoffPhase.PreparingSafeLocation:
                return TickSafeLocation(session);
            case AutoRetainerHandoffPhase.SwitchingFishingCharacter:
                return TickSwitchToFishing(session);
            case AutoRetainerHandoffPhase.WaitingForFishingCharacter:
                return TickWaitForFishing(session);
            case AutoRetainerHandoffPhase.StartingOceanVoyage:
                return TickStartVoyage(session);
            case AutoRetainerHandoffPhase.WaitingForOceanVoyage:
                return TickWaitForVoyage(session);
            case AutoRetainerHandoffPhase.WaitingForWahToolsStop:
                return TickWaitForWahToolsStop(session);
            case AutoRetainerHandoffPhase.ChainingAutoRetainer:
                return TickChainedAutoRetainer(session);
            case AutoRetainerHandoffPhase.ReturningOriginalCharacter:
                return TickReturnCharacter(session);
            case AutoRetainerHandoffPhase.WaitingForOriginalCharacter:
                return TickWaitForOriginal(session);
            case AutoRetainerHandoffPhase.ReturningOriginalTerritory:
                return TickReturnTerritory(session);
            case AutoRetainerHandoffPhase.Resuming:
                return TickResume(chain, session);
            case AutoRetainerHandoffPhase.Failed:
                Status = session.LastError ?? "Ocean Fishing handoff requires manual recovery";
                return true;
            default:
                Fail(session, $"Unsupported Ocean Fishing phase {session.Phase}");
                return true;
        }
    }

    private bool TickSafeLocation(OceanFishingHandoffSession session)
    {
        if (!IsOriginalCharacter(session)) { Fail(session, "Original character changed before safe departure"); return true; }
        if (DateTime.UtcNow >= session.WindowStartUtc + OceanFishingSchedule.LaunchCutoffOffset)
        {
            configuration.LastHandledOceanFishingWindowUtc = session.WindowStartUtc;
            BeginReturnToOrigin(session);
            Status = "Ocean Fishing launch cutoff passed; skipping this window";
            return true;
        }
        var player = objectTable.LocalPlayer;
        var loaded = clientState.TerritoryType != 0 && playerState.IsLoaded && player is { CurrentHp: > 0 } &&
                     !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51];
        var result = safeLocationRoute.Tick(clientState.TerritoryType, player?.Position, loaded,
            IsPlayerSafe() && configuration.TeleportEnabled, DateTimeOffset.UtcNow);
        if (result == AetheryteReturnResult.Arrived)
        {
            SetPhase(session, session.FishingContentId == session.OriginalContentId
                ? AutoRetainerHandoffPhase.StartingOceanVoyage
                : AutoRetainerHandoffPhase.SwitchingFishingCharacter);
            Status = "At safe aetheryte; preparing fishing character";
        }
        else if (result == AetheryteReturnResult.Failed) Fail(session, safeLocationRoute.Failure ?? "Could not reach safe aetheryte");
        else Status = safeLocationRoute.IsTeleportIssued ? $"Returning to {safeLocationRoute.DestinationName} before Ocean Fishing" : "Preparing safe departure location";
        return true;
    }

    private bool TickSwitchToFishing(OceanFishingHandoffSession session)
    {
        if (!IsOriginalCharacter(session) || !IsPlayerSafe()) { Status = "Waiting for safe original-character departure"; return true; }
        if (!lifestream.TryIsBusy(out var busy) || busy) { Status = "Waiting for Lifestream to become available"; return true; }
        if (DateTimeOffset.UtcNow < nextCharacterAttempt) return true;
        nextCharacterAttempt = DateTimeOffset.UtcNow.AddSeconds(5);
        if (!lifestream.TryChangeCharacter(session.FishingCharacterName, session.FishingWorldName, out var error))
        {
            Status = $"Lifestream character switch not accepted: {error}";
            return true;
        }
        SetPhase(session, AutoRetainerHandoffPhase.WaitingForFishingCharacter);
        Status = $"Switching to {session.FishingCharacterName}@{session.FishingWorldName}";
        return true;
    }

    private bool TickWaitForFishing(OceanFishingHandoffSession session)
    {
        if (DateTimeOffset.UtcNow - session.PhaseStartedAtUtc > CharacterTimeout)
        { Fail(session, "Selected fishing character did not load within ten minutes"); return true; }
        if (clientState.IsLoggedIn && playerState.ContentId == session.FishingContentId && IsPlayerSafe())
        {
            SetPhase(session, AutoRetainerHandoffPhase.StartingOceanVoyage);
            return true;
        }
        Status = $"Waiting for selected fishing character {session.FishingCharacterName}@{session.FishingWorldName}";
        return true;
    }

    private bool TickStartVoyage(OceanFishingHandoffSession session)
    {
        if (!IsFishingCharacter(session) || !IsPlayerSafe() || IsOceanTerritory(clientState.TerritoryType) ||
            condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56])
        { Status = "Waiting for selected character and safe voyage launch state"; return true; }
        if (!OceanFishingPolicy.CanDispatch(DateTime.UtcNow, session.WindowStartUtc,
                IsFishingCharacter(session), IsPlayerSafe()))
        {
            configuration.LastHandledOceanFishingWindowUtc = session.WindowStartUtc;
            SetPhase(session, AutoRetainerHandoffPhase.ReturningOriginalCharacter);
            Status = "Ocean Fishing cutoff passed before launch; returning";
            return true;
        }
        if (session.LaunchRequested)
        {
            SetPhase(session, AutoRetainerHandoffPhase.WaitingForOceanVoyage);
            Status = "Launch intent was persisted; observing without retry";
            return true;
        }
        var leaseResult = yesAlreadyLease.TryAcquire(out var leaseError);
        if (leaseResult == YesAlreadyLeaseResult.Unavailable)
        {
            Fail(session, leaseError);
            return true;
        }
        session.YesAlreadyLeaseRequired = leaseResult == YesAlreadyLeaseResult.Acquired;
        session.YesAlreadyLeaseAcquired = yesAlreadyLease.IsAcquired;
        configuration.Save();

        var autoOcean = feesh.DisableAutoOcean(out var autoOceanError);
        if (autoOcean != FeeshRequestResult.Accepted)
        {
            Fail(session, autoOceanError);
            return true;
        }

        session.LaunchRequested = true;
        session.LaunchAmbiguous = true;
        configuration.Save();
        var result = feesh.CatchTheBoat(out var error);
        if (result == FeeshRequestResult.Accepted)
        {
            session.LaunchAccepted = true;
            session.LaunchAmbiguous = false;
            feeshSessionObservedThisRuntime = true;
            configuration.Save();
            SetPhase(session, AutoRetainerHandoffPhase.WaitingForOceanVoyage);
            Status = "WahTools accepted voyage request; waiting for duty";
        }
        else if (result == FeeshRequestResult.Refused)
        {
            session.LaunchAmbiguous = false;
            session.WindowConsumed = true;
            configuration.LastHandledOceanFishingWindowUtc = session.WindowStartUtc;
            ReleaseYesAlreadyLease(session);
            configuration.Save();
            SetPhase(session, AutoRetainerHandoffPhase.ReturningOriginalCharacter);
            Status = $"WahTools refused voyage request; returning to farming character: {error}";
        }
        else
        {
            session.LaunchAmbiguous = true;
            configuration.Save();
            SetPhase(session, AutoRetainerHandoffPhase.WaitingForOceanVoyage);
            Status = "WahTools launch outcome ambiguous; observing and will not retry";
        }
        return true;
    }

    private bool TickWaitForVoyage(OceanFishingHandoffSession session)
    {
        RefreshFeeshStatus();
        if (session.DutyCompleted)
        {
            SetPhase(session, AutoRetainerHandoffPhase.WaitingForWahToolsStop);
            return true;
        }

        if (feeshStatus is { IsAvailable: true } failedStatus && WahToolsFeeshStatusPolicy.IsTripFailed(failedStatus))
        {
            Fail(session, $"WahTools boat trip failed: {failedStatus.TripStatus ?? "no failure detail"}");
            return true;
        }

        var observation = ObserveOceanVoyage(session);
        if (observation == OceanVoyageObservation.TerritoryEntered)
        {
                session.OceanTerritoryEntered = true;
                session.DutyStarted = true;
                feeshSessionObservedThisRuntime = true;
                ReleaseYesAlreadyLease(session);
            configuration.Save();
        }
        else if (observation == OceanVoyageObservation.DutyActive && !session.DutyStarted)
        {
            session.OceanTerritoryEntered = true;
            session.DutyStarted = true;
            feeshSessionObservedThisRuntime = true;
            ReleaseYesAlreadyLease(session);
            configuration.Save();
        }
        if (observation == OceanVoyageObservation.TerritoryExitedUnconfirmed)
        {
            session.OceanTerritoryExited = true;
            Fail(session, "Ocean territory exited without confirmed duty completion; manual recovery required");
            return true;
        }

        var timeout = session.DutyStarted ? VoyageTimeout : DutyStartTimeout;
        if (DateTimeOffset.UtcNow - session.PhaseStartedAtUtc > timeout)
        {
            Fail(session, session.DutyStarted
                ? "Ocean Fishing voyage did not complete within two hours"
                : "WahTools did not enter Ocean Fishing within 30 minutes");
            return true;
        }
        if (session.LaunchAmbiguous && !session.OceanTerritoryEntered &&
            DateTimeOffset.UtcNow - session.PhaseStartedAtUtc > AmbiguousLaunchTimeout)
        {
            Fail(session, "WahTools launch outcome is ambiguous and no voyage entry was observed; confirm Feesh stopped");
            return true;
        }
        Status = session.DutyStarted ? "Ocean Fishing voyage active; waiting for duty completion" : "Waiting for WahTools to enter Ocean Fishing";
        return true;
    }

    private bool TickWaitForWahToolsStop(OceanFishingHandoffSession session)
    {
        RefreshFeeshStatus();
        if (!session.DutyCompleted)
        {
            Fail(session, "WahTools stop phase entered without confirmed duty completion");
            return true;
        }

        var snapshot = CaptureCleanupSnapshot();
        var source = OceanCleanupConfirmationPolicy.SelectSource(feeshStatus, DateTime.UtcNow, session.StopAcceptedAtUtc);
        if (source == OceanCleanupConfirmationSource.WahToolsReflection && feeshStatus is { } reflected &&
            (WahToolsFeeshStatusPolicy.IsTripFailed(reflected) || WahToolsFeeshStatusPolicy.HasCleanerFailure(reflected)))
        {
            Fail(session, WahToolsFeeshStatusPolicy.IsTripFailed(reflected)
                ? $"WahTools trip failed during cleanup: {reflected.TripStatus ?? "no detail"}"
                : $"WahTools cleaner failed: {reflected.CleanerStatus ?? "no detail"}");
            return true;
        }
        var blockers = GetCleanupBlockers(snapshot, source);
        if (session.StopRequested && !session.StopAccepted)
        {
            Fail(session, "WahTools Stop outcome is ambiguous; confirm Feesh stopped before returning");
            return true;
        }

        if (session.StopAcceptedAtUtc is null)
        {
            if (blockers.Count > 0)
            {
                ResetCleanupStability();
                Status = $"Waiting for post-voyage cleanup: {string.Join("; ", blockers)}";
                return true;
            }
            if (!AccumulateCleanupStableTime(snapshot, source, DateTime.UtcNow))
            {
                Status = $"Confirming {source} cleanup state before stopping Feesh";
                return true;
            }

            if (!session.LaunchAccepted || session.LaunchAmbiguous || !feeshSessionObservedThisRuntime)
            {
                // QR avoids issuing Stop when ownership of Feesh automation is uncertain.
                CompleteOceanFishingLeg(session);
                return true;
            }

            session.StopRequested = true;
            session.StopRequestedAtUtc = DateTime.UtcNow;
            session.StopStatusGeneration = feeshStatus?.Generation;
            configuration.Save();
            var result = feesh.Stop(out var error);
            if (result != FeeshRequestResult.Accepted)
            {
                Fail(session, $"WahTools Stop was not confirmed: {error}");
                return true;
            }
            session.StopAccepted = true;
            session.StopAcceptedAtUtc = DateTime.UtcNow;
            configuration.Save();
            ResetCleanupStability();
            Status = "WahTools Stop accepted; waiting for fresh post-stop idle state";
            return true;
        }

        if (source == OceanCleanupConfirmationSource.None)
        {
            ResetCleanupStability();
            Status = "WahTools Stop accepted; waiting for fresh post-stop status";
            return true;
        }
        if (source == OceanCleanupConfirmationSource.WahToolsReflection && feeshStatus is { } current &&
            session.StopStatusGeneration != current.Generation)
        {
            session.StopStatusGeneration = current.Generation;
            configuration.Save();
            ResetCleanupStability();
        }
        if (blockers.Count > 0)
        {
            ResetCleanupStability();
            Status = $"Waiting for Feesh to stop: {string.Join("; ", blockers)}";
            return true;
        }
        if (!AccumulateCleanupStableTime(snapshot, source, DateTime.UtcNow))
        {
            Status = $"Confirming post-stop {source} idle state";
            return true;
        }
        CompleteOceanFishingLeg(session);
        return true;
    }

    private bool TickChainedAutoRetainer(OceanFishingHandoffSession session)
    {
        if (session.RetainerLegRequested)
        {
            if (autoRetainerHandoff.HasSession)
            {
                if (!session.RetainerLegObserved)
                {
                    session.RetainerLegObserved = true;
                    configuration.Save();
                }
                Status = "Chained AutoRetainer pass active; YAF remains paused";
                return true;
            }
            if (session.RetainerLegObserved || !autoRetainerHandoff.HasPendingManualPass)
            {
                session.WindowConsumed = true;
                BeginReturnToOrigin(session);
                return true;
            }
            if (DateTimeOffset.UtcNow - session.PhaseStartedAtUtc >= TimeSpan.FromMinutes(5))
            {
                autoRetainerHandoff.CancelPendingManualPass();
                session.WindowConsumed = true;
                BeginReturnToOrigin(session);
                Status = "AutoRetainer safe-boundary wait timed out; returning to FATE farming";
                return true;
            }
            Status = "Waiting for chained AutoRetainer safe boundary";
            return true;
        }

        if (!configuration.AutoRetainerHandoffEnabled)
        {
            session.WindowConsumed = true;
            BeginReturnToOrigin(session);
            return true;
        }
        if (DateTimeOffset.UtcNow - session.PhaseStartedAtUtc < TimeSpan.FromSeconds(5))
        {
            Status = "Waiting before refreshing AutoRetainer readiness after Ocean Fishing";
            return true;
        }
        if (!autoRetainer.TryGetReadiness(configuration.AutoRetainerMode, out var readiness))
        {
            if (DateTimeOffset.UtcNow - session.PhaseStartedAtUtc >= TimeSpan.FromMinutes(1))
            {
                session.WindowConsumed = true;
                BeginReturnToOrigin(session);
                Status = "AutoRetainer readiness unavailable for one minute; returning to FATE farming";
                return true;
            }
            Status = "Waiting for fresh AutoRetainer readiness after Ocean Fishing";
            return true;
        }
        if (!readiness.WorkReady)
        {
            session.WindowConsumed = true;
            BeginReturnToOrigin(session);
            Status = "No AutoRetainer work ready; returning to FATE farming";
            return true;
        }

        session.RetainerLegRequested = true;
        configuration.Save();
        autoRetainerHandoff.RequestManualPass(resumeAutomator: false);
        Status = "Ocean Fishing complete; starting eligible AutoRetainer pass before resuming farming";
        return true;
    }

    private void CompleteOceanFishingLeg(OceanFishingHandoffSession session)
    {
        session.WindowConsumed = true;
        configuration.LastHandledOceanFishingWindowUtc = session.WindowStartUtc;
        ReleaseYesAlreadyLease(session);
        ResetCleanupStability();
        configuration.Save();
        SetPhase(session, AutoRetainerHandoffPhase.ChainingAutoRetainer);
        Status = "Ocean Fishing and Feesh cleanup confirmed; checking chained AutoRetainer work";
    }

    private OceanVoyageObservation ObserveOceanVoyage(OceanFishingHandoffSession session) => OceanFishingPolicy.Observe(
        IsOceanTerritory(clientState.TerritoryType),
        IsOceanTerritory(clientState.TerritoryType) && (dutyState.IsDutyStarted || condition[ConditionFlag.BoundByDuty]),
        session.DutyCompleted,
        session.OceanTerritoryEntered);

    private void RefreshFeeshStatus()
    {
        if (DateTime.UtcNow < nextStatusRefreshUtc) return;
        feeshStatus = feesh.ReadStatus();
        nextStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1);
    }

    private OceanFishingCleanupSnapshot CaptureCleanupSnapshot()
    {
        var resultAddon = gameGui.GetAddonByName("IKDResult");
        var autoAvailable = autoRetainer.TryGetState(out var autoBusy, out var multiActive);
        var lifestreamAvailable = lifestream.TryIsBusy(out var lifestreamBusy);
        return new OceanFishingCleanupSnapshot(
            configuration.OceanFishingHandoff?.DutyCompleted == true,
            IsOceanTerritory(clientState.TerritoryType),
            !resultAddon.IsNull && resultAddon.IsVisible,
            condition[ConditionFlag.NormalConditions],
            !IsPlayerSafe(),
            condition[ConditionFlag.Fishing],
            condition[ConditionFlag.ExecutingGatheringAction],
            condition[ConditionFlag.Casting],
            condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51],
            clientState.IsLoggedIn && playerState.ContentId != 0 && objectTable.LocalPlayer is not null,
            lifestreamAvailable, lifestreamBusy,
            autoAvailable, autoBusy, multiActive);
    }

    private List<string> GetCleanupBlockers(OceanFishingCleanupSnapshot snapshot, OceanCleanupConfirmationSource source)
    {
        var blockers = OceanFishingCleanupPolicy.GetWaitReasons(snapshot).ToList();
        if (source == OceanCleanupConfirmationSource.WahToolsReflection && feeshStatus is { } status)
        {
            if (WahToolsFeeshStatusPolicy.IsTripFailed(status)) blockers.Add("WahTools trip failed");
            if (WahToolsFeeshStatusPolicy.HasCleanerFailure(status)) blockers.Add("WahTools cleaner failed");
            blockers.AddRange(WahToolsFeeshStatusPolicy.GetCleanupBlockers(status));
        }
        return blockers;
    }

    private bool AccumulateCleanupStableTime(OceanFishingCleanupSnapshot snapshot, OceanCleanupConfirmationSource source, DateTime nowUtc)
    {
        var player = objectTable.LocalPlayer;
        if (player is null || source == OceanCleanupConfirmationSource.None) return false;
        var generation = source == OceanCleanupConfirmationSource.WahToolsReflection ? feeshStatus?.Generation : null;
        var positionChanged = cleanupStableSinceUtc is not null &&
                              (cleanupStableTerritory != clientState.TerritoryType ||
                               cleanupStableContentId != playerState.ContentId ||
                               cleanupStablePosition is not { } stablePosition ||
                               Vector3.Distance(player.Position, stablePosition) > CleanupPositionTolerance);
        var evidenceChanged = cleanupStableSinceUtc is not null &&
                              !OceanCleanupConfirmationPolicy.IsSameEvidence(
                                  source, generation, cleanupSource, cleanupStableGeneration);
        if (cleanupStableSinceUtc is null || positionChanged || evidenceChanged)
        {
            cleanupStableSinceUtc = nowUtc;
            cleanupStablePosition = player.Position;
            cleanupStableTerritory = clientState.TerritoryType;
            cleanupStableContentId = playerState.ContentId;
            cleanupStableGeneration = generation;
            cleanupSource = source;
            return false;
        }
        return OceanCleanupConfirmationPolicy.HasConfirmedStableTime(cleanupStableSinceUtc.Value, nowUtc, source);
    }

    private void ResetCleanupStability()
    {
        cleanupStableSinceUtc = null;
        cleanupStablePosition = null;
        cleanupStableTerritory = 0;
        cleanupStableContentId = 0;
        cleanupStableGeneration = null;
        cleanupSource = OceanCleanupConfirmationSource.None;
    }

    private bool TickReturnCharacter(OceanFishingHandoffSession session)
    {
        if (IsOriginalCharacter(session) && IsPlayerSafe())
        {
            BeginReturnToOrigin(session);
            return true;
        }
        if (DateTimeOffset.UtcNow - session.PhaseStartedAtUtc > CharacterTimeout) { Fail(session, "Could not return to farming character"); return true; }
        if (!lifestream.TryIsBusy(out var busy) || busy) { Status = "Waiting for Lifestream before returning to farming character"; return true; }
        if (DateTimeOffset.UtcNow < nextCharacterAttempt) return true;
        nextCharacterAttempt = DateTimeOffset.UtcNow.AddSeconds(5);
        if (lifestream.TryChangeCharacter(session.OriginalCharacterName, session.OriginalWorldName, out var error))
        {
            SetPhase(session, AutoRetainerHandoffPhase.WaitingForOriginalCharacter);
            Status = $"Returning to {session.OriginalCharacterName}@{session.OriginalWorldName}";
        }
        else Status = $"Lifestream return failed: {error}";
        return true;
    }

    private bool TickWaitForOriginal(OceanFishingHandoffSession session)
    {
        if (DateTimeOffset.UtcNow - session.PhaseStartedAtUtc > CharacterTimeout) { Fail(session, "Farming character did not finish loading"); return true; }
        if (IsOriginalCharacter(session) && IsPlayerSafe()) BeginReturnToOrigin(session);
        else Status = $"Waiting for {session.OriginalCharacterName}@{session.OriginalWorldName}";
        return true;
    }

    private bool TickReturnTerritory(OceanFishingHandoffSession session)
    {
        if (!IsOriginalCharacter(session)) { SetPhase(session, AutoRetainerHandoffPhase.ReturningOriginalCharacter); return true; }
        var result = territoryReturnRoute.Tick(session.OriginalTerritoryId, session.OriginalPosition,
            clientState.TerritoryType, IsPlayerSafe(), DateTimeOffset.UtcNow);
        if (result == TerritoryReturnResult.Arrived) SetPhase(session, AutoRetainerHandoffPhase.Resuming);
        else if (result == TerritoryReturnResult.Failed)
        {
            var reason = territoryReturnRoute.Failure ?? "Could not return to farming territory";
            log.Warning("Ocean Fishing origin return failed. OriginalContentId={ContentId} CurrentTerritory={CurrentTerritory} DestinationTerritory={DestinationTerritory} PlayerSafe={PlayerSafe} ElapsedSeconds={ElapsedSeconds:0.0} Attempts={Attempts} Failure={Failure}",
                session.OriginalContentId, clientState.TerritoryType, session.OriginalTerritoryId, IsPlayerSafe(),
                territoryReturnRoute.Elapsed.TotalSeconds, territoryReturnRoute.Attempts, reason);
            Fail(session, reason);
        }
        else Status = $"Returning to farming territory {session.OriginalTerritoryId}";
        return true;
    }

    private bool TickResume(AutomatorChain chain, OceanFishingHandoffSession session)
    {
        if (!IsOriginalCharacter(session) || !IsPlayerSafe() || clientState.TerritoryType != session.OriginalTerritoryId)
        { SetPhase(session, AutoRetainerHandoffPhase.ReturningOriginalCharacter); return true; }
        configuration.LastHandledOceanFishingWindowUtc = session.WindowStartUtc;
        var resume = session.WasAutomatorEnabled && configuration.AutomatorEnabled;
        configuration.OceanFishingHandoff = null;
        configuration.Save();
        if (resume) chain.ResumeAfterExternalHandoff("Ocean Fishing handoff complete");
        else chain.PrepareForExternalHandoff("Ocean Fishing complete; automator disabled");
        Status = resume ? "Voyage complete; resuming FATE automation" : "Voyage complete";
        return false;
    }

    private void StartSession(AutomatorChain chain, OceanFishingWindow window, AutoRetainerCharacter target)
    {
        var player = objectTable.LocalPlayer;
        var name = player?.Name.ToString() ?? string.Empty;
        var world = player?.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty;
        if (player is null || playerState.ContentId == 0 || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world) || clientState.TerritoryType == 0)
        { Status = "Ocean Fishing requires loaded farming character identity and territory"; return; }
        var now = DateTimeOffset.UtcNow;
        var session = new OceanFishingHandoffSession
        {
            Phase = AutoRetainerHandoffPhase.PreparingSafeLocation,
            WindowStartUtc = window.StartUtc,
            OriginalContentId = playerState.ContentId,
            OriginalTerritoryId = clientState.TerritoryType,
            OriginalPosition = player.Position,
            OriginalCharacterName = name,
            OriginalWorldName = world,
            FishingContentId = target.ContentId,
            FishingCharacterName = target.Name,
            FishingWorldName = target.World,
            WasAutomatorEnabled = configuration.AutomatorEnabled,
            StartedAtUtc = now,
            PhaseStartedAtUtc = now,
        };
        configuration.OceanFishingHandoff = session;
        configuration.LastHandledOceanFishingWindowUtc = window.StartUtc;
        configuration.Save();
        chain.PrepareForExternalHandoff("Ocean Fishing handoff starting");
        safeLocationRoute.Reset();
        Status = "Preparing safe departure before Ocean Fishing";
        log.Information("Ocean Fishing handoff claimed window {Window} for {Character}@{World}.", window.StartUtc, target.Name, target.World);
    }

    private void OnDutyStarted(Dalamud.Game.DutyState.IDutyStateEventArgs args)
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            var session = configuration.OceanFishingHandoff;
            if (disposed || session is null || !OceanFishingPolicy.CanObserveDutyStarted(
                    session.LaunchRequested, IsOceanTerritory(clientState.TerritoryType), session.DutyStarted)) return;
            session.OceanTerritoryEntered = true;
            session.DutyStarted = true;
            feeshSessionObservedThisRuntime = true;
            ReleaseYesAlreadyLease(session);
            configuration.Save();
            log.Information("Ocean Fishing duty-start event observed. Territory={Territory}", clientState.TerritoryType);
        });
    }

    private void OnDutyCompleted(Dalamud.Game.DutyState.IDutyStateEventArgs args)
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            var session = configuration.OceanFishingHandoff;
            if (disposed || session is null || !OceanFishingPolicy.CanObserveDutyCompleted(
                    session.LaunchRequested, session.DutyStarted, session.DutyCompleted)) return;
            session.DutyCompleted = true;
            configuration.Save();
            log.Information("Ocean Fishing duty-completed event observed.");
        });
    }

    private void SetPhase(OceanFishingHandoffSession session, AutoRetainerHandoffPhase phase)
    {
        session.Phase = phase;
        session.PhaseStartedAtUtc = DateTimeOffset.UtcNow;
        configuration.Save();
    }

    private void BeginReturnToOrigin(OceanFishingHandoffSession session)
    {
        if (IsOriginalCharacter(session))
        {
            territoryReturnRoute.Reset();
            log.Information("Starting Ocean Fishing return to origin territory. ContentId={ContentId} CurrentTerritory={CurrentTerritory} DestinationTerritory={DestinationTerritory} PlayerSafe={PlayerSafe}; route state reset.",
                session.OriginalContentId, clientState.TerritoryType, session.OriginalTerritoryId, IsPlayerSafe());
            SetPhase(session, AutoRetainerHandoffPhase.ReturningOriginalTerritory);
        }
        else
        {
            SetPhase(session, AutoRetainerHandoffPhase.ReturningOriginalCharacter);
        }
    }

    private void Fail(OceanFishingHandoffSession session, string reason)
    {
        ReleaseYesAlreadyLease(session);
        session.Phase = AutoRetainerHandoffPhase.Failed;
        session.PhaseStartedAtUtc = DateTimeOffset.UtcNow;
        session.LastError = reason;
        configuration.Save();
        Status = reason;
        log.Warning("Ocean Fishing handoff held for recovery: {Reason}", reason);
    }

    private void ReleaseYesAlreadyLease(OceanFishingHandoffSession session)
    {
        if (!session.YesAlreadyLeaseAcquired) return;
        yesAlreadyLease.Release();
        session.YesAlreadyLeaseAcquired = false;
        configuration.Save();
    }

    private bool IsOriginalCharacter(OceanFishingHandoffSession s) => clientState.IsLoggedIn && playerState.ContentId == s.OriginalContentId;
    private bool IsFishingCharacter(OceanFishingHandoffSession s) => clientState.IsLoggedIn && playerState.ContentId == s.FishingContentId;
    private static bool IsOceanTerritory(uint territory) => territory is OceanFishingTerritoryOne or OceanFishingTerritoryTwo;
    private bool IsPlayerSafe()
    {
        if (!clientState.IsLoggedIn || playerState.ContentId == 0 || !playerState.IsLoaded || objectTable.LocalPlayer is not { CurrentHp: > 0 })
            return false;
        ConditionFlag[] blockers =
        [
            ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95,
            ConditionFlag.InDutyQueue, ConditionFlag.InCombat, ConditionFlag.Occupied, ConditionFlag.Occupied30,
            ConditionFlag.Occupied33, ConditionFlag.Occupied38, ConditionFlag.Occupied39,
            ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedSummoningBell, ConditionFlag.OccupiedInQuestEvent,
            ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78,
            ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.LoggingOut,
            ConditionFlag.Unconscious, ConditionFlag.Casting, ConditionFlag.Fishing,
            ConditionFlag.ExecutingGatheringAction,
        ];
        return blockers.All(flag => !condition[flag]);
    }
}
