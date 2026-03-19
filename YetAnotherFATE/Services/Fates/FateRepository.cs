using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates;

public sealed class FateRepository : IFateRepository
{
    private static readonly IReadOnlyList<ActiveFate> Empty = Array.Empty<ActiveFate>();

    private readonly IClientState clientState;

    public FateRepository(IClientState clientState)
    {
        this.clientState = clientState;
    }

    public IReadOnlyList<ActiveFate> EnumerateActiveFates()
    {
        if (!clientState.IsLoggedIn)
        {
            return Empty;
        }

        return Empty;
    }
}
