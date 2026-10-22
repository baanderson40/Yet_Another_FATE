using System;
using System.Collections.Generic;
using System.Linq;

namespace YetAnotherFATE.Services.Retainers;

public static class AutoRetainerReadinessPolicy
{
    public static bool CanStartAutomaticPass(DateTimeOffset now, DateTimeOffset cooldownUntil) => now >= cooldownUntil;

    public static bool IsRetainerReady(
        bool characterEnabled,
        bool hasSelectedRetainerOnVenture,
        IReadOnlyList<long> remainingSeconds,
        long? closestEligibleSeconds,
        bool waitForAll,
        long advanceSeconds)
    {
        if (!characterEnabled || !hasSelectedRetainerOnVenture ||
            closestEligibleSeconds is not { } closest || closest > advanceSeconds)
            return false;

        return !waitForAll || remainingSeconds.Count == 0 ||
               remainingSeconds.All(seconds => seconds <= advanceSeconds);
    }
}
