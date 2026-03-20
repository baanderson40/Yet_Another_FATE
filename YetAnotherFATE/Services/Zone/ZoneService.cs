using System;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using YetAnotherFATE.Data.Zone;

namespace YetAnotherFATE.Services.Zone;

public sealed class ZoneService : IZoneService
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    public ZoneService(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public ZoneInfo? GetZone(uint territoryId)
    {
        try
        {
            var territories = dataManager.GetExcelSheet<TerritoryType>();
            if (territories == null)
            {
                log.Debug("ZoneService: Territory sheet unavailable while resolving {TerritoryId}", territoryId);
                return null;
            }

            if (!territories.TryGetRow(territoryId, out var row))
            {
                log.Debug("ZoneService: Territory {TerritoryId} not found", territoryId);
                return null;
            }

            var placeName = row.PlaceName.ValueNullable;
            var name = placeName?.Name.ToString() ?? $"Territory {territoryId}";
            var placeNameId = placeName?.RowId ?? 0;

            var primaryAetheryte = BuildAetheryteSummary(row);
            var zone = new ZoneInfo(territoryId, name, placeNameId, primaryAetheryte.HasValue, primaryAetheryte);
            log.Debug("ZoneService: Resolved territory {TerritoryId} -> {Name} (aetheryte: {Aetheryte})",
                territoryId,
                name,
                primaryAetheryte.HasValue ? primaryAetheryte.Value.Name : "none");

            return zone;
        }
        catch (Exception ex)
        {
            log.Error(ex, "ZoneService: Failed to determine data for territory {TerritoryId}", territoryId);
            return null;
        }
    }

    private AetheryteSummary? BuildAetheryteSummary(TerritoryType territoryRow)
    {
        var aetheryteId = territoryRow.Aetheryte.RowId;
        if (aetheryteId == 0)
        {
            return null;
        }

        var sheet = dataManager.GetExcelSheet<Aetheryte>();
        if (sheet == null)
        {
            log.Debug("ZoneService: Aetheryte sheet unavailable while resolving territory {TerritoryId}", territoryRow.RowId);
            return null;
        }

        if (!sheet.TryGetRow((uint)aetheryteId, out var aetheryteRow))
        {
            log.Debug("ZoneService: Aetheryte {AetheryteId} not found", aetheryteId);
            return null;
        }

        var name = aetheryteRow.PlaceName.ValueNullable?.Name.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Aetheryte {aetheryteId}";
        }

        var placeNameId = aetheryteRow.PlaceName.RowId;
        return new AetheryteSummary((uint)aetheryteId, name, placeNameId);
    }
}
