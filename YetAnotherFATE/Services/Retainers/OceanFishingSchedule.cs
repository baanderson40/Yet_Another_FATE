using System;

namespace YetAnotherFATE.Services.Retainers;

public readonly record struct OceanFishingWindow(DateTime StartUtc, DateTime RegistrationEndUtc, DateTime LaunchCutoffUtc);

public readonly record struct OceanFishingScheduleState(OceanFishingWindow Window, TimeSpan TimeUntilRegistration,
    bool RegistrationOpen, bool LaunchEligible);

public static class OceanFishingSchedule
{
    public static readonly TimeSpan RegistrationLength = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(2);
    public static readonly TimeSpan LaunchCutoffOffset = TimeSpan.FromMinutes(13);

    public static OceanFishingScheduleState GetState(DateTime utcNow, TimeSpan leadTime)
    {
        utcNow = EnsureUtc(utcNow);
        leadTime = TimeSpan.FromMinutes(Math.Clamp(leadTime.TotalMinutes, 0, 30));
        var window = GetWindow(utcNow);
        return new(window, window.StartUtc - utcNow, utcNow >= window.StartUtc && utcNow < window.RegistrationEndUtc,
            utcNow >= window.StartUtc - leadTime && utcNow < window.LaunchCutoffUtc);
    }

    public static OceanFishingWindow GetWindow(DateTime utcNow)
    {
        utcNow = EnsureUtc(utcNow);
        var hour = utcNow.Hour - utcNow.Hour % 2;
        var start = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, hour, 0, 0, DateTimeKind.Utc);
        if (utcNow >= start + RegistrationLength)
            start += Interval;
        return new(start, start + RegistrationLength, start + LaunchCutoffOffset);
    }

    public static bool IsHandled(DateTime? handled, DateTime windowStart) =>
        handled is { } value && EnsureUtc(value) == EnsureUtc(windowStart);

    private static DateTime EnsureUtc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
