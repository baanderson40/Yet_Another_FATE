using YetAnotherFATE.Data.Travel;

namespace YetAnotherFATE.Services.Travel;

public enum TeleportRequestResult
{
    Issued,
    Busy,
    InvalidTarget,
    Blocked,
    ClientUnavailable,
    Failed,
}

public enum TeleportState
{
    Idle,
    Starting,
    Loading,
    Completed,
    Failed,
}

public interface ITeleportExecutor
{
    TeleportState State { get; }

    TeleportRequestResult RequestTeleport(in TeleportRecommendation recommendation);
}
