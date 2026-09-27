using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Chains;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Retainers;
using YetAnotherFATE.Services.Travel;

namespace YetAnotherFATE.Services.Retainers;

public sealed class AutoRetainerHandoffController : IDisposable
{
    private static readonly TimeSpan OriginalReturnTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PassTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly Configuration configuration;
    private readonly IAutoRetainerClient autoRetainer;
    private readonly ILifestreamCharacterClient lifestream;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private DateTimeOffset nextReadinessScan = DateTimeOffset.MinValue;
    private DateTimeOffset nextReturnAttempt = DateTimeOffset.MinValue;
    private bool manualPassRequested;
    private bool completionEventReceived;
    private bool autoRetainerObservedActive;
    private bool disposed;
    private AutoRetainerReadinessSnapshot lastReadiness;

    public AutoRetainerHandoffController(
        Configuration configuration,
        IAutoRetainerClient autoRetainer,
        ILifestreamCharacterClient lifestream,
        IClientState clientState,
        IPlayerState playerState,
        IObjectTable objectTable,
        ICondition condition,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.autoRetainer = autoRetainer;
        this.lifestream = lifestream;
        this.clientState = clientState;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.log = log;
        autoRetainer.SinglePassFinished += OnSinglePassFinished;

        if (configuration.AutoRetainerHandoff is { Phase: not AutoRetainerHandoffPhase.Idle })
            log.Information("AutoRetainer handoff session restored at phase {Phase}", configuration.AutoRetainerHandoff.Phase);
    }

    public AutoRetainerHandoffPhase Phase => configuration.AutoRetainerHandoff?.Phase ??
        (manualPassRequested || lastReadiness.WorkReady ? AutoRetainerHandoffPhase.PendingSafeBoundary : AutoRetainerHandoffPhase.Idle);

    public AutoRetainerReadinessSnapshot LastReadiness => lastReadiness;

    public string Status { get; private set; } = "Disabled";

    public bool IsActive => configuration.AutoRetainerHandoff is not null || manualPassRequested || lastReadiness.WorkReady;

    public void RequestManualPass()
    {
        manualPassRequested = true;
        if (configuration.AutoRetainerHandoff?.Phase == AutoRetainerHandoffPhase.Failed)
        {
            configuration.AutoRetainerHandoff = null;
            configuration.Save();
        }
        Status = "Manual AutoRetainer pass pending safe boundary";
    }

    public bool RetryFailedSession()
    {
        if (configuration.AutoRetainerHandoff?.Phase != AutoRetainerHandoffPhase.Failed)
            return false;
        configuration.AutoRetainerHandoff = null;
        configuration.Save();
        manualPassRequested = true;
        Status = "AutoRetainer handoff retry pending safe boundary";
        return true;
    }

    public bool AbandonFailedSession()
    {
        if (configuration.AutoRetainerHandoff?.Phase != AutoRetainerHandoffPhase.Failed)
            return false;
        configuration.AutoRetainerHandoff = null;
        configuration.Save();
        Status = "AutoRetainer handoff abandoned";
        return true;
    }

    public bool ResumeFailedSession()
    {
        var session = configuration.AutoRetainerHandoff;
        if (session?.Phase != AutoRetainerHandoffPhase.Failed)
            return false;
        session.LastError = null;
        session.Phase = clientState.IsLoggedIn && playerState.ContentId == session.OriginalContentId
            ? AutoRetainerHandoffPhase.Resuming
            : AutoRetainerHandoffPhase.ReturningOriginalCharacter;
        session.PhaseStartedAtUtc = DateTimeOffset.UtcNow;
        configuration.Save();
        return true;
    }

    public bool Tick(AutomatorChain chain)
    {
        if (disposed)
            return false;

        if (configuration.AutoRetainerHandoff is not null)
            return TickSession(chain);

        if (!clientState.IsLoggedIn || (!configuration.AutomatorEnabled && !manualPassRequested))
            return false;

        if (!manualPassRequested && !configuration.AutoRetainerHandoffEnabled)
            return false;

        ScanReadinessIfDue();
        if (!manualPassRequested && !lastReadiness.WorkReady)
            return false;

        Status = "AutoRetainer work pending safe boundary";
        if (!chain.CanStartAutoRetainerHandoff())
            return false;

        StartSession(chain);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        autoRetainer.SinglePassFinished -= OnSinglePassFinished;
    }

