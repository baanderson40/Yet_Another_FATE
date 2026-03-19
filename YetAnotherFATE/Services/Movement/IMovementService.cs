using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Movement;

public interface IMovementService
{
    void NavigateTo(FateSelection selection);
}
