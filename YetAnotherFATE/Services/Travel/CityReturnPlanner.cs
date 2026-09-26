using System;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Services.Zone;

namespace YetAnotherFATE.Services.Travel;

public sealed class CityReturnPlanner(IDataManager data, IZoneService zones, IPluginLog log) : ICityReturnPlanner
{
    public CityReturnPlan? Resolve(DeathRecoveryContext destination)
    {
        try
        {
            var sheet = data.GetExcelSheet<Aetheryte>();
            // Firmament is a separate, explicitly supported Lifestream endpoint, not an Aetheryte row.
            if (destination.TerritoryId == 886 && sheet.TryGetRow(70, out var foundation))
            {
                var gateway = zones.GetZone(foundation.Territory.RowId)?.UnlockedAetherytes
                    .FirstOrDefault(a => a.AetheryteId == 70 && a.TerritoryId == foundation.Territory.RowId);
                return gateway is { AetheryteId: > 0 } found ? new CityReturnPlan(found, 0) : null;
            }

            var nodes = zones.GetZone(destination.TerritoryId)?.Aetherytes;
            if (nodes == null) return null;
            foreach (var node in nodes
                         .Where(a => a.TerritoryId == destination.TerritoryId && a.HasPosition &&
                                     float.IsFinite(a.Position.X) && float.IsFinite(a.Position.Z))
                         .OrderBy(a => Vector2.DistanceSquared(new(a.Position.X, a.Position.Z),
                             new(destination.Position.X, destination.Position.Z))))
            {
                if (!sheet.TryGetRow(node.AetheryteId, out var shard) || shard.IsAetheryte ||
                    shard.AethernetGroup == 0 || shard.Territory.RowId != destination.TerritoryId) continue;
                foreach (var primary in sheet.Where(a => a.IsAetheryte && a.AethernetGroup == shard.AethernetGroup))
                {
                    var gateway = zones.GetZone(primary.Territory.RowId)?.UnlockedAetherytes
                        .FirstOrDefault(a => a.AetheryteId == primary.RowId && a.TerritoryId == primary.Territory.RowId);
                    if (gateway is { AetheryteId: > 0 } found) return new CityReturnPlan(found, shard.RowId);
                }
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Death return: city/aethernet route metadata unavailable");
        }
        return null;
    }
}
