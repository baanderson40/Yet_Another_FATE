using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates;

public sealed class FateRepository : IFateRepository
{
    private static readonly IReadOnlyList<ActiveFate> Empty = Array.Empty<ActiveFate>();

    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly IFateTable fateTable;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private uint cachedTerritory;
    private Dictionary<uint, (uint First, uint Last)>? fatesByLocation;
    private readonly Dictionary<uint, ActiveFate> lastSeen = new();
    private readonly Dictionary<uint, ActiveFate> terminal = new();

    public FateRepository(
        IClientState clientState,
        IPlayerState playerState,
        IObjectTable objectTable,
        IFateTable fateTable,
        IPluginLog log,
        IDataManager dataManager)
    {
        this.clientState = clientState;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.fateTable = fateTable;
        this.log = log;
        this.dataManager = dataManager;
    }

    public IReadOnlyList<ActiveFate> EnumerateActiveFates()
    {
        if (!clientState.IsLoggedIn || !playerState.IsLoaded || objectTable.LocalPlayer is not { } player)
        {
            lastSeen.Clear();
            terminal.Clear();
            log.Debug("FateRepository: player not ready (IsLoggedIn={LoggedIn}, IsLoaded={Loaded}, HasLocal={HasLocal})",
                clientState.IsLoggedIn,
                playerState.IsLoaded,
                objectTable.LocalPlayer != null);
            return Empty;
        }

        if (cachedTerritory != clientState.TerritoryType)
        {
            cachedTerritory = clientState.TerritoryType;
            lastSeen.Clear();
            terminal.Clear();
        }

        if (fateTable.Count == 0)
        {
            return Empty;
        }

        var results = new List<ActiveFate>(fateTable.Count);
        var playerPosition = player.Position;
        var mapFateIds = GetMapFateIds();
        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Fate>();
        if (fatesByLocation == null)
        {
            fatesByLocation = new Dictionary<uint, (uint First, uint Last)>();
            foreach (var sheetRow in sheet)
            {
                if (sheetRow.RowId == 0 || sheetRow.Location == 0)
                {
                    continue;
                }

                fatesByLocation[sheetRow.Location] = fatesByLocation.TryGetValue(sheetRow.Location, out var range)
                    ? (Math.Min(range.First, sheetRow.RowId), Math.Max(range.Last, sheetRow.RowId))
                    : (sheetRow.RowId, sheetRow.RowId);
            }
        }

        foreach (var fate in fateTable)
        {
            if (fate == null || !fateTable.IsValid(fate))
            {
                continue;
            }

            if (fate.State is Dalamud.Game.ClientState.Fates.FateState.Ending or
                Dalamud.Game.ClientState.Fates.FateState.Ended or
                Dalamud.Game.ClientState.Fates.FateState.Failed)
            {
                if (lastSeen.TryGetValue(fate.FateId, out var previous))
                {
                    terminal[fate.FateId] = previous with
                    {
                        State = fate.State,
                        TimeRemaining = TimeSpan.Zero,
                        Progress = fate.Progress,
                    };
                }

                continue;
            }

            var name = SanitizeName(fate.Name);
            var territoryId = fate.TerritoryType.RowId;
            var distance = Vector3.Distance(playerPosition, fate.Position);
            var timeRemaining = TimeSpan.FromSeconds(fate.TimeRemaining);
            var context = GetFateContext(fate);

            var hasMetadata = sheet.TryGetRow(fate.FateId, out var row) && row.Location != 0;
            // clib's Lumina HasFollowUp extension is a location/row-ID heuristic,
            // not a sheet column: any later sheet row at this location qualifies.
            var locationRange = hasMetadata ? fatesByLocation[row.Location] : default;
            var hasFollowUp = hasMetadata && locationRange.Last > fate.FateId;
            var isContinuation = hasMetadata && locationRange.First < fate.FateId;

            var active = new ActiveFate(
                fate.FateId,
                name,
                fate.Level,
                territoryId,
                fate.Position,
                fate.Radius,
                distance,
                isContinuation,
                timeRemaining,
                fate.State,
                context.FATEChain,
                SanitizeText(fate.Description),
                SanitizeText(fate.Objective),
                fate.Progress,
                fate.HandInCount,
                fate.HasBonus,
                fate.MaxLevel,
                fate.IconId,
                fate.MapIconId,
                fate.StartTimeEpoch,
                fate.Duration,
                context.MotivationNpc,
                context.ObjectiveNpc,
                context.Rule,
                context.FateRuleEx,
                context.EventItem,
                context.ReqEventItem,
                context.TurnInEventItem)
            {
                HasSheetMetadata = hasMetadata,
                HasFollowUp = hasFollowUp,
                LocationId = hasMetadata ? row.Location : 0,
                IsOnMap = mapFateIds.Contains(fate.FateId) && fate.Position != Vector3.Zero,
            };
            results.Add(active);
            lastSeen[active.FateId] = active;
            terminal.Remove(active.FateId);
        }

        return results;
    }

    public bool TryGetTerminalFate(uint fateId, out ActiveFate fate)
        => terminal.TryGetValue(fateId, out fate);

    private static string SanitizeName(SeString rawName)
    {
        var name = rawName.TextValue;
        return string.IsNullOrWhiteSpace(name) ? "Unknown Fate" : name;
    }

    private static string SanitizeText(SeString rawText) => rawText.TextValue.Trim();

    private static unsafe HashSet<uint> GetMapFateIds()
    {
        var ids = new HashSet<uint>();
        var agent = AgentMap.Instance();
        if (agent == null)
        {
            return ids;
        }

        // Event markers otherwise remain stale while the map is closed.
        agent->UpdateEventMapMarkers(&agent->EventMarkersPtrs);
        foreach (var marker in agent->EventMarkers)
        {
            if (marker.IconId != 0 && marker.DataId != 0)
            {
                ids.Add(marker.DataId);
            }
        }

        return ids;
    }

    private static unsafe FateContext GetFateContext(IFate fate)
    {
        return fate.Address == nint.Zero ? default : *(FateContext*)fate.Address;
    }
}
