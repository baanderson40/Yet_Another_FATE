namespace YetAnotherFATE.Services.Travel;

public static class IdleAetheryteReturnPolicy
{
    public static bool ShouldYieldToEligibleFate(bool hasEligibleFate, bool teleportIssued, bool parked) =>
        hasEligibleFate && (parked || !teleportIssued);
}
