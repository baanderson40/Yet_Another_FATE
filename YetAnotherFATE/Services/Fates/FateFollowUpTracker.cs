using System;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates;

public sealed class FateFollowUpTracker : IFateFollowUpTracker
{
    private ActiveFate? completedFate;
    private DateTimeOffset expiresAt;
    private readonly Configuration configuration;

    public FateFollowUpTracker(Configuration configuration)
    {
        this.configuration = configuration;
    }

    public bool IsWatching
    {
        get
        {
            if (!configuration.WaitForFollowUpFates)
            {
                // Disabling follow-up waiting cancels the stored watch.
                Clear();
                return false;
            }

            if (completedFate.HasValue && DateTimeOffset.UtcNow >= expiresAt)
                Clear();
            return completedFate.HasValue;
        }
    }

    public string Status => !completedFate.HasValue
        ? "not watching"
        : !IsWatching
            ? "follow-up watch expired"
            : $"watching after FATE #{completedFate.Value.FateId} ({(expiresAt - DateTimeOffset.UtcNow).TotalSeconds:F0}s)";

    public void Begin(ActiveFate completed)
    {
        if (!completed.HasSheetMetadata || !completed.HasFollowUp || completed.LocationId == 0)
        {
            Clear();
            return;
        }

        completedFate = completed;
        expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(configuration.FollowUpWaitSeconds, 1, 120));
    }

    public bool IsPreferred(ActiveFate fate)
    {
        if (!IsWatching || completedFate is not { } previous || !fate.HasSheetMetadata || fate.TerritoryId != previous.TerritoryId || fate.FateId <= previous.FateId)
        {
            return false;
        }

        return previous.LocationId == fate.LocationId;
    }

    public void Clear()
    {
        completedFate = null;
        expiresAt = default;
    }
}
