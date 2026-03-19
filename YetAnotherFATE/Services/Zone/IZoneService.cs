using YetAnotherFATE.Data.Zone;

namespace YetAnotherFATE.Services.Zone;

public interface IZoneService
{
    ZoneInfo? GetZone(uint territoryId);
}
