using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace YetAnotherFATE.Services.Movement;

public interface INavmeshClient
{
    bool IsAvailable { get; }

    string Status { get; }

    bool IsPathfinding { get; }

    bool EnsureReady();

    // Ground movement is safe by default. Callers must explicitly authorize flight.
    bool SimpleMoveTo(Vector3 destination, bool fly = false);

    Vector3? NearestPointReachable(Vector3 position, float halfExtentXZ = 5f, float halfExtentY = 5f);

    Vector3? PointOnFloor(Vector3 position, bool allowUnlandable = false, float halfExtentXZ = 5f);

    Task<IReadOnlyList<Vector3>?> Pathfind(Vector3 from, Vector3 to, bool fly = false);

    bool Stop();
}
