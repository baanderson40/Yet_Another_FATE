using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Movement;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;

namespace YetAnotherFATE.Chains;

public sealed class AutomatorChain
{
    private readonly IFateRepository fateRepository;
    private readonly IFateSelector fateSelector;
    private readonly IMovementService movementService;
    private readonly IPluginLog log;
    private readonly Configuration configuration;

    public AutomatorChain(
        IFateRepository fateRepository,
        IFateSelector fateSelector,
        IMovementService movementService,
        IPluginLog log,
        Configuration configuration)
    {
        this.fateRepository = fateRepository;
        this.fateSelector = fateSelector;
        this.movementService = movementService;
        this.log = log;
        this.configuration = configuration;
    }

    public void Tick()
    {
        if (!configuration.AutomatorEnabled)
        {
            return;
        }

        var fates = fateRepository.EnumerateActiveFates();
        if (fates.Count == 0)
        {
            log.Debug("AutomatorChain: No active fates detected this tick.");
            return;
        }

        var selection = fateSelector.SelectBest(fates);
        if (selection == null)
        {
            log.Debug("AutomatorChain: Selector did not return a candidate.");
            return;
        }

        log.Debug("AutomatorChain: Selected fate {FateId} ({Name}) distance={Distance:F0}y score={Score:F1}", selection.Value.Fate.FateId, selection.Value.Fate.Name, selection.Value.Fate.DistanceFromPlayer, selection.Value.Score);
        log.Debug("AutomatorChain: Teleport decision => {Decision} ({Reason})", selection.Value.ShouldTeleport ? "TELEPORT" : "RUN", selection.Value.Teleport.Reason);

        movementService.NavigateTo(selection.Value);
    }
}