    private bool TickSession(AutomatorChain chain)
    {
        var session = configuration.AutoRetainerHandoff!;
        switch (session.Phase)
        {
            case AutoRetainerHandoffPhase.Starting:
                return TickStarting(session);
            case AutoRetainerHandoffPhase.WaitingForAutoRetainer:
                return TickWaitingForAutoRetainer(session);
            case AutoRetainerHandoffPhase.ReturningOriginalCharacter:
                return TickReturning(session);
            case AutoRetainerHandoffPhase.WaitingForOriginalCharacter:
                return TickWaitingForOriginal(session);
            case AutoRetainerHandoffPhase.Resuming:
                return TickResuming(chain, session);
            case AutoRetainerHandoffPhase.Failed:
                Status = session.LastError ?? "AutoRetainer handoff failed; manual recovery required";
                return true;
            default:
                return true;
        }
    }

    private bool TickStarting(AutoRetainerHandoffSession session)
    {
        if (!clientState.IsLoggedIn || playerState.ContentId != session.OriginalContentId)
        {
            Fail(session, "Original character was not available when AutoRetainer handoff started");
            return true;
        }

        if (!autoRetainer.TryGetState(out var busy, out var active))
        {
            if (DateTimeOffset.UtcNow - session.PhaseStartedAtUtc > StartTimeout)
                Fail(session, "AutoRetainer state IPC did not become available within 30 seconds");
            else
                Status = "Waiting for AutoRetainer IPC";
            return true;
        }

        if (busy || active)
        {
            session.ObservedAutoRetainerActive = true;
            SetPhase(session, AutoRetainerHandoffPhase.WaitingForAutoRetainer);
            Status = "AutoRetainer pass already active; observing";
            return true;
        }

        if (session.RequestIssued)
        {
            SetPhase(session, AutoRetainerHandoffPhase.WaitingForAutoRetainer);
            Status = "Restored AutoRetainer handoff; waiting for pass state";
            return true;
        }

        completionEventReceived = false;
        autoRetainerObservedActive = false;
        session.RequestIssued = true;
        configuration.Save();
        if (!autoRetainer.TryEnableSinglePass(session.Mode))
        {
            Fail(session, "AutoRetainer rejected the single-pass request");
            return true;
        }

        SetPhase(session, AutoRetainerHandoffPhase.WaitingForAutoRetainer);
        Status = "AutoRetainer single pass starting";
        return true;
    }

    private bool TickWaitingForAutoRetainer(AutoRetainerHandoffSession session)
    {
        if (DateTimeOffset.UtcNow - session.PhaseStartedAtUtc > PassTimeout)
        {
            if (autoRetainer.TryGetState(out var timedOutBusy, out var timedOutActive) && !timedOutBusy && !timedOutActive)
                SetPhase(session, AutoRetainerHandoffPhase.ReturningOriginalCharacter);
            else
                Fail(session, "AutoRetainer did not finish within two hours");
            return true;
        }

        if (!autoRetainer.TryGetState(out var busy, out var active))
        {
            Status = "Waiting for AutoRetainer state";
            return true;
        }

        autoRetainerObservedActive |= busy || active;
        session.ObservedAutoRetainerActive |= busy || active;
        session.CompletionEventObserved |= completionEventReceived;
        if (busy || active)
        {
            Status = "AutoRetainer single pass active";
            return true;
        }

        var elapsed = DateTimeOffset.UtcNow - session.PhaseStartedAtUtc;
        if (!completionEventReceived && !autoRetainerObservedActive && elapsed < StartTimeout)
        {
            Status = "Waiting for AutoRetainer single pass to start";
            return true;
        }

        if (!completionEventReceived && !autoRetainerObservedActive)
        {
            Fail(session, "AutoRetainer did not enter Multi Mode within 30 seconds");
            return true;
        }

        SetPhase(session, AutoRetainerHandoffPhase.ReturningOriginalCharacter);
        return true;
    }

    private bool TickReturning(AutoRetainerHandoffSession session)
    {
        if (clientState.IsLoggedIn && playerState.ContentId == session.OriginalContentId)
        {
            SetPhase(session, AutoRetainerHandoffPhase.Resuming);
            return true;
        }

        if (DateTimeOffset.UtcNow - session.PhaseStartedAtUtc > OriginalReturnTimeout)
        {
            Fail(session, "Could not return to the original character within ten minutes");
            return true;
        }

        if (DateTimeOffset.UtcNow < nextReturnAttempt)
            return true;

        if (!lifestream.TryIsBusy(out var busy))
        {
            Status = "Waiting for Lifestream IPC";
            return true;
        }

        if (busy)
        {
            Status = "Waiting for Lifestream to become idle";
            return true;
        }

        nextReturnAttempt = DateTimeOffset.UtcNow.AddSeconds(5);
        if (lifestream.TryChangeCharacter(session.OriginalCharacterName, session.OriginalWorldName, out var error))
        {
            SetPhase(session, AutoRetainerHandoffPhase.WaitingForOriginalCharacter);
            Status = $"Returning to {session.OriginalCharacterName}@{session.OriginalWorldName}";
        }
        else
        {
            Status = $"Lifestream return failed: {error}";
        }

        return true;
    }

