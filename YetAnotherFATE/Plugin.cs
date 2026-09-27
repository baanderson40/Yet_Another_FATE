using Dalamud.Game.ClientState.Aetherytes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using YetAnotherFATE.Commands;
using YetAnotherFATE.Config;
using YetAnotherFATE.Chains;
using YetAnotherFATE.Modules.Automator;
using YetAnotherFATE.Modules.Automator.Handlers;
using YetAnotherFATE.Modules.Automator.Handlers.ParticipatingInFate.Handlers;
using YetAnotherFATE.Modules.Automator.Handlers.TravellingToFate.Handlers;
using YetAnotherFATE.Modules.Ux;
using YetAnotherFATE.Renderers;
using YetAnotherFATE.Services.Combat;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Fates.CombatHelper.LevelSync;
using YetAnotherFATE.Services.Fates.CombatHelper.Engagement;
using YetAnotherFATE.Services.Fates.CombatHelper.Targeter;
using YetAnotherFATE.Services.Movement;
using YetAnotherFATE.Services.Zone;
using YetAnotherFATE.Services.Travel;
using YetAnotherFATE.Windows;
using YetAnotherFATE.Services.Combat.RotationSolver;
using YetAnotherFATE.Services.Combat.Wrath;
using YetAnotherFATE.Services.Combat.BossMod;
using YetAnotherFATE.Services.Retainers;

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
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IAetheryteList AetheryteList { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;

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
        serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

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
        services.AddSingleton(GameGui);
        services.AddSingleton(AddonLifecycle);
        services.AddSingleton(AetheryteList);
        services.AddSingleton(Condition);
        services.AddSingleton(PartyList);

        services.AddSingleton<ConfigWindow>();
        services.AddSingleton<DebugRenderer>();
        services.AddSingleton<DebugWindow>();
        services.AddSingleton<MainRenderer>();
        services.AddSingleton<MainWindow>();

        services.AddSingleton<UxModule>();
        services.AddSingleton<AutomatorModule>();
        services.AddSingleton<AutomatorChain>();
        services.AddSingleton<IAutoRetainerClient, AutoRetainerClient>();
        services.AddSingleton<ILifestreamCharacterClient, LifestreamCharacterClient>();
        services.AddSingleton<TerritoryReturnRoute>();
        services.AddSingleton<AutoRetainerHandoffController>();
        services.AddSingleton<IAutomatorStateHandler, TravellingToFateStateHandler>();
        services.AddSingleton<IAutomatorStateHandler, ActivatingFateStateHandler>();
        services.AddSingleton<IAutomatorStateHandler, RecoveringStateHandler>();
        services.AddSingleton<IAutomatorStateHandler, ClearingCombatStateHandler>();
        services.AddSingleton<IAutomatorStateHandler, RotatingZoneStateHandler>();
        services.AddSingleton<ZoneRotationController>();
        services.AddSingleton<AutomaticTravelPolicy>();
        services.AddSingleton<IAutomatorStateHandler, ParticipatingInFateStateHandler>();

        services.AddSingleton<IMainCommandDelegate>(provider => new MainCommandDelegate(
            provider.GetRequiredService<UxModule>(),
            provider.GetRequiredService<IFateDebugReporter>(),
            provider.GetRequiredService<IFateRepository>(),
            provider.GetRequiredService<IFateSelector>(),
            provider.GetRequiredService<ITeleportExecutor>(),
            provider.GetRequiredService<INavmeshClient>(),
            provider.GetRequiredService<ITravelController>(),
            provider.GetRequiredService<IChatGui>(),
            provider.GetRequiredService<IFateRewardTracker>(),
            provider.GetRequiredService<ZoneRotationController>(),
            provider.GetRequiredService<AutoRetainerHandoffController>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<IMainCommand, MainCommand>();

        services.AddSingleton<IFateRepository>(provider => new FateRepository(
            provider.GetRequiredService<IClientState>(),
            provider.GetRequiredService<IPlayerState>(),
            provider.GetRequiredService<IObjectTable>(),
            provider.GetRequiredService<IFateTable>(),
            provider.GetRequiredService<IPluginLog>(),
            provider.GetRequiredService<IDataManager>()));
        services.AddSingleton<IFateCompletionTracker, FateCompletionTracker>();
        services.AddSingleton<IFateRewardTracker, FateRewardTracker>();
        services.AddSingleton<ITextAdvanceClient, TextAdvanceClient>();
        services.AddSingleton<IFateFollowUpTracker, FateFollowUpTracker>();
        services.AddSingleton<IFateActivationService>(provider => new FateActivationService(
            provider.GetRequiredService<IObjectTable>(),
            provider.GetRequiredService<IPluginLog>(),
            provider.GetRequiredService<ICondition>()));
        services.AddSingleton<ICombatPluginRegistry>(provider => new CombatPluginRegistry(
            provider.GetRequiredService<IDalamudPluginInterface>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<IRotationSolverBridge>(provider => new RotationSolverBridge(
            provider.GetRequiredService<IDalamudPluginInterface>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<IWrathComboBridge>(provider => new WrathComboBridge(
            provider.GetRequiredService<IDalamudPluginInterface>(),
            provider.GetRequiredService<IPlayerState>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<BossModPresetManager>();
        services.AddSingleton<IBossModObstacleMap, BossModObstacleMap>();
        services.AddSingleton<FateObstacleMapAssurance>();
        services.AddSingleton<IFateTargetTracker>(provider => new FateTargetTracker(
            provider.GetRequiredService<IObjectTable>(),
            provider.GetRequiredService<IClientState>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<IFateCombatHandler>(provider => new FateCombatHandler(
            provider.GetRequiredService<IFateTargetTracker>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<IFateDebugReporter, FateDebugReporter>();
        services.AddSingleton<IFateSelector>(provider => new FateSelector(
            provider.GetRequiredService<IPlayerState>(),
            provider.GetRequiredService<IDataManager>(),
            provider.GetRequiredService<IObjectTable>(),
            provider.GetRequiredService<ITeleportAdvisor>(),
            provider.GetRequiredService<Configuration>(),
            provider.GetRequiredService<IFateFollowUpTracker>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<IZoneService>(provider => new ZoneService(
            provider.GetRequiredService<IDataManager>(),
            provider.GetRequiredService<IAetheryteList>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<INavmeshClient>(provider => new NavmeshClient(
            provider.GetRequiredService<IDalamudPluginInterface>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<ITeleportExecutor>(provider => new TeleportExecutor(
            provider.GetRequiredService<IClientState>(),
            provider.GetRequiredService<ICondition>(),
            provider.GetRequiredService<IFramework>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<ICityReturnPlanner, CityReturnPlanner>();
        services.AddSingleton<ICityAethernetClient, LifestreamAethernetClient>();
        services.AddSingleton<CityDeathReturnRoute>();
        services.AddSingleton<ITravelController>(provider => new TravelController(
            provider.GetRequiredService<ITeleportExecutor>(),
            provider.GetRequiredService<INavmeshClient>(),
            provider.GetRequiredService<IClientState>(),
            provider.GetRequiredService<ICondition>(),
            provider.GetRequiredService<IObjectTable>(),
            provider.GetRequiredService<IFramework>(),
            provider.GetRequiredService<IGameGui>(),
            provider.GetRequiredService<IBossModObstacleMap>(),
            provider.GetRequiredService<AutomaticTravelPolicy>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<ILevelSyncService>(provider => new LevelSyncService(
            provider.GetRequiredService<IPlayerState>(),
            provider.GetRequiredService<ICondition>(),
            provider.GetRequiredService<IPluginLog>()));
        services.AddSingleton<ITeleportAdvisor>(provider => new TeleportAdvisor(
            provider.GetRequiredService<IZoneService>(),
            provider.GetRequiredService<Configuration>(),
            provider.GetRequiredService<IPluginLog>()));
    }
}
