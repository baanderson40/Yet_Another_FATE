using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace YetAnotherFATE.Services.Movement;

public sealed class NavmeshClient : INavmeshClient
{
    private const string PluginName = "vnavmesh";
    private const string PathPrefix = "Nav";
    private const string PathSimpleMove = "SimpleMove";
    private const string PathPath = "Path";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private readonly bool isInstalled;
    private string status = "navmesh not initialized";

    private ICallGateSubscriber<bool>? isReady;
    private ICallGateSubscriber<bool>? reload;
    private ICallGateSubscriber<Vector3, bool, bool>? simpleMove;
    private ICallGateSubscriber<bool>? stop;

    public NavmeshClient(IDalamudPluginInterface pluginInterface, IFramework framework, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.log = log;
        isInstalled = pluginInterface.InstalledPlugins.Any(p => p.Name == PluginName && p.IsLoaded);
        status = isInstalled ? "navmesh detected" : "vnavmesh not installed";

        if (isInstalled)
        {
            InitializeSubscribers();
        }
    }

    public bool IsAvailable => isInstalled;
    public string Status => status;

    private void InitializeSubscribers()
    {
        try
        {
            isReady = pluginInterface.GetIpcSubscriber<bool>($"{PluginName}.{PathPrefix}.IsReady");
            reload = pluginInterface.GetIpcSubscriber<bool>($"{PluginName}.{PathPrefix}.Reload");
            simpleMove = pluginInterface.GetIpcSubscriber<Vector3, bool, bool>($"{PluginName}.{PathSimpleMove}.PathfindAndMoveTo");
            stop = pluginInterface.GetIpcSubscriber<bool>($"{PluginName}.{PathPath}.Stop");
            status = "navmesh IPC connected";
        }
        catch (Exception ex)
        {
            status = $"navmesh IPC init failed: {ex.Message}";
            log.Error(ex, "NavmeshClient: failed to bind IPC");
        }
    }

    public bool EnsureReady()
    {
        if (!IsAvailable || isReady == null)
        {
            status = "navmesh IPC missing";
            return false;
        }

        try
        {
            if (isReady.InvokeFunc())
            {
                status = "navmesh ready";
                return true;
            }
            status = "navmesh reports not ready";
        }
        catch (IpcNotReadyError ex)
        {
            status = "vnavmesh IPC not registered (plugin disabled?)";
            log.Debug(ex, "NavmeshClient: ready IPC not registered");
            return false;
        }
        catch (Exception ex)
        {
            status = $"ready check failed: {ex.Message}";
            log.Debug(ex, "NavmeshClient: ready check failed");
        }

        if (!reloadAttempted && reload != null)
        {
            reloadAttempted = true;
            try
            {
                reload.InvokeFunc();
                status = "navmesh reload requested";
            }
            catch (IpcNotReadyError ex)
            {
                status = "vnavmesh IPC not registered (plugin disabled?)";
                log.Debug(ex, "NavmeshClient: reload IPC not registered");
                return false;
            }
            catch (Exception ex)
            {
                status = $"navmesh reload failed: {ex.Message}";
                log.Debug(ex, "NavmeshClient: reload failed");
            }
        }

        return false;
    }

    public bool SimpleMoveTo(Vector3 destination, bool useMesh = true)
    {
        if (!IsAvailable || simpleMove == null)
        {
            status = "navmesh plugin unavailable";
            return false;
        }

        if (!EnsureReady())
        {
            return false;
        }

            try
            {
                var result = false;
                framework.RunOnFrameworkThread(() =>
                {
                    try
                    {
                        result = simpleMove.InvokeFunc(destination, useMesh);
                        status = result ? "navmesh move issued" : "navmesh move rejected";
                    }
                    catch (Exception ex)
                    {
                        status = $"navmesh move failed: {ex.Message}";
                        log.Debug(ex, "NavmeshClient: move failed inside framework thread");
                    }
                }).Wait();

                return result;
            }
        catch (AggregateException agg) when (agg.InnerException is IpcNotReadyError ipcEx)
        {
            status = "vnavmesh IPC not registered (plugin disabled?)";
            log.Debug(ipcEx, "NavmeshClient: move IPC not registered");
            return false;
        }
        catch (Exception ex)
        {
            status = $"navmesh move failed: {ex.Message}";
            log.Debug(ex, "NavmeshClient: move failed");
            return false;
        }
    }

    public bool Stop()
    {
        if (!IsAvailable || stop == null)
        {
            return false;
        }

        try
        {
            stop.InvokeAction();
            status = "navmesh stopped";
            return true;
        }
        catch (IpcNotReadyError ex)
        {
            status = "vnavmesh IPC not registered (plugin disabled?)";
            log.Debug(ex, "NavmeshClient: stop IPC not registered");
            return false;
        }
        catch (Exception ex)
        {
            status = $"navmesh stop failed: {ex.Message}";
            log.Debug(ex, "NavmeshClient: stop failed");
            return false;
        }
    }

    private bool reloadAttempted;
}
