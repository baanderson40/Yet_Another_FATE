namespace YetAnotherFATE.Services.Travel;

public enum CityAethernetState { Unavailable, Idle, Busy }

public interface ICityAethernetClient
{
    CityAethernetState State { get; }
    uint ActiveAetheryte { get; }
    bool CanRoute(uint destinationAethernetId);
    bool Request(uint destinationAethernetId);
    bool Abort();
}
