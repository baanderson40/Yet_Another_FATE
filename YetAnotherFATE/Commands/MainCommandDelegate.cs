using System;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Modules.Ux;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Movement;
using YetAnotherFATE.Services.Travel;
using YetAnotherFATE.Data.Travel;

namespace YetAnotherFATE.Commands;

public sealed class MainCommandDelegate : IMainCommandDelegate
{
    private readonly UxModule uxModule;
    private readonly IFateDebugReporter fateDebugReporter;
    private readonly IFateRepository fateRepository;
    private readonly IFateSelector fateSelector;
    private readonly ITeleportExecutor teleportExecutor;
    private readonly INavmeshClient navmeshClient;
    private readonly ITravelController travelController;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly IFateRewardTracker rewardTracker;
    private readonly ZoneRotationController zoneRotation;

    public MainCommandDelegate(
        UxModule uxModule,
        IFateDebugReporter fateDebugReporter,
        IFateRepository fateRepository,
        IFateSelector fateSelector,
        ITeleportExecutor teleportExecutor,
        INavmeshClient navmeshClient,
        ITravelController travelController,
        IChatGui chatGui,
        IFateRewardTracker rewardTracker,
        ZoneRotationController zoneRotation,
        IPluginLog log)
    {
        this.uxModule = uxModule;
        this.fateDebugReporter = fateDebugReporter;
        this.fateRepository = fateRepository;
        this.fateSelector = fateSelector;
        this.teleportExecutor = teleportExecutor;
        this.navmeshClient = navmeshClient;
        this.travelController = travelController;
        this.chatGui = chatGui;
        this.rewardTracker = rewardTracker;
        this.zoneRotation = zoneRotation;
        this.log = log;
    }

    public void Execute(string command, string args)
    {
        var trimmed = args?.Trim();
        log.Debug("MainCommandDelegate: command invoked with args='{Args}'", trimmed ?? string.Empty);

        string? commandArg = null;
        if (!string.IsNullOrEmpty(trimmed))
        {
            var split = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (split.Length > 1)
            {
                commandArg = split[1];
            }

            trimmed = split[0];
        }

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

        if (string.Equals(trimmed, "debug", StringComparison.OrdinalIgnoreCase))
        {
            uxModule.ToggleDebug();
            return;
        }

        if (string.Equals(trimmed, "fates", StringComparison.OrdinalIgnoreCase))
        {
            fateDebugReporter.PrintActiveFates();
            return;
        }

        if (string.Equals(trimmed, "teleport", StringComparison.OrdinalIgnoreCase))
        {
            HandleTeleportCommand();
            return;
        }

        if (string.Equals(trimmed, "move", StringComparison.OrdinalIgnoreCase))
        {
            HandleMoveCommand(string.Equals(commandArg, "run", StringComparison.OrdinalIgnoreCase));
            return;
        }

        if (string.Equals(trimmed, "run", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(commandArg, out var count) || count < 1)
            {
                chatGui.PrintError("Usage: /yafate run <count>");
                return;
            }

            rewardTracker.Start(count);
            chatGui.Print($"YAFate: running until {count} FATE rewards");
            return;
        }

        if (string.Equals(trimmed, "stop", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(commandArg, "safe", StringComparison.OrdinalIgnoreCase))
            {
                rewardTracker.RequestStopWhenSafe();
                chatGui.Print("YAFate: stop requested; waiting for FATE and combat cleanup");
                return;
            }
            rewardTracker.Stop();
            chatGui.Print("YAFate: stopped");
            return;
        }

        uxModule.ToggleMain();
    }

    private void HandleTeleportCommand()
    {
        var fates = fateRepository.EnumerateActiveFates();
        if (fates.Count == 0)
        {
            chatGui.PrintError("Teleport debug: no active FATEs detected");
            return;
        }

        var selection = fateSelector.SelectBest(fates);
        if (selection == null)
        {
            chatGui.PrintError("Teleport debug: unable to find a suitable fate");
            return;
        }

        var chosen = selection.Value;
        if (!chosen.ShouldTeleport || chosen.Teleport.AetheryteId is null)
        {
            chatGui.PrintError($"Teleport debug: best fate {chosen.Fate.Name} selected run mode ({chosen.Teleport.Reason})");
            return;
        }

        zoneRotation.Suspend("Debug teleport requested");
        travelController.Cancel("Debug teleport requested", TravelRequestKind.Automatic);
        var result = teleportExecutor.RequestTeleport(chosen.Teleport);
        chatGui.Print($"Teleport debug: {result} targeting {chosen.Teleport.AetheryteName ?? chosen.Teleport.AetheryteId.Value.ToString()}");
    }

    private void HandleMoveCommand(bool forceRun)
    {
        if (!navmeshClient.IsAvailable)
        {
            chatGui.PrintError("Navmesh debug: vnavmesh not installed");
            return;
        }

        log.Debug("Navmesh debug: available={Available}", navmeshClient.IsAvailable);
        if (!navmeshClient.EnsureReady())
        {
            chatGui.PrintError($"Navmesh debug: navmesh not ready ({navmeshClient.Status})");
            return;
        }

        var fates = fateRepository.EnumerateActiveFates();
        if (fates.Count == 0)
        {
            chatGui.PrintError("Navmesh debug: no active FATEs detected");
            return;
        }

        var selection = fateSelector.SelectBest(fates);
        if (selection == null)
        {
            chatGui.PrintError("Navmesh debug: unable to find a suitable fate");
            return;
        }

        var chosen = selection.Value;
        if (forceRun && chosen.ShouldTeleport)
        {
            chatGui.Print($"Navmesh debug: overriding teleport for {chosen.Fate.Name}, forcing run/navmesh");
            chosen = new FateSelection(
                chosen.Fate,
                chosen.Destination,
                false,
                chosen.PreferredAetheryteId,
                chosen.Score,
                chosen.Reason + ", forced-run",
                chosen.Teleport);
        }

        var kind = forceRun ? TravelRequestKind.DebugForceRun : TravelRequestKind.Automatic;
        zoneRotation.Suspend("Debug movement requested");
        travelController.RequestTravel(chosen, kind);
        chatGui.Print($"Navmesh debug: issued move toward {chosen.Fate.Name} (mode={kind})");
    }

}
