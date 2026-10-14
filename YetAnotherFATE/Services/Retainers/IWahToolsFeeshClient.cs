namespace YetAnotherFATE.Services.Retainers;

public enum FeeshRequestResult { Accepted, Refused, Unavailable }

public interface IWahToolsFeeshClient
{
    bool IsAvailable { get; }
    WahToolsFeeshStatus ReadStatus();
    FeeshRequestResult DisableAutoOcean(out string error);
    FeeshRequestResult CatchTheBoat(out string error);
    FeeshRequestResult Stop(out string error);
}
