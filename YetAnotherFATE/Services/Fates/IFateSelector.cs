using System.Collections.Generic;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates;

public interface IFateSelector
{
    FateSelection? SelectBest(IReadOnlyList<ActiveFate> activeFates);
    FateSelectionDiagnostics Evaluate(IReadOnlyList<ActiveFate> activeFates);
}
