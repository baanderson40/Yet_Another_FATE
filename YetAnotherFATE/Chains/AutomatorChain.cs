using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Movement;

namespace YetAnotherFATE.Chains;

public sealed class AutomatorChain
{
    private readonly IFateRepository fateRepository;
    private readonly IFateSelector fateSelector;
    private readonly IMovementService movementService;

    public AutomatorChain(IFateRepository fateRepository, IFateSelector fateSelector, IMovementService movementService)
    {
        this.fateRepository = fateRepository;
        this.fateSelector = fateSelector;
        this.movementService = movementService;
    }

    public void Tick()
    {
        var fates = fateRepository.EnumerateActiveFates();
        if (fates.Count == 0)
        {
            return;
        }

        var selection = fateSelector.SelectBest(fates);
        if (selection == null)
        {
            return;
        }

        movementService.NavigateTo(selection.Value);
    }
}
