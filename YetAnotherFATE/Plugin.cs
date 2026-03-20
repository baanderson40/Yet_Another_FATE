using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using YetAnotherFATE.Commands;
using YetAnotherFATE.Config;
using YetAnotherFATE.Chains;
using YetAnotherFATE.Modules.Automator;
using YetAnotherFATE.Modules.Ux;
using YetAnotherFATE.Renderers;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Movement;
using YetAnotherFATE.Services.Zone;
using YetAnotherFATE.Services.Travel;
using YetAnotherFATE.Windows;

namespace YetAnotherFATE;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IFateTable FateTable { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    public Configuration Configuration { get; }
    public WindowSystem WindowSystem { get; } = new("YetAnotherFATE");

    private readonly ServiceProvider serviceProvider;
    private readonly IMainCommand mainCommand;
    private readonly UxModule uxModule;
    private readonly AutomatorModule automatorModule;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        var services = new ServiceCollection();
        BootstrapServices(services);
        serviceProvider = services.BuildServiceProvider();

        automatorModule = serviceProvider.GetRequiredService<AutomatorModule>();
        automatorModule.Initialize();

        uxModule = serviceProvider.GetRequiredService<UxModule>();
        uxModule.Initialize();

        mainCommand = serviceProvider.GetRequiredService<IMainCommand>();
        mainCommand.Register();

        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    public void Dispose()
    {
        mainCommand.Dispose();
        uxModule.Dispose();
        automatorModule.Dispose();
        serviceProvider.Dispose();
    }

    private void BootstrapServices(ServiceCollection services)
    {
        services.AddSingleton(PluginInterface);
        services.AddSingleton(WindowSystem);
        services.AddSingleton(Configuration);

        services.AddSingleton(TextureProvider);
        services.AddSingleton(CommandManager);
        services.AddSingleton(ClientState);
        services.AddSingleton(PlayerState);
        services.AddSingleton(DataManager);
        services.AddSingleton(Log);
        services.AddSingleton(Framework);
        services.AddSingleton(FateTable);
        services.AddSingleton(ObjectTable);
        services.AddSingleton(ChatGui);

        services.AddSingleton<ConfigWindow>();
        services.AddSingleton<MainRenderer>();
        services.AddSingleton<MainWindow>();

        services.AddSingleton<UxModule>();
        services.AddSingleton<AutomatorModule>();
        services.AddSingleton<AutomatorChain>();

        services.AddSingleton<IMainCommandDelegate, MainCommandDelegate>();
        services.AddSingleton<IMainCommand, MainCommand>();

        services.AddSingleton<IFateRepository>(provider => new FateRepository(
            provider.GetRequiredService<IClientState>(),
            provider.GetRequiredService<IPlayerState>(),
            provider.GetRequiredService<IObjectTable>(),
            provider.GetRequiredService<IFateTable>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<IFateDebugReporter, FateDebugReporter>();
        services.AddSingleton<IFateSelector>(provider => new FateSelector(
            provider.GetRequiredService<IPlayerState>(),
            provider.GetRequiredService<ITeleportAdvisor>(),
            provider.GetRequiredService<Configuration>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<IZoneService, ZoneService>();
        services.AddSingleton<IMovementService, MovementService>();
        services.AddSingleton<ITeleportAdvisor, TeleportAdvisor>();
    }
}
