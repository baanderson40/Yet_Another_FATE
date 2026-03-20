using Dalamud.Plugin.Services;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Movement;

public sealed class MovementService(IPluginLog log) : IMovementService
{
    private readonly IPluginLog log = log;

    public void NavigateTo(FateSelection selection)
    {
        log.Debug("MovementService: would {Mode} toward fate {FateId} ({Name}) reason={Reason}",
            selection.ShouldTeleport ? "teleport" : "run",
            selection.Fate.FateId,
            selection.Fate.Name,
            selection.Reason);
    }
}
