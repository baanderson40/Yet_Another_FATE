using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using YetAnotherFATE.Data.Zone;

namespace YetAnotherFATE.Services.Zone;

public sealed class ZoneService : IZoneService
{
    private readonly IDataManager dataManager;
    private readonly IAetheryteList aetheryteList;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, ZoneStaticInfo> cache = new();
    private readonly object cacheGate = new();

    public ZoneService(IDataManager dataManager, IAetheryteList aetheryteList, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.aetheryteList = aetheryteList;
        this.log = log;
    }

    public ZoneInfo? GetZone(uint territoryId)
    {
        var metadata = GetOrCreateMetadata(territoryId);
        if (metadata == null)
        {
            return null;
        }

        var unlocked = DetermineUnlockedAetherytes(metadata);
        AetheryteSummary? primary = null;
        if (unlocked.Count > 0)
        {
            primary = unlocked[0];
        }
        else if (metadata.Aetherytes.Count > 0)
        {
            primary = metadata.Aetherytes[0];
        }

        var zone = new ZoneInfo(
            metadata.TerritoryId,
            metadata.Name,
            metadata.PlaceNameId,
            metadata.Aetherytes.Count > 0,
            unlocked.Count > 0,
            primary,
            metadata.Aetherytes,
            unlocked,
            metadata.CanFly,
            metadata.MountRestricted,
            metadata.MapCenter,
            DateTimeOffset.UtcNow);

        return zone;
    }

    private ZoneStaticInfo? GetOrCreateMetadata(uint territoryId)
    {
        lock (cacheGate)
        {
            if (cache.TryGetValue(territoryId, out var cached))
            {
                return cached;
            }
        }

        var metadata = ResolveMetadata(territoryId);
        if (metadata == null)
        {
            return null;
        }

        lock (cacheGate)
        {
            cache[territoryId] = metadata;
        }

        return metadata;
    }

    private ZoneStaticInfo? ResolveMetadata(uint territoryId)
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
            var placeNameId = placeName?.RowId ?? row.PlaceName.RowId;

            var aetherytes = BuildAetheryteSummaries(territoryId, row);
            var lookup = aetherytes.ToDictionary(a => a.AetheryteId, a => a);

            var metadata = new ZoneStaticInfo(
                territoryId,
                name,
                placeNameId,
                aetherytes,
                lookup,
                DetermineCanFly(row),
                DetermineMountRestriction(row),
                DetermineMapCenter(row));

            log.Debug("ZoneService: Cached static data for territory {TerritoryId} -> {Name} (aetherytes: {AetheryteCount})",
                territoryId,
                name,
                aetherytes.Count);

