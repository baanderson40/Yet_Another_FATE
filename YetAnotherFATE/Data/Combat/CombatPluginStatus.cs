namespace YetAnotherFATE.Data.Combat;

public sealed record CombatPluginStatus(
    CombatPluginId Id,
    string DisplayName,
    string InternalName,
    bool SupportsRotation,
    bool SupportsDodge,
    bool Installed,
    string? Version,
    bool IsLoaded);
