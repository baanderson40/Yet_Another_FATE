using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
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

            if (fate.State is Dalamud.Game.ClientState.Fates.FateState.Ended or Dalamud.Game.ClientState.Fates.FateState.Failed)
            {
                continue;
            }

            var name = SanitizeName(fate.Name);
            var territoryId = fate.TerritoryType.RowId;
            var distance = Vector3.Distance(playerPosition, fate.Position);
            var timeRemaining = TimeSpan.FromSeconds(Math.Max(0, fate.TimeRemaining));
            var context = GetFateContext(fate);

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
                timeRemaining,
                fate.State,
                SanitizeName(fate.Description),
                SanitizeName(fate.Objective),
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
                context.TurnInEventItem));
        }

        return results;
    }

    private static string SanitizeName(SeString rawName)
    {
        var name = rawName.TextValue;
        return string.IsNullOrWhiteSpace(name) ? "Unknown Fate" : name;
    }

    private static unsafe FateContext GetFateContext(IFate fate)
    {
        return fate.Address == nint.Zero ? default : *(FateContext*)fate.Address;
    }
}
