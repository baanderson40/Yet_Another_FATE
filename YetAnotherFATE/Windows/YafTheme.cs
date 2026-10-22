using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace YetAnotherFATE.Windows;

/// <summary>YAF-owned slate and accent palettes. Styling is scoped to YAF windows.</summary>
internal static class YafTheme
{
    private readonly record struct Palette(string Name, uint Window, uint Panel, uint Raised,
        uint Hover, uint Border, uint Accent, uint Text);

    private static readonly Palette[] Palettes =
    [
        new("Fiery Ocean", 0x003049, 0x780000, 0x9B1019, 0x4D687B, 0x669BBC, 0xC1121F, 0xFDF0D5),
        new("Dark Sunset", 0x335C67, 0x540B0E, 0x640D14, 0x80551E, 0xE09F3E, 0xE09F3E, 0xFFF3B0),
        new("Summer Fun", 0x023047, 0x06435B, 0x126A86, 0xA95700, 0x8ECAE6, 0xFFB703, 0xFFFFFF),
    ];

    private static Palette current = Palettes[0];
    private const int StyleVarCount = 10;

    public static string[] PresetNames { get; } = Palettes.Select(palette => palette.Name).ToArray();
    public static string CurrentPreset => current.Name;
    public static Vector4 Window => Rgb(current.Window);
    public static Vector4 Panel => Rgb(current.Panel);
    public static Vector4 Raised => Rgb(current.Raised);
    public static Vector4 Hover => Rgb(current.Hover);
    public static Vector4 Border => Rgb(current.Border);
    public static Vector4 Accent => Rgb(current.Accent);
    public static Vector4 Text => Rgb(current.Text);
    public static readonly Vector4 Muted = Rgb(0xB8C8D0);
    public static Vector4 Selected => new(Accent.X * 0.36f, Accent.Y * 0.36f, Accent.Z * 0.36f, 0.8f);
    public static readonly Vector4 Mint = Rgb(0x87E6C7);
    public static readonly Vector4 Amber = Rgb(0xFFD38A);
    public static readonly Vector4 Rose = Rgb(0xEE7788);
    private static float Scale => ImGuiHelpers.GlobalScale;

    private static (ImGuiCol Slot, Vector4 Color)[] Colors =>
    [
        (ImGuiCol.WindowBg, Window), (ImGuiCol.ChildBg, Panel), (ImGuiCol.PopupBg, Panel),
        (ImGuiCol.Text, Text), (ImGuiCol.TextDisabled, Muted), (ImGuiCol.Border, Border),
        (ImGuiCol.Separator, Border), (ImGuiCol.SeparatorHovered, Hover), (ImGuiCol.SeparatorActive, Accent),
        (ImGuiCol.FrameBg, Raised), (ImGuiCol.FrameBgHovered, Hover), (ImGuiCol.FrameBgActive, Selected),
        (ImGuiCol.Button, Raised), (ImGuiCol.ButtonHovered, Hover), (ImGuiCol.ButtonActive, Selected),
        (ImGuiCol.Header, Selected), (ImGuiCol.HeaderHovered, Hover), (ImGuiCol.HeaderActive, Accent),
        (ImGuiCol.Tab, Raised), (ImGuiCol.TabHovered, Hover), (ImGuiCol.TabActive, Selected),
        (ImGuiCol.TabUnfocused, Raised), (ImGuiCol.TabUnfocusedActive, Selected),
        (ImGuiCol.CheckMark, Accent), (ImGuiCol.SliderGrab, Selected), (ImGuiCol.SliderGrabActive, Accent),
        (ImGuiCol.TableHeaderBg, Raised), (ImGuiCol.TableRowBg, Vector4.Zero),
        (ImGuiCol.TableRowBgAlt, new Vector4(0.15f, 0.27f, 0.31f, 0.35f)),
        (ImGuiCol.TitleBg, Window), (ImGuiCol.TitleBgActive, Panel), (ImGuiCol.TitleBgCollapsed, Window),
        (ImGuiCol.ScrollbarBg, Window), (ImGuiCol.ScrollbarGrab, Border),
        (ImGuiCol.ScrollbarGrabHovered, Hover), (ImGuiCol.ScrollbarGrabActive, Accent),
        (ImGuiCol.ResizeGrip, Border), (ImGuiCol.ResizeGripHovered, Hover), (ImGuiCol.ResizeGripActive, Accent),
        (ImGuiCol.TextSelectedBg, Selected), (ImGuiCol.NavHighlight, Accent),
        (ImGuiCol.ModalWindowDimBg, new Vector4(Window.X, Window.Y, Window.Z, 0.65f)),
    ];

    public static void SetPreset(string? name)
    {
        var index = Array.FindIndex(Palettes, palette => string.Equals(palette.Name, name, StringComparison.Ordinal));
        current = index < 0 ? Palettes[0] : Palettes[index];
    }

    public static void Push()
    {
        var scale = Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 7) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10, 8) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(8, 6) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(10, 7) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, scale);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 6 * scale);
        foreach (var (slot, color) in Colors)
            ImGui.PushStyleColor(slot, color);
    }

    public static void Pop()
    {
        ImGui.PopStyleColor(Colors.Length);
        ImGui.PopStyleVar(StyleVarCount);
    }

    public static Vector4 ForStatus(string? status) => status switch
    {
        "Eligible" or "Completed" or "Ready" or "Active" or "Loaded" => Mint,
        "Blocked" or "Unknown" or "Unavailable" or "Missing" or "Installed" => Amber,
        "Failed" or "RecoveryRequired" or "Error" => Rose,
        _ => Muted,
    };

    public static Vector4 Rgb(uint rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f,
        1f);
}
