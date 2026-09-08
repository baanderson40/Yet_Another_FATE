using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Combat;
using YetAnotherFATE.Services.Combat;

namespace YetAnotherFATE.Windows;

public class ConfigWindow : Window, IDisposable
{
    private const float ControlWidth = 150f;
    private readonly Configuration configuration;
    private readonly ICombatPluginRegistry combatPlugins;
    private bool initialRefreshDone;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Configuration configuration, ICombatPluginRegistry combatPlugins) : base("Yet Another FATE Config")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(520, 600);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.configuration = configuration;
        this.combatPlugins = combatPlugins;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var diagnostics = configuration.DiagnosticLoggingEnabled;
        if (ImGui.Checkbox("Enable detailed diagnostic logging", ref diagnostics))
        {
            configuration.DiagnosticLoggingEnabled = diagnostics;
            configuration.Save();
        }

        ImGui.Separator();
        EnsureProvidersDetected();
        DrawCombatSection();
        ImGui.NewLine();
        ImGui.Separator();
        ImGui.TextUnformatted("Teleport Heuristics");
        ImGui.Separator();
        DrawTeleportSection();
        ImGui.NewLine();
    }

    private void DrawCombatSection()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Combat Providers");
        ImGui.Separator();

        if (ImGui.Button("Refresh Providers"))
        {
            combatPlugins.Refresh();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Detected plugins: " + combatPlugins.Statuses.Count);

        ImGui.NewLine();

        DrawProviderCombo(
            "Rotation Provider",
            configuration.PreferredRotationProvider,
            status => status.SupportsRotation,
            id => configuration.PreferredRotationProvider = id);

        var rotation = configuration.PreferredRotationProvider;
        var disableDodge = rotation is CombatPluginId.BossMod or CombatPluginId.BossModReborn;

        if (disableDodge)
        {
            ImGui.BeginDisabled();
        }

        DrawProviderCombo(
            "Dodge Provider",
            configuration.PreferredDodgeProvider,
            status => status.SupportsDodge,
            id => configuration.PreferredDodgeProvider = id);

        if (disableDodge)
        {
            ImGui.EndDisabled();
        }

        ImGui.NewLine();

    }

    private void EnsureProvidersDetected()
    {
        if (initialRefreshDone)
        {
            return;
        }

        combatPlugins.Refresh();
        initialRefreshDone = true;
    }

    private void DrawTeleportSection()
    {
        var enabled = configuration.TeleportEnabled;
        if (ImGui.Checkbox("Enable Teleport Advisor", ref enabled))
        {
            configuration.TeleportEnabled = enabled;
            configuration.Save();
        }

        var runSpeed = configuration.TeleportMountedRunSpeed;
        DrawFloatSetting("Mounted run speed (y/s)", ref runSpeed, 1.0f, 20.0f, v => configuration.TeleportMountedRunSpeed = v);
        var penalty = configuration.TeleportPenaltySeconds;
        DrawFloatSetting("Teleport penalty (s)", ref penalty, 0.0f, 60.0f, v => configuration.TeleportPenaltySeconds = v);
        var buffer = configuration.TeleportPostTeleportRunBufferYalms;
        DrawFloatSetting("Post-teleport buffer (y)", ref buffer, 0.0f, 1000.0f, v => configuration.TeleportPostTeleportRunBufferYalms = v);
        var minDistance = configuration.TeleportMinimumDistanceYalms;
        DrawFloatSetting("Min teleport distance (y)", ref minDistance, 0.0f, 10000.0f, v => configuration.TeleportMinimumDistanceYalms = v);
        var advantage = configuration.TeleportAdvantageThresholdSeconds;
        DrawFloatSetting("Required advantage (s)", ref advantage, 0.0f, 60.0f, v => configuration.TeleportAdvantageThresholdSeconds = v);

        var maxCost = (int)configuration.TeleportMaxGilCost;
        ImGui.SetNextItemWidth(ControlWidth);
        if (ImGui.InputInt("Max teleport cost (gil)", ref maxCost))
        {
            configuration.TeleportMaxGilCost = (uint)Math.Max(0, maxCost);
            configuration.Save();
        }
    }

    private void DrawFloatSetting(string label, ref float value, float min, float max, Action<float> assign)
    {
        ImGui.SetNextItemWidth(ControlWidth);
        var temp = value;
        if (ImGui.InputFloat(label, ref temp))
        {
            var clamped = Math.Clamp(temp, min, max);
            assign(clamped);
            configuration.Save();
        }
    }

    private void DrawProviderCombo(string label, CombatPluginId current, Func<CombatPluginStatus, bool> predicate, Action<CombatPluginId> assign)
    {
        var statuses = combatPlugins.Statuses
            .Where(s => predicate(s) && s.Installed && s.IsLoaded)
            .ToList();
        var currentLabel = GetProviderDisplayName(current);
        ImGui.SetNextItemWidth(ControlWidth + 100f);
        if (ImGui.BeginCombo(label, currentLabel))
        {
            var autoSelected = current == CombatPluginId.None;
            if (ImGui.Selectable("Auto-select", autoSelected) && !autoSelected)
            {
                assign(CombatPluginId.None);
                configuration.Save();
            }

            foreach (var status in statuses)
            {
                var selected = current == status.Id;
                if (ImGui.Selectable(status.DisplayName, selected) && !selected)
                {
                    assign(status.Id);
                    configuration.Save();
                }
            }

            ImGui.EndCombo();
        }
    }

    private string GetProviderDisplayName(CombatPluginId provider)
    {
        if (provider == CombatPluginId.None)
        {
            return "Auto-select";
        }

        var status = combatPlugins.GetStatus(provider);
        if (status == null)
        {
            return provider.ToString();
        }

        if (!status.Installed || !status.IsLoaded)
        {
            return "Auto-select";
        }

        return status.DisplayName;
    }

}
