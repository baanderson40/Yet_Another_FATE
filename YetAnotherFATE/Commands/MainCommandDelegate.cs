using System;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Modules.Ux;

namespace YetAnotherFATE.Commands;

public sealed class MainCommandDelegate : IMainCommandDelegate
{
    private readonly UxModule uxModule;
    private readonly IFateDebugReporter fateDebugReporter;
    private readonly IPluginLog log;

    public MainCommandDelegate(UxModule uxModule, IFateDebugReporter fateDebugReporter, IPluginLog log)
    {
        this.uxModule = uxModule;
        this.fateDebugReporter = fateDebugReporter;
        this.log = log;
    }

    public void Execute(string command, string args)
    {
        var trimmed = args?.Trim();
        log.Debug("MainCommandDelegate: command invoked with args='{Args}'", trimmed ?? string.Empty);

        if (string.IsNullOrEmpty(trimmed))
        {
            uxModule.ToggleMain();
            return;
        }

        if (string.Equals(trimmed, "config", StringComparison.OrdinalIgnoreCase))
        {
            uxModule.ToggleConfig();
            return;
        }

        if (string.Equals(trimmed, "fates", StringComparison.OrdinalIgnoreCase))
        {
            fateDebugReporter.PrintActiveFates();
            return;
        }

        uxModule.ToggleMain();
    }
}
