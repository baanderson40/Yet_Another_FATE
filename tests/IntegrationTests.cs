using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using YetAnotherFATE;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Travel;
using YetAnotherFATE.Services.Retainers;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Chains;
using YetAnotherFATE.Modules.Automator.Handlers;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Services.Combat;
using YetAnotherFATE.Services.Combat.BossMod;
using YetAnotherFATE.Services.Combat.RotationSolver;
using YetAnotherFATE.Services.Combat.Wrath;
using YetAnotherFATE.Services.Fates.CombatHelper.Engagement;
using YetAnotherFATE.Services.Fates.CombatHelper.Targeter;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Conditions;
using YetAnotherFATE.Modules.Automator.Handlers.ParticipatingInFate.Handlers;
using YetAnotherFATE.Services.Fates.CombatHelper.LevelSync;
using YetAnotherFATE.Services.Zone;
using YetAnotherFATE.Data.Zone;
using System.Numerics;

class Program
{
    private sealed class RetainerConfigurationFixture
    {
        public int AdvanceTimer = 60;
        public bool MultiWaitForAll = true;
    }

    static int checks;
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
    static void Main()
    {
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            var root = Environment.GetEnvironmentVariable("DALAMUD_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
            var path = Path.Combine(root, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        Run();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        var pluginType = typeof(Plugin);
        var plugin = RuntimeHelpers.GetUninitializedObject(pluginType);
        var config = new Configuration();
        pluginType.GetField("<Configuration>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, config);
        var windowField = pluginType.GetField("<WindowSystem>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
        windowField.SetValue(plugin, RuntimeHelpers.GetUninitializedObject(windowField.FieldType));
        foreach (var property in pluginType.GetProperties(BindingFlags.Static | BindingFlags.NonPublic))
            if (property.PropertyType.IsInterface)
                property.SetValue(null, DispatchProxy.Create(property.PropertyType, typeof(Noop)));
        var services = new ServiceCollection();
        pluginType.GetMethod("BootstrapServices", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, [services]);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var types = pluginType.Assembly.GetTypes();
        int constructors = 0;
        foreach (var descriptor in services)
        {
            var implementation = descriptor.ImplementationType;
            if (implementation == null && descriptor.ImplementationFactory != null)
                implementation = types.Single(t => !t.IsAbstract && !t.IsInterface && descriptor.ServiceType.IsAssignableFrom(t));
            if (implementation == null) continue;
            var constructor = implementation.GetConstructors().Single();
            foreach (var parameter in constructor.GetParameters())
            {
                var type = parameter.ParameterType;
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)) type = type.GetGenericArguments()[0];
                Check(services.Any(d => d.ServiceType == type), $"Missing DI dependency: {implementation.Name} -> {type.Name}");
            }
            constructors++;
        }
        Console.WriteLine($"DI: validated {services.Count} registrations and {constructors} production constructors, including factory dependencies");

        var log = DispatchProxy.Create<IPluginLog, Noop>();
        var lifecycle = DispatchProxy.Create<IAddonLifecycle, Noop>();
        using var rewards = new FateRewardTracker(lifecycle, config, log);
        var args = (AddonArgs)RuntimeHelpers.GetUninitializedObject(typeof(AddonArgs));
        void Reward() => typeof(FateRewardTracker).GetMethod("OnReward", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rewards, [AddonEvent.PostSetup, args]);
        void Close() => typeof(FateRewardTracker).GetMethod("OnRewardFinalized", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rewards, [AddonEvent.PreFinalize, args]);
        Reward(); Check(rewards.CompletedCount == 0, "disabled reward ignored"); Close();
        rewards.Start(2); Reward(); Reward();
        Check(rewards.CompletedCount == 1 && !rewards.PendingStop, "duplicate PostSetup counted once");
        rewards.Start(3); Check(rewards.CompletedCount == 1 && rewards.Remaining == 2, "active run target preserves reward baseline");
        Close(); Reward(); Check(rewards.CompletedCount == 2, "reused addon address counts after finalize");
        rewards.Start(2); Check(rewards.PendingStop, "lowered target stops when already reached");
        rewards.CompleteSafeStop(); Check(!config.AutomatorEnabled && rewards.RunUntil == null, "safe stop ends session");
        Close(); rewards.Start(1); Check(rewards.CompletedCount == 0, "new session resets count");
        Reward(); Check(rewards.PendingStop, "new reward hits target");
        rewards.Stop();
        rewards.Start();
        Check(config.AutomatorEnabled && rewards.RunUntil == null && !rewards.PendingStop, "unlimited UI start resets run target");

        Check(new Configuration().FateMinTimeRemainingSeconds == 120, "reference minimum time default");
        var sceneMatcher = typeof(ActivatingFateStateHandler).GetMethod("MatchesInteractionScene", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((bool)sceneMatcher.Invoke(null, [77UL, 77UL, 12U])!, "owned SelectYesno matches NPC game object id");
        Check((bool)sceneMatcher.Invoke(null, [12UL, 77UL, 12U])!, "owned SelectYesno matches NPC entity id");
        Check(!(bool)sceneMatcher.Invoke(null, [99UL, 77UL, 12U])!, "unrelated SelectYesno is rejected");
        Check(!(bool)sceneMatcher.Invoke(null, [0UL, 77UL, 12U])!, "missing event scene is rejected");
        var promptMatcher = typeof(ActivatingFateStateHandler).GetMethod("MatchesActivationPrompt", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((bool)promptMatcher.Invoke(null, ["Slay earth sprites for their cores?", "Slay earth sprites for their cores."])!, "SelectYesno punctuation matches FATE objective");
        Check((bool)promptMatcher.Invoke(null, ["Slay earth sprites for their cores?\0", "slay earth sprites for their cores"])!, "SelectYesno metadata suffix matches FATE objective");
        Check(!(bool)promptMatcher.Invoke(null, ["Proceed with unrelated task?", "Slay earth sprites for their cores"])!, "unrelated SelectYesno objective is rejected");
        var fateNpcIdentity = typeof(FateTargetPolicy).GetMethod("IsFateNpcIdentity", BindingFlags.Static | BindingFlags.Public)!;
        Check((bool)fateNpcIdentity.Invoke(null, [77UL, 12U, 77U, 99U])!, "motivation NPC identity is excluded from FATE targets");
        Check((bool)fateNpcIdentity.Invoke(null, [77UL, 12U, 99U, 12U])!, "objective NPC entity identity is excluded from FATE targets");
        Check(!(bool)fateNpcIdentity.Invoke(null, [77UL, 12U, 99U, 98U])!, "unrelated NPC identity remains eligible for further filtering");
        var combatKind = typeof(FateTargetPolicy).GetMethod("IsCombatNpcKind", BindingFlags.Static | BindingFlags.Public)!;
        Check((bool)combatKind.Invoke(null, ["Combatant"])! && (bool)combatKind.Invoke(null, ["BNpcPart"])!, "combat BattleNpc kinds accepted");
        Check(!(bool)combatKind.Invoke(null, ["Pet"])! && !(bool)combatKind.Invoke(null, ["EventNpc"])!, "non-combat BattleNpc kinds rejected");
        var fateNameplate = typeof(FateTargetPolicy).GetMethod("IsKnownFateNpcNameplate", BindingFlags.Static | BindingFlags.Public)!;
        Check((bool)fateNameplate.Invoke(null, [60093U])! && (bool)fateNameplate.Invoke(null, [60732U])!, "known FATE NPC nameplates rejected");
        Check(!(bool)fateNameplate.Invoke(null, [0U])!, "ordinary nameplate remains eligible for further filtering");
        Check(FateTargetPolicy.GetForlornPriority(6737) == 1, "Forlorn Maiden NameId detected");
        Check(FateTargetPolicy.GetForlornPriority(6738) == 2, "The Forlorn NameId detected and preferred");
        Check(FateTargetPolicy.GetForlornPriority(1) == 0, "ordinary NameId has no Forlorn priority");
        var pi = DispatchProxy.Create<IDalamudPluginInterface, Noop>();
        var migrated = new Configuration { FateMinTimeRemainingSeconds = 240 };
        migrated.Initialize(pi); Check(migrated.Version == 3 && migrated.FateMinTimeRemainingSeconds == 120, "old erroneous default migrated");
        var custom = new Configuration { FateMinTimeRemainingSeconds = 75 };
        custom.Initialize(pi); Check(custom.FateMinTimeRemainingSeconds == 75, "custom minimum retained");
        var explicit240 = new Configuration { Version = 1, FateMinTimeRemainingSeconds = 240 };
        explicit240.Initialize(pi); Check(explicit240.FateMinTimeRemainingSeconds == 240, "versioned choice retained");
        var retainerReady = typeof(AutoRetainerClient).GetMethod("IsRetainerReady", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((bool)retainerReady.Invoke(null, [new long[] { 30, 45 }, 30L, true, 60L])!, "all retainers honor advance threshold");
        Check(!(bool)retainerReady.Invoke(null, [new long[] { 30, 90 }, 30L, true, 60L])!, "wait-for-all blocks on late retainer");
        Check((bool)retainerReady.Invoke(null, [new long[] { 90 }, 30L, false, 60L])!, "any-retainer mode uses closest ready retainer");
        var memberReader = typeof(AutoRetainerClient).GetMethod("ReadMember", BindingFlags.Static | BindingFlags.NonPublic)!;
        var fixture = new RetainerConfigurationFixture();
        Check((long)memberReader.MakeGenericMethod(typeof(long)).Invoke(null, [fixture.GetType(), fixture, "AdvanceTimer", 0L])! == 60,
            "AutoRetainer advance field is read");
        Check((bool)memberReader.MakeGenericMethod(typeof(bool)).Invoke(null, [fixture.GetType(), fixture, "MultiWaitForAll", false])!,
            "AutoRetainer wait-for-all field is read");
        Check((int)FateSortCriteria.HasBonus == 1 && (int)FateSortCriteria.Progress == 2 && (int)FateSortCriteria.Distance == 5 && (int)FateSortCriteria.TimeRemaining == 4, "persisted criterion values stable");
        var duplicatedPriorities = new Configuration
        {
            Version = 2,
            FateSortOrder =
            [
                new() { Criteria = FateSortCriteria.Distance, Descending = true },
                new() { Criteria = FateSortCriteria.Distance, Descending = false },
                new() { Criteria = FateSortCriteria.Level, Descending = true },
            ]
        };
        duplicatedPriorities.Initialize(pi);
        Check(duplicatedPriorities.Version == 3 && duplicatedPriorities.FateSortOrder.Count == 2
            && duplicatedPriorities.FateSortOrder[0].Criteria == FateSortCriteria.Distance
            && duplicatedPriorities.FateSortOrder[0].Descending
            && duplicatedPriorities.FateSortOrder[1].Criteria == FateSortCriteria.Level,
            "duplicate FATE priorities normalize while preserving first occurrence");
        var follow = new FateFollowUpTracker(config);
        var fate = default(ActiveFate) with { FateId = 10, TerritoryId = 100, HasSheetMetadata = true, HasFollowUp = true, LocationId = 9, Progress = 100 };
        follow.Begin(fate);
        Check(follow.IsPreferred(fate with { FateId = 11 }), "higher ID same location preferred");
        config.WaitForFollowUpFates = false;
        Check(!follow.IsWatching && !follow.IsPreferred(fate with { FateId = 11 }), "disabling followups removes existing preference");
        config.WaitForFollowUpFates = true;
        Check(!follow.IsPreferred(fate with { FateId = 9 }) && !follow.IsPreferred(fate with { FateId = 11, TerritoryId = 101 }), "lower ID and foreign territory rejected");
        follow.Clear(); Check(!follow.IsWatching, "followup cleanup");
        var completion = new FateCompletionTracker();
        completion.Observe(fate); Check(completion.RecordMissing(fate).Kind == FateCompletionKind.Completed, "observed 100 percent completes");
        completion.Observe(fate with { Progress = 50, TimeRemaining = TimeSpan.FromMinutes(1) });
        Check(completion.RecordMissing(fate).Kind == FateCompletionKind.Lost, "disappearance alone not completion");
        completion.Observe(fate with { StartTimeEpoch = 1 });
        Check(completion.RecordMissing(fate with { StartTimeEpoch = 2, Progress = 30, TimeRemaining = TimeSpan.FromSeconds(50) }).Kind == FateCompletionKind.Lost, "new occurrence cannot inherit old completion");
        completion.Reset(); Check(completion.LastResult == null, "completion reset");
        var advisor = Stub<ITeleportAdvisor>((m, a) => new TeleportAnalysis(null, default, 6, 15, 200, 0, 5, null, []));
        var selectorConfig = new Configuration();
        var selector = new FateSelector(Stub<IPlayerState>(), Stub<IDataManager>(), Stub<IObjectTable>(), advisor, selectorConfig, follow, log);
        var eligible = fate with { Progress = 20, Level = 50, Duration = 900, IsOnMap = true,
            State = Dalamud.Game.ClientState.Fates.FateState.Running, TimeRemaining = TimeSpan.FromSeconds(180), Name = "Urgent" };
        Check(selector.SelectBest([eligible]) != null, "urgent 180s candidate eligible by default");
        Check(selector.SelectBest([eligible with { TimeRemaining = TimeSpan.FromSeconds(120) }]) == null, "minimum boundary excluded");
        Check(selector.SelectBest([eligible with { State = Dalamud.Game.ClientState.Fates.FateState.Preparing, TimeRemaining = TimeSpan.FromSeconds(-1), MotivationNpc = 1 }]) != null, "unactivated negative time eligible");
        Check(selector.SelectBest([eligible with { IsOnMap = false }]) == null, "pending FATE excluded");
        selectorConfig.FateMaxProgress = 100;
        Check(selector.SelectBest([eligible with { Progress = 100 }]) == null, "finished collect cannot be selected again");
        selectorConfig.FateSortOrder = [new() { Criteria = FateSortCriteria.Progress, Descending = true }];
        var high = eligible with { FateId = 20, Progress = 80, DistanceFromPlayer = 2000 };
        Check(selector.SelectBest([eligible, high])?.Fate.FateId == 20, "configured priority beats diagnostic distance score");
        Check(selector.Evaluate([eligible, high]).Candidates[0].Fate.FateId == 20, "UI shares selector order");

        uint hp = 100; uint territory = 100; bool unconscious = false; int mapsCleared = 0; int textDisabled = 0; int combatResets = 0;
        var player = Stub<IPlayerCharacter>((m,a) => m.Name == "get_CurrentHp" ? hp : Noop.Default(m));
        var objects = Stub<IObjectTable>((m,a) => m.Name == "get_LocalPlayer" ? player : Noop.Default(m));
        var client = Stub<IClientState>((m,a) => m.Name == "get_IsLoggedIn" ? true : m.Name == "get_TerritoryType" ? territory : Noop.Default(m));
        var conditions = Stub<ICondition>((m,a) => m.Name == "get_Item" ? unconscious && (ConditionFlag)a![0]! == ConditionFlag.Unconscious : Noop.Default(m));
        var map = Stub<IBossModObstacleMap>((m,a) => { if (m.Name == "ClearTemporaryMap") mapsCleared++; return Noop.Default(m); });
        var text = Stub<ITextAdvanceClient>((m,a) => { if (m.Name == "Disable") textDisabled++; return Noop.Default(m); });
        var combat = Stub<IFateCombatHandler>((m,a) => { if (m.Name == "Reset") combatResets++; return Noop.Default(m); });
        var probe = new ProbeHandler();
        config.AutomatorEnabled = true;
        var automaticTravel = new AutomaticTravelPolicy(config, client, objects, Stub<IFateTable>(), conditions);
        using var chain = new AutomatorChain(Stub<ITravelController>(), log, conditions, client, objects, follow, completion, map,
            text, Stub<IFateRewardTracker>(), config, [probe], new BossModPresetManager(pi, Stub<ICombatPluginRegistry>(), config, log, Stub<IPlayerState>()),
            Stub<IRotationSolverBridge>(), Stub<IWrathComboBridge>(), combat,
            new FateObstacleMapAssurance(map, Stub<YetAnotherFATE.Services.Movement.INavmeshClient>()), automaticTravel);
        chain.Tick(); Check(chain.State == AutomatorState.SelectingFate, "chain enables");
        hp = 0; follow.Begin(fate); chain.Tick();
        Check(chain.State == AutomatorState.Recovering && mapsCleared > 0 && textDisabled > 0 && combatResets > 0 && !follow.IsWatching, "zero HP triggers global cleanup before handler");
        hp = 100; territory = 101; chain.Tick();
        Check(chain.State != AutomatorState.Recovering && chain.CurrentSelection == null && probe.Resets > 0, "manual territory takeover exits recovery without reissuing return");
        unconscious = true; chain.Tick(); Check(chain.State == AutomatorState.Recovering, "unconscious flag triggers global recovery");
        config.AutomatorEnabled = false; follow.Begin(fate); chain.Tick();
        Check(chain.State == AutomatorState.Disabled && !follow.IsWatching, "disable clears followup and state");
        unconscious = false;
        int rewardCount = 0;
        var collect = eligible with { Rule = (byte)FateRule.Collect, Progress = 100 };
        var repo = Stub<IFateRepository>((m,a) => m.Name == "EnumerateActiveFates" ? new[] { collect } : Noop.Default(m));
        var collectionRewards = Stub<IFateRewardTracker>((m,a) => m.Name == "get_CompletedCount" ? rewardCount : m.Name == "get_PendingStop" ? true : Noop.Default(m));
        var participant = new ParticipatingInFateStateHandler(repo, combat, Stub<ILevelSyncService>(), Stub<ICombatPluginRegistry>(),
            Stub<IRotationSolverBridge>(), Stub<IWrathComboBridge>(), new BossModPresetManager(pi, Stub<ICombatPluginRegistry>(), config, log, Stub<IPlayerState>()),
            config, Stub<ITravelController>(), follow, text, map, completion, conditions, collectionRewards, Stub<IGameGui>(), objects, automaticTravel, log);
        var context = new AutomatorContext();
        context.UpdateSelection(new FateSelection(collect, default, false, null, 0, "test", default), "test", log);
        context.TransitionTo(AutomatorState.Participating, "test", log);
        var ended = participant.Tick(context);
        Check(ended.NextState == AutomatorState.PostFate, "100 percent collection exits participation before disappearance");
        context.TransitionTo(AutomatorState.PostFate, "test", log);
        Check(participant.Tick(context).IsWait, "collection reward grace blocks immediate departure");
        follow.Begin(fate); rewardCount = 1;
        Check(participant.Tick(context).NextState == AutomatorState.SelectingFate, "observed reward ends grace and pending stop bypasses followup wait");
        participant.Reset();
        Check((DateTimeOffset)typeof(ParticipatingInFateStateHandler).GetField("collectionEndedAt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(participant)! == default, "participant reset removes stale grace");
        TestDeathReturn(log, pi);
        TestTerritoryReturn();
        TestCityDeathReturn(log);
        RotationTests.Run();
        Console.WriteLine($"PASS: {checks} integration assertions against compiled production assembly");
    }

    static void Set(object target, string field, object? value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    static void TestDeathReturn(IPluginLog log, IDalamudPluginInterface pi)
    {
        var now = DateTimeOffset.UtcNow;
        var death = new DeathRecoveryContext(100, new Vector3(500, 0, 500), null);
        var near = new AetheryteSummary(7, 100, "Near", 0, new(501, 0, 501), "test", true, false, 2, 99999, false);
        var far = near with { AetheryteId = 8, Position = new(100, 0, 100), GilCost = 1 };
        var zone = new ZoneInfo(100, "Death zone", 0, true, true, far, [near, far], [far, near], true, false, default, now);
        ZoneInfo? availableZone = zone;
        var zones = Stub<IZoneService>((m,a) => availableZone);
        int issued = 0; TeleportRecommendation requested = default;
        var executorState = TeleportState.Idle;
        var requestResult = TeleportRequestResult.Issued;
        var executor = Stub<ITeleportExecutor>((m,a) => {
            if (m.Name == "get_State") return executorState;
            if (m.Name == "RequestTeleport") { issued++; requested = (TeleportRecommendation)a![0]!; return requestResult; }
            return Noop.Default(m);
        });
        var route = new DeathReturnRoute(zones, executor);
        Check(route.Tick(death, 200, false, now) == DeathReturnResult.Waiting && issued == 0, "no teleport during loading");
        route.Tick(death, 200, true, now.AddSeconds(1));
        route.Tick(death, 200, true, now.AddSeconds(2));
        Check(issued == 1 && requested.AetheryteId == 7 && requested.SubIndex == 2 && requested.ShouldTeleport && requested.EstimatedGilCost == 99999, "mandatory return chooses closest unlocked destination preserving subindex without profitability/cost heuristic");
        executorState = TeleportState.Completed;
        Check(route.Tick(death, 200, true, now.AddSeconds(3)) == DeathReturnResult.Waiting, "executor completion cannot prove destination arrival");
        Check(route.Tick(death, 100, false, now.AddSeconds(4)) == DeathReturnResult.Waiting, "correct territory still waits for loading");
        route.Tick(death, 100, true, now.AddSeconds(5));
        Check(route.Tick(death, 100, true, now.AddSeconds(6)) == DeathReturnResult.Arrived && issued == 1, "settled destination arrives without extra teleport");
        route.Reset(); executorState = TeleportState.Idle; issued = 0;
        route.Tick(death, 100, true, now);
        Check(route.Tick(death, 100, true, now.AddSeconds(1)) == DeathReturnResult.Arrived && issued == 0, "same-zone raise requires no teleport");
        route.Reset(); availableZone = zone with { UnlockedAetherytes = [near with { TerritoryId = 999 }, near with { AetheryteId = 0 }] };
        route.Tick(death, 200, true, now);
        Check(route.Tick(death, 200, true, now.AddSeconds(1)) == DeathReturnResult.Failed && issued == 0, "wrong-zone and zero-ID targets rejected");
        availableZone = null; route.Reset(); route.Tick(death, 200, true, now);
        Check(route.Tick(death, 200, true, now.AddSeconds(1)) == DeathReturnResult.Failed, "missing zone metadata fails explicitly");
        availableZone = zone with { UnlockedAetherytes = [] }; route.Reset(); route.Tick(death, 200, true, now);
        Check(route.Tick(death, 200, true, now.AddSeconds(1)) == DeathReturnResult.Failed, "locked aetherytes cannot be used");
        availableZone = zone with { UnlockedAetherytes = [near with { HasPosition = false }] }; route.Reset(); route.Tick(death, 200, true, now);
        route.Tick(death, 200, true, now.AddSeconds(1));
        Check(issued == 1, "valid unlocked teleport works without optional position metadata");
        availableZone = zone;
        foreach (var response in new[] { TeleportRequestResult.Issued, TeleportRequestResult.Failed })
        {
            route.Reset(); issued = 0; requestResult = response;
            route.Tick(death, 200, true, now);
            for (var i = 1; i <= 95; i++) route.Tick(death, 200, true, now.AddSeconds(i));
            Check(issued == 3 && route.Failure != null, $"{response}: three-attempt cap latches failure");
        }
        foreach (var response in new[] { TeleportRequestResult.Blocked, TeleportRequestResult.Busy })
        {
            route.Reset(); issued = 0; requestResult = response;
            route.Tick(death, 200, true, now); route.Tick(death, 200, true, now.AddSeconds(1));
            Check(route.Tick(death, 200, true, now.AddMinutes(2)) == DeathReturnResult.Failed, $"{response}: bounded readiness failure");
        }
        route.Reset(); issued = 0;
        route.Tick(death, 200, false, now);
        Check(route.Tick(death, 100, false, now.AddMinutes(2)) == DeathReturnResult.Failed && issued == 0, "loading timeout bounded even in destination");
        route.Reset(); Check(route.Failure == null, "reset clears route failure and retry state");

        // Real chain + recovery handler + route, with game-facing interfaces faked.
        uint hp = 100, territory = 100; bool loading = false, loggedIn = true, loaded = true;
        int queries = 0, moves = 0, cancels = 0;
        TravelJob? manualJob = null;
        var fate = default(ActiveFate) with { FateId = 55, TerritoryId = 100, StartTimeEpoch = 123,
            Center = new(500, 0, 500), Progress = 60, State = Dalamud.Game.ClientState.Fates.FateState.Running };
        ActiveFate[] fates = [fate];
        var selection = new FateSelection(fate, fate.Center, true, 8, 0, "before death", requested);
        var player = Stub<IPlayerCharacter>((m,a) => m.Name == "get_CurrentHp" ? hp : m.Name == "get_Position" ? death.Position : Noop.Default(m));
        var objects = Stub<IObjectTable>((m,a) => m.Name == "get_LocalPlayer" ? player : Noop.Default(m));
        var client = Stub<IClientState>((m,a) => m.Name == "get_IsLoggedIn" ? loggedIn : m.Name == "get_TerritoryType" ? territory : Noop.Default(m));
        var playerState = Stub<IPlayerState>((m,a) => m.Name == "get_IsLoaded" ? loaded : Noop.Default(m));
        var conditions = Stub<ICondition>((m,a) => m.Name == "get_Item" ? loading && (ConditionFlag)a![0]! == ConditionFlag.BetweenAreas51 : Noop.Default(m));
        var repo = Stub<IFateRepository>((m,a) => { if (m.Name == "EnumerateActiveFates") { queries++; return fates; } return Noop.Default(m); });
        FateSelection moved = default;
        var travel = Stub<ITravelController>((m,a) => { if (m.Name == "get_CurrentJob") return manualJob; if (m.Name == "RequestTravel") { moves++; moved = (FateSelection)a![0]!; } if (m.Name == "Cancel") cancels++; return Noop.Default(m); });
        var config = new Configuration { AutomatorEnabled = true, TeleportEnabled = false, TeleportMaxGilCost = 1 };
        var boss = new BossModPresetManager(pi, Stub<ICombatPluginRegistry>(), config, log, playerState);
        var text = Stub<ITextAdvanceClient>(); var combat = Stub<IFateCombatHandler>(); var map = Stub<IBossModObstacleMap>();
        var rotation = Stub<IRotationSolverBridge>(); var wrath = Stub<IWrathComboBridge>();
        var recovery = new RecoveringStateHandler(conditions, travel, repo, Stub<IGameGui>(), objects, Stub<IPartyList>(), text, boss, rotation, wrath, combat, map,
            client, playerState, zones, executor, log);
        var follow = new FateFollowUpTracker(config);
        using var chain = new AutomatorChain(travel, log, conditions, client, objects, follow, new FateCompletionTracker(), map, text,
            Stub<IFateRewardTracker>(), config, [recovery, new ProbeHandler()], boss, rotation, wrath, combat,
            new FateObstacleMapAssurance(map, Stub<YetAnotherFATE.Services.Movement.INavmeshClient>()),
            new AutomaticTravelPolicy(config, client, objects, Stub<IFateTable>(), conditions));
        var context = (AutomatorContext)typeof(AutomatorChain).GetField("context", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chain)!;
        var handlerRoute = typeof(RecoveringStateHandler).GetField("returnRoute", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recovery)!;
        void Settle() => Set(handlerRoute, "readySince", DateTimeOffset.UtcNow.AddSeconds(-2));
        chain.Tick(); context.UpdateSelection(selection, "test", log);
        hp = 0; loading = true; chain.Tick();
        Check(chain.State == AutomatorState.Recovering && context.DeathRecovery is { TerritoryId: 100 } saved && saved.Position == death.Position && saved.Selection == selection, "death snapshot captures territory position and FATE before release");
        territory = 200; hp = 100; fates = []; chain.Tick();
        Check(chain.State == AutomatorState.Recovering && context.CurrentSelection == selection && queries == 0, "homepoint zoning retains target and never queries foreign FATE table");
        loading = false; issued = 0; requestResult = TeleportRequestResult.Issued;
        chain.Tick(); Settle(); chain.Tick();
        Check(issued == 1 && moves == 0 && queries == 0, "cross-zone recovery teleports despite disabled optional teleport and absent local FATE");
        territory = 100; loading = true; chain.Tick();
        Check(chain.State == AutomatorState.Recovering && moves == 0, "return zoning does not reset or prematurely route");
        loading = false; loaded = false; chain.Tick(); Settle(); chain.Tick();
        Check(moves == 0 && queries == 0, "player-state load gates revalidation");
        loaded = true; fates = [fate with { Progress = 90, Center = new(600, 0, 600) }]; chain.Tick(); Settle(); chain.Tick();
        Check(chain.State == AutomatorState.Travelling && moves == 1 && moved.Destination == fates[0].Center && context.ReturningAfterRecovery, "destination arrival refreshes active FATE and resumes existing travel");
        Check(!moved.ShouldTeleport && moved.Teleport.AetheryteId == null && context.DeathRecovery == null, "handoff clears stale teleport and death snapshot");

        foreach (var active in new ActiveFate[][] { [], [fate with { StartTimeEpoch = 124 }], [fate with { TerritoryId = 200 }], [fate with { Progress = 100 }], [fate with { State = Dalamud.Game.ClientState.Fates.FateState.Ended }] })
        {
            recovery.Reset(); context.Reset(log); context.UpdateSelection(selection, "test", log);
            context.DeathRecovery = death with { Selection = selection }; context.TransitionTo(AutomatorState.Recovering, "test", log);
            fates = active; recovery.Tick(context); Settle(); var result = recovery.Tick(context);
            Check(result.NextState == AutomatorState.SelectingFate && context.CurrentSelection == null && !context.ReturningAfterRecovery, "missing/changed/foreign/completed/ended FATE returns to normal selection");
        }
        recovery.Reset(); context.Reset(log); context.DeathRecovery = death;
        context.TransitionTo(AutomatorState.Recovering, "test", log); fates = [];
        recovery.Tick(context); Settle();
        Check(recovery.Tick(context).NextState == AutomatorState.SelectingFate && context.DeathRecovery == null, "death without selected FATE still completes zone return");

        recovery.Reset(); context.Reset(log); context.DeathRecovery = death;
        context.TransitionTo(AutomatorState.Recovering, "test", log); territory = 200;
        manualJob = new TravelJob(Guid.NewGuid(), selection, TravelRequestKind.DebugForceRun, now, 12);
        var issuedBeforeManual = issued; recovery.Tick(context); Settle(); recovery.Tick(context);
        Check(recovery.Phase == RecoveryPhase.Failed && issued == issuedBeforeManual, "manual travel cancels automatic death return without issuing teleport");
        manualJob = null;

        foreach (var logout in new[] { false, true })
        foreach (var alreadyIssued in new[] { false, true })
        {
            recovery.Reset(); context.Reset(log); context.DeathRecovery = death with { Selection = selection };
            context.TransitionTo(AutomatorState.Recovering, "test", log); territory = 200;
            config.AutomatorEnabled = true; loggedIn = true; chain.Tick();
            if (alreadyIssued) { Settle(); chain.Tick(); }
            int before = issued;
            if (logout) loggedIn = false; else config.AutomatorEnabled = false;
            chain.Tick(); Settle(); chain.Tick();
            Check(chain.State == AutomatorState.Disabled && context.DeathRecovery == null && recovery.Phase == RecoveryPhase.Idle && issued == before, $"{(logout ? "logout" : "disable")} cancels pending/issued return and discards context");
        }
        Check(cancels > 0, "recovery/disable cancel automatic movement");

        // Executor lifecycle checks avoid native calls while exercising production transitions.
        using var nativeExecutor = new TeleportExecutor(client, conditions, Stub<IFramework>(), log);
        void UpdateExecutor() => typeof(TeleportExecutor).GetMethod("OnFrameworkUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(nativeExecutor, [Stub<IFramework>()]);
        loggedIn = true; territory = 200; loading = true;
        Set(nativeExecutor, "state", TeleportState.Loading); Set(nativeExecutor, "startingTerritory", (ushort)100); Set(nativeExecutor, "issuedAt", DateTimeOffset.UtcNow);
        UpdateExecutor(); Check(nativeExecutor.State == TeleportState.Loading, "BetweenAreas51 alone prevents premature teleport completion");
        loading = false; UpdateExecutor(); Check(nativeExecutor.State == TeleportState.Completed, "executor completes after both loading flags clear");
        UpdateExecutor(); Check(nativeExecutor.State == TeleportState.Idle, "completed executor becomes reusable");
        loading = true; Set(nativeExecutor, "state", TeleportState.Loading); Set(nativeExecutor, "issuedAt", DateTimeOffset.UtcNow.AddMinutes(-1));
        UpdateExecutor(); Check(nativeExecutor.State == TeleportState.Failed, "executor loading has bounded failure");
        loggedIn = false; UpdateExecutor(); Check(nativeExecutor.State == TeleportState.Idle, "logout resets stale executor ownership");
        Check(nativeExecutor.RequestTeleport(requested with { AetheryteId = 0 }) == TeleportRequestResult.InvalidTarget, "executor rejects zero target before native call");
        Check(nativeExecutor.RequestTeleport(requested) == TeleportRequestResult.ClientUnavailable, "executor rejects logged-out request");
        loggedIn = true;
        Check(nativeExecutor.RequestTeleport(requested) == TeleportRequestResult.Blocked, "executor rejects loading request before native call");
    }
    static void TestCityDeathReturn(IPluginLog log)
    {
        var now = DateTimeOffset.UtcNow;
        var death = new DeathRecoveryContext(478, new(100, 0, 100), null);
        var gateway = new AetheryteSummary(75, 4780, "Gateway", 0, new(20, 0, 20), "test", true, true, 2, 1000, false);
        var other = gateway with { AetheryteId = 99, GilCost = 1 };
        var zone = new ZoneInfo(gateway.TerritoryId, "City", 0, true, true, gateway, [gateway, other], [other, gateway], false, true, default, now);
        bool direct = false, locked = false;
        var zones = Stub<IZoneService>((m,a) => (uint)a![0]! == gateway.TerritoryId ? (locked ? zone with { UnlockedAetherytes = [] } : zone) :
            direct ? zone with { TerritoryId = death.TerritoryId, UnlockedAetherytes = [gateway with { TerritoryId = death.TerritoryId }] } : null);
        CityReturnPlan? plan = new(gateway, 90);
        int planned = 0, teleported = 0, requests = 0, aborts = 0; uint active = 0, destination = 0;
        TeleportRecommendation teleport = default;
        var executor = Stub<ITeleportExecutor>((m,a) => {
            if (m.Name == "get_State") return TeleportState.Idle;
            if (m.Name == "RequestTeleport") { teleported++; teleport = (TeleportRecommendation)a![0]!; return TeleportRequestResult.Issued; }
            return Noop.Default(m);
        });
        var planner = Stub<ICityReturnPlanner>((m,a) => { planned++; return plan; });
        var state = CityAethernetState.Idle; bool supported = true, accepted = true, throwing = false, abortWorks = true;
        var client = Stub<ICityAethernetClient>((m,a) => {
            if (m.Name == "get_State") return state;
            if (m.Name == "get_ActiveAetheryte") return active;
            if (m.Name == "CanRoute") return supported;
            if (m.Name == "Request") { requests++; destination = (uint)a![0]!; if (accepted) state = CityAethernetState.Busy; if (throwing) throw new Exception("partial enqueue"); return accepted; }
            if (m.Name == "Abort") { aborts++; if (abortWorks) state = CityAethernetState.Idle; return abortWorks; }
            return Noop.Default(m);
        });
        var city = new CityDeathReturnRoute(planner, client, zones, executor);
        var route = new DeathReturnRoute(zones, executor, city);
        void Reset() { route.Reset(); state = CityAethernetState.Idle; active = 0; planned = teleported = requests = aborts = 0; supported = accepted = abortWorks = true; throwing = locked = direct = false; plan = new(gateway, 90); }
        void Start() { route.Tick(death, 200, true, now); route.Tick(death, 200, true, now.AddSeconds(1)); route.Tick(death, 200, true, now.AddSeconds(2)); }
        void Issue() { active = gateway.AetheryteId; route.Tick(death, gateway.TerritoryId, false, now.AddSeconds(3)); route.Tick(death, gateway.TerritoryId, true, now.AddSeconds(4)); route.Tick(death, gateway.TerritoryId, true, now.AddSeconds(5)); route.Tick(death, gateway.TerritoryId, true, now.AddSeconds(6)); }

        Reset(); Start();
        Check(teleported == 1 && teleport.AetheryteId == gateway.AetheryteId && teleport.SubIndex == 2 && requests == 0, "city return selects exact unlocked gateway, not cheaper same-zone alternate");
        Issue();
        Check(requests == 1 && destination == 90, "city return waits for loading and exact active gateway then requests numeric aethernet destination once");
        Check(route.Tick(death, death.TerritoryId, true, now.AddSeconds(7)) == DeathReturnResult.Waiting, "destination territory while provider busy cannot hand off movement");
        state = CityAethernetState.Idle;
        Check(route.Tick(death, death.TerritoryId, false, now.AddSeconds(8)) == DeathReturnResult.Waiting, "provider idle and destination still wait for loading");
        route.Tick(death, death.TerritoryId, true, now.AddSeconds(9));
        Check(route.Tick(death, death.TerritoryId, true, now.AddSeconds(10)) == DeathReturnResult.Arrived, "settled actual destination and drained provider complete city return");
        route.Reset(); Check(aborts == 0, "completed route releases provider ownership without aborting");

        Reset(); direct = true; Start();
        Check(teleported == 1 && planned == 0 && requests == 0, "direct destination unlock always takes precedence over optional city route");
        Reset(); state = CityAethernetState.Unavailable; Start();
        Check(route.Failure != null && teleported == 0 && requests == 0, "absent optional provider fails before gateway teleport");
        Reset(); supported = false; Start();
        Check(route.Failure != null && teleported == 0, "missing destination endpoint fails before gateway teleport");
        Reset(); plan = null; Start();
        Check(route.Failure != null && teleported == 0, "no supported city graph fails without speculative commands");
        Reset(); locked = true; Start();
        Check(route.Failure != null && teleported == 0, "gateway unlock is revalidated before dispatch");
        Reset(); state = CityAethernetState.Busy; Start();
        Check(teleported == 0 && requests == 0, "foreign provider work blocks gateway and aethernet dispatch");
        route.Tick(death, 200, true, now.AddMinutes(2)); route.Reset();
        Check(aborts == 0, "timeout and reset never abort unowned provider work");

        Reset(); active = 999;
        route.Tick(death, gateway.TerritoryId, true, now); route.Tick(death, gateway.TerritoryId, true, now.AddSeconds(1)); route.Tick(death, gateway.TerritoryId, true, now.AddSeconds(2));
        Check(teleported == 1 && requests == 0, "already in gateway city but away from network forces exact gateway teleport");

        Reset(); Start(); Issue(); state = CityAethernetState.Idle;
        Check(route.Tick(death, gateway.TerritoryId, true, now.AddSeconds(7)) == DeathReturnResult.Waiting, "queue drain alone cannot prove destination arrival");
        Check(route.Tick(death, gateway.TerritoryId, true, now.AddSeconds(40)) == DeathReturnResult.Failed && requests == 1, "idle no-op or locked shard fails after bounded transition grace without retry spam");
        Check(aborts == 0, "idle failed request has no remaining owned queue to abort");
        Reset(); Start(); Issue();
        Check(route.Tick(death, gateway.TerritoryId, true, now.AddSeconds(40)) == DeathReturnResult.Failed && aborts == 1, "stuck busy aethernet queue is aborted on timeout");
        Reset(); Start(); accepted = false; Issue();
        Check(route.Failure != null && aborts == 0, "rejected request never claims provider queue");
        Reset(); Start(); throwing = true; Issue();
        Check(route.Failure != null && aborts == 1, "throw after partial enqueue cancels owned work");
        Reset(); Start(); Issue(); route.Reset(); route.Reset();
        Check(aborts == 1, "reset cancels owned aethernet exactly once");
        Reset(); Start(); Issue(); state = CityAethernetState.Unavailable; route.Reset();
        Check(aborts == 0, "provider loss preserves pending cancellation without invalid IPC");
        state = CityAethernetState.Busy; route.Reset();
        Check(aborts == 1, "provider recovery retries pending cancellation");
        Reset(); Start(); Issue(); abortWorks = false; route.Reset();
        abortWorks = true; route.Reset();
        Check(aborts == 2, "transient abort failure keeps ownership for cleanup retry");
        Reset(); Start(); Issue(); abortWorks = false; route.Reset(); direct = true;
        var beforeCleanupTeleport = teleported;
        route.Tick(death, 200, true, now); route.Tick(death, 200, true, now.AddSeconds(2));
        Check(teleported == beforeCleanupTeleport, "pending city cancellation blocks replacement direct return");
        abortWorks = true; route.Reset();
        Reset(); plan = new(gateway, 0); Start(); Issue();
        Check(requests == 1 && destination == 0, "Firmament plan selects dedicated verified endpoint");
        route.Reset();

        // Exercise production IPC signatures through fake Dalamud call gates.
        var gateNames = new List<string>(); int invokeAborts = 0; uint requestedNode = 0;
        bool hasLifecycle = true, hasEndpoint = true, busy = false;
        var ipc = Stub<IDalamudPluginInterface>((m,a) => {
            if (m.Name != "GetIpcSubscriber") return Noop.Default(m);
            string name = (string)a![0]!; gateNames.Add(name);
            var gate = DispatchProxy.Create(m.ReturnType, typeof(Noop));
            ((Noop)gate).Handler = (method,args) => {
                if (method.Name == "get_HasAction") return hasLifecycle;
                if (method.Name == "get_HasFunction") return name.Contains("AethernetTeleport") ? hasEndpoint : hasLifecycle;
                if (method.Name == "InvokeAction") { invokeAborts++; return null; }
                if (method.Name == "InvokeFunc") {
                    if (name == "Lifestream.GetActiveAetheryte") return 75u;
                    if (name == "Lifestream.IsBusy") return busy;
                    if (name == "Lifestream.AethernetTeleportById") requestedNode = (uint)args![0]!;
                    return true;
                }
                return Noop.Default(method);
            };
            return gate;
        });
        var bridge = new LifestreamAethernetClient(ipc, log);
        Check(bridge.State == CityAethernetState.Idle && bridge.ActiveAetheryte == 75, "real Lifestream bridge binds lifecycle and numeric active-node getters");
        Check(bridge.CanRoute(90) && bridge.Request(90) && requestedNode == 90, "real bridge invokes verified uint-to-bool aethernet IPC");
        Check(bridge.CanRoute(0) && bridge.Request(0) && gateNames.Contains("Lifestream.AethernetTeleportToFirmament"), "real bridge uses dedicated Firmament signature");
        Check(bridge.Abort() && invokeAborts == 1, "real bridge invokes Abort as action");
        busy = true; Check(!bridge.Request(91) && requestedNode == 90, "bridge refuses foreign busy queue");
        hasLifecycle = false; Check(bridge.State == CityAethernetState.Unavailable, "bridge requires cancellation lifecycle before declaring availability");
        hasEndpoint = false; Check(!bridge.CanRoute(90) && !bridge.CanRoute(0), "bridge probes actual destination endpoint availability");

        foreach (var exit in new[] { "complete", "disable", "logout", "manual", "death", "dispose" })
        {
            Reset();
            uint hp = 100, territory = death.TerritoryId; bool loggedIn = true, loading = false;
            int fateQueries = 0; TravelJob? manual = null;
            var config = new Configuration { AutomatorEnabled = true, TeleportEnabled = false };
            var objects = Stub<IObjectTable>((m,a) => m.Name == "get_LocalPlayer" ?
                Stub<IPlayerCharacter>((p,b) => p.Name == "get_CurrentHp" ? hp : p.Name == "get_Position" ? death.Position : Noop.Default(p)) : Noop.Default(m));
            var gameClient = Stub<IClientState>((m,a) => m.Name == "get_IsLoggedIn" ? loggedIn : m.Name == "get_TerritoryType" ? territory : Noop.Default(m));
            var conditions = Stub<ICondition>((m,a) => m.Name == "get_Item" ? loading && (ConditionFlag)a![0]! == ConditionFlag.BetweenAreas51 : Noop.Default(m));
            var travel = Stub<ITravelController>((m,a) => m.Name == "get_CurrentJob" ? manual : Noop.Default(m));
            var repo = Stub<IFateRepository>((m,a) => { if (m.Name == "EnumerateActiveFates") { fateQueries++; return Array.Empty<ActiveFate>(); } return Noop.Default(m); });
            var text = Stub<ITextAdvanceClient>(); var combat = Stub<IFateCombatHandler>(); var map = Stub<IBossModObstacleMap>();
            var rotation = Stub<IRotationSolverBridge>(); var wrath = Stub<IWrathComboBridge>();
            var boss = new BossModPresetManager(Stub<IDalamudPluginInterface>(), Stub<ICombatPluginRegistry>(), config, log, Stub<IPlayerState>());
            var handler = new RecoveringStateHandler(conditions, travel, repo, Stub<IGameGui>(), objects, Stub<IPartyList>(), text, boss, rotation, wrath, combat, map,
                gameClient, Stub<IPlayerState>((m,a) => m.Name == "get_IsLoaded" ? true : Noop.Default(m)), zones, executor, log, city);
            using var chain = new AutomatorChain(travel, log, conditions, gameClient, objects, new FateFollowUpTracker(config), new FateCompletionTracker(), map, text,
                Stub<IFateRewardTracker>(), config, [handler, new ProbeHandler()], boss, rotation, wrath, combat,
                new FateObstacleMapAssurance(map, Stub<YetAnotherFATE.Services.Movement.INavmeshClient>()),
                new AutomaticTravelPolicy(config, gameClient, objects, Stub<IFateTable>(), conditions));
            var outer = typeof(RecoveringStateHandler).GetField("returnRoute", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
            var inner = typeof(CityDeathReturnRoute).GetField("gatewayRoute", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(city)!;
            void Settle() { chain.Tick(); Set(outer, "readySince", DateTimeOffset.UtcNow.AddSeconds(-2)); Set(inner, "readySince", DateTimeOffset.UtcNow.AddSeconds(-2)); chain.Tick(); }
            chain.Tick(); hp = 0; loading = true; chain.Tick(); hp = 100; territory = 200; loading = true; chain.Tick(); loading = false;
            Settle(); Settle();
            Check(teleported == 1 && chain.State == AutomatorState.Recovering, $"{exit}: real chain enters mandatory city gateway return");
            territory = gateway.TerritoryId; loading = true; chain.Tick(); loading = false; active = gateway.AetheryteId;
            Settle(); Settle();
            Check(requests == 1 && chain.State == AutomatorState.Recovering && fateQueries == 0, $"{exit}: intermediate city preserves recovery without querying foreign FATEs");
            switch (exit)
            {
                case "complete":
                    territory = death.TerritoryId; Settle();
                    Check(chain.State == AutomatorState.Recovering, "real recovery waits for owned aethernet queue to finish even in destination");
                    state = CityAethernetState.Idle; Settle();
                    Check(chain.State == AutomatorState.SelectingFate && aborts == 0, "real city handoff resumes selection after verified completion");
                    break;
                case "disable": config.AutomatorEnabled = false; chain.Tick(); break;
                case "logout": loggedIn = false; chain.Tick(); break;
                case "manual": manual = new TravelJob(Guid.NewGuid(), default, TravelRequestKind.DebugForceRun, now, 12); chain.Tick(); break;
                case "death": hp = 0; chain.Tick(); break;
                case "dispose": chain.Dispose(); break;
            }
            if (exit != "complete") Check(aborts == 1 && state == CityAethernetState.Idle, $"{exit}: real chain/handler cancels owned optional provider work");
        }
    }

    static void TestTerritoryReturn()
    {
        var now = DateTimeOffset.UtcNow;
        var destination = new Vector3(500, 0, 500);
        var near = new AetheryteSummary(7, 100, "Near", 0, new(501, 0, 501), "test", true, false, 2, 99999, false);
        var zone = new ZoneInfo(100, "Origin zone", 0, true, true, near, [near], [near], true, false, default, now);
        var zones = Stub<IZoneService>((m, a) => zone);
        var issued = 0;
        var executor = Stub<ITeleportExecutor>((m, a) =>
        {
            if (m.Name == "get_State") return TeleportState.Idle;
            if (m.Name == "RequestTeleport") { issued++; return TeleportRequestResult.Issued; }
            return Noop.Default(m);
        });
        var route = new TerritoryReturnRoute(zones, executor);

        route.Tick(100, destination, 200, false, now);
        route.Tick(100, destination, 200, true, now.AddSeconds(1));
        route.Tick(100, destination, 200, true, now.AddSeconds(2));
        Check(issued == 1, "same-character handoff requests return to saved origin territory");

        route.Reset();
        issued = 0;
        route.Tick(100, destination, 100, true, now);
        Check(route.Tick(100, destination, 100, true, now.AddSeconds(1)) == TerritoryReturnResult.Arrived && issued == 0,
            "character return already in origin territory skips extra teleport");

        var missingRoute = new TerritoryReturnRoute(Stub<IZoneService>((m, a) => null), executor);
        missingRoute.Tick(100, destination, 200, true, now);
        Check(missingRoute.Tick(100, destination, 200, true, now.AddSeconds(1)) == TerritoryReturnResult.Failed,
            "missing origin route fails without resuming in wrong territory");
    }

    static T Stub<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var value = DispatchProxy.Create<T, Noop>();
        ((Noop)(object)value).Handler = handler;
        return value;
    }
}
public class Noop : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?>? Handler;
    public static object? Default(MethodInfo method) => method.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => Handler != null ? Handler(method!, args) : Default(method!);
}
class ProbeHandler : IAutomatorStateHandler
{
    public int Resets;
    public bool CanHandle(AutomatorState state) => true;
    public AutomatorStateHandlerResult Tick(AutomatorContext context) => AutomatorStateHandlerResult.Wait("test probe");
    public void Reset() => Resets++;
}
