using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using YetAnotherFATE.Data.Combat;

namespace YetAnotherFATE.Config;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool AutomatorEnabled { get; set; } = false;
    public bool DiagnosticLoggingEnabled { get; set; } = false;
    public List<uint> FateBlacklist { get; set; } = new();

    public bool TeleportEnabled { get; set; } = true;
    public float TeleportMountedRunSpeed { get; set; } = 6.0f;
    public float TeleportPenaltySeconds { get; set; } = 15.0f;
    public float TeleportPostTeleportRunBufferYalms { get; set; } = 200.0f;
    public float TeleportMinimumDistanceYalms { get; set; } = 300.0f;
    public float TeleportAdvantageThresholdSeconds { get; set; } = 5.0f;
    public uint TeleportMaxGilCost { get; set; } = 9999;
    public CombatPluginId PreferredRotationProvider { get; set; } = CombatPluginId.None;
    public CombatPluginId PreferredDodgeProvider { get; set; } = CombatPluginId.None;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface dalamudPluginInterface)
    {
        pluginInterface = dalamudPluginInterface;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}
