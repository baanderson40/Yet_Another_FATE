using System.Reflection;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using YetAnotherFATE.Chains;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Automator;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Data.Zone;
using YetAnotherFATE.Modules.Automator.Handlers;
using YetAnotherFATE.Modules.Automator.Handlers.ParticipatingInFate.Handlers;
using YetAnotherFATE.Services.Fates.CombatHelper.LevelSync;
using YetAnotherFATE.Services.Combat;
using YetAnotherFATE.Services.Combat.BossMod;
using YetAnotherFATE.Services.Combat.RotationSolver;
using YetAnotherFATE.Services.Combat.Wrath;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Fates.CombatHelper.Engagement;
using YetAnotherFATE.Services.Travel;
using YetAnotherFATE.Services.Zone;

static class RotationTests
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception("Rotation: " + name); checks++; }
    static T Stub<T>(Func<MethodInfo, object?[]?, object?>? action = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, Noop>();
        ((Noop)(object)proxy).Handler = action;
        return proxy;
    }
    static void Set(object obj, string field, object? value) => obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    static ZoneInfo Zone(uint id, uint cost = 100, bool unlocked = true)
    {
        var a = new AetheryteSummary(id * 10, id, $"aetheryte {id}", 0, default, "test", true, true, 0, cost, false);
        return new(id, $"zone {id}", 0, true, unlocked, a, [a], unlocked ? [a] : [], true, false, default, default);
    }
    public static void Run()
    {
        var defaults = new Configuration();
        Check(!defaults.ZoneRotationEnabled && !defaults.ZoneRotationWhenNoEligibleFates && defaults.ZoneRotationTerritories.Count == 0, "opt-in defaults");
        Check(defaults.ZoneRotationEmptyDelaySeconds == 30 && defaults.ZoneRotationArrivalSettleSeconds == 5 && defaults.ZoneRotationCircuitBackoffSeconds == 60, "timing defaults");
        var config = new Configuration { AutomatorEnabled = true, ZoneRotationEnabled = true, ZoneRotationTerritories = [1, 2, 3] };
        var state = TeleportState.Idle;
        var result = TeleportRequestResult.Issued;
        var requests = new List<TeleportRecommendation>();
        var available = new Dictionary<uint, ZoneInfo> { [1] = Zone(1), [2] = Zone(2), [3] = Zone(3) };
        var zones = Stub<IZoneService>((m,a) => available.GetValueOrDefault((uint)a![0]!));
        var executor = Stub<ITeleportExecutor>((m,a) => {
            if (m.Name == "get_State") return state;
            requests.Add((TeleportRecommendation)a![0]!);
            if (result == TeleportRequestResult.Issued) state = TeleportState.Starting;
            return result;
        });
        var rotation = new ZoneRotationController(config, zones, executor);
        var now = DateTimeOffset.UtcNow;
        var obs = new ZoneRotationObservation(1, true, true, false, true);
        bool Tick(int seconds, ZoneRotationObservation? value = null) => rotation.Tick(value ?? obs, now.AddSeconds(seconds));
        void Reset()
        {
            rotation.Reset(); requests.Clear(); state = TeleportState.Idle; result = TeleportRequestResult.Issued;
            config.ZoneRotationEnabled = config.AutomatorEnabled = config.TeleportEnabled = true;
            config.ZoneRotationTerritories = [1, 2, 3]; config.ZoneRotationEmptyDelaySeconds = 30;
            config.ZoneRotationArrivalSettleSeconds = 5; config.ZoneRotationCircuitBackoffSeconds = 60;
            config.TeleportMaxGilCost = 9999;
            available[2] = Zone(2); available[3] = Zone(3);
        }
        Tick(0); Tick(29); Check(requests.Count == 0, "sustained 30 second empty delay");
        Tick(30); Check(requests.Count == 1 && requests[0].AetheryteId == 20 && rotation.ExpectsArrival(2), "ordered successor issued once");
        Tick(31); Check(requests.Count == 1, "no duplicate while casting");
        state = TeleportState.Loading; Tick(32, obs with { Territory = 2, Ready = false });
        Check(rotation.Phase == ZoneRotationPhase.Settling && rotation.AttemptedCount == 2, "expected arrival preserves circuit");
        state = TeleportState.Completed; Tick(40, obs with { Territory = 2 }); state = TeleportState.Idle; Tick(44, obs with { Territory = 2 });
        Check(rotation.Phase == ZoneRotationPhase.Settling, "five second local scan settle");
        Tick(45, obs with { Territory = 2 }); state = TeleportState.Idle;
        Tick(75, obs with { Territory = 2 }); Check(requests.Count == 2 && requests[1].AetheryteId == 30, "next zone follows preserved circuit");
        Tick(76, obs with { Territory = 3 }); Tick(81, obs with { Territory = 3 }); state = TeleportState.Idle;
        Tick(111, obs with { Territory = 3 });
        Check(rotation.Phase == ZoneRotationPhase.Backoff && rotation.CompletedCircuits == 1 && requests.Count == 2, "full circuit backs off without returning to checked origin");
        Tick(170, obs with { Territory = 3 }); Check(requests.Count == 2, "60 second full circuit backoff enforced");
        Tick(171, obs with { Territory = 3 }); Tick(201, obs with { Territory = 3 });
        Check(requests.Count == 3 && requests[2].AetheryteId == 10, "new circuit wraps ordered list");

        Reset(); Tick(0); Tick(29, obs with { Empty = false }); Tick(30); Tick(59);
        Check(requests.Count == 0, "local FATE resets empty timer"); Tick(60); Check(requests.Count == 1, "fresh continuous empty delay required");
        foreach (var blocked in new[] { obs with { Ready = false }, obs with { Safe = false }, obs with { HasTwist = true } })
        {
            Reset(); Tick(0); Tick(29, blocked); Tick(60, blocked);
            Check(requests.Count == 0, "readiness/safety/Twist gate blocks requests");
            Tick(61); Tick(90); Check(requests.Count == 0, "safety recovery restarts empty grace");
            Tick(91); Check(requests.Count == 1, "rotation resumes after safe empty grace");
        }
        Reset(); config.TeleportEnabled = false; Tick(0); Tick(100); Check(requests.Count == 0, "teleport toggle honored");
        Reset(); available[2] = Zone(2, unlocked: false); available[3] = Zone(3, 101); config.TeleportMaxGilCost = 100;
        Tick(0); Tick(30); Check(requests.Count == 0 && rotation.Phase == ZoneRotationPhase.Backoff, "locked and over-budget destinations skipped");
        Reset(); config.ZoneRotationTerritories = [1]; Tick(0); Tick(30); Check(requests.Count == 0 && rotation.Phase == ZoneRotationPhase.Backoff, "only current zone bounded");
        Reset(); config.ZoneRotationTerritories = []; Tick(0); Tick(30); Check(requests.Count == 0 && rotation.Phase == ZoneRotationPhase.Backoff, "empty list bounded");
        Reset(); config.ZoneRotationTerritories = [0, 1, 1, 2, 2]; Tick(0); Tick(30);
        Check(requests.Count == 1 && rotation.AttemptedCount == 2, "null territory and duplicates sanitized");
        Reset(); Tick(0); config.ZoneRotationTerritories = [1, 3, 2]; Tick(29); Tick(59);
        Check(requests.Single().AetheryteId == 30, "reorder restarts delay and respects new order");
        config.ZoneRotationTerritories = [1, 2]; Tick(60, obs with { Territory = 3 });
        Check(rotation.Phase == ZoneRotationPhase.Settling, "removing issued destination preserves arrival ownership");
        Tick(65, obs with { Territory = 3 }); Tick(95, obs with { Territory = 3 });
        Check(requests.Count == 2 && requests[1].AetheryteId == 10, "edited list used after outstanding arrival");

        foreach (var failed in new[] { TeleportRequestResult.InvalidTarget, TeleportRequestResult.Failed, TeleportRequestResult.ClientUnavailable })
        {
            Reset(); result = failed; Tick(0); Tick(30);
            Check(requests.Count == 1 && rotation.LastFailure!.Contains(failed.ToString()), "failed request visible and bounded: " + failed);
            Tick(31); Tick(32); Check(requests.Count == 2 && rotation.Phase == ZoneRotationPhase.Backoff, "each failed destination attempted at most once per circuit");
        }
        Reset(); Tick(0); Tick(30); state = TeleportState.Idle; Tick(31);
        Check(rotation.LastFailure != null && rotation.Destination == null, "canceled game cast recognized");
        Tick(61); Check(requests.Count == 2 && requests[1].AetheryteId == 30, "canceled destination skipped");
        Reset(); Tick(0); Tick(30); Tick(90); Check(rotation.LastFailure != null, "stuck teleport times out");
        Reset(); config.ZoneRotationEmptyDelaySeconds = 0; config.ZoneRotationCircuitBackoffSeconds = 1; result = TeleportRequestResult.Failed;
        Tick(0); Tick(5); Tick(6); Tick(7); Tick(8);
        Check(requests.Count == 2, "failure cooldown survives short circuit backoff");
        Reset(); Tick(0); Tick(30); Tick(31, obs with { Territory = 2 }); Tick(34, obs with { Territory = 2, Ready = false });
        Tick(40, obs with { Territory = 2 }); Tick(44, obs with { Territory = 2 });
        Check(rotation.Phase == ZoneRotationPhase.Settling, "interrupted arrival readiness restarts settle");
        Tick(45, obs with { Territory = 2, Empty = false }); Check(rotation.Phase == ZoneRotationPhase.Idle, "post-settle local FATE immediately wins");
        Reset(); Tick(0); Tick(30); Tick(31, obs with { Territory = 2, Ready = false }); Tick(121, obs with { Territory = 2, Ready = false });
        Check(rotation.Phase == ZoneRotationPhase.Suspended && requests.Count == 1, "never-ready arrival stops boundedly");
        Reset(); Tick(0); rotation.Suspend("Manual travel"); Tick(100); Check(requests.Count == 0 && rotation.Phase == ZoneRotationPhase.Suspended, "manual travel latches suspension");
        config.ZoneRotationEnabled = false; Tick(101); config.ZoneRotationEnabled = true; Tick(102); Tick(132);
        Check(requests.Count == 1, "explicit toggle rearms suspended rotation");
        Reset(); Tick(0); Tick(30); config.AutomatorEnabled = false; Tick(31); Tick(100);
        Check(requests.Count == 1 && rotation.Destination == null, "disable clears pending rotation without reissue");
        Reset(); config.ZoneRotationTerritories = [1]; Tick(0); Tick(30);
        Check(!Tick(31, obs with { Empty = false }) && rotation.Phase == ZoneRotationPhase.Idle, "local work interrupts full circuit backoff immediately");
        Reset(); config.ZoneRotationTerritories = [1]; Tick(0); Tick(30);
        Check(!Tick(31, obs with { Empty = false, Safe = false, HasTwist = true }) && rotation.AttemptedCount == 0,
            "local work resets exhausted circuit even during combat or Twist preservation");
        Reset(); Tick(0); Tick(30); config.ZoneRotationEnabled = false; Tick(31); Tick(100);
        Check(requests.Count == 1 && rotation.Phase == ZoneRotationPhase.Suspended, "runtime rotation toggle stops all further issues");

        Reset(); config.ZoneRotationEmptyDelaySeconds = 0; Tick(0); Tick(4);
        Check(requests.Count == 0, "initial zero delay still requires stable scan grace");
        Tick(5, obs with { Ready = false }); Tick(6); Tick(10);
        Check(requests.Count == 0, "readiness loss resets initial grace"); Tick(11);
        Check(requests.Count == 1, "stable five second scan enables zero-delay issue");
        Reset(); config.ZoneRotationEmptyDelaySeconds = 0; Tick(0); Tick(4);
        config.ZoneRotationEnabled = false; Tick(5); config.ZoneRotationEnabled = true; Tick(6); Tick(10);
        Check(requests.Count == 0, "re-enable at zero delay cannot inherit old scan grace"); Tick(11);
        Check(requests.Count == 1, "re-enabled rotation waits fresh stable scan grace");
        Reset(); config.TeleportMaxGilCost = 0; Tick(0); Tick(30);
        Check(requests.Count == 1 && requests[0].EstimatedGilCost > 0, "zero budget means unlimited");
        foreach (var contention in new[] { TeleportRequestResult.Busy, TeleportRequestResult.Blocked })
        {
            Reset(); result = contention; Tick(0); Tick(30);
            Check(rotation.AttemptedCount == 1 && rotation.LastFailure == null, "contention neither attempts nor fails destination");
            for (var i = 0; i < 100; i++) Tick(31);
            Check(requests.Count == 1, "contention never dispatches per frame");
            Tick(32); Tick(33); Check(requests.Count == 2, "short bounded contention retry");
            result = TeleportRequestResult.Issued; Tick(36);
            Check(requests.Count == 3 && requests.All(r => r.AetheryteId == 20), "contention retries same destination without failure cooldown");
        }
        Reset(); result = TeleportRequestResult.Failed; Tick(0); Tick(30);
        config.ZoneRotationTerritories = [1, 3, 2]; Tick(31); Tick(61); Tick(62);
        Check(requests.Count(r => r.AetheryteId == 20) == 1, "reorder retains unchanged destination cooldown");
        foreach (var toggle in new[] { "rotation", "teleport" })
        {
            Reset(); Tick(0); Tick(30);
            if (toggle == "rotation") config.ZoneRotationEnabled = false; else config.TeleportEnabled = false;
            Tick(31); config.ZoneRotationEnabled = config.TeleportEnabled = true;
            state = TeleportState.Loading; Tick(32, obs with { Territory = 0, Ready = false });
            state = TeleportState.Completed; Tick(40, obs with { Territory = 2 });
            state = TeleportState.Idle; Tick(100, obs with { Territory = 2 });
            Check(requests.Count == 1 && rotation.Phase == ZoneRotationPhase.Suspended, toggle + " toggle cannot add hop after outstanding cast");
        }

        ChainTests();
        Console.WriteLine($"PASS: {checks} rotation assertions against compiled production controller, handler, and chain");
    }

    static void ChainTests()
    {
        foreach (var scenario in new[] { "arrival", "manual", "death", "stop", "disable", "logout", "debug", "followup", "combat", "local", "filtered", "foreign", "collect", "cast", "auto-cleanup", "auto-teleport", "not-ready", "settling-cast", "zero-unrelated" })
        {
            uint territory = 1, hp = 100; bool loggedIn = true, loaded = true, loading = false, pendingStop = false, combat = false, cast = false;
            var config = new Configuration { AutomatorEnabled = true, ZoneRotationEnabled = true, ZoneRotationTerritories = [1, 2, 3], ZoneRotationEmptyDelaySeconds = 0 };
            var log = Stub<IPluginLog>();
            var client = Stub<IClientState>((m,a) => m.Name == "get_IsLoggedIn" ? loggedIn : m.Name == "get_TerritoryType" ? territory : Noop.Default(m));
            var player = Stub<IPlayerCharacter>((m,a) => m.Name == "get_CurrentHp" ? hp : m.Name == "get_IsCasting" ? cast : m.Name == "get_CastActionId" ? 5u : Noop.Default(m));
            var objects = Stub<IObjectTable>((m,a) => m.Name == "get_LocalPlayer" ? player : Noop.Default(m));
            var playerState = Stub<IPlayerState>((m,a) => m.Name == "get_IsLoaded" ? loaded : Noop.Default(m));
            var conditions = Stub<ICondition>((m,a) => m.Name == "get_Item" ? (ConditionFlag)a![0]! switch {
                ConditionFlag.BetweenAreas => loading, ConditionFlag.InCombat => combat, ConditionFlag.Casting => cast, _ => false
            } : Noop.Default(m));
            TravelJob? job = null;
            var travel = Stub<ITravelController>((m,a) => m.Name == "get_CurrentJob" ? job : Noop.Default(m));
            int requests = 0, queries = 0;
            var executorState = TeleportState.Idle;
            var executor = Stub<ITeleportExecutor>((m,a) => {
                if (m.Name == "get_State") return executorState;
                if (executorState != TeleportState.Idle) return TeleportRequestResult.Busy;
                requests++; executorState = TeleportState.Starting; return TeleportRequestResult.Issued;
            });
            var zones = Stub<IZoneService>((m,a) => Zone((uint)a![0]!));
            var rotation = new ZoneRotationController(config, zones, executor);
            var follow = new FateFollowUpTracker(config);
            var rewardCount = 0;
            var rewards = Stub<IFateRewardTracker>((m,a) => m.Name == "get_PendingStop" ? pendingStop : m.Name == "get_CompletedCount" ? rewardCount : Noop.Default(m));
            ActiveFate[] fates = [];
            var repo = Stub<IFateRepository>((m,a) => { if (m.Name == "EnumerateActiveFates") { queries++; return fates; } return Noop.Default(m); });
            var selector = Stub<IFateSelector>();
            var automaticTravel = new AutomaticTravelPolicy(config, client, objects, Stub<IFateTable>(), conditions);
            var handler = new RotatingZoneStateHandler(rotation, config, client, playerState, objects, conditions, repo, selector, follow, rewards, travel, Stub<IGameGui>(), log, automaticTravel);
            var boss = new BossModPresetManager(Stub<IDalamudPluginInterface>(), Stub<ICombatPluginRegistry>(), config, log, playerState);
            var combatHandler = Stub<IFateCombatHandler>(); var text = Stub<ITextAdvanceClient>(); var map = Stub<IBossModObstacleMap>();
            var completion = new FateCompletionTracker();
            var participant = new ParticipatingInFateStateHandler(repo, combatHandler, Stub<ILevelSyncService>(), Stub<ICombatPluginRegistry>(),
                Stub<IRotationSolverBridge>(), Stub<IWrathComboBridge>(), boss, config, travel, follow, text, map, completion,
                conditions, rewards, Stub<IGameGui>(), objects, automaticTravel, log);
            var probe = new ProbeHandler();
            using var chain = new AutomatorChain(travel, log, conditions, client, objects, follow, completion, map, text,
                rewards, config, [handler, participant, probe], boss, Stub<IRotationSolverBridge>(), Stub<IWrathComboBridge>(), combatHandler,
                new FateObstacleMapAssurance(map, Stub<YetAnotherFATE.Services.Movement.INavmeshClient>()), automaticTravel);
            var context = (AutomatorContext)typeof(AutomatorChain).GetField("context", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chain)!;
            if (scenario == "followup") follow.Begin(default(ActiveFate) with { HasFollowUp = true, TerritoryId = 1, HasSheetMetadata = true, LocationId = 9 });
            if (scenario == "followup") Check(follow.IsWatching, "follow-up fixture really watches a nonzero location");
            if (scenario == "combat") combat = true;
            if (scenario == "cast") cast = true;
            if (scenario is "local" or "filtered" or "foreign")
                fates = [default(ActiveFate) with { TerritoryId = scenario == "foreign" ? 999u : 1u, IsOnMap = true, State = Dalamud.Game.ClientState.Fates.FateState.Running }];
            if (scenario == "filtered") config.ZoneRotationWhenNoEligibleFates = true;
            if (scenario == "collect")
            {
                var collect = default(ActiveFate) with { FateId = 42, TerritoryId = 1, Rule = (byte)FateRule.Collect, Progress = 100,
                    HasFollowUp = true, HasSheetMetadata = true, LocationId = 9 };
                fates = [collect];
                context.UpdateSelection(new FateSelection(collect, default, false, null, 0, "test", default), "test", log);
                context.TransitionTo(AutomatorState.Participating, "collection completes", log);
            }
            if (scenario is "auto-cleanup" or "auto-teleport") job = new TravelJob(Guid.NewGuid(), default, TravelRequestKind.Automatic, DateTimeOffset.UtcNow, 12);
            if (scenario == "auto-teleport") { context.TransitionTo(AutomatorState.Travelling, "owned teleport", log); loading = true; }
            if (scenario == "not-ready") loaded = false;
            chain.Tick();
            if (scenario == "arrival")
            {
                var wait = handler.Tick(context);
                Check(wait.Reason == rotation.Detail && wait.Reason != rotation.Status, "wait logs use stable detail, not live countdown status");
                Check(requests == 0, "real chain observes initial scan grace at zero empty delay");
            }
            Set(rotation, "scanReadySince", DateTimeOffset.UtcNow.AddSeconds(-6));
            chain.Tick();
            if (scenario == "collect")
            {
                Check(chain.State == AutomatorState.PostFate && requests == 0, "real collection handler reward grace blocks rotation");
                Check(follow.IsWatching, "real completed collection starts follow-up watch");
                rewardCount = 1; fates = []; chain.Tick();
                Check(chain.State == AutomatorState.PostFate && requests == 0, "real post-FATE handler waits for followup after reward");
                follow.Clear(); chain.Tick(); chain.Tick();
                Set(rotation, "scanReadySince", DateTimeOffset.UtcNow.AddSeconds(-6)); chain.Tick();
                Check(requests == 1, "rotation resumes only after real post-FATE cleanup and followup");
                continue;
            }
            if (scenario is "auto-cleanup" or "auto-teleport")
            {
                Check(requests == 0 && rotation.Phase != ZoneRotationPhase.Suspended, scenario + " doesn't suspend rotation for owned FATE travel");
                job = null; loading = false; context.TransitionTo(AutomatorState.SelectingFate, "job drained", log); chain.Tick();
                Set(rotation, "scanReadySince", DateTimeOffset.UtcNow.AddSeconds(-6)); chain.Tick();
                Check(requests == 1, scenario + " resumes rotation when automatic travel drains");
                continue;
            }
            if (scenario is "followup" or "combat" or "local" or "collect" or "cast" or "not-ready")
            {
                Check(requests == 0, scenario + " must finish/wait before rotation");
                continue;
            }
            Check(requests == 1 && chain.State == AutomatorState.RotatingZone, scenario + ": rotation enters distinct state");
            if (scenario is "foreign" or "filtered") continue;
            switch (scenario)
            {
                case "arrival":
                    territory = 0; loading = true; executorState = TeleportState.Loading; chain.Tick();
                    Check(rotation.Destination == 2 && rotation.AttemptedCount == 2, "owned territory-zero loading preserves circuit");
                    territory = 2; loading = true; loaded = false; chain.Tick();
                    Check(rotation.AttemptedCount == 2 && chain.State == AutomatorState.RotatingZone && probe.Resets == 0, "real chain preserves circuit through expected territory change");
                    loading = false; loaded = true; executorState = TeleportState.Completed; chain.Tick();
                    executorState = TeleportState.Idle;
                    Set(rotation, "readySince", DateTimeOffset.UtcNow.AddSeconds(-6)); Set(rotation, "scanReadySince", DateTimeOffset.UtcNow.AddSeconds(-6)); chain.Tick();
                    Check(requests == 2 && rotation.Destination == 3, "real chain continues circuit after settled arrival");
                    break;
                case "settling-cast":
                    territory = 2; executorState = TeleportState.Completed; chain.Tick(); executorState = TeleportState.Idle;
                    cast = true; chain.Tick();
                    Check(rotation.Phase == ZoneRotationPhase.Suspended && requests == 1, "manual Teleport during settling is not owned"); break;
                case "zero-unrelated":
                    territory = 0; loading = true; executorState = TeleportState.Loading; chain.Tick();
                    territory = 99; chain.Tick();
                    Check(rotation.Phase == ZoneRotationPhase.Suspended && requests == 1, "zero transit never authorizes unrelated nonzero territory"); break;
                case "manual": territory = 99; chain.Tick(); Check(rotation.Phase == ZoneRotationPhase.Suspended && requests == 1, "unexpected territory suspends rotation"); break;
                case "death": hp = 0; loading = true; chain.Tick(); Check(chain.State == AutomatorState.Recovering && rotation.Destination == null && requests == 1, "death during loading preempts rotation globally"); break;
                case "stop": pendingStop = true; combat = true; chain.Tick(); Check(requests == 1 && rotation.Destination == null, "pending stop preempts rotation even before safe cleanup"); break;
                case "disable": config.AutomatorEnabled = false; chain.Tick(); Check(chain.State == AutomatorState.Disabled && rotation.Destination == null, "disable clears rotation"); break;
                case "logout": loggedIn = false; chain.Tick(); Check(chain.State == AutomatorState.Disabled && rotation.Destination == null, "logout clears rotation"); break;
                case "debug": job = new TravelJob(Guid.NewGuid(), default, TravelRequestKind.DebugForceRun, DateTimeOffset.UtcNow, 12); chain.Tick(); Check(rotation.Phase == ZoneRotationPhase.Suspended && requests == 1, "debug travel preempts rotation"); break;
            }
        }
    }
}
