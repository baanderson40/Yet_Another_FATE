using System;
using YetAnotherFATE.Data.Retainers;

namespace YetAnotherFATE.Services.Retainers;

public interface IAutoRetainerClient : IDisposable
{
    event Action? SinglePassFinished;

    bool TryGetState(out bool busy, out bool multiModeActive);
    bool TryGetReadiness(AutoRetainerMode mode, out AutoRetainerReadinessSnapshot snapshot);
    bool TryEnableSinglePass(AutoRetainerMode mode);
}
