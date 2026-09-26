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

    public IReadOnlyList<uint> GetRotationTerritories(uint? sameExpansionAs = null)
    {
        var territories = dataManager.GetExcelSheet<TerritoryType>();
        uint? expansion = null;
        if (sameExpansionAs is { } source)
        {
            if (!territories.TryGetRow(source, out var current)) return Array.Empty<uint>();
            expansion = current.ExVersion.RowId;
        }
        var teleportTerritories = dataManager.GetExcelSheet<Aetheryte>()
            .Where(a => a.IsAetheryte).Select(a => a.Territory.RowId).ToHashSet();
        // Native intended-use enum: Overworld = 1. Exclude cities, duties,
        // unused duplicate maps and PvP; require an actual teleport destination.
        return territories.Where(t => t.IsInUse && !t.IsPvpZone &&
                t.TerritoryIntendedUse.RowId == (uint)FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.Overworld &&
                t.Map.RowId != 0 && teleportTerritories.Contains(t.RowId) &&
                (!expansion.HasValue || t.ExVersion.RowId == expansion))
            .OrderBy(t => t.RowId).Select(t => t.RowId).ToArray();
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

            var position = ResolveAetherytePosition(aetheryte, territoryId);
            var summary = new AetheryteSummary(
                (uint)aetheryte.RowId,
                territoryId,
                name!,
                aetheryte.PlaceName.RowId,
                position.Position,
                position.Source,
                position.HasValue,
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

    private (Vector3 Position, string Source, bool HasValue) ResolveAetherytePosition(Aetheryte aetheryte, uint territoryId)
    {
        try
        {
            var level = aetheryte.Level[0].ValueNullable;
            if (level is { } levelPosition)
            {
                return (new Vector3(levelPosition.X, levelPosition.Y, levelPosition.Z), "Level", true);
            }

            var maps = dataManager.GetExcelSheet<Map>();
            var markers = dataManager.GetSubrowExcelSheet<MapMarker>();
            if (maps == null || markers == null)
            {
                return (Vector3.Zero, "unavailable", false);
            }

            var aethernetNameId = aetheryte.AethernetName.RowId;
            Vector3? preferred = null;
            Vector3? fallback = null;
            foreach (var map in maps)
            {
                if (map.MapMarkerRange == 0 || map.SizeFactor == 0 || !markers.TryGetRow(map.MapMarkerRange, out var markerGroup))
                {
                    continue;
                }

                foreach (var marker in markerGroup)
                {
                    var matches = marker.DataType == 3 && marker.DataKey.RowId == aetheryte.RowId
                        || marker.DataType == 4 && marker.DataKey.RowId == aethernetNameId;
                    if (!matches)
                    {
                        continue;
                    }

                    var scale = map.SizeFactor / 100f;
                    var position = new Vector3(
                        (marker.X - 1024f) / scale - map.OffsetX,
                        0,
                        (marker.Y - 1024f) / scale - map.OffsetY);
                    if (map.TerritoryType.RowId == territoryId)
                    {
                        preferred = position;
                    }
                    else
                    {
                        fallback ??= position;
                    }
                }
            }

            if (preferred is { } preferredPosition)
            {
                return (preferredPosition, "MapMarker", true);
            }

            if (fallback is { } fallbackPosition)
            {
                return (fallbackPosition, "MapMarker (fallback map)", true);
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "ZoneService: Failed to resolve position for aetheryte {AetheryteId}", aetheryte.RowId);
        }

        return (Vector3.Zero, "unavailable", false);
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
                    SubIndex = entry.SubIndex,
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
            return Array.Empty<AetheryteSummary>();
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
