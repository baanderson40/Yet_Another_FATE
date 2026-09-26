using System.Collections.Generic;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates;

public interface IFateRepository
{
    IReadOnlyList<ActiveFate> EnumerateActiveFates();
    bool TryGetTerminalFate(uint fateId, out ActiveFate fate)
    {
        fate = default;
        return false;
    }
}
