using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
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

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose() { }

    private static string GetWindowTitle()
    {
        var devBuild = typeof(MainWindow).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "YetAnotherFATE.DevBuild")?.Value ?? "unknown";
        return $"Yet Another FATE [DEV {devBuild}]##main";
    }

    public override void Draw()
    {
        renderer.Draw(configWindow.Toggle, debugWindow.Toggle);
    }
}
