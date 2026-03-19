using System;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace YetAnotherFATE.Commands;

public sealed class MainCommand(ICommandManager commandManager, IPluginLog logger, IMainCommandDelegate commandDelegate)
    : IMainCommand
{
    private readonly ICommandManager commandManager = commandManager;
    private readonly IPluginLog logger = logger;
    private readonly IMainCommandDelegate commandDelegate = commandDelegate;
    private bool registered;

    private const string CommandName = "/yafate";

    public void Register()
    {
        if (registered)
        {
            return;
        }

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Yet Another FATE UI (/yafate config for settings)"
        });

        registered = true;
        logger.Information("Registered /yafate command");
    }

    private void OnCommand(string command, string args)
    {
        commandDelegate.Execute(command, args);
    }

    public void Dispose()
    {
        if (!registered)
        {
            return;
        }

        commandManager.RemoveHandler(CommandName);
        registered = false;
        logger.Information("Unregistered /yafate command");
    }
}
