using System;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Data.Zone;
using YetAnotherFATE.Services.Zone;

namespace YetAnotherFATE.Services.Travel;

public sealed class TeleportAdvisor : ITeleportAdvisor
{
    private const float DefaultRunSpeed = 6.0f; // yalms per second while mounted
    private readonly IZoneService zoneService;
    private readonly Configuration configuration;
    private readonly IPluginLog log;

    public TeleportAdvisor(IZoneService zoneService, Configuration configuration, IPluginLog log)
    {
        this.zoneService = zoneService;
        this.configuration = configuration;
        this.log = log;
    }

    public TeleportRecommendation Evaluate(ActiveFate fate)
    {
        var zone = zoneService.GetZone(fate.TerritoryId);

        if (zone == null)
        {
            log.Debug("TeleportAdvisor: No zone metadata for fate {FateId}", fate.FateId);
            return BuildRecommendation(false, null, "missing zone data", fate.DistanceFromPlayer, fate.DistanceFromPlayer, fate.DistanceFromPlayer, 0, 0, 0, false, null, 0);
        }

        if (!zone.Value.HasUnlockedAetheryte)
        {
            log.Debug("TeleportAdvisor: Zone {Territory} lacks an unlocked aetheryte", zone.Value.TerritoryId);
            return BuildRecommendation(false, null, "no unlocked aetheryte", fate.DistanceFromPlayer, fate.DistanceFromPlayer, fate.DistanceFromPlayer, 0, 0, 0, false, null, 0);
        }

        var target = SelectBestAetheryte(zone.Value, fate.Center);
        if (target == null)
        {
            return BuildRecommendation(false, null, "no candidate", fate.DistanceFromPlayer, fate.DistanceFromPlayer, fate.DistanceFromPlayer, 0, 0, 0, false, null, 0);
        }

        var runDistance = fate.DistanceFromPlayer;
        var runTime = runDistance / GetRunSpeed();

        var teleportDistance = Vector3.Distance(target.Value.Position, fate.Center);
        var postTeleportBuffer = Math.Max(0, configuration.TeleportPostTeleportRunBufferYalms);
        var adjustedTeleportDistance = Math.Max(0, teleportDistance - postTeleportBuffer);
        var teleportRunTime = adjustedTeleportDistance / GetRunSpeed();
        var teleportPenalty = Math.Max(0, configuration.TeleportPenaltySeconds);
        var teleportTime = teleportPenalty + teleportRunTime;

        var recommendation = Decide(runDistance, runTime, teleportTime, target.Value);

        return BuildRecommendation(
            recommendation.ShouldTeleport,
            target.Value.AetheryteId,
            recommendation.Reason,
            runDistance,
            runDistance,
            teleportDistance,
            runTime,
            teleportTime,
            target.Value.GilCost,
            target.Value.IsFavourite,
            target.Value.Name,
            target.Value.SubIndex);
    }

    private (bool ShouldTeleport, string Reason) Decide(float runDistance, float runTime, float teleportTime, AetheryteSummary candidate)
    {
        if (!configuration.TeleportEnabled)
        {
            return (false, "teleport disabled");
        }

        if (runDistance < Math.Max(0, configuration.TeleportMinimumDistanceYalms))
        {
            return (false, $"distance<{configuration.TeleportMinimumDistanceYalms:F0}");
        }

        var maxCost = configuration.TeleportMaxGilCost;
        if (maxCost > 0 && candidate.GilCost > maxCost)
        {
            return (false, $"cost>{maxCost}");
        }

        var advantageThreshold = Math.Max(0, configuration.TeleportAdvantageThresholdSeconds);
        var advantage = runTime - teleportTime;
        if (advantage < advantageThreshold)
        {
            return (false, $"advantage<{advantageThreshold:F1}s");
        }

        var reason = $"teleport faster ({teleportTime:F1}s vs {runTime:F1}s)";
        return (true, reason);
    }

    private float GetRunSpeed()
    {
        if (configuration.TeleportMountedRunSpeed <= 0)
        {
            return DefaultRunSpeed;
        }

        return configuration.TeleportMountedRunSpeed;
    }

    private static AetheryteSummary? SelectBestAetheryte(ZoneInfo zone, Vector3 fateCenter)
    {
        var candidates = zone.UnlockedAetherytes.Count > 0 ? zone.UnlockedAetherytes : zone.Aetherytes;
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderBy(a => Vector3.Distance(a.Position, fateCenter))
            .First();
    }

    private static TeleportRecommendation BuildRecommendation(
        bool shouldTeleport,
        uint? aetheryteId,
        string reason,
        float directDistance,
        float runDistance,
        float teleportRunDistance,
        float runTimeSeconds,
        float teleportTimeSeconds,
        uint gilCost,
        bool usesFavourite,
        string? aetheryteName,
        byte subIndex)
    {
        return new TeleportRecommendation(
            shouldTeleport,
            aetheryteId,
            reason,
            directDistance,
            runDistance,
            teleportRunDistance,
            runTimeSeconds,
            teleportTimeSeconds,
            gilCost,
            usesFavourite,
            aetheryteName,
            subIndex);
    }
}
