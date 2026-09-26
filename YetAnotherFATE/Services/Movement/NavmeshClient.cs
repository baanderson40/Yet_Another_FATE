using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Numerics;
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
    private const string PathNearestPoint = "Query.Mesh.NearestPointReachable";
    private const string PathPointOnFloor = "Query.Mesh.PointOnFloor";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    private string status = "navmesh not initialized";

    private ICallGateSubscriber<bool>? isReady;
    private ICallGateSubscriber<bool>? reload;
    private ICallGateSubscriber<float>? buildProgress;
    private ICallGateSubscriber<Vector3, bool, bool>? simpleMove;
    private ICallGateSubscriber<Vector3, float, float, Vector3?>? nearestPointReachable;
    private ICallGateSubscriber<Vector3, bool, float, Vector3?>? pointOnFloor;
    private ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>? pathfind;
    private ICallGateSubscriber<bool>? stop;
    private ICallGateSubscriber<bool>? pathfinding;
    private ICallGateSubscriber<bool>? cancelPathfinding;
    private bool ownsMove;
    private bool cancellationIssued;

    public NavmeshClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        if (IsPluginLoaded())
        {
            InitializeSubscribers();
        }
        else
        {
            status = "vnavmesh not loaded";
        }
    }

    public bool IsAvailable => IsPluginLoaded();
    public string Status => status;
    public bool IsPathfinding
    {
        get
        {
            try
            {
                pathfinding ??= pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
                return pathfinding.InvokeFunc();
            }
            catch { return false; }
        }
    }

    private void InitializeSubscribers()
    {
        try
        {
            isReady = pluginInterface.GetIpcSubscriber<bool>($"{PluginName}.{PathPrefix}.IsReady");
            reload = pluginInterface.GetIpcSubscriber<bool>($"{PluginName}.{PathPrefix}.Reload");
            buildProgress = pluginInterface.GetIpcSubscriber<float>($"{PluginName}.{PathPrefix}.BuildProgress");
            simpleMove = pluginInterface.GetIpcSubscriber<Vector3, bool, bool>($"{PluginName}.{PathSimpleMove}.PathfindAndMoveTo");
            stop = pluginInterface.GetIpcSubscriber<bool>($"{PluginName}.{PathPath}.Stop");
            nearestPointReachable = pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>($"{PluginName}.{PathNearestPoint}");
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
        if (!IsPluginLoaded())
        {
            status = "vnavmesh not loaded";
            return false;
        }

        if (isReady == null || reload == null || simpleMove == null || stop == null)
        {
            InitializeSubscribers();
        }

        if (isReady == null)
        {
            status = "navmesh IPC missing";
            return false;
        }

        try
        {
            if (isReady.InvokeFunc())
            {
                reloadAttempted = false;
                status = "navmesh ready";
                return true;
            }
            status = "navmesh reports not ready";
            if (buildProgress is { HasFunction: true } && buildProgress.InvokeFunc() >= 0)
            {
                status = "navmesh building";
                return false;
            }
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
                status = reload.InvokeFunc() ? "navmesh reload requested" : "navmesh reload rejected";
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

    public bool SimpleMoveTo(Vector3 destination, bool fly = false)
    {
        if (!IsPluginLoaded())
        {
            status = "vnavmesh not loaded";
            return false;
        }

        if (!EnsureReady())
        {
            return false;
        }

        if (simpleMove == null)
        {
            status = "navmesh IPC missing";
            return false;
        }

        try
        {
            var result = simpleMove.InvokeFunc(destination, fly);
            if (result)
            {
                ownsMove = true;
                cancellationIssued = false;
            }
            status = result ? "navmesh move issued" : "navmesh move rejected";
            return result;
        }
        catch (IpcNotReadyError ex)
        {
            status = "vnavmesh IPC not registered (plugin disabled?)";
            log.Debug(ex, "NavmeshClient: move IPC not registered");
            return false;
        }
        catch (Exception ex)
        {
            status = $"navmesh move failed: {ex.Message}";
            log.Debug(ex, "NavmeshClient: move failed");
            return false;
        }
    }

    public Vector3? NearestPointReachable(Vector3 position, float halfExtentXZ = 5f, float halfExtentY = 5f)
    {
        if (!IsPluginLoaded())
        {
            status = "vnavmesh not loaded";
            return null;
        }

        try
        {
            nearestPointReachable ??= pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>($"{PluginName}.{PathNearestPoint}");
            var point = nearestPointReachable.InvokeFunc(position, halfExtentXZ, halfExtentY);
            status = point.HasValue ? "reachable mesh point resolved" : "no reachable mesh point found";
            return point;
        }
        catch (IpcNotReadyError ex)
        {
            log.Debug(ex, "NavmeshClient: nearest reachable-point IPC unavailable");
            return null;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "NavmeshClient: nearest reachable-point query failed");
            return null;
        }
    }

    public Vector3? PointOnFloor(Vector3 position, bool allowUnlandable = false, float halfExtentXZ = 5f)
    {
        if (!IsPluginLoaded())
        {
            status = "vnavmesh not loaded";
            return null;
        }

        try
        {
            pointOnFloor ??= pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>($"{PluginName}.{PathPointOnFloor}");
            var point = pointOnFloor.InvokeFunc(position, allowUnlandable, halfExtentXZ);
            status = point.HasValue ? "landable floor point resolved" : "no landable floor point found";
            return point;
        }
        catch (IpcNotReadyError ex)
        {
            log.Debug(ex, "NavmeshClient: floor-point IPC unavailable");
            return null;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "NavmeshClient: floor-point query failed");
            return null;
        }
    }

    public bool Stop()
    {
        if (!IsPluginLoaded())
        {
            return false;
        }

        if (stop == null)
        {
            InitializeSubscribers();
        }

        if (stop == null)
        {
            status = "navmesh IPC missing";
            return false;
        }

        try
        {
            // Path.Stop alone does not cancel AsyncMoveRequest; its result can start moving later.
            if (ownsMove && IsPathfinding && !cancellationIssued)
            {
                cancelPathfinding ??= pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindCancelAll");
                if (cancelPathfinding.HasAction)
                {
                    cancelPathfinding.InvokeAction();
                    cancellationIssued = true;
                    reloadAttempted = true; // CancelAll itself reloads the mesh.
                }
            }
            stop.InvokeAction();
            if (!IsPathfinding) ownsMove = false;
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

    public async Task<IReadOnlyList<Vector3>?> Pathfind(Vector3 from, Vector3 to, bool fly = false)
    {
        if (!IsPluginLoaded())
        {
            status = "vnavmesh not loaded";
            return null;
        }

        try
        {
            pathfind ??= pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>("vnavmesh.Nav.Pathfind");
            var result = await pathfind.InvokeFunc(from, to, fly).ConfigureAwait(false);
            status = result is { Count: > 0 } ? "ground path resolved" : "no ground path found";
            return result;
        }
        catch (IpcNotReadyError ex)
        {
            log.Debug(ex, "NavmeshClient: path query IPC unavailable");
            return null;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "NavmeshClient: path query failed");
            return null;
        }
    }

    private bool IsPluginLoaded()
    {
        var plugin = pluginInterface.InstalledPlugins.FirstOrDefault(p =>
            string.Equals(p.InternalName, PluginName, StringComparison.OrdinalIgnoreCase));

        if (plugin?.IsLoaded == true)
        {
            return true;
        }

        isReady = null;
        reload = null;
        buildProgress = null;
        simpleMove = null;
        nearestPointReachable = null;
        pointOnFloor = null;
        pathfind = null;
        stop = null;
        pathfinding = null;
        cancelPathfinding = null;
        ownsMove = false;
        cancellationIssued = false;
        reloadAttempted = false;
        return false;
    }

    private bool reloadAttempted;
}
