using System;
using System.Collections.Generic;
using System.Numerics;

namespace YetAnotherFATE.Data.Zone;

public readonly record struct ZoneInfo(
    uint TerritoryId,
    string Name,
    uint PlaceNameId,
    bool HasAetheryte,
    bool HasUnlockedAetheryte,
    AetheryteSummary? PrimaryAetheryte,
    IReadOnlyList<AetheryteSummary> Aetherytes,
    IReadOnlyList<AetheryteSummary> UnlockedAetherytes,
    bool CanFly,
    bool MountRestricted,
    Vector3 MapCenter,
    DateTimeOffset RefreshedAt
);
