using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using YetAnotherFATE.Renderers;

namespace YetAnotherFATE.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly MainRenderer renderer;
    private readonly ConfigWindow configWindow;
    private readonly DebugWindow debugWindow;

    public MainWindow(MainRenderer renderer, ConfigWindow configWindow, DebugWindow debugWindow)
        : base(GetWindowTitle(), ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.renderer = renderer;
        this.configWindow = configWindow;
        this.debugWindow = debugWindow;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(1, 1),
            Priority = int.MinValue,
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
            Click = _ => configWindow.Toggle(),
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Bug,
            IconOffset = new Vector2(1, 1),
            Priority = int.MinValue + 1,
            ShowTooltip = () => ImGui.SetTooltip("Debug"),
            Click = _ => debugWindow.Toggle(),
        });

        Size = new Vector2(300, 175);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 175),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose() { }

    public override void PreDraw() => YafTheme.Push();

    public override void PostDraw() => YafTheme.Pop();

    private static string GetWindowTitle()
    {
        var devBuild = typeof(MainWindow).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "YetAnotherFATE.DevBuild")?.Value ?? "unknown";
        return FormatWindowTitle(devBuild);
    }

    internal static string FormatWindowTitle(string devBuild)
        => $"Yet Another FATE [DEV {devBuild}]###YetAnotherFATE.Main";

    public override void Draw()
    {
        renderer.Draw();
    }
}
