namespace YetAnotherFATE.Services.Companions;

public interface IChocoboController
{
    string Status { get; }
    bool Tick();
    void TickDuringCombat();
    void Reset();
}
