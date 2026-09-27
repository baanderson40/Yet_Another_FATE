using System;

namespace YetAnotherFATE.Services.Travel;

public interface ILifestreamCharacterClient
{
    bool TryIsBusy(out bool busy);
    bool TryChangeCharacter(string name, string world, out string error);
}
