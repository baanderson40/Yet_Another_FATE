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
    private readonly IObjectTable objectTable;
    private readonly ITeleportAdvisor teleportAdvisor;
    private readonly Configuration configuration;
    private readonly IFateFollowUpTracker followUpTracker;
    private readonly IPluginLog log;
    private DateTimeOffset nextDiagnosticLogAt = DateTimeOffset.MinValue;

    private const double DistanceWeight = 0.5;
    private const double LevelWeight = 20.0;

    public FateSelector(IPlayerState playerState, IDataManager dataManager, IObjectTable objectTable, ITeleportAdvisor teleportAdvisor, Configuration configuration, IFateFollowUpTracker followUpTracker, IPluginLog log)
    {
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.teleportAdvisor = teleportAdvisor;
        this.configuration = configuration;
        this.followUpTracker = followUpTracker;
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

        FateSelection? best = null;
        var diagnostics = new List<FateCandidateDiagnostic>(activeFates.Count);

        foreach (var fate in activeFates)
        {
            var teleport = teleportAdvisor.Analyze(fate);
            if (fate.IsPending)
            {
                diagnostics.Add(new FateCandidateDiagnostic(fate, false, "pending; not on map or position unavailable", null, teleport));
                continue;
            }

            if (configuration.FateBlacklist?.Contains(fate.FateId) == true)
            {
                diagnostics.Add(new FateCandidateDiagnostic(fate, false, "blacklisted", null, teleport));
                continue;
            }

            if (configuration.BlacklistedFateRules?.Contains(fate.FateRule) == true)
            {
                diagnostics.Add(new FateCandidateDiagnostic(fate, false, $"rule {fate.FateRule} blacklisted", null, teleport));
                continue;
            }

            if (fate.Level < configuration.FateMinLevel || fate.Level > configuration.FateMaxLevel)
            {
                diagnostics.Add(new FateCandidateDiagnostic(fate, false, $"level {fate.Level} outside configured range", null, teleport));
                continue;
            }

            if (fate.Progress >= 100 || fate.Progress > configuration.FateMaxProgress)
            {
                diagnostics.Add(new FateCandidateDiagnostic(fate, false, $"progress {fate.Progress}% exceeds configured maximum", null, teleport));
                continue;
            }

            if (fate.Duration > configuration.FateMaxDurationSeconds)
            {
                diagnostics.Add(new FateCandidateDiagnostic(fate, false, $"duration {fate.Duration}s exceeds configured maximum", null, teleport));
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

            if (fate.TimeRemaining.TotalSeconds >= 0 && fate.TimeRemaining.TotalSeconds <= configuration.FateMinTimeRemainingSeconds)
            {
                diagnostics.Add(new FateCandidateDiagnostic(fate, false, $"{configuration.FateMinTimeRemainingSeconds} seconds or less remaining", null, teleport));
                continue;
            }

            var reasons = new List<string>();
            var breakdown = ComputeScore(fate, teleport.Recommendation, reasons);
            diagnostics.Add(new FateCandidateDiagnostic(fate, true, null, breakdown, teleport));

            if (best == null || ComparePriority(fate, best.Value.Fate) < 0)
            {
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

        // Stable ordering is shared by automation and the UI. Scores are diagnostics,
        // never an implicit extra criterion after the configured order.
        var ordered = diagnostics.OrderBy(candidate => candidate, Comparer<FateCandidateDiagnostic>.Create((left, right) =>
        {
            var eligibility = right.Eligible.CompareTo(left.Eligible);
            return eligibility != 0 ? eligibility : ComparePriority(left.Fate, right.Fate, left.Eligible);
        })).ToArray();
        var result = new FateSelectionDiagnostics(DateTimeOffset.UtcNow, ordered, best);
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

        var priorityAdjustment = CalculatePriorityAdjustment(fate);
        score += priorityAdjustment;

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
        else if (seconds is >= 0 and < 60)
        {
            score -= 250;
            timeAdjustment = -250;
            reasons.Add("time<60s");
        }
        else if (seconds is >= 0 and < 180)
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
        if (followUpTracker.IsPreferred(fate))
        {
            continuationBonus = 150;
            score += continuationBonus;
            reasons.Add("verified follow-up priority");
        }

        return new SelectionScoreBreakdown(
            baseScore,
            priorityAdjustment,
            distancePenalty,
            timeAdjustment,
            activationPenalty,
            levelDiff,
            levelPenalty,
            travelAdjustment,
            continuationBonus,
            score);
    }

    private double CalculatePriorityAdjustment(ActiveFate fate)
    {
        var adjustment = 0.0;
        var order = configuration.FateSortOrder ?? new List<FateSortOrder>();
        for (var index = 0; index < order.Count; index++)
        {
            var multiplier = 100.0 / (index + 1);
            var value = order[index].Criteria switch
            {
                FateSortCriteria.HasBonusWithTwist => fate.HasBonus && HasTwistOfFate() ? 1.0 : 0.0,
                FateSortCriteria.HasBonus => fate.HasBonus ? 1.0 : 0.0,
                FateSortCriteria.Progress => fate.Progress / 100.0,
                FateSortCriteria.TimeRemainingUrgent => IsUrgent(fate) ? 1.0 : 0.0,
                FateSortCriteria.TimeRemaining => (240.0 - GetSortableTime(fate)) / 240.0,
                FateSortCriteria.Distance => Math.Clamp(1.0 - fate.DistanceFromPlayer / 2000.0, -1.0, 1.0),
                FateSortCriteria.Level => Math.Clamp(fate.Level / 100.0, 0.0, 1.0),
                FateSortCriteria.Name => 0.0,
                _ => 0.0,
            };

            adjustment += order[index].Descending ? value * multiplier : -value * multiplier;
        }

        return adjustment;
    }

    private int ComparePriority(ActiveFate candidate, ActiveFate current, bool eligible = true)
    {
        if (eligible)
        {
            var candidateFollowUp = followUpTracker.IsPreferred(candidate);
            var currentFollowUp = followUpTracker.IsPreferred(current);
            var followUpComparison = currentFollowUp.CompareTo(candidateFollowUp);
            if (followUpComparison != 0)
            {
                return followUpComparison;
            }

            if (candidateFollowUp && currentFollowUp)
            {
                return candidate.DistanceFromPlayer.CompareTo(current.DistanceFromPlayer);
            }
        }

        foreach (var order in configuration.FateSortOrder ?? new List<FateSortOrder>())
        {
            var comparison = CompareCriterion(order.Criteria, candidate, current);
            if (comparison == 0)
            {
                continue;
            }

            return order.Descending ? -comparison : comparison;
        }

        return candidate.FateId.CompareTo(current.FateId);
    }

    private int CompareCriterion(FateSortCriteria criteria, ActiveFate left, ActiveFate right)
    {
        return criteria switch
        {
            FateSortCriteria.HasBonusWithTwist => CompareBool(left.HasBonus && HasTwistOfFate(), right.HasBonus && HasTwistOfFate()),
            FateSortCriteria.HasBonus => CompareBool(left.HasBonus, right.HasBonus),
            FateSortCriteria.Progress => left.Progress.CompareTo(right.Progress),
            FateSortCriteria.TimeRemainingUrgent => CompareBool(IsUrgent(left), IsUrgent(right)),
            FateSortCriteria.TimeRemaining => GetSortableTime(left).CompareTo(GetSortableTime(right)),
            FateSortCriteria.Distance => left.DistanceFromPlayer.CompareTo(right.DistanceFromPlayer),
            FateSortCriteria.Level => left.Level.CompareTo(right.Level),
            FateSortCriteria.Name => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase),
            _ => 0,
        };
    }

    private static int CompareBool(bool left, bool right) => left == right ? 0 : left ? 1 : -1;

    private static bool IsUrgent(ActiveFate fate)
        => fate.TimeRemaining.TotalSeconds is >= 0 and < 240;

    private static double GetSortableTime(ActiveFate fate)
        => fate.TimeRemaining.TotalSeconds is >= 0 and < 240 ? fate.TimeRemaining.TotalSeconds : 240;

    private bool HasTwistOfFate()
        => objectTable.LocalPlayer?.StatusList.Any(status => status.StatusId is 1288 or 1289) == true;
}
