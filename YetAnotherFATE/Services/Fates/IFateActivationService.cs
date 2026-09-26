using YetAnotherFATE.Data.Fates;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace YetAnotherFATE.Services.Fates;

public interface IFateActivationService
{
    string Status { get; }

    bool TryGetMotivationNpc(ActiveFate fate, out IGameObject? npc);

    bool Interact(IGameObject npc);

    void ClearOwnedTarget();
}
