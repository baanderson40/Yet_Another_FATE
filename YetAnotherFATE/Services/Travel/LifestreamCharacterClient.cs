using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using ECommons;

namespace YetAnotherFATE.Services.Travel;

public sealed class LifestreamCharacterClient : ILifestreamCharacterClient
{
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<string, string, ErrorCode> changeCharacter;
    private readonly IPluginLog log;

    public LifestreamCharacterClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        changeCharacter = pluginInterface.GetIpcSubscriber<string, string, ErrorCode>("Lifestream.ChangeCharacter");
        this.log = log;
    }

    public bool TryIsBusy(out bool busy)
    {
        try
        {
            busy = isBusy.InvokeFunc();
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Lifestream character IPC unavailable");
            busy = false;
            return false;
        }
    }

    public bool TryChangeCharacter(string name, string world, out string error)
    {
        try
        {
            var result = changeCharacter.InvokeFunc(name, world);
            error = result == ErrorCode.Success ? string.Empty : result.ToString();
            return result == ErrorCode.Success;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not return to original character through Lifestream");
            error = "Lifestream IPC unavailable";
            return false;
        }
    }
}
