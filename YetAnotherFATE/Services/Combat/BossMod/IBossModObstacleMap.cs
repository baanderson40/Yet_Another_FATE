using System.Numerics;
using System.Threading.Tasks;

namespace YetAnotherFATE.Services.Combat.BossMod;

public interface IBossModObstacleMap
{
    bool BeginForFate(uint fateId, Vector3 center, float radius);
    TaskStatus GetGenerationStatus();
    bool IsCurrentMapBad(out string details);
    void Blacklist(uint fateId);
    bool ClearTemporaryMap();
    string Status { get; }
}
