using System;

namespace YetAnotherFATE.Commands;

public interface IMainCommand : IDisposable
{
    void Register();
}
