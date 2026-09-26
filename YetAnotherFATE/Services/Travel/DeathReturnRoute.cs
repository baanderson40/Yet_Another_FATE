using System;
using System.Linq;
using System.Numerics;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Zone;

namespace YetAnotherFATE.Services.Travel;

// Framework-tick owned: no background work survives reset/disable/logout.
public sealed class DeathReturnRoute(IZoneService zones, ITeleportExecutor teleports, CityDeathReturnRoute? cityRoute = null)
{
    private DateTimeOffset? startedAt;
    private DateTimeOffset? readySince;
    private DateTimeOffset? issuedAt;
    private DateTimeOffset nextAttempt;
    private int attempts;
    private bool usingCityRoute;
    private uint? stagedTerritory;
    public string? Failure { get; private set; }

    internal bool HasIssuedReturn => issuedAt.HasValue ||
        (usingCityRoute && cityRoute?.HasIssuedRequest == true);

    public void ObserveTransit(uint territory, bool loading)
    {
        if (!HasIssuedReturn && loading && territory != 0)
            stagedTerritory = territory;
    }

    public bool ExpectsTerritory(DeathRecoveryContext destination, uint territory)
        => territory == 0 || territory == destination.TerritoryId ||
           (usingCityRoute && cityRoute?.ExpectsTerritory(destination, territory) == true) ||
           (!HasIssuedReturn && stagedTerritory == territory);

    public void Reset()
    {
        cityRoute?.Reset();
        usingCityRoute = false;
        startedAt = readySince = issuedAt = null;
        stagedTerritory = null;
        nextAttempt = default;
        attempts = 0;
        Failure = null;
    }

    public DeathReturnResult Tick(DeathRecoveryContext destination, uint territory, bool ready, DateTimeOffset now,
        uint? requiredAetheryteId = null, bool atDestination = true)
    {
        if (Failure != null) { cityRoute?.Cancel(); return DeathReturnResult.Failed; }
        startedAt ??= now;
        if (now - startedAt.Value >= TimeSpan.FromMinutes(2))
            return Fail("Death return timed out; manual return required");
        if (cityRoute?.HasPendingCancellation == true)
        {
            cityRoute.Cancel();
            if (cityRoute.HasPendingCancellation) return DeathReturnResult.Waiting;
        }
        if (usingCityRoute)
        {
            if (!ready) readySince = null;
            else readySince ??= now;
            var cityResult = cityRoute!.Tick(destination, territory,
                ready && now - readySince!.Value >= TimeSpan.FromSeconds(1), now);
            return cityResult == DeathReturnResult.Failed ? Fail(cityRoute.Failure!) : cityResult;
        }
        if (!ready)
        {
            readySince = null;
            return DeathReturnResult.Waiting;
        }
        readySince ??= now;
        if (now - readySince.Value < TimeSpan.FromSeconds(1)) return DeathReturnResult.Waiting;
        // Verify the actual territory, never infer arrival from executor completion alone.
        if (destination.TerritoryId == 0) return Fail("Death territory unavailable");
        if (territory == destination.TerritoryId && atDestination) return DeathReturnResult.Arrived;
        if (issuedAt.HasValue)
        {
            if (now - issuedAt.Value < TimeSpan.FromSeconds(30)) return DeathReturnResult.Waiting;
            issuedAt = null;
        }
        if (attempts >= 3) return Fail("Death return teleport failed after three attempts");
        if (now < nextAttempt || teleports.State != TeleportState.Idle) return DeathReturnResult.Waiting;

        var zone = zones.GetZone(destination.TerritoryId);
        var candidates = zone?.UnlockedAetherytes
            .Where(a => a.AetheryteId != 0 && a.TerritoryId == destination.TerritoryId &&
                        (!requiredAetheryteId.HasValue || a.AetheryteId == requiredAetheryteId))
            .OrderBy(a => a.HasPosition && float.IsFinite(a.Position.X) && float.IsFinite(a.Position.Z)
                ? Vector2.DistanceSquared(new(a.Position.X, a.Position.Z), new(destination.Position.X, destination.Position.Z))
                : float.MaxValue)
            .ThenBy(a => a.GilCost).ToArray();
        if (candidates == null || candidates.Length == 0)
        {
            if (cityRoute == null)
                return Fail("No unlocked direct aetheryte in death territory; manual return required");
            usingCityRoute = true;
            return cityRoute.Tick(destination, territory, true, now) == DeathReturnResult.Failed
                ? Fail(cityRoute.Failure!) : DeathReturnResult.Waiting;
        }

        var target = candidates[0];
        // Return is mandatory, independent of optional same-zone profitability settings.
        var recommendation = new TeleportRecommendation(true, target.AetheryteId, "Return to death territory",
            0, 0, 0, 0, 0, target.GilCost, target.IsFavourite, target.Name, target.SubIndex);
        var result = teleports.RequestTeleport(recommendation);
        nextAttempt = now.AddSeconds(1);
        if (result == TeleportRequestResult.Issued)
        {
            attempts++;
            issuedAt = now;
            readySince = null;
        }
        else if (result is not (TeleportRequestResult.Busy or TeleportRequestResult.Blocked))
        {
            attempts++;
            if (result is TeleportRequestResult.InvalidTarget or TeleportRequestResult.ClientUnavailable)
                return Fail($"Death return teleport rejected: {result}");
        }
        return DeathReturnResult.Waiting;
    }

    private DeathReturnResult Fail(string reason)
    {
        cityRoute?.Cancel();
        Failure = reason;
        return DeathReturnResult.Failed;
    }
}

public enum DeathReturnResult { Waiting, Arrived, Failed }
