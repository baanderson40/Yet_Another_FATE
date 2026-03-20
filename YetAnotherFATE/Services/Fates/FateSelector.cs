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
    {
        if (activeFates == null || activeFates.Count == 0)
        {
            return null;
        }

        double? bestScore = null;
        FateSelection? best = null;

        foreach (var fate in activeFates)
        {
            if (configuration.FateBlacklist?.Contains(fate.FateId) == true)
            {
                continue;
            }

            if (fate.TimeRemaining.TotalSeconds <= 20)
            {
                continue;
            }

            var teleport = teleportAdvisor.Evaluate(fate);
            var reasons = new List<string>();
            var score = ComputeScore(fate, teleport, reasons);

            if (bestScore == null || score > bestScore)
            {
                bestScore = score;
                best = new FateSelection(
                    fate,
                    fate.Center,
                    teleport.ShouldTeleport,
                    teleport.AetheryteId,
                    score,
                    string.Join(", ", reasons),
                    teleport);
            }
        }

        return best;
    }

    private double ComputeScore(ActiveFate fate, TeleportRecommendation teleport, List<string> reasons)
    {
        double score = 1000;

        var distancePenalty = fate.DistanceFromPlayer * DistanceWeight;
        score -= distancePenalty;
        reasons.Add($"dist-{fate.DistanceFromPlayer:F0}");

        var seconds = fate.TimeRemaining.TotalSeconds;
        if (seconds < 60)
        {
            score -= 250;
            reasons.Add("time<60s");
        }
        else if (seconds < 180)
        {
            score -= 75;
            reasons.Add("time<3m");
        }
        else
        {
            score += 25;
        }

        var levelDiff = Math.Abs(fate.Level - playerState.EffectiveLevel);
        score -= levelDiff * LevelWeight;
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

        if (fate.IsContinuation)
        {
            score += 40;
            reasons.Add("chain");
        }

        return score;
    }
}
