using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using YetAnotherFATE.Renderers;

namespace YetAnotherFATE.Windows;

public sealed class DebugWindow : Window, IDisposable
{
    private readonly DebugRenderer renderer;

    public DebugWindow(DebugRenderer renderer)
        : base("Yet Another FATE Debug")
    {
        this.renderer = renderer;
        Size = new Vector2(1000, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw() => renderer.Draw();
}
