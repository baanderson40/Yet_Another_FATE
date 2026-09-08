using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Data.Combat;

namespace YetAnotherFATE.Services.Combat;

public sealed class CombatPluginRegistry : ICombatPluginRegistry
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly List<CombatPluginStatus> statuses = new();

    private static readonly CombatPluginMetadata[] KnownPlugins =
    {
        new(CombatPluginId.BossMod, "BossMod", new[] { "BossMod" }, true, true),
        new(CombatPluginId.BossModReborn, "BossMod Reborn", new[] { "BossModReborn" }, true, true),
        new(CombatPluginId.RotationSolver, "Rotation Solver Reborn", new[] { "RotationSolver" }, true, false),
        new(CombatPluginId.WrathCombo, "Wrath Combo", new[] { "WrathCombo" }, true, false),
        new(CombatPluginId.Vnavmesh, "vnavmesh", new[] { "vnavmesh" }, false, false)
    };

    public CombatPluginRegistry(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        Refresh();
    }

    public IReadOnlyList<CombatPluginStatus> Statuses => statuses;

    public CombatPluginStatus? GetStatus(CombatPluginId id)
        => statuses.FirstOrDefault(s => s.Id == id);

    public CombatPluginStatus? GetPreferredRotation(CombatPluginId preferred)
    {
        if (preferred != CombatPluginId.None)
        {
            var configured = GetStatus(preferred);
            if (configured?.SupportsRotation == true && configured.Installed && configured.IsLoaded)
            {
                return configured;
            }
        }

        return statuses.FirstOrDefault(s => s.SupportsRotation && s.Installed && s.IsLoaded);
    }

    public CombatPluginStatus? GetPreferredDodge(CombatPluginId preferred)
    {
        if (preferred != CombatPluginId.None)
        {
            var configured = GetStatus(preferred);
            if (configured?.SupportsDodge == true && configured.Installed && configured.IsLoaded)
            {
                return configured;
            }
        }

        return statuses.FirstOrDefault(s => s.SupportsDodge && s.Installed && s.IsLoaded);
    }

    public void Refresh()
    {
        statuses.Clear();
        var installed = pluginInterface.InstalledPlugins;

        foreach (var metadata in KnownPlugins)
        {
            var descriptor = installed.FirstOrDefault(p => metadata.InternalNames.Any(name => string.Equals(p.InternalName, name, StringComparison.OrdinalIgnoreCase)));
            var installedFlag = descriptor != null;
            var loaded = descriptor?.IsLoaded ?? false;
            var version = descriptor?.Version.ToString();
            var internalName = descriptor?.InternalName ?? metadata.InternalNames.FirstOrDefault() ?? metadata.DisplayName;

            var status = new CombatPluginStatus(
                metadata.Id,
                metadata.DisplayName,
                internalName,
                metadata.SupportsRotation,
                metadata.SupportsDodge,
                installedFlag,
                version,
                loaded);

            statuses.Add(status);
            log.Debug("CombatPluginRegistry: {Name} internalName={InternalName} version={Version} installed={Installed} loaded={Loaded} supportsRotation={Rot} supportsDodge={Dodge}",
                status.DisplayName,
                status.InternalName,
                status.Version ?? "n/a",
                status.Installed,
                status.IsLoaded,
                status.SupportsRotation,
                status.SupportsDodge);
        }
    }

    private sealed record CombatPluginMetadata(
        CombatPluginId Id,
        string DisplayName,
        IReadOnlyList<string> InternalNames,
        bool SupportsRotation,
        bool SupportsDodge);
}
