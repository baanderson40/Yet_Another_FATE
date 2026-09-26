using YetAnotherFATE.Data.Automator;

namespace YetAnotherFATE.Modules.Automator.Handlers;

public interface IAutomatorStateHandler
{
    bool CanHandle(AutomatorState state);
    AutomatorStateHandlerResult Tick(AutomatorContext context);
    void Reset() { }
}
