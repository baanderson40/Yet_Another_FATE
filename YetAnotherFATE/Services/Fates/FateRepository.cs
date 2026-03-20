using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
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

    public FateRepository(
        IClientState clientState,
        IPlayerState playerState,
        IObjectTable objectTable,
        IFateTable fateTable,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.fateTable = fateTable;
        this.log = log;
    }

    public IReadOnlyList<ActiveFate> EnumerateActiveFates()
    {
        if (!clientState.IsLoggedIn || !playerState.IsLoaded || objectTable.LocalPlayer is not { } player)
        {
            log.Debug("FateRepository: player not ready (IsLoggedIn={LoggedIn}, IsLoaded={Loaded}, HasLocal={HasLocal})",
                clientState.IsLoggedIn,
                playerState.IsLoaded,
                objectTable.LocalPlayer != null);
            return Empty;
        }

        if (fateTable.Count == 0)
        {
            log.Debug("FateRepository: fate table empty in territory {TerritoryId}", clientState.TerritoryType);
            return Empty;
        }

        var results = new List<ActiveFate>(fateTable.Count);
        var playerPosition = player.Position;

        foreach (var fate in fateTable)
        {
            if (fate == null || !fateTable.IsValid(fate))
            {
                continue;
            }

            if (fate.State is FateState.Ended or FateState.Failed or FateState.WaitingForEnd)
            {
                continue;
            }

            var name = SanitizeName(fate.Name);
            var territoryId = fate.TerritoryType.RowId;
            var distance = Vector3.Distance(playerPosition, fate.Position);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var estimatedEnd = fate.StartTimeEpoch + fate.Duration;
            var remainingSeconds = Math.Max(0, estimatedEnd - now);
            var timeRemaining = TimeSpan.FromSeconds(remainingSeconds);

            var isContinuation = false;

            results.Add(new ActiveFate(
                fate.FateId,
                name,
                fate.Level,
                territoryId,
                fate.Position,
                fate.Radius,
                distance,
                isContinuation,
                timeRemaining));
        }

        log.Debug("FateRepository: Enumerated {Count} fates in territory {TerritoryId}", results.Count, clientState.TerritoryType);
        return results;
    }

    private static string SanitizeName(SeString rawName)
    {
        var name = rawName.TextValue;
        return string.IsNullOrWhiteSpace(name) ? "Unknown Fate" : name;
    }
}
