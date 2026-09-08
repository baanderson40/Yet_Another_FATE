using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Services.Travel;
using YetAnotherFATE.Data.Travel;

namespace YetAnotherFATE.Services.Fates;

public sealed class FateSelector : IFateSelector
{
    private readonly IPlayerState playerState;
    private readonly ITeleportAdvisor teleportAdvisor;
    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private DateTimeOffset nextDiagnosticLogAt = DateTimeOffset.MinValue;

    private const double DistanceWeight = 0.5;
    private const double LevelWeight = 20.0;

    public FateSelector(IPlayerState playerState, ITeleportAdvisor teleportAdvisor, Configuration configuration, IPluginLog log)
    {
        this.playerState = playerState;
        this.teleportAdvisor = teleportAdvisor;
        this.configuration = configuration;
        this.log = log;
    }

    public FateSelection? SelectBest(IReadOnlyList<ActiveFate> activeFates)
        => Evaluate(activeFates).Selected;

    public FateSelectionDiagnostics Evaluate(IReadOnlyList<ActiveFate> activeFates)
    {
        if (activeFates == null || activeFates.Count == 0)
        {
            return new FateSelectionDiagnostics(DateTimeOffset.UtcNow, Array.Empty<FateCandidateDiagnostic>(), null);
        }

        double? bestScore = null;
        FateSelection? best = null;
        var diagnostics = new List<FateCandidateDiagnostic>(activeFates.Count);

        foreach (var fate in activeFates)
        {
            var teleport = teleportAdvisor.Analyze(fate);
            if (configuration.FateBlacklist?.Contains(fate.FateId) == true)
            {
                diagnostics.Add(new FateCandidateDiagnostic(fate, false, "blacklisted", null, teleport));
                continue;
            }

            if (fate.State != Dalamud.Game.ClientState.Fates.FateState.Running)
            {
                if (!fate.RequiresActivation || fate.MotivationNpc == 0 || fate.MotivationNpc == 0xE0000000)
                {
                    var reason = fate.RequiresActivation
                        ? "preparing; motivation NPC id unavailable"
                        : $"FATE state is {fate.State}";
                    diagnostics.Add(new FateCandidateDiagnostic(fate, false, reason, null, teleport));
                    continue;
                }
            }

            if (fate.State == Dalamud.Game.ClientState.Fates.FateState.Running && fate.TimeRemaining.TotalSeconds <= 20)
            {
                diagnostics.Add(new FateCandidateDiagnostic(fate, false, "20 seconds or less remaining", null, teleport));
                continue;
            }

            var reasons = new List<string>();
            var breakdown = ComputeScore(fate, teleport.Recommendation, reasons);
            diagnostics.Add(new FateCandidateDiagnostic(fate, true, null, breakdown, teleport));

            if (bestScore == null || breakdown.FinalScore > bestScore)
            {
                bestScore = breakdown.FinalScore;
                best = new FateSelection(
                    fate,
                    fate.Center,
                    teleport.Recommendation.ShouldTeleport,
                    teleport.Recommendation.AetheryteId,
                    breakdown.FinalScore,
                    string.Join(", ", reasons),
                    teleport.Recommendation);
            }
        }

        var result = new FateSelectionDiagnostics(DateTimeOffset.UtcNow, diagnostics, best);
        if (configuration.DiagnosticLoggingEnabled && DateTimeOffset.UtcNow >= nextDiagnosticLogAt)
        {
            nextDiagnosticLogAt = DateTimeOffset.UtcNow.AddSeconds(10);
            log.Debug("FateSelector: evaluated {Count} candidates; selected={Selected}", diagnostics.Count, best?.Fate.FateId.ToString() ?? "none");
            foreach (var candidate in diagnostics)
            {
                log.Debug(
                    "FateSelector: fate #{FateId} eligible={Eligible} score={Score} reason={Reason} teleport={Teleport} aetheryte={Aetheryte}",
                    candidate.Fate.FateId,
                    candidate.Eligible,
                    candidate.Score?.FinalScore.ToString("F1") ?? "n/a",
                    candidate.ExclusionReason ?? candidate.Score?.ToString() ?? "n/a",
                    candidate.Teleport.Recommendation.ShouldTeleport,
                    candidate.Teleport.Recommendation.AetheryteName ?? "none");
            }
        }

        return result;
    }

    private SelectionScoreBreakdown ComputeScore(ActiveFate fate, TeleportRecommendation teleport, List<string> reasons)
    {
        const double baseScore = 1000;
        var score = baseScore;

        var distancePenalty = fate.DistanceFromPlayer * DistanceWeight;
        score -= distancePenalty;
        reasons.Add($"dist-{fate.DistanceFromPlayer:F0}");

        var seconds = fate.TimeRemaining.TotalSeconds;
        var timeAdjustment = 0.0;
        var activationPenalty = 0.0;
        if (fate.RequiresActivation)
        {
            activationPenalty = 25;
            score -= activationPenalty;
            reasons.Add("activation");
        }
        else if (seconds < 60)
        {
            score -= 250;
            timeAdjustment = -250;
            reasons.Add("time<60s");
        }
        else if (seconds < 180)
        {
            score -= 75;
            timeAdjustment = -75;
            reasons.Add("time<3m");
        }
        else
        {
            score += 25;
            timeAdjustment = 25;
        }

        var levelDiff = Math.Abs(fate.Level - playerState.EffectiveLevel);
        var levelPenalty = levelDiff * LevelWeight;
        score -= levelPenalty;
        reasons.Add($"lvl∆{levelDiff}");

        if (teleport.ShouldTeleport)
        {
            score += 80;
            reasons.Add($"teleport:{teleport.Reason}");
        }
        else
        {
            score -= 15;
            reasons.Add($"run:{teleport.Reason}");
        }

        var travelAdjustment = teleport.ShouldTeleport ? 80 : -15;

        var continuationBonus = 0.0;
        if (fate.IsContinuation)
        {
            score += 40;
            continuationBonus = 40;
            reasons.Add("chain");
        }

        return new SelectionScoreBreakdown(
            baseScore,
            distancePenalty,
            timeAdjustment,
            activationPenalty,
            levelDiff,
            levelPenalty,
            travelAdjustment,
            continuationBonus,
            score);
    }
}
