using System;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates.CombatHelper.LevelSync;

public sealed class LevelSyncService : ILevelSyncService
{
    private static readonly TimeSpan AttemptCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TimeoutBackoff = TimeSpan.FromSeconds(10);

    private readonly ILevelSyncGameApi gameApi;
    private readonly Func<DateTimeOffset> clock;
    private DateTimeOffset nextAttemptAt;
    private DateTimeOffset lastRequestAt;
    private (uint Territory, uint FateId, int StartTime, uint ClassJob)? trackedSelection;

    public LevelSyncService(ILevelSyncGameApi gameApi)
        : this(gameApi, () => DateTimeOffset.UtcNow)
    {
    }

    internal LevelSyncService(ILevelSyncGameApi gameApi, Func<DateTimeOffset> clock)
    {
        this.gameApi = gameApi;
        this.clock = clock;
    }

    public string Status { get; private set; } = "Idle";

    public bool EnsureSynced(FateSelection selection)
    {
        var snapshot = gameApi.Read();
        var key = (selection.Fate.TerritoryId, selection.Fate.FateId, selection.Fate.StartTimeEpoch, snapshot.ClassJobId);
        if (trackedSelection != key)
        {
            trackedSelection = key;
            nextAttemptAt = DateTimeOffset.MinValue;
            lastRequestAt = DateTimeOffset.MinValue;
        }

        if (!snapshot.PlayerReady)
        {
            Status = "Waiting for player";
            return false;
        }
        if (!snapshot.NativeAvailable)
        {
            Status = "FateManager unavailable";
            return false;
        }
        if (snapshot.TerritoryId != selection.Fate.TerritoryId || snapshot.CurrentFateId != selection.Fate.FateId ||
            snapshot.CurrentFateId == 0 ||
            (snapshot.CurrentFateStartTimeEpoch != 0 && selection.Fate.StartTimeEpoch != 0 &&
             snapshot.CurrentFateStartTimeEpoch != selection.Fate.StartTimeEpoch))
        {
            Status = $"Waiting for selected FATE registration (selected #{selection.Fate.FateId}, native #{snapshot.CurrentFateId})";
            return false;
        }

        if (snapshot.IsSyncedToCurrentFate)
        {
            Status = $"Synced to FATE #{snapshot.CurrentFateId} (effective {snapshot.EffectiveLevel})";
            lastRequestAt = DateTimeOffset.MinValue;
            return true;
        }

        var maxLevel = snapshot.CurrentFateMaxLevel > 0
            ? snapshot.CurrentFateMaxLevel
            : selection.Fate.MaxLevel;
        if (maxLevel <= 0 || snapshot.CurrentFateLevel > maxLevel)
        {
            Status = $"FATE #{snapshot.CurrentFateId} level cap unavailable";
            return false;
        }

        if (snapshot.PlayerLevel <= maxLevel)
        {
            Status = $"No sync required (player {snapshot.PlayerLevel}, FATE max {maxLevel})";
            lastRequestAt = DateTimeOffset.MinValue;
            return true;
        }

        var now = clock();
        if (lastRequestAt != DateTimeOffset.MinValue && now - lastRequestAt >= ConfirmationTimeout)
        {
            Status = $"Sync confirmation timed out for FATE #{snapshot.CurrentFateId}";
            lastRequestAt = DateTimeOffset.MinValue;
            nextAttemptAt = now + TimeoutBackoff;
        }
        if (now < nextAttemptAt)
        {
            if (lastRequestAt != DateTimeOffset.MinValue)
                Status = $"Waiting for sync confirmation ({Math.Max(0, (ConfirmationTimeout - (now - lastRequestAt)).TotalSeconds):F0}s)";
            return false;
        }

        var result = gameApi.TryRequestSync(selection);
        switch (result)
        {
            case LevelSyncRequestResult.Requested:
                lastRequestAt = now;
                nextAttemptAt = now + ConfirmationTimeout;
                Status = $"Sync requested for FATE #{snapshot.CurrentFateId}; waiting for native confirmation";
                return false;
            case LevelSyncRequestResult.AlreadyReady:
                var after = gameApi.Read();
                if (after.IsSyncedToCurrentFate || after.PlayerLevel <= maxLevel)
                {
                    Status = after.IsSyncedToCurrentFate ? $"Synced to FATE #{after.CurrentFateId}" : "No sync required";
                    return true;
                }
                Status = "Sync request already satisfied; waiting for native confirmation";
                nextAttemptAt = now + AttemptCooldown;
                return false;
            case LevelSyncRequestResult.WrongCurrentFate:
                Status = $"Waiting for selected FATE #{selection.Fate.FateId} to become native current FATE";
                nextAttemptAt = now + AttemptCooldown;
                return false;
            case LevelSyncRequestResult.NotReady:
                Status = "Waiting for safe sync state";
                nextAttemptAt = now + AttemptCooldown;
                return false;
            default:
                Status = $"Sync unavailable ({result})";
                nextAttemptAt = now + AttemptCooldown;
                return false;
        }
    }

    public void Reset()
    {
        trackedSelection = null;
        nextAttemptAt = DateTimeOffset.MinValue;
        lastRequestAt = DateTimeOffset.MinValue;
        Status = "Idle";
    }
}
