using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Services.Fates;

namespace YetAnotherFATE.Services.Travel;

public sealed class IdleAetheryteReturnController
{
    private readonly Configuration configuration;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IFateRepository fateRepository;
    private readonly IFateSelector fateSelector;
    private readonly IFateFollowUpTracker followUps;
    private readonly ITravelController travel;
    private readonly AutomaticTravelPolicy automaticTravel;
    private readonly NearestAetheryteReturnRoute route;
    private readonly IPluginLog log;
    private DateTimeOffset? emptySince;
    private bool parked;

    public IdleAetheryteReturnController(
        Configuration configuration,
        IClientState clientState,
        IPlayerState playerState,
        IObjectTable objectTable,
        ICondition condition,
        IFateRepository fateRepository,
        IFateSelector fateSelector,
        IFateFollowUpTracker followUps,
        ITravelController travel,
        AutomaticTravelPolicy automaticTravel,
        NearestAetheryteReturnRoute route,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.fateRepository = fateRepository;
        this.fateSelector = fateSelector;
        this.followUps = followUps;
        this.travel = travel;
        this.automaticTravel = automaticTravel;
        this.route = route;
        this.log = log;
    }

    public string Status { get; private set; } = "Disabled";
    public string RouteStatus => route.Failure ?? $"{(parked ? "Parked" : "Waiting")}: {route.DestinationName}";

    public bool Tick()
    {
        if (!configuration.ReturnToNearestAetheryteWhenIdle || configuration.ZoneRotationEnabled ||
            !configuration.AutomatorEnabled || !clientState.IsLoggedIn)
        {
            Reset();
            return false;
        }

        if (travel.CurrentJob is not null)
        {
            Status = "Waiting for travel job";
            return false;
        }

        var fates = fateRepository.EnumerateActiveFates()
            .Where(f => f.TerritoryId == clientState.TerritoryType && f.IsOnMap &&
                f.State is FateState.Preparing or FateState.Running)
            .ToArray();
        var eligible = fateSelector.SelectBest(fates) is not null;
        if (eligible && !route.IsTeleportIssued)
        {
            Reset();
            return false;
        }
        if (followUps.IsWatching)
        {
            Status = "Waiting for follow-up FATE";
            return true;
        }

        var player = objectTable.LocalPlayer;
        var ready = clientState.TerritoryType != 0 && playerState.IsLoaded && player is { CurrentHp: > 0 } &&
            !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51];
        var safe = ready && !condition[ConditionFlag.InCombat] && !condition[ConditionFlag.Unconscious] &&
            !condition[ConditionFlag.Occupied] && !condition[ConditionFlag.OccupiedInEvent] &&
            !condition[ConditionFlag.OccupiedInQuestEvent] && !condition[ConditionFlag.Casting] &&
            configuration.TeleportEnabled && automaticTravel.CanTeleport();

        if (eligible && route.IsTeleportIssued)
        {
            Status = "Finishing aetheryte return before FATE selection";
        }
        else if (!route.IsInProgress)
        {
            emptySince ??= DateTimeOffset.UtcNow;
            var delay = TimeSpan.FromSeconds(Math.Clamp(configuration.ZoneRotationEmptyDelaySeconds, 0, 3600));
            if (DateTimeOffset.UtcNow - emptySince.Value < delay)
            {
                Status = $"Waiting for no-eligible-FATE delay ({Math.Max(0, (delay - (DateTimeOffset.UtcNow - emptySince.Value)).TotalSeconds):F0}s)";
                return true;
            }
        }

        if (parked)
        {
            Status = "Parked at nearest aetheryte";
            return true;
        }

        var result = route.Tick(clientState.TerritoryType, player?.Position, ready, safe, DateTimeOffset.UtcNow);
        switch (result)
        {
            case AetheryteReturnResult.Arrived:
                if (eligible)
                {
                    Reset();
                    return false;
                }
                parked = true;
                Status = $"Parked at {route.DestinationName}";
                log.Debug("IdleAetheryteReturnController: parked at {Aetheryte}", route.DestinationName);
                return true;
            case AetheryteReturnResult.Failed:
                Status = route.Failure ?? "Aetheryte return failed";
                return true;
            default:
                Status = safe ? $"Returning to {route.DestinationName}" : "Waiting for safe aetheryte return";
                return true;
        }
    }

    public void Reset()
    {
        emptySince = null;
        parked = false;
        route.Reset();
        Status = configuration.ReturnToNearestAetheryteWhenIdle ? "Waiting" : "Disabled";
    }
}
