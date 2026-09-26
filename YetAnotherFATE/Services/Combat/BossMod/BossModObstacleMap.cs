using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace YetAnotherFATE.Services.Combat.BossMod;

public sealed class BossModObstacleMap : IBossModObstacleMap, IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private bool cleanupPending;
    private bool disposed;
    private DateTimeOffset nextCleanupAttempt;
    private ICallGateSubscriber<Vector3, float, bool, bool>? generate;
    private ICallGateSubscriber<TaskStatus>? getGenerationStatus;
    private ICallGateSubscriber<bool>? hasTemporaryMap;
    private ICallGateSubscriber<BitmapQuality?>? evaluateQuality;
    private ICallGateSubscriber<bool>? clearTemporaryMap;
    private Vector3? generatedCenter;
    private float generatedRadius;
    private readonly HashSet<uint> blacklist = new();

    public BossModObstacleMap(IDalamudPluginInterface pluginInterface, IFramework framework, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.framework = framework;
        framework.Update += OnUpdate;
    }

    public string Status { get; private set; } = "BossMod obstacle map unavailable";

    public bool BeginForFate(uint fateId, Vector3 center, float radius)
    {
        if (disposed || cleanupPending) return false;
        lock (blacklist)
        {
            if (blacklist.Contains(fateId))
            {
                Status = $"FATE {fateId} obstacle map blacklisted";
                return false;
            }
        }

        try
        {
            generate ??= pluginInterface.GetIpcSubscriber<Vector3, float, bool, bool>("BossMod.ObstacleMap.Generate");
            if (!generate.HasFunction) return false;

            if (generatedCenter is { } previousCenter &&
                Vector3.DistanceSquared(previousCenter, center) < 1f &&
                Math.Abs(generatedRadius - radius) < 1f &&
                HasTemporaryMap())
            {
                Status = "BossMod obstacle map active";
                return true;
            }

            // Global provider has no owner token: do not replace another consumer's map/task.
            if (generatedCenter == null && (HasTemporaryMap() || GetGenerationStatus() is not (TaskStatus.RanToCompletion or TaskStatus.Faulted or TaskStatus.Canceled)))
            {
                Status = "BossMod obstacle map owned by another consumer";
                return false;
            }
            if (generatedCenter != null && !ClearTemporaryMap()) return false;
            if (!generate.InvokeFunc(center, radius, false))
            {
                Status = "BossMod obstacle map request rejected";
                return false;
            }

            generatedCenter = center;
            generatedRadius = radius;
            Status = "BossMod obstacle map generation requested";
            return true;
        }
        catch (IpcNotReadyError ex)
        {
            Status = "BossMod obstacle map IPC not ready";
            log.Debug(ex, "BossModObstacleMap: IPC not ready");
            return false;
        }
        catch (Exception ex)
        {
            Status = $"BossMod obstacle map failed: {ex.Message}";
            log.Debug(ex, "BossModObstacleMap: generation failed");
            return false;
        }
    }

    public TaskStatus GetGenerationStatus()
    {
        try
        {
            getGenerationStatus ??= pluginInterface.GetIpcSubscriber<TaskStatus>("BossMod.ObstacleMap.GetGenerationStatus");
            return getGenerationStatus.InvokeFunc();
        }
        catch
        {
            return TaskStatus.Canceled;
        }
    }

    public bool IsCurrentMapBad(out string details)
    {
        details = string.Empty;
        try
        {
            evaluateQuality ??= pluginInterface.GetIpcSubscriber<BitmapQuality?>("BossMod.ObstacleMap.EvaluateTempMapQuality");
            var quality = evaluateQuality.InvokeFunc();
            if (quality is not { } value)
            {
                details = "No completed temporary map";
                return true;
            }

            details = value.ToString();
            return value.IsBad;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "BossModObstacleMap: quality evaluation unavailable");
            return false;
        }
    }

    public void Blacklist(uint fateId)
    {
        lock (blacklist) blacklist.Add(fateId);
    }

    private bool HasTemporaryMap()
    {
        try
        {
            hasTemporaryMap ??= pluginInterface.GetIpcSubscriber<bool>("BossMod.ObstacleMap.HasTempMap");
            return hasTemporaryMap.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public bool ClearTemporaryMap()
    {
        if (generatedCenter == null) return true;
        try
        {
            getGenerationStatus ??= pluginInterface.GetIpcSubscriber<TaskStatus>("BossMod.ObstacleMap.GetGenerationStatus");
            clearTemporaryMap ??= pluginInterface.GetIpcSubscriber<bool>("BossMod.ObstacleMap.ClearTempMap");
            if (!getGenerationStatus.HasFunction || !clearTemporaryMap.HasFunction)
            {
                // Provider unloaded: its map is gone. Do not clear a future provider's unrelated map.
                generatedCenter = null;
                cleanupPending = false;
                return true;
            }
            if (getGenerationStatus.InvokeFunc() is not (TaskStatus.RanToCompletion or TaskStatus.Faulted or TaskStatus.Canceled))
            {
                // ClearTempMap does not cancel Generate. Drain it before clearing the late result.
                cleanupPending = true;
                return false;
            }
            var cleared = clearTemporaryMap.InvokeFunc();
            if (cleared || !HasTemporaryMap())
            {
                generatedCenter = null;
                cleanupPending = false;
                Status = "BossMod temporary map released";
                return true;
            }

            cleanupPending = true;
            return false;
        }
        catch
        {
            cleanupPending = true;
            return false;
        }
    }

    private void OnUpdate(IFramework _)
    {
        if (cleanupPending && DateTimeOffset.UtcNow >= nextCleanupAttempt)
        {
            nextCleanupAttempt = DateTimeOffset.UtcNow.AddMilliseconds(200);
            ClearTemporaryMap();
        }
        if (disposed && !cleanupPending) framework.Update -= OnUpdate;
    }

    public void Dispose()
    {
        disposed = true;
        ClearTemporaryMap();
        if (!cleanupPending) framework.Update -= OnUpdate;
    }

    private readonly record struct BitmapQuality(
        float BlockedFraction,
        float LargestPassableComponentFraction,
        float TinyPassableComponentFraction,
        float SpeckleFraction,
        int PassableComponents)
    {
        // Provider divides largest cluster by passable cells: higher is better.
        public bool IsBad => !float.IsFinite(BlockedFraction) || !float.IsFinite(LargestPassableComponentFraction)
            || !float.IsFinite(TinyPassableComponentFraction) || !float.IsFinite(SpeckleFraction)
            || PassableComponents <= 0 || BlockedFraction >= 0.85f
            || LargestPassableComponentFraction < 0.5f
            || TinyPassableComponentFraction >= 0.03f
            || SpeckleFraction >= 0.003f;
    }
}
