using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using YetAnotherFATE.Config;

namespace YetAnotherFATE.Windows;

public class ConfigWindow : Window, IDisposable
{
    private const float ControlWidth = 150f;
    private readonly Configuration configuration;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Configuration configuration) : base("Yet Another FATE Config")
    {
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(420, 260);
        SizeCondition = ImGuiCond.Always;

        this.configuration = configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Teleport Heuristics");
        ImGui.Separator();
        DrawTeleportSection();
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
}
