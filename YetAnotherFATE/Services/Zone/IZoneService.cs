using YetAnotherFATE.Data.Zone;

namespace YetAnotherFATE.Services.Zone;

public interface IZoneService
{
    ZoneInfo? GetZone(uint territoryId);
    System.Collections.Generic.IReadOnlyList<uint> GetRotationTerritories(uint? sameExpansionAs = null) => System.Array.Empty<uint>();
}
