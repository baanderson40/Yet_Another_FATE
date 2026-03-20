using System.Numerics;

namespace YetAnotherFATE.Services.Movement;

public interface INavmeshClient
{
    bool IsAvailable { get; }

    string Status { get; }

    bool EnsureReady();

    bool SimpleMoveTo(Vector3 destination, bool useMesh = true);

    bool Stop();
}
