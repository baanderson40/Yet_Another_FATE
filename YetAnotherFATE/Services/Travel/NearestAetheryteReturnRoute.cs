using System;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Data.Zone;
using YetAnotherFATE.Services.Zone;

namespace YetAnotherFATE.Services.Travel;

public enum AetheryteReturnResult
{
    Waiting,
    Arrived,
    Failed,
}

public sealed class NearestAetheryteReturnRoute
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ArrivalTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StableArrivalWindow = TimeSpan.FromSeconds(1);
    private const float ArrivalRadius = 30f;

    private readonly Configuration configuration;
    private readonly IZoneService zones;
    private readonly ITeleportExecutor teleports;
    private DateTimeOffset? startedAt;
    private DateTimeOffset? issuedAt;
    private DateTimeOffset? readySince;
    private AetheryteSummary? destination;
    private string? failure;

    public NearestAetheryteReturnRoute(Configuration configuration, IZoneService zones, ITeleportExecutor teleports)
    {
        this.configuration = configuration;
        this.zones = zones;
        this.teleports = teleports;
    }

    public bool IsInProgress => startedAt.HasValue;
    public bool IsTeleportIssued => issuedAt.HasValue;
    public string? Failure => failure;
    public string DestinationName => destination?.Name ?? "none";
    public uint DestinationAetheryteId => destination?.AetheryteId ?? 0;
    public byte DestinationSubIndex => destination?.SubIndex ?? 0;
    public double DestinationDistance(Vector3 position)
        => destination is { HasPosition: true } target
            ? HorizontalDistance(position, target.Position)
            : double.PositiveInfinity;

    public void Reset()
    {
        startedAt = issuedAt = readySince = null;
        destination = null;
        failure = null;
    }

    public AetheryteReturnResult Tick(
        uint territory,
        Vector3? playerPosition,
        bool ready,
        bool safe,
        DateTimeOffset now)
    {
        startedAt ??= now;
        if (failure != null)
            return AetheryteReturnResult.Failed;
        if (now - startedAt.Value >= Timeout)
            return Fail("Aetheryte return timed out");

        if (issuedAt.HasValue)
            return ObserveIssuedTeleport(territory, playerPosition, ready, safe, now);

        if (!ready || !safe || playerPosition is not { } position)
        {
            readySince = null;
            return AetheryteReturnResult.Waiting;
        }
        if (teleports.State != TeleportState.Idle)
            return AetheryteReturnResult.Waiting;

        destination = FindDestination(territory, position);
        if (destination is not { } target)
            return Fail("No unlocked positioned main aetheryte within teleport budget");
        if (HorizontalDistance(position, target.Position) <= ArrivalRadius)
            return AetheryteReturnResult.Arrived;

        var recommendation = new TeleportRecommendation(
            true,
            target.AetheryteId,
            "Return to nearest aetheryte",
            0,
            0,
            0,
            0,
            0,
            target.GilCost,
            target.IsFavourite,
            target.Name,
            target.SubIndex);
        var result = teleports.RequestTeleport(recommendation);
        if (result == TeleportRequestResult.Issued)
        {
            issuedAt = now;
            readySince = null;
        }
        else if (result is not (TeleportRequestResult.Busy or TeleportRequestResult.Blocked))
        {
            return Fail($"Aetheryte teleport rejected: {result}");
        }

        return AetheryteReturnResult.Waiting;
    }

    private AetheryteReturnResult ObserveIssuedTeleport(
        uint territory,
        Vector3? playerPosition,
        bool ready,
        bool safe,
        DateTimeOffset now)
    {
        var issued = issuedAt!.Value;
        if (teleports.LastFailedAt >= issued || teleports.State == TeleportState.Failed)
            return Fail("Aetheryte teleport failed");

        var completed = teleports.State == TeleportState.Completed || teleports.LastCompletedAt >= issued;
        if (!completed)
            return AetheryteReturnResult.Waiting;
        if (now - issued >= ArrivalTimeout)
            return Fail("Aetheryte arrival timed out");
        if (!ready || !safe || playerPosition is not { } position || destination is not { } target)
        {
            readySince = null;
            return AetheryteReturnResult.Waiting;
        }
        if (territory != target.TerritoryId || HorizontalDistance(position, target.Position) > ArrivalRadius)
        {
            readySince = null;
            return AetheryteReturnResult.Waiting;
        }

        readySince ??= now;
        return now - readySince.Value >= StableArrivalWindow
            ? AetheryteReturnResult.Arrived
            : AetheryteReturnResult.Waiting;
    }

    private AetheryteSummary? FindDestination(uint territory, Vector3 position)
    {
        var zone = zones.GetZone(territory);
        return zone?.UnlockedAetherytes
            .Where(a => a.AetheryteId != 0 && a.TerritoryId == territory && a.IsPrimary && a.HasPosition &&
                float.IsFinite(a.Position.X) && float.IsFinite(a.Position.Z) &&
                (configuration.TeleportMaxGilCost == 0 || a.GilCost <= configuration.TeleportMaxGilCost))
            .OrderBy(a => HorizontalDistanceSquared(position, a.Position))
            .ThenBy(a => a.AetheryteId)
            .Select(a => (AetheryteSummary?)a)
            .FirstOrDefault();
    }

    private AetheryteReturnResult Fail(string reason)
    {
        failure = reason;
        return AetheryteReturnResult.Failed;
    }

    private static float HorizontalDistanceSquared(Vector3 left, Vector3 right)
        => Vector2.DistanceSquared(new(left.X, left.Z), new(right.X, right.Z));

    private static float HorizontalDistance(Vector3 left, Vector3 right)
        => MathF.Sqrt(HorizontalDistanceSquared(left, right));
}
