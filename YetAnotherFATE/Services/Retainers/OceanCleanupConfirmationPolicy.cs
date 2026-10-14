using System;

namespace YetAnotherFATE.Services.Retainers;

public static class OceanCleanupConfirmationPolicy
{
    public static readonly TimeSpan ReflectedDuration = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan FallbackDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromSeconds(2);

    public static OceanCleanupConfirmationSource SelectSource(WahToolsFeeshStatus? status, DateTime nowUtc, DateTime? requireAfterUtc = null)
    {
        if (status is { IsAvailable: true } available)
        {
            if (requireAfterUtc is { } after && available.ObservedAtUtc <= after)
                return OceanCleanupConfirmationSource.None;
            var age = nowUtc - available.ObservedAtUtc;
            if (age >= TimeSpan.Zero && age <= MaximumSnapshotAge)
                return OceanCleanupConfirmationSource.WahToolsReflection;
        }
        return OceanCleanupConfirmationSource.GameStateFallback;
    }

    public static TimeSpan GetRequiredDuration(OceanCleanupConfirmationSource source) => source switch
    {
        OceanCleanupConfirmationSource.WahToolsReflection => ReflectedDuration,
        OceanCleanupConfirmationSource.GameStateFallback => FallbackDuration,
        _ => TimeSpan.Zero,
    };

    public static bool IsSameEvidence(OceanCleanupConfirmationSource source, long? generation,
        OceanCleanupConfirmationSource previousSource, long? previousGeneration) =>
        source == previousSource && generation == previousGeneration;

    public static bool HasConfirmedStableTime(DateTime stableSinceUtc, DateTime nowUtc, OceanCleanupConfirmationSource source) =>
        source != OceanCleanupConfirmationSource.None && nowUtc >= stableSinceUtc + GetRequiredDuration(source);
}
