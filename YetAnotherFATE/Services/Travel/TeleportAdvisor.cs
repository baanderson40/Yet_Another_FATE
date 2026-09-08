using System;
using System.Collections.Generic;
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
    private DateTimeOffset nextDiagnosticLogAt = DateTimeOffset.MinValue;

    public TeleportAdvisor(IZoneService zoneService, Configuration configuration, IPluginLog log)
    {
        this.zoneService = zoneService;
        this.configuration = configuration;
        this.log = log;
    }

    public TeleportRecommendation Evaluate(ActiveFate fate)
        => Analyze(fate).Recommendation;

    public TeleportAnalysis Analyze(ActiveFate fate)
    {
        var zone = zoneService.GetZone(fate.TerritoryId);

        if (zone == null)
        {
            log.Debug("TeleportAdvisor: No zone metadata for fate {FateId}", fate.FateId);
            return BuildAnalysis(fate, null, "missing zone data", Array.Empty<AetheryteTeleportDiagnostic>());
        }

        if (!zone.Value.HasUnlockedAetheryte)
        {
            log.Debug("TeleportAdvisor: Zone {Territory} lacks an unlocked aetheryte", zone.Value.TerritoryId);
            return BuildAnalysis(fate, zone, "no unlocked aetheryte", Array.Empty<AetheryteTeleportDiagnostic>());
        }

        var target = SelectBestAetheryte(zone.Value, fate.Center, out var aetherytes);
        if (target == null)
        {
            return BuildAnalysis(fate, zone, "no affordable aetheryte", aetherytes);
        }

        var runDistance = fate.DistanceFromPlayer;
        var runTime = runDistance / GetRunSpeed();

        var teleportDistance = HorizontalDistance(target.Value.Position, fate.Center);
        var postTeleportBuffer = Math.Max(0, configuration.TeleportPostTeleportRunBufferYalms);
        var adjustedTeleportDistance = Math.Max(0, teleportDistance - postTeleportBuffer);
        var teleportRunTime = adjustedTeleportDistance / GetRunSpeed();
        var teleportPenalty = Math.Max(0, configuration.TeleportPenaltySeconds);
        var teleportTime = teleportPenalty + teleportRunTime;

        var recommendation = Decide(runDistance, runTime, teleportTime, target.Value);

        var recommendationResult = BuildRecommendation(
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

        var advantageThreshold = Math.Max(0, configuration.TeleportAdvantageThresholdSeconds);
        var result = new TeleportAnalysis(
            zone,
            recommendationResult,
            GetRunSpeed(),
            teleportPenalty,
            postTeleportBuffer,
            runTime - teleportTime,
            advantageThreshold,
            null,
            aetherytes);

        if (configuration.DiagnosticLoggingEnabled && DateTimeOffset.UtcNow >= nextDiagnosticLogAt)
        {
            nextDiagnosticLogAt = DateTimeOffset.UtcNow.AddSeconds(10);
            log.Debug("TeleportAdvisor: fate #{FateId} selected={Aetheryte} id={AetheryteId} decision={Decision} reason={Reason} run={Run:F1}s teleport={Teleport:F1}s advantage={Advantage:F1}s", fate.FateId, recommendationResult.AetheryteName ?? "none", recommendationResult.AetheryteId?.ToString() ?? "none", recommendationResult.ShouldTeleport ? "teleport" : "run", recommendationResult.Reason, runTime, teleportTime, runTime - teleportTime);
            foreach (var item in aetherytes)
            {
                log.Debug("TeleportAdvisor: fate #{FateId} aetheryte {Name} id={Id} pos={Position} distance={Distance:F1} cost={Cost} withinCost={WithinCost} selected={Selected} exclusion={Exclusion}", fate.FateId, item.Aetheryte.Name, item.Aetheryte.AetheryteId, item.Aetheryte.Position, item.DistanceToFate, item.Aetheryte.GilCost, item.WithinCostLimit, item.Selected, item.ExclusionReason ?? "none");
            }
        }

        return result;
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

    private AetheryteSummary? SelectBestAetheryte(ZoneInfo zone, Vector3 fateCenter, out IReadOnlyList<AetheryteTeleportDiagnostic> diagnostics)
    {
        var candidates = zone.UnlockedAetherytes;
        if (candidates.Count == 0)
        {
            diagnostics = Array.Empty<AetheryteTeleportDiagnostic>();
            return null;
        }

        var maxCost = configuration.TeleportMaxGilCost;
        var analysis = candidates.Select(a =>
        {
            var distance = HorizontalDistance(a.Position, fateCenter);
            var withinCost = maxCost == 0 || a.GilCost <= maxCost;
            var hasPosition = a.HasPosition && a.Position != Vector3.Zero;
            return new AetheryteTeleportDiagnostic(
                a,
                distance,
                Math.Max(0, distance - Math.Max(0, configuration.TeleportPostTeleportRunBufferYalms)),
                Math.Max(0, distance - Math.Max(0, configuration.TeleportPostTeleportRunBufferYalms)) / GetRunSpeed(),
                withinCost && hasPosition,
                false,
                !hasPosition ? "position unavailable" : withinCost ? null : $"cost>{maxCost}");
        }).ToList();

        diagnostics = analysis;
        var affordable = candidates
            .Where(a => a.HasPosition && a.Position != Vector3.Zero && (maxCost == 0 || a.GilCost <= maxCost))
            .ToList();

        if (affordable.Count == 0)
        {
            return null;
        }

        var selected = affordable
            .OrderBy(a => HorizontalDistance(a.Position, fateCenter))
            .First();

        diagnostics = analysis.Select(a => a with { Selected = a.Aetheryte.AetheryteId == selected.AetheryteId }).ToList();
        return selected;
    }

    private static float HorizontalDistance(Vector3 from, Vector3 to)
    {
        var dx = from.X - to.X;
        var dz = from.Z - to.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private TeleportAnalysis BuildAnalysis(
        ActiveFate fate,
        ZoneInfo? zone,
        string reason,
        IReadOnlyList<AetheryteTeleportDiagnostic> aetherytes)
    {
        var recommendation = BuildRecommendation(false, null, reason, fate.DistanceFromPlayer, fate.DistanceFromPlayer, fate.DistanceFromPlayer, 0, 0, 0, false, null, 0);
        return new TeleportAnalysis(
            zone,
            recommendation,
            GetRunSpeed(),
            Math.Max(0, configuration.TeleportPenaltySeconds),
            Math.Max(0, configuration.TeleportPostTeleportRunBufferYalms),
            0,
            Math.Max(0, configuration.TeleportAdvantageThresholdSeconds),
            reason,
            aetherytes);
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
