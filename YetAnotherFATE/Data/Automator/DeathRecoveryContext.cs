using System.Numerics;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Data.Automator;

public sealed record DeathRecoveryContext(uint TerritoryId, Vector3 Position, FateSelection? Selection);
