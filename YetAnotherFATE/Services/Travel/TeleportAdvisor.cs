using System;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Zone;

namespace YetAnotherFATE.Services.Travel;

public sealed class TeleportAdvisor : ITeleportAdvisor
{
    private const float RunSpeed = 6.0f; // yalms per second while mounted
    private const float TeleportPenaltySeconds = 15.0f; // animation + menu delay
    private const float PostTeleportRunBuffer = 200.0f; // average distance from aetheryte to fate start

    private readonly IZoneService zoneService;
    private readonly IPluginLog log;

    public TeleportAdvisor(IZoneService zoneService, IPluginLog log)
    {
        this.zoneService = zoneService;
        this.log = log;
    }

    public TeleportRecommendation Evaluate(ActiveFate fate)
    {
        var zone = zoneService.GetZone(fate.TerritoryId);

        if (zone == null)
        {
            log.Debug("TeleportAdvisor: No zone metadata for fate {FateId}", fate.FateId);
            return BuildRecommendation(false, null, "missing zone data", fate.DistanceFromPlayer, fate.DistanceFromPlayer, fate.DistanceFromPlayer);
        }

        if (!zone.Value.HasAetheryte || zone.Value.PrimaryAetheryte is null)
        {
            log.Debug("TeleportAdvisor: Zone {Territory} lacks an aetheryte", zone.Value.TerritoryId);
            return BuildRecommendation(false, null, "zone has no aetheryte", fate.DistanceFromPlayer, fate.DistanceFromPlayer, fate.DistanceFromPlayer);
        }

        var runDistance = fate.DistanceFromPlayer;
        var runTime = runDistance / RunSpeed;

        var estimatedPostTeleportRunDistance = Math.Max(0, runDistance - PostTeleportRunBuffer);
        var teleportRunTime = estimatedPostTeleportRunDistance / RunSpeed;
        var teleportTime = TeleportPenaltySeconds + teleportRunTime;

        var shouldTeleport = teleportTime + 5.0f < runTime;
        var reason = shouldTeleport
            ? $"teleport faster ({teleportTime:F1}s vs {runTime:F1}s)"
            : $"running faster ({runTime:F1}s vs {teleportTime:F1}s)";

        log.Debug("TeleportAdvisor: Fate {FateId} run={RunTime:F1}s tele={TeleportTime:F1}s decision={Decision}",
            fate.FateId,
            runTime,
            teleportTime,
            shouldTeleport ? "teleport" : "run");

        return BuildRecommendation(shouldTeleport, zone.Value.PrimaryAetheryte.Value.AetheryteId, reason, runDistance, runDistance, estimatedPostTeleportRunDistance);
    }

    private static TeleportRecommendation BuildRecommendation(bool shouldTeleport, uint? aetheryteId, string reason, float directDistance, float runDistance, float teleportRunDistance)
    {
        return new TeleportRecommendation(shouldTeleport, aetheryteId, reason, directDistance, runDistance, teleportRunDistance);
    }
}
