using System.Linq;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Travel;

// Framework-thread authorization, rechecked immediately before every automatic cast.
// Death-return and explicit manual teleports deliberately use separate paths.
public sealed class AutomaticTravelPolicy(Configuration configuration, IClientState clientState,
    IObjectTable objects, IFateTable table, ICondition condition)
{
    private uint territory;
    public uint? WaitForExpiryFateId { get; private set; }

    public void WaitForExpiry(ActiveFate fate)
    {
        territory = fate.TerritoryId;
        WaitForExpiryFateId = fate.FateId;
    }

    public void Synchronize()
    {
        if (!clientState.IsLoggedIn || territory != clientState.TerritoryType)
            WaitForExpiryFateId = null;
        // Do not mistake loading/player unavailability for native-table expiry.
        else if (objects.LocalPlayer != null &&
                 !condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] &&
                 !condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51] &&
                 WaitForExpiryFateId is { } id &&
                 !(table.Count > 0 && table.Any(f => f != null && table.IsValid(f) && f.FateId == id)))
            WaitForExpiryFateId = null;
        territory = clientState.TerritoryType;
    }

    public bool CanTeleport(ActiveFate? fate = null)
    {
        Synchronize();
        return Allows(configuration.TeleportEnabled,
            objects.LocalPlayer?.StatusList?.Any(s => s.StatusId is 1288 or 1289) == true,
            WaitForExpiryFateId.HasValue, fate?.Progress) && clientState.IsLoggedIn && objects.LocalPlayer != null;
    }

    public static bool Allows(bool enabled, bool twist, bool awaitingExpiry, int? progress)
        => enabled && !twist && !awaitingExpiry && (!progress.HasValue || progress > 0);
}
