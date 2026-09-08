namespace YetAnotherFATE.Data.Automator;

public readonly record struct AutomatorStateHandlerResult(AutomatorState? NextState, string? Reason = null)
{
    public bool IsWait { get; init; }

    public static AutomatorStateHandlerResult None(string? reason = null) => new(null, reason);

    public static AutomatorStateHandlerResult Wait(string? reason = null)
        => new(null, reason) { IsWait = true };

    public static AutomatorStateHandlerResult Transition(AutomatorState state, string? reason = null)
        => new(state, reason);
}
