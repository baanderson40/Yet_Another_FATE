using Dalamud.Plugin.Services;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Movement;

public sealed class MovementService(IPluginLog log) : IMovementService
{
    private readonly IPluginLog log = log;

    public void NavigateTo(FateSelection selection)
    {
        log.Debug($"Placeholder movement request for fate {selection.Fate.FateId} ({selection.Fate.Name}).");
    }
}
