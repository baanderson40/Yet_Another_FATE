using System;

namespace YetAnotherFATE.Services.Retainers;

public enum OceanVoyageObservation
{
    Waiting,
    TerritoryEntered,
    DutyActive,
    DutyCompleted,
    TerritoryExitedUnconfirmed,
}

public static class OceanFishingPolicy
{
    public static bool CanObserveDutyStarted(bool launchRequested, bool inOceanTerritory, bool alreadyStarted) =>
        launchRequested && inOceanTerritory && !alreadyStarted;

    public static bool CanObserveDutyCompleted(bool launchRequested, bool dutyStarted, bool alreadyCompleted) =>
        launchRequested && dutyStarted && !alreadyCompleted;

    public static OceanVoyageObservation Observe(
        bool inOceanTerritory,
        bool dutyActive,
        bool dutyCompleted,
        bool territoryEnteredPreviously) =>
        dutyCompleted
            ? OceanVoyageObservation.DutyCompleted
            : dutyActive
                ? OceanVoyageObservation.DutyActive
                : inOceanTerritory
                    ? territoryEnteredPreviously
                        ? OceanVoyageObservation.Waiting
                        : OceanVoyageObservation.TerritoryEntered
                    : territoryEnteredPreviously
                        ? OceanVoyageObservation.TerritoryExitedUnconfirmed
                        : OceanVoyageObservation.Waiting;

    public static bool CanDispatch(DateTime nowUtc, DateTime? windowStartUtc, bool targetConfirmed, bool playerSafe) =>
        targetConfirmed && playerSafe && windowStartUtc is { } start &&
        nowUtc < start + OceanFishingSchedule.LaunchCutoffOffset;

    public static bool IsStableForReturn(DateTime stableSinceUtc, DateTime nowUtc, TimeSpan requiredDuration) =>
        nowUtc >= stableSinceUtc + requiredDuration;
}