    private bool TickWaitingForOriginal(AutoRetainerHandoffSession session)
    {
        if (DateTimeOffset.UtcNow - session.PhaseStartedAtUtc > OriginalReturnTimeout)
        {
            Fail(session, "Original character did not finish loading within ten minutes");
            return true;
        }

        if (clientState.IsLoggedIn && playerState.ContentId == session.OriginalContentId && IsPlayerSafe())
            SetPhase(session, AutoRetainerHandoffPhase.Resuming);
        else
            Status = $"Waiting for {session.OriginalCharacterName}@{session.OriginalWorldName}";
        return true;
    }

    private bool TickResuming(AutomatorChain chain, AutoRetainerHandoffSession session)
    {
        if (!clientState.IsLoggedIn || playerState.ContentId != session.OriginalContentId || !IsPlayerSafe())
        {
            Status = "Waiting for original character to become safe";
            return true;
        }

        var shouldResume = session.WasAutomatorEnabled && configuration.AutomatorEnabled;
        configuration.AutoRetainerHandoff = null;
        configuration.Save();
        nextReadinessScan = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(configuration.AutoRetainerRetryCooldownSeconds, 30, 1800));
        if (shouldResume)
            chain.ResumeAfterAutoRetainerHandoff();
        else
            chain.PrepareForExternalHandoff("AutoRetainer pass complete; automator disabled");
        manualPassRequested = false;
        autoRetainerObservedActive = false;
        completionEventReceived = false;
        Status = shouldResume ? "AutoRetainer pass complete; resuming FATE automation" : "AutoRetainer pass complete";
        return false;
    }

    private void ScanReadinessIfDue()
    {
        if (DateTimeOffset.UtcNow < nextReadinessScan)
            return;

        nextReadinessScan = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(configuration.AutoRetainerReadinessIntervalSeconds, 15, 600));
        if (autoRetainer.TryGetReadiness(configuration.AutoRetainerMode, out var snapshot))
        {
            lastReadiness = snapshot;
            Status = snapshot.WorkReady
                ? $"AutoRetainer ready: {snapshot.ReadyCharacterCount}/{snapshot.RegisteredCharacterCount} characters"
                : "AutoRetainer monitoring";
        }
        else
        {
            lastReadiness = snapshot;
            Status = "AutoRetainer unavailable";
        }
    }

    private void StartSession(AutomatorChain chain)
    {
        var player = objectTable.LocalPlayer;
        var name = player?.Name.ToString() ?? string.Empty;
        var world = player?.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty;
        if (player == null || playerState.ContentId == 0 || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world))
        {
            Status = "AutoRetainer handoff requires character identity";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var session = new AutoRetainerHandoffSession
        {
            Phase = AutoRetainerHandoffPhase.Starting,
            OriginalContentId = playerState.ContentId,
            OriginalCharacterName = name,
            OriginalWorldName = world,
            WasAutomatorEnabled = configuration.AutomatorEnabled,
            Mode = configuration.AutoRetainerMode,
            StartedAtUtc = now,
            PhaseStartedAtUtc = now,
            StartedByYaf = true,
            RequestIssued = false,
        };
        configuration.AutoRetainerHandoff = session;
        configuration.Save();
        chain.PrepareForExternalHandoff("AutoRetainer handoff starting");
        manualPassRequested = false;
        Status = "Preparing AutoRetainer handoff";
    }

    private void SetPhase(AutoRetainerHandoffSession session, AutoRetainerHandoffPhase phase)
    {
        session.Phase = phase;
        session.PhaseStartedAtUtc = DateTimeOffset.UtcNow;
        configuration.Save();
    }

    private void Fail(AutoRetainerHandoffSession session, string reason)
    {
        session.Phase = AutoRetainerHandoffPhase.Failed;
        session.PhaseStartedAtUtc = DateTimeOffset.UtcNow;
        session.LastError = reason;
        configuration.Save();
        nextReadinessScan = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(configuration.AutoRetainerRetryCooldownSeconds, 30, 1800));
        log.Warning("AutoRetainer handoff failed: {Reason}", reason);
        Status = reason;
    }

    private bool IsPlayerSafe()
        => clientState.IsLoggedIn && playerState.IsLoaded && objectTable.LocalPlayer is { CurrentHp: > 0 } &&
           !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] &&
           !condition[ConditionFlag.InCombat] && !condition[ConditionFlag.Occupied] &&
           !condition[ConditionFlag.OccupiedInEvent] && !condition[ConditionFlag.OccupiedInQuestEvent];

    private void OnSinglePassFinished() => completionEventReceived = true;
}
