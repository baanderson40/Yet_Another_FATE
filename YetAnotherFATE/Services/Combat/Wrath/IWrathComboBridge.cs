namespace YetAnotherFATE.Services.Combat.Wrath;

public interface IWrathComboBridge
{
    bool TryGetComboOptions(out System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>? combos);
    bool ConfigureForFateAutomation();
    void NotifyAutomationStopped();
}
