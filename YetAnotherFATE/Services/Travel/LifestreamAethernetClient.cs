using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace YetAnotherFATE.Services.Travel;

// Verified against Lifestream/IPC/IPCProvider.cs. These are local-network primitives,
// not a general territory/position router. Zero selects the dedicated Firmament endpoint.
public sealed class LifestreamAethernetClient(IDalamudPluginInterface plugin, IPluginLog log) : ICityAethernetClient
{
    private DateTimeOffset nextLogAt;

    public CityAethernetState State
    {
        get
        {
            try
            {
                var busy = plugin.GetIpcSubscriber<bool>("Lifestream.IsBusy");
                if (!busy.HasFunction || !plugin.GetIpcSubscriber<object>("Lifestream.Abort").HasAction ||
                    !plugin.GetIpcSubscriber<uint>("Lifestream.GetActiveAetheryte").HasFunction)
                    return CityAethernetState.Unavailable;
                return busy.InvokeFunc() ? CityAethernetState.Busy : CityAethernetState.Idle;
            }
            catch (Exception ex) { Unavailable(ex); return CityAethernetState.Unavailable; }
        }
    }

    public uint ActiveAetheryte
    {
        get
        {
            try { return plugin.GetIpcSubscriber<uint>("Lifestream.GetActiveAetheryte").InvokeFunc(); }
            catch (Exception ex) { Unavailable(ex); return 0; }
        }
    }

    public bool CanRoute(uint destinationAethernetId)
    {
        try
        {
            return destinationAethernetId == 0
                ? plugin.GetIpcSubscriber<bool>("Lifestream.AethernetTeleportToFirmament").HasFunction
                : plugin.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportById").HasFunction;
        }
        catch (Exception ex) { Unavailable(ex); return false; }
    }

    public bool Request(uint destinationAethernetId)
    {
        // Reject busy providers; never append to another caller's queue.
        if (State != CityAethernetState.Idle) return false;
        // Let invocation exceptions propagate to the route: a throwing provider may have queued work.
        return destinationAethernetId == 0
            ? plugin.GetIpcSubscriber<bool>("Lifestream.AethernetTeleportToFirmament").InvokeFunc()
            : plugin.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportById").InvokeFunc(destinationAethernetId);
    }

    public bool Abort()
    {
        try
        {
            plugin.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction();
            return true;
        }
        catch (Exception ex) { Unavailable(ex); return false; }
    }

    private void Unavailable(Exception ex)
    {
        if (DateTimeOffset.UtcNow < nextLogAt) return;
        nextLogAt = DateTimeOffset.UtcNow.AddMinutes(1);
        log.Debug(ex, "Death return: optional Lifestream aethernet IPC unavailable");
    }
}
