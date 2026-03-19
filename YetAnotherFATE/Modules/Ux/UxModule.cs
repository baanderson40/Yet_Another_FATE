using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using YetAnotherFATE.Windows;

namespace YetAnotherFATE.Modules.Ux;

public sealed class UxModule : IDisposable
{
    private readonly WindowSystem windowSystem;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private bool initialized;

    public UxModule(WindowSystem windowSystem, IDalamudPluginInterface pluginInterface, MainWindow mainWindow, ConfigWindow configWindow)
    {
        this.windowSystem = windowSystem;
        this.pluginInterface = pluginInterface;
        this.mainWindow = mainWindow;
        this.configWindow = configWindow;
    }

    public void Initialize()
    {
        if (initialized)
        {
            return;
        }

        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMain;
        initialized = true;
    }

    public void ToggleMain() => mainWindow.Toggle();

    public void ToggleConfig() => configWindow.Toggle();

    public void Dispose()
    {
        if (!initialized)
        {
            return;
        }

        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        windowSystem.RemoveAllWindows();
        initialized = false;
    }
}
