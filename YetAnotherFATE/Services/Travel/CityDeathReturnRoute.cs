using System;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Services.Zone;

namespace YetAnotherFATE.Services.Travel;

// Framework-tick owned. A provider's idle queue is not proof of arrival.
public sealed class CityDeathReturnRoute(ICityReturnPlanner planner, ICityAethernetClient client,
    IZoneService zones, ITeleportExecutor teleports) : IDisposable
{
    private readonly DeathReturnRoute gatewayRoute = new(zones, teleports);
    private CityReturnPlan? plan;
    private bool ownsRequest;
    private bool issued;
    private DateTimeOffset issuedAt;
    public string? Failure { get; private set; }
    internal bool HasIssuedRequest => issued || ownsRequest || gatewayRoute.HasIssuedReturn;

    public bool ExpectsTerritory(DeathRecoveryContext destination, uint territory)
        => territory == destination.TerritoryId || plan?.Gateway.TerritoryId == territory;
    public bool HasPendingCancellation => ownsRequest && !issued;

    public void Dispose() => Reset();

    public void Reset()
    {
        Cancel();
        gatewayRoute.Reset();
        plan = null;
        issued = false;
        Failure = null;
    }

    public void Cancel()
    {
        if (!ownsRequest) return;
        var state = client.State;
        if (state == CityAethernetState.Idle || (state == CityAethernetState.Busy && client.Abort()))
            ownsRequest = false;
        // If the provider temporarily disappears, keep cleanup pending for subsequent reset/failure ticks.
    }

    public DeathReturnResult Tick(DeathRecoveryContext destination, uint territory, bool ready, DateTimeOffset now)
    {
        if (Failure != null) { Cancel(); return DeathReturnResult.Failed; }
        var state = client.State;
        if (state == CityAethernetState.Unavailable)
            return Fail("No unlocked direct aetheryte; optional Lifestream aethernet lifecycle IPC unavailable");
        if (issued)
        {
            if (state == CityAethernetState.Idle) ownsRequest = false;
            if (ready && territory == destination.TerritoryId && state == CityAethernetState.Idle)
                return DeathReturnResult.Arrived;
            // Queue can drain before the game's loading flags become visible. Allow transition grace.
            if (now - issuedAt >= TimeSpan.FromSeconds(30))
                return Fail("Lifestream aethernet return did not reach death territory; manual return required");
            return DeathReturnResult.Waiting;
        }
        if (ownsRequest)
        {
            Cancel();
            return DeathReturnResult.Waiting;
        }
        if (!ready || state == CityAethernetState.Busy) return DeathReturnResult.Waiting;
        plan ??= planner.Resolve(destination);
        if (plan == null)
            return Fail("No unlocked direct aetheryte or supported city/aethernet route to death territory; manual return required");
        if (!client.CanRoute(plan.DestinationAethernetId))
            return Fail("Lifestream death return destination IPC unavailable; manual return required");

        var gateway = gatewayRoute.Tick(new DeathRecoveryContext(plan.Gateway.TerritoryId, plan.Gateway.Position, null),
            territory, ready, now, plan.Gateway.AetheryteId, client.ActiveAetheryte == plan.Gateway.AetheryteId);
        if (gateway == DeathReturnResult.Failed) return Fail(gatewayRoute.Failure ?? "City gateway teleport failed");
        if (gateway != DeathReturnResult.Arrived) return DeathReturnResult.Waiting;
        // The provider's ById endpoint only routes within the active network. Require the exact gateway.
        if (client.ActiveAetheryte != plan.Gateway.AetheryteId) return DeathReturnResult.Waiting;
        try
        {
            ownsRequest = true;
            if (!client.Request(plan.DestinationAethernetId))
            {
                ownsRequest = false;
                return Fail("Lifestream rejected death return aethernet destination");
            }
        }
        catch (Exception)
        {
            return Fail("Lifestream death return request failed; cancelling queued work");
        }
        issued = true;
        issuedAt = now;
        return DeathReturnResult.Waiting;
    }

    private DeathReturnResult Fail(string reason)
    {
        Failure = reason;
        Cancel();
        return DeathReturnResult.Failed;
    }
}
