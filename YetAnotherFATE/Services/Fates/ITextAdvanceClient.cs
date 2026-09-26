namespace YetAnotherFATE.Services.Fates;

public interface ITextAdvanceClient
{
    // Enable only during the caller's owned NPC interaction; neither mode auto-interacts.
    bool EnableForActivation();
    bool EnableForCollection();
    // Releases only control acquired by this client. Safe when TextAdvance is absent.
    void Disable();
}
