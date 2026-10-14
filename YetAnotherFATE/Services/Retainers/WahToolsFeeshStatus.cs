using System;
using System.Collections.Generic;

namespace YetAnotherFATE.Services.Retainers;

public enum WahToolsStatusAvailability { Available, NotInstalled, Incompatible, ReadFailed }
public enum OceanCleanupConfirmationSource { None, WahToolsReflection, GameStateFallback }

public sealed record WahToolsFeeshStatus(
    WahToolsStatusAvailability Availability,
    DateTime ObservedAtUtc,
    string Detail,
    bool? TripInitialized,
    string? TripState,
    string? TripStatus,
    bool? TripRunning,
    bool? OnBoat,
    bool? Cleaning,
    string? CleanerState,
    string? CleanerStatus,
    bool? CleanerRunning,
    bool? DesyntherRunning,
    string? DesyntherStatus,
    bool? DiscarderRunning,
    string? DiscarderStatus,
    bool? BuyerBusy)
{
    public long Generation { get; init; }
    public bool IsAvailable => Availability == WahToolsStatusAvailability.Available;
    public static WahToolsFeeshStatus Unavailable(WahToolsStatusAvailability availability, string detail, DateTime? observedAt = null) =>
        new(availability, observedAt ?? DateTime.UtcNow, detail, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
}

public static class WahToolsFeeshStatusPolicy
{
    public static IReadOnlyList<string> GetCleanupBlockers(WahToolsFeeshStatus status)
    {
        if (!status.IsAvailable) return [];
        var blockers = new List<string>();
        if (status.Cleaning == true) blockers.Add("WahTools cleaning active");
        if (status.CleanerRunning == true) blockers.Add($"WahTools cleaner {status.CleanerState ?? "running"}: {status.CleanerStatus ?? "in progress"}");
        if (status.DesyntherRunning == true) blockers.Add($"WahTools desynthesis active: {status.DesyntherStatus ?? "in progress"}");
        if (status.DiscarderRunning == true) blockers.Add($"WahTools discard active: {status.DiscarderStatus ?? "in progress"}");
        if (status.BuyerBusy == true) blockers.Add("WahTools bait buyer active");
        if (status.OnBoat == true) blockers.Add("WahTools still observes player aboard boat");
        if (status.TripRunning == true) blockers.Add($"WahTools trip {status.TripState ?? "running"}: {status.TripStatus ?? "in progress"}");
        return blockers;
    }

    public static bool IsTripFailed(WahToolsFeeshStatus status) =>
        status.IsAvailable && string.Equals(status.TripState, "Failed", StringComparison.OrdinalIgnoreCase);

    public static bool HasCleanerFailure(WahToolsFeeshStatus status) =>
        status.IsAvailable && string.Equals(status.CleanerState, "Failed", StringComparison.OrdinalIgnoreCase);
}
