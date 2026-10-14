using System;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace YetAnotherFATE.Services.Retainers;

public sealed class WahToolsFeeshClient : IWahToolsFeeshClient
{
    private readonly ICallGateSubscriber<bool, bool> autoOcean;
    private readonly ICallGateSubscriber<bool> catchTheBoat;
    private readonly ICallGateSubscriber<bool> stop;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly WahToolsFeeshStatusReader statusReader;
    private readonly IPluginLog log;

    public WahToolsFeeshClient(IDalamudPluginInterface pluginInterface, WahToolsFeeshStatusReader statusReader, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.statusReader = statusReader;
        autoOcean = pluginInterface.GetIpcSubscriber<bool, bool>("WahTools.Feesh.AutoOcean");
        catchTheBoat = pluginInterface.GetIpcSubscriber<bool>("WahTools.Feesh.CatchTheBoat");
        stop = pluginInterface.GetIpcSubscriber<bool>("WahTools.Feesh.Stop");
        this.log = log;
    }

    public bool IsAvailable => pluginInterface.InstalledPlugins.Any(p =>
        string.Equals(p.InternalName, "WahTools", StringComparison.OrdinalIgnoreCase) && p.IsLoaded);

    public WahToolsFeeshStatus ReadStatus() => statusReader.Read();

    public FeeshRequestResult DisableAutoOcean(out string error) => Invoke(autoOcean, false, "AutoOcean(false)", out error);

    public FeeshRequestResult CatchTheBoat(out string error)
    {
        try
        {
            var accepted = catchTheBoat.InvokeFunc();
            error = accepted ? string.Empty : "WahTools refused CatchTheBoat";
            log.Information("WahTools Feesh CatchTheBoat accepted={Accepted}", accepted);
            return accepted ? FeeshRequestResult.Accepted : FeeshRequestResult.Refused;
        }
        catch (Exception ex)
        {
            error = "WahTools CatchTheBoat IPC failed; launch result is ambiguous";
            log.Warning(ex, "WahTools CatchTheBoat failed; observe state without retry");
            return FeeshRequestResult.Unavailable;
        }
    }

    public FeeshRequestResult Stop(out string error) => Invoke(stop, "Stop", out error);

    private FeeshRequestResult Invoke(ICallGateSubscriber<bool, bool> ipc, bool argument, string name, out string error)
    {
        try
        {
            var accepted = ipc.InvokeFunc(argument);
            error = accepted ? string.Empty : $"WahTools refused {name}";
            log.Information("WahTools Feesh {Ipc} accepted={Accepted}", name, accepted);
            return accepted ? FeeshRequestResult.Accepted : FeeshRequestResult.Refused;
        }
        catch (Exception ex)
        {
            error = $"WahTools {name} IPC unavailable";
            log.Warning(ex, "WahTools Feesh {Ipc} failed", name);
            return FeeshRequestResult.Unavailable;
        }
    }

    private FeeshRequestResult Invoke(ICallGateSubscriber<bool> ipc, string name, out string error)
    {
        try
        {
            var accepted = ipc.InvokeFunc();
            error = accepted ? string.Empty : $"WahTools refused {name}";
            log.Information("WahTools Feesh {Ipc} accepted={Accepted}", name, accepted);
            return accepted ? FeeshRequestResult.Accepted : FeeshRequestResult.Refused;
        }
        catch (Exception ex)
        {
            error = $"WahTools {name} IPC unavailable";
            log.Warning(ex, "WahTools Feesh {Ipc} failed", name);
            return FeeshRequestResult.Unavailable;
        }
    }
}
