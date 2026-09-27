using System;
using System.Linq;
using System.Numerics;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Zone;

namespace YetAnotherFATE.Services.Travel;

public sealed class TerritoryReturnRoute(IZoneService zones, ITeleportExecutor teleports)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
    private DateTimeOffset? startedAt;
    private DateTimeOffset? readySince;
    private DateTimeOffset? issuedAt;
    private DateTimeOffset nextAttempt;
    private int attempts;

    public string? Failure { get; private set; }

    public void Reset()
    {
        startedAt = readySince = issuedAt = null;
        nextAttempt = default;
        attempts = 0;
        Failure = null;
    }

    public TerritoryReturnResult Tick(uint destinationTerritory, Vector3 destinationPosition, uint territory,
        bool ready, DateTimeOffset now)
    {
        if (Failure != null)
            return TerritoryReturnResult.Failed;

        startedAt ??= now;
        if (now - startedAt.Value >= Timeout)
            return Fail("Origin territory return timed out; manual recovery required");

        if (!ready)
        {
            readySince = null;
            return TerritoryReturnResult.Waiting;
        }

        readySince ??= now;
        if (now - readySince.Value < TimeSpan.FromSeconds(1))
            return TerritoryReturnResult.Waiting;

        if (destinationTerritory == 0)
            return Fail("Origin territory unavailable; manual recovery required");
        if (territory == destinationTerritory)
            return TerritoryReturnResult.Arrived;

        if (issuedAt.HasValue)
        {
            if (now - issuedAt.Value < TimeSpan.FromSeconds(30))
                return TerritoryReturnResult.Waiting;
            issuedAt = null;
        }

        if (attempts >= 3)
            return Fail("Origin territory teleport failed after three attempts");
        if (now < nextAttempt || teleports.State != TeleportState.Idle)
            return TerritoryReturnResult.Waiting;

        var zone = zones.GetZone(destinationTerritory);
        var candidates = zone?.UnlockedAetherytes
            .Where(a => a.AetheryteId != 0 && a.TerritoryId == destinationTerritory)
            .OrderBy(a => a.HasPosition && float.IsFinite(a.Position.X) && float.IsFinite(a.Position.Z)
                ? Vector2.DistanceSquared(new(a.Position.X, a.Position.Z), new(destinationPosition.X, destinationPosition.Z))
                : float.MaxValue)
            .ThenBy(a => a.GilCost)
            .ToArray();
        if (candidates is not { Length: > 0 })
            return Fail("No unlocked aetheryte in origin territory; manual recovery required");

        var target = candidates[0];
        var recommendation = new TeleportRecommendation(true, target.AetheryteId, "Return to AutoRetainer origin territory",
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
                return Fail($"Origin territory teleport rejected: {result}");
        }

        return TerritoryReturnResult.Waiting;
    }

    private TerritoryReturnResult Fail(string reason)
    {
        Failure = reason;
        return TerritoryReturnResult.Failed;
    }
}

public enum TerritoryReturnResult
{
    Waiting,
    Arrived,
    Failed,
}
