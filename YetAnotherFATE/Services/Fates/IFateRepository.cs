using System.Collections.Generic;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates;

public interface IFateRepository
{
    IReadOnlyList<ActiveFate> EnumerateActiveFates();
}
