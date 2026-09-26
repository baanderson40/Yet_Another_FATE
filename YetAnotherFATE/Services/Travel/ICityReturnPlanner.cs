using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Data.Zone;

namespace YetAnotherFATE.Services.Travel;

public sealed record CityReturnPlan(AetheryteSummary Gateway, uint DestinationAethernetId);

public interface ICityReturnPlanner
{
    CityReturnPlan? Resolve(DeathRecoveryContext destination);
}
