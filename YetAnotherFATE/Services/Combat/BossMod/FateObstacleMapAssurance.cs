using System;
using System.Numerics;
using System.Threading.Tasks;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Services.Movement;

namespace YetAnotherFATE.Services.Combat.BossMod;

// Tick-owned engagement assurance also covers arrival shortcuts and activation cleanup.
public sealed class FateObstacleMapAssurance(IBossModObstacleMap maps, INavmeshClient navmesh)
{
    private (uint Territory, uint Id, int Start)? instance;
    private DateTimeOffset nextAttempt;
    private DateTimeOffset? started;
    private int failures;

    public void Reset()
    {
        instance = null;
        started = null;
        nextAttempt = default;
        failures = 0;
        maps.ClearTemporaryMap();
    }

    public void Tick(ActiveFate fate, DateTimeOffset now)
    {
        var key = (fate.TerritoryId, fate.FateId, fate.StartTimeEpoch);
        if (instance != key)
        {
            Reset();
            instance = key;
        }
        if (failures >= 3 || now < nextAttempt) return;
        if (started.HasValue)
        {
            var status = maps.GetGenerationStatus();
            if (status == TaskStatus.RanToCompletion)
            {
                started = null;
                if (maps.IsCurrentMapBad(out _))
                {
                    maps.ClearTemporaryMap();
                    maps.Blacklist(fate.FateId);
                    failures = 3;
                }
                nextAttempt = now.AddSeconds(2);
                return;
            }
            if (status is TaskStatus.Faulted or TaskStatus.Canceled || now - started.Value >= TimeSpan.FromSeconds(15))
            {
                maps.ClearTemporaryMap();
                started = null;
                failures++;
                nextAttempt = now.AddSeconds(5);
            }
            return;
        }
        nextAttempt = now.AddSeconds(2);
        if (!navmesh.EnsureReady()) return;
        var center = navmesh.NearestPointReachable(fate.Center) ?? fate.Center;
        if (maps.BeginForFate(fate.FateId, center, fate.Radius + Vector3.Distance(center, fate.Center)))
            started = now;
    }
}
