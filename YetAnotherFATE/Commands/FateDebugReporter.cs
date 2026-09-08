using System;
using System.Globalization;
using System.Linq;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Services.Fates;

namespace YetAnotherFATE.Commands;

public sealed class FateDebugReporter : IFateDebugReporter
{
    private readonly IFateRepository fateRepository;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private const int MaxEntriesToDisplay = 5;

    public FateDebugReporter(IFateRepository fateRepository, IChatGui chatGui, IPluginLog log)
    {
        this.fateRepository = fateRepository;
        this.chatGui = chatGui;
        this.log = log;
    }

    public void PrintActiveFates()
    {
        var fates = fateRepository.EnumerateActiveFates();

        if (fates.Count == 0)
        {
            chatGui.Print("[YAFate] No active fates detected.");
            log.Debug("FateDebugReporter: No fates to display.");
            return;
        }

        chatGui.Print($"[YAFate] Active fates: {fates.Count}");
        log.Debug("FateDebugReporter: Displaying {Count} fates", fates.Count);

        var ordered = fates
            .OrderBy(f => f.DistanceFromPlayer)
            .ThenBy(f => f.TimeRemaining)
            .Take(MaxEntriesToDisplay)
            .ToList();

        foreach (var fate in ordered)
        {
            chatGui.Print(FormatFateSummary(fate));
            log.Debug("FateDebugReporter: fate {FateId} {Name} distance={Distance:F0}y", fate.FateId, fate.Name, fate.DistanceFromPlayer);
        }

        if (fates.Count > ordered.Count)
        {
            chatGui.Print($"[YAFate] …and {fates.Count - ordered.Count} more.");
        }
    }

    private static string FormatFateSummary(ActiveFate fate)
    {
        var distance = fate.DistanceFromPlayer.ToString("F0", CultureInfo.InvariantCulture);
        var remaining = fate.TimeRemaining.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        return $"[YAFate] #{fate.FateId} {fate.Name} [{fate.State}] @ {distance}y — {remaining} remaining, progress={fate.Progress}%";
    }
}