            return metadata;
        }
        catch (Exception ex)
        {
            log.Error(ex, "ZoneService: Failed to determine data for territory {TerritoryId}", territoryId);
            return null;
        }
    }

    private IReadOnlyList<AetheryteSummary> BuildAetheryteSummaries(uint territoryId, TerritoryType territoryRow)
    {
        var sheet = dataManager.GetExcelSheet<Aetheryte>();
        if (sheet == null)
        {
            log.Debug("ZoneService: Aetheryte sheet unavailable while resolving territory {TerritoryId}", territoryId);
            return Array.Empty<AetheryteSummary>();
        }

        var summaries = new List<AetheryteSummary>();
        foreach (var aetheryte in sheet)
        {
            if (aetheryte.Territory.ValueNullable?.RowId != territoryId)
            {
                continue;
            }

            var name = aetheryte.PlaceName.ValueNullable?.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Aetheryte {aetheryte.RowId}";
            }

            var summary = new AetheryteSummary(
                (uint)aetheryte.RowId,
                territoryId,
                name!,
                aetheryte.PlaceName.RowId,
                new Vector3(
                    ReadFloatProperty(aetheryte, "X", "PosX", "MapX", "Unknown6"),
                    ReadFloatProperty(aetheryte, "Y", "PosY", "MapY", "Unknown7"),
                    ReadFloatProperty(aetheryte, "Z", "PosZ", "MapZ", "Unknown8")),
                aetheryte.RowId == territoryRow.Aetheryte.RowId,
                0,
                0,
                false);

            summaries.Add(summary);
        }

        return summaries
            .OrderByDescending(s => s.IsPrimary)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<AetheryteSummary> DetermineUnlockedAetherytes(ZoneStaticInfo metadata)
    {
        if (metadata.Aetherytes.Count == 0)
        {
            return Array.Empty<AetheryteSummary>();
        }

        try
        {
            var unlocked = new List<AetheryteSummary>();

            foreach (var entry in aetheryteList)
            {
                if (entry == null || entry.TerritoryId != metadata.TerritoryId)
                {
                    continue;
                }

                if (!metadata.AetheryteLookup.TryGetValue(entry.AetheryteId, out var summary))
                {
                    continue;
                }

                var enriched = summary with
                {
                    GilCost = entry.GilCost,
                    IsFavourite = entry.IsFavourite,
                };

                unlocked.Add(enriched);
            }

            return unlocked
                .OrderByDescending(a => a.IsPrimary)
                .ThenByDescending(a => a.IsFavourite)
                .ThenBy(a => a.GilCost)
                .ToList();
        }
        catch (Exception ex)
        {
            log.Error(ex, "ZoneService: Failed to enumerate IAetheryteList for territory {TerritoryId}", metadata.TerritoryId);
            return metadata.Aetherytes;
        }
    }

    private static bool DetermineCanFly(TerritoryType row)
    {
        return ReadBooleanProperty(row, true, "IsFly", "IsFlying", "CanFly", "IsAirPlayable");
    }

    private static bool DetermineMountRestriction(TerritoryType row)
    {
        var restricted = ReadBooleanProperty(row, false, "MountRestricted", "IsMountRestricted", "IsMountProhibited");
        if (restricted)
        {
            return true;
        }

        var allowed = ReadBooleanProperty(row, true, "CanUseMount", "IsMountAllowed");
        return !allowed;
    }

    private static Vector3 DetermineMapCenter(TerritoryType row)
    {
        return new Vector3(
            ReadFloatProperty(row, "CenterX", "MapCenterX", "Unknown70"),
            ReadFloatProperty(row, "CenterY", "MapCenterY", "Unknown71"),
            ReadFloatProperty(row, "CenterZ", "MapCenterZ", "Unknown72"));
    }

    private static float ReadFloatProperty(object row, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = row.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
            {
                continue;
            }

            var value = property.GetValue(row);
            if (value == null)
            {
                continue;
            }

            switch (value)
            {
                case float f:
                    return f;
                case double d:
                    return (float)d;
                case int i:
                    return i;
                case uint ui:
                    return ui;
                case short s:
                    return s;
                case ushort us:
                    return us;
            }
        }

        return 0f;
    }

    private static bool ReadBooleanProperty(object row, bool defaultValue, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = row.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
            {
                continue;
            }

            var value = property.GetValue(row);
            if (value == null)
            {
                continue;
            }

            switch (value)
            {
                case bool b:
                    return b;
                case byte b8:
                    return b8 != 0;
                case sbyte sb:
                    return sb != 0;
                case short s:
                    return s != 0;
                case ushort us:
                    return us != 0;
                case int i:
                    return i != 0;
                case uint ui:
                    return ui != 0;
            }
        }

        return defaultValue;
    }

    private sealed record ZoneStaticInfo(
        uint TerritoryId,
        string Name,
        uint PlaceNameId,
        IReadOnlyList<AetheryteSummary> Aetherytes,
        IReadOnlyDictionary<uint, AetheryteSummary> AetheryteLookup,
        bool CanFly,
        bool MountRestricted,
        Vector3 MapCenter);
}
