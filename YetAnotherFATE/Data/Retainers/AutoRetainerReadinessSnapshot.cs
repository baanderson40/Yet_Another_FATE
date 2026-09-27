using System;

namespace YetAnotherFATE.Data.Retainers;

public readonly record struct AutoRetainerReadinessSnapshot(
    bool Available,
    bool WorkReady,
    int RegisteredCharacterCount,
    int ReadyCharacterCount,
    long? ClosestRetainerSeconds,
    bool RetainerWorkReady,
    bool DeployableWorkReady,
    bool Busy,
    bool MultiModeActive,
    DateTimeOffset ObservedAt,
    string? FailureReason);
