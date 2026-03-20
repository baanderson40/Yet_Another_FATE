using YetAnotherFATE.Services.Fates;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;
using YetAnotherFATE.Services.Travel;
using YetAnotherFATE.Data.Travel;

namespace YetAnotherFATE.Chains;

public sealed class AutomatorChain
{
    private readonly IFateRepository fateRepository;
    private readonly IFateSelector fateSelector;
    private readonly ITravelController travelController;
    private readonly IPluginLog log;
    private readonly Configuration configuration;

    public AutomatorChain(
        IFateRepository fateRepository,
        IFateSelector fateSelector,
        ITravelController travelController,
        IPluginLog log,
        Configuration configuration)
    {
        this.fateRepository = fateRepository;
        this.fateSelector = fateSelector;
        this.travelController = travelController;
        this.log = log;
        this.configuration = configuration;
    }

    public void Tick()
    {
        if (!configuration.AutomatorEnabled)
        {
            travelController.Cancel("Automator disabled", TravelRequestKind.Automatic);
            return;
        }

        var fates = fateRepository.EnumerateActiveFates();
        if (fates.Count == 0)
        {
            log.Debug("AutomatorChain: No active fates detected this tick.");
            travelController.Cancel("No active fates", TravelRequestKind.Automatic);
            return;
        }

        var selection = fateSelector.SelectBest(fates);
        if (selection == null)
        {
            log.Debug("AutomatorChain: Selector did not return a candidate.");
            travelController.Cancel("No candidate from selector", TravelRequestKind.Automatic);
            return;
        }

        travelController.RequestTravel(selection.Value);
    }
}
