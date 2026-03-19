using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using YetAnotherFATE.Data.Zone;

namespace YetAnotherFATE.Services.Zone;

public sealed class ZoneService(IDataManager dataManager) : IZoneService
{
    private readonly IDataManager dataManager = dataManager;

    public ZoneInfo? GetZone(uint territoryId)
    {
        var territories = dataManager.GetExcelSheet<TerritoryType>();
        if (territories == null)
        {
            return null;
        }

        if (!territories.TryGetRow(territoryId, out var row))
        {
            return null;
        }

        var placeName = row.PlaceName.ValueNullable;
        var name = placeName?.Name.ToString() ?? string.Empty;
        var placeNameId = placeName?.RowId ?? 0;
        var hasAetheryte = row.Aetheryte.ValueNullable != null;

        return new ZoneInfo(territoryId, name, placeNameId, hasAetheryte);
    }
}
