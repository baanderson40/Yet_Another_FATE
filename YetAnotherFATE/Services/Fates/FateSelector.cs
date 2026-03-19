using System.Collections.Generic;
using System.Linq;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates;

public sealed class FateSelector : IFateSelector
{

    public FateSelection? SelectBest(IReadOnlyList<ActiveFate> activeFates)
    {
        if (activeFates == null || activeFates.Count == 0)
        {
            return null;
        }

        var candidate = activeFates
            .OrderBy(static fate => fate.DistanceFromPlayer)
            .ThenBy(static fate => fate.TimeRemaining)
            .First();

        return new FateSelection(candidate, candidate.Center, false, null);
    }
}
