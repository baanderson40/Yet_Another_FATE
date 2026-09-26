using System;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using YetAnotherFATE.Data.Travel;

namespace YetAnotherFATE.Services.Travel;

public sealed unsafe class TeleportExecutor : ITeleportExecutor, IDisposable
{
    private static readonly ConditionFlag[] BlockingFlags =
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
        ConditionFlag.Unconscious,
    };

    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private static readonly TimeSpan SameTerritoryCompletionDelay = TimeSpan.FromSeconds(1);

    private TeleportState state = TeleportState.Idle;
    private TeleportRecommendation? pending;
    private DateTimeOffset? issuedAt;
    private DateTimeOffset? lastCompletedAt;
    private DateTimeOffset? lastFailedAt;
    private ushort startingTerritory;

    public TeleportExecutor(IClientState clientState, ICondition condition, IFramework framework, IPluginLog log)
    {
        this.clientState = clientState;
        this.condition = condition;
        this.framework = framework;
        this.log = log;

        framework.Update += OnFrameworkUpdate;
    }

    public TeleportState State => state;
    public DateTimeOffset? LastCompletedAt => lastCompletedAt;
    public DateTimeOffset? LastFailedAt => lastFailedAt;

    public TeleportRequestResult RequestTeleport(in TeleportRecommendation recommendation)
    {
        if (recommendation.AetheryteId is null or 0)
        {
            return TeleportRequestResult.InvalidTarget;
        }

        if (state != TeleportState.Idle)
        {
            return TeleportRequestResult.Busy;
        }

        if (!clientState.IsLoggedIn)
        {
            return TeleportRequestResult.ClientUnavailable;
        }

        if (IsBlocked(out var blockingFlag))
        {
            log.Debug("TeleportExecutor: cannot teleport while {Flag} is active", blockingFlag);
            return TeleportRequestResult.Blocked;
        }

        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null) return TeleportRequestResult.ClientUnavailable;
            if (!telepo->Teleport(recommendation.AetheryteId.Value, recommendation.SubIndex))
            {
                lastFailedAt = DateTimeOffset.UtcNow;
                return TeleportRequestResult.Failed;
            }
        }
        catch (Exception ex)
        {
            lastFailedAt = DateTimeOffset.UtcNow;
            log.Error(ex, "TeleportExecutor: failed to invoke Telepo for {Target}", recommendation.AetheryteName ?? recommendation.AetheryteId.Value.ToString());
            TransitionState(TeleportState.Failed);
            return TeleportRequestResult.Failed;
        }

        pending = recommendation;
        startingTerritory = checked((ushort)clientState.TerritoryType);
        issuedAt = DateTimeOffset.UtcNow;
        TransitionState(TeleportState.Starting);

        log.Debug("TeleportExecutor: Teleport issued for {Target} cost={Cost}", recommendation.AetheryteName ?? recommendation.AetheryteId.Value.ToString(), recommendation.EstimatedGilCost);
        return TeleportRequestResult.Issued;
    }

    private bool IsBlocked(out ConditionFlag blockingFlag)
    {
        foreach (var flag in BlockingFlags)
        {
            if (condition[flag])
            {
                blockingFlag = flag;
                return true;
            }
        }

        blockingFlag = 0;
        return false;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!clientState.IsLoggedIn)
        {
            Reset();
            return;
        }
        switch (state)
        {
            case TeleportState.Starting:
                if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
                {
                    TransitionState(TeleportState.Loading);
                }
                else if (issuedAt.HasValue && DateTimeOffset.UtcNow - issuedAt > TimeSpan.FromSeconds(10))
                {
                    log.Debug("TeleportExecutor: teleport request timed out before loading started");
                    lastFailedAt = DateTimeOffset.UtcNow;
                    Reset();
                }

                break;
            case TeleportState.Loading:
                if (!condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51])
                {
                    if (clientState.TerritoryType != startingTerritory)
                    {
                        lastCompletedAt = DateTimeOffset.UtcNow;
                        TransitionState(TeleportState.Completed);
                    }
                    else if (issuedAt.HasValue && DateTimeOffset.UtcNow - issuedAt > SameTerritoryCompletionDelay)
                    {
                        lastCompletedAt = DateTimeOffset.UtcNow;
                        TransitionState(TeleportState.Completed);
                    }
                }
                else if (issuedAt.HasValue && DateTimeOffset.UtcNow - issuedAt > TimeSpan.FromSeconds(45))
                {
                    lastFailedAt = DateTimeOffset.UtcNow;
                    TransitionState(TeleportState.Failed);
                }

                break;
            case TeleportState.Completed:
                Reset();
                break;
            case TeleportState.Failed:
                Reset();
                break;
        }
    }

    private void Reset()
    {
        pending = null;
        issuedAt = null;
        startingTerritory = 0;
        TransitionState(TeleportState.Idle);
    }

    private void TransitionState(TeleportState newState)
    {
        if (state == newState)
        {
            return;
        }

        state = newState;
        log.Debug("TeleportExecutor: state => {State}", newState);
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }
}
