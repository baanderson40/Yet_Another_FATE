namespace YetAnotherFATE.Commands;

public interface IMainCommandDelegate
{
    void Execute(string command, string args);
}
