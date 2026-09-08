using System.Collections.Generic;
using YetAnotherFATE.Data.Combat;

namespace YetAnotherFATE.Services.Combat;

public interface ICombatPluginRegistry
{
    IReadOnlyList<CombatPluginStatus> Statuses { get; }

    CombatPluginStatus? GetStatus(CombatPluginId id);

    CombatPluginStatus? GetPreferredRotation(CombatPluginId preferred);

    CombatPluginStatus? GetPreferredDodge(CombatPluginId preferred);

    void Refresh();
}
