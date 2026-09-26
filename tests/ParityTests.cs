using System.Numerics;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;
using YetAnotherFATE.Services.Combat;
using YetAnotherFATE.Services.Combat.BossMod;

static class Tests
{
    static int assertions;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); assertions++; }
    static void RetryNow(object manager) => manager.GetType().GetField("retryAfter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, DateTimeOffset.MinValue);
    static void Main()
    {
        foreach (var lists in new[] { true, false })
        {
            var ipc = new FakeIpc();
            var active = new List<string> { "User" };
            var adds = 0; var clears = 0; var deletes = 0; var rejectRestore = false; var writes = 0;
            ipc.Bind("Presets.Create", new Func<string, bool, bool>((_, overwrite) => !overwrite));
            ipc.Bind("Presets.Delete", new Func<string, bool>(_ => { deletes++; return true; }));
            if (lists)
            {
                ipc.Bind("Presets.GetActiveList", new Func<List<string>>(() => active.ToList()));
                ipc.Bind("Presets.SetActiveList", new Func<List<string>, bool>(names => { writes++; if (rejectRestore && names.Contains("User")) return false; active = names.ToList(); return true; }));
            }
            else
            {
                ipc.Bind("Presets.GetActive", new Func<string?>(() => active.FirstOrDefault()));
                ipc.Bind("Presets.GetForceDisabled", new Func<bool>(() => false));
                ipc.Bind("Presets.SetActive", new Func<string, bool>(name => { writes++; if (rejectRestore && name == "User") return false; active = new() { name }; return true; }));
                ipc.Bind("Presets.ClearActive", new Func<bool>(() => { active.Clear(); return true; }));
            }
            var tracks = new Dictionary<string, string>();
            ipc.Bind("Presets.AddTransientStrategy", new Func<string, string, string, string, bool>((p, m, t, v) => { adds++; tracks[m + "/" + t] = v; return true; }));
            ipc.Bind("Presets.ClearTransientStrategy", new Func<string, string, string, bool>((p, m, t) => { clears++; return tracks.Remove(m + "/" + t); }));
            var config = new Configuration { BossModMaxTargets = 6 };
            var manager = new BossModPresetManager(ipc, new Registry(), config, new Log(), new PlayerState());
            manager.ActivateRotationPreset();
            Check(active.SequenceEqual(new[] { "YAFate Farming" }), "provider activation");
            for (var i = 0; i < 100; i++) manager.ApplyFateTargetLimit();
            Check(adds == 2, "unchanged target limit emits one call per owned strategy");
            config.BossModMaxTargets = 9;
            manager.ApplyFateTargetLimit();
            Check(adds == 3, "target limit changes once");
            config.CollectionPolicy = FateCollectionPolicy.KillOnly;
            manager.ApplyFateTargetLimit();
            Check(tracks["BossMod.Autorotation.MiscAI.AutoTarget/CollectFATE"] == "Disabled", "KillOnly target control");
            Check(tracks["BossMod.Autorotation.MiscAI.FateUtils/Collect"] == "Disabled", "KillOnly collect control");
            Check(!tracks.Keys.Any(k => k.EndsWith("Hand-in") || k.EndsWith("Chocobo") || k.EndsWith("Sync")), "provider defaults preserved");
            config.CollectionPolicy = FateCollectionPolicy.BossModDefault;
            manager.ApplyFateTargetLimit();
            Check(clears == 1 && tracks.Count == 2, "runtime policy removes only AutoTarget collection override");
            rejectRestore = true;
            manager.ReleasePresets();
            Check(deletes == 0 && active[0] == "YAFate Farming", "failed restore retains owned preset");
            var before = writes;
            manager.ReleasePresets();
            Check(writes == before, "restore failures throttled");
            rejectRestore = false; RetryNow(manager);
            manager.ReleasePresets();
            Check(active.SequenceEqual(new[] { "User" }) && deletes == 2, "restore retries original snapshot before deletion");
            Check(tracks.Count == 0, "owned target strategy removed");
        }
        {
            var ipc = new FakeIpc();
            var active = new List<string>();
            var adds = 0;
            ipc.Bind("Presets.Create", new Func<string, bool, bool>((_, _) => true));
            ipc.Bind("Presets.GetActiveList", new Func<List<string>>(() => active.ToList()));
            ipc.Bind("Presets.SetActiveList", new Func<List<string>, bool>(names => { active = names.ToList(); return true; }));
            ipc.Bind("Presets.AddTransientStrategy", new Func<string, string, string, string, bool>((_, _, _, _) => { adds++; return true; }));
            ipc.Bind("Presets.ClearTransientStrategy", new Func<string, string, string, bool>((_, _, _) => false));
            ipc.Bind("Presets.Delete", new Func<string, bool>(_ => true));
            var manager = new BossModPresetManager(ipc, new Registry(), new Configuration { BossModMaxTargets = 4 }, new Log(), new PlayerState());
            manager.ActivateRotationPreset();
            manager.ApplyFateTargetLimit();
            manager.DeactivateActivePreset();
            manager.ActivateRotationPreset();
            manager.ApplyFateTargetLimit();
            Check(adds == 4, "already-absent transient tracks are reapplied after reactivation");
        }
        {
            var ipc = new FakeIpc(); var mutated = false;
            ipc.Bind("Presets.Create", new Func<string, bool, bool>((_, _) => true));
            ipc.Bind("Presets.GetActiveList", new Func<List<string>>(() => throw new Exception("capture failed")));
            ipc.Bind("Presets.SetActiveList", new Func<List<string>, bool>(_ => mutated = true));
            new BossModPresetManager(ipc, new Registry(), new Configuration(), new Log(), new PlayerState()).ActivateRotationPreset();
            Check(!mutated, "capture failure does not change global presets");
        }
        {
            var ipc = new FakeIpc(); var frame = new Frame(); var state = TaskStatus.RanToCompletion; var hasMap = false; var clearCount = 0; var rejected = true;
            ipc.Bind("ObstacleMap.GetGenerationStatus", new Func<TaskStatus>(() => state));
            ipc.Bind("ObstacleMap.HasTempMap", new Func<bool>(() => hasMap));
            ipc.Bind("ObstacleMap.ClearTempMap", new Func<bool>(() => { clearCount++; var old = hasMap; hasMap = false; return old; }));
            ipc.Bind("ObstacleMap.Generate", new Func<Vector3, float, bool, bool>((_, _, _) => { if (rejected) return false; state = TaskStatus.Running; return true; }));
            var map = new BossModObstacleMap(ipc, frame, new Log());
            Check(!map.BeginForFate(1, Vector3.One, 20), "rejected generation");
            rejected = false;
            Check(map.BeginForFate(1, Vector3.One, 20), "transient rejection not blacklisted");
            Check(!map.ClearTemporaryMap() && clearCount == 0, "in-flight cleanup defers clear");
            state = TaskStatus.RanToCompletion; hasMap = true; frame.Tick();
            Check(!hasMap && clearCount == 1, "late generation drained and cleared");
            map.Dispose();
            Check(clearCount == 1 && frame.Subscribers == 0, "clean disposal unsubscribes");
            hasMap = true;
            using var foreign = new BossModObstacleMap(ipc, frame, new Log());
            Check(!foreign.BeginForFate(2, Vector3.Zero, 30), "foreign map not overwritten");
            foreign.Dispose();
            Check(hasMap && clearCount == 1, "foreign map not cleared on dispose");
        }
        var dto = typeof(BossModObstacleMap).GetNestedType("BitmapQuality", BindingFlags.NonPublic)!;
        bool Bad(float blocked, float largest, float tiny, float speckle, int components) => (bool)dto.GetProperty("IsBad")!.GetValue(Activator.CreateInstance(dto, blocked, largest, tiny, speckle, components))!;
        Check(!Bad(.1f, 1f, 0, 0, 1), "connected quality accepted");
        Check(Bad(.1f, .49f, 0, 0, 5), "fragmented quality rejected");
        Check(Bad(.85f, 1, 0, 0, 1), "blocked threshold");
        Check(Bad(.1f, 1, .03f, 0, 1), "tiny threshold");
        Check(Bad(.1f, 1, 0, .003f, 1), "speckle threshold");
        Check(Bad(float.NaN, 1, 0, 0, 1), "invalid quality rejected");
        {
            var ipc = new FakeIpc(); var ready = false; var progress = .5f; var reloads = 0; var pending = false; var cancels = 0; var stops = 0; var flew = false; var allowUnlandable = true;
            ipc.BindNav("Nav.IsReady", new Func<bool>(() => ready));
            ipc.BindNav("Nav.BuildProgress", new Func<float>(() => progress));
            ipc.BindNav("Nav.Reload", new Func<bool>(() => { reloads++; return true; }));
            ipc.BindNav("SimpleMove.PathfindAndMoveTo", new Func<Vector3, bool, bool>((_, fly) => { flew = fly; pending = true; return true; }));
            ipc.BindNav("SimpleMove.PathfindInProgress", new Func<bool>(() => pending));
            ipc.BindNav("Query.Mesh.PointOnFloor", new Func<Vector3, bool, float, Vector3?>((point, allow, _) => { allowUnlandable = allow; return new Vector3(point.X, 1f, point.Z); }));
            ipc.BindNav("Path.Stop", new Action(() => stops++));
            ipc.BindNav("Nav.PathfindCancelAll", new Action(() => cancels++));
            var nav = new YetAnotherFATE.Services.Movement.NavmeshClient(ipc, new Log());
            Check(!nav.EnsureReady() && reloads == 0, "readiness does not interrupt active build");
            progress = -1;
            nav.EnsureReady(); nav.EnsureReady();
            Check(reloads == 1, "one reload per unready cycle");
            ready = true;
            Check(nav.SimpleMoveTo(Vector3.One) && !flew, "SimpleMove defaults to ground movement");
            Check(nav.SimpleMoveTo(Vector3.One, true) && flew, "SimpleMove explicitly enables flight");
            Check(nav.PointOnFloor(Vector3.Zero, false, 5f) is { Y: 1f } && !allowUnlandable, "PointOnFloor rejects unlandable floor by default");
            nav.Stop(); nav.Stop();
            Check(cancels == 1 && stops == 2, "pending movement cancelled once while stop drains");
            pending = false; nav.Stop();
            pending = true; nav.Stop();
            Check(cancels == 1, "external pending task not cancelled after ownership released");
            ready = false; nav.EnsureReady();
            Check(reloads == 1, "CancelAll reload not duplicated");
            ready = true; nav.EnsureReady(); ready = false; nav.EnsureReady();
            Check(reloads == 2, "ready state resets next reload cycle");
        }
        Console.WriteLine($"PASS: {assertions} parity/lifecycle assertions against linked production sources");
    }
}

class FakeIpc : IDalamudPluginInterface
{
    readonly Dictionary<string, Delegate> endpoints = new();
    public FileInfo AssemblyLocation => new(Path.Combine(AppContext.BaseDirectory, "Test.dll"));
    public void Bind(string suffix, Delegate fn) => endpoints["BossMod." + suffix] = fn;
    public void BindNav(string suffix, Delegate fn) => endpoints["vnavmesh." + suffix] = fn;
    public List<PluginStatus> InstalledPlugins => new() { new() };
    public bool Has(string name) => endpoints.ContainsKey(name);
    public object? Call(string name, params object?[] args) => endpoints[name].DynamicInvoke(args);
    public ICallGateSubscriber<R> GetIpcSubscriber<R>(string n) => new Gate<R>(this, n);
    public ICallGateSubscriber<A, R> GetIpcSubscriber<A, R>(string n) => new Gate<A, R>(this, n);
    public ICallGateSubscriber<A, B, R> GetIpcSubscriber<A, B, R>(string n) => new Gate<A, B, R>(this, n);
    public ICallGateSubscriber<A, B, C, R> GetIpcSubscriber<A, B, C, R>(string n) => new Gate<A, B, C, R>(this, n);
    public ICallGateSubscriber<A, B, C, D, R> GetIpcSubscriber<A, B, C, D, R>(string n) => new Gate<A, B, C, D, R>(this, n);
}
class Gate<R>(FakeIpc i, string n) : ICallGateSubscriber<R> { public bool HasFunction => i.Has(n); public bool HasAction => i.Has(n); public void InvokeAction() => i.Call(n); public R InvokeFunc() => (R)i.Call(n)!; }
class Gate<A, R>(FakeIpc i, string n) : ICallGateSubscriber<A, R> { public bool HasFunction => i.Has(n); public R InvokeFunc(A a) => (R)i.Call(n, a)!; }
class Gate<A, B, R>(FakeIpc i, string n) : ICallGateSubscriber<A, B, R> { public bool HasFunction => i.Has(n); public R InvokeFunc(A a, B b) => (R)i.Call(n, a, b)!; }
class Gate<A, B, C, R>(FakeIpc i, string n) : ICallGateSubscriber<A, B, C, R> { public bool HasFunction => i.Has(n); public R InvokeFunc(A a, B b, C c) => (R)i.Call(n, a, b, c)!; }
class Gate<A, B, C, D, R>(FakeIpc i, string n) : ICallGateSubscriber<A, B, C, D, R> { public bool HasFunction => i.Has(n); public R InvokeFunc(A a, B b, C c, D d) => (R)i.Call(n, a, b, c, d)!; }
class Frame : IFramework { public event Action<IFramework>? Update; public int Subscribers => Update?.GetInvocationList().Length ?? 0; public void Tick() => Update?.Invoke(this); }
class Log : IPluginLog { public void Warning(string m, params object[] a) {} public void Warning(Exception e, string m, params object[] a) {} public void Debug(string m, params object[] a) {} public void Debug(Exception e, string m, params object[] a) {} public void Error(Exception e, string m, params object[] a) {} }
class Registry : ICombatPluginRegistry { public void Refresh() {} public List<Status> Statuses => new() { new() }; }
namespace Dalamud.Plugin { public class PluginStatus { public string InternalName => "vnavmesh"; public bool IsLoaded => true; } public interface IDalamudPluginInterface { List<PluginStatus> InstalledPlugins { get; } FileInfo AssemblyLocation { get; } ICallGateSubscriber<R> GetIpcSubscriber<R>(string n); ICallGateSubscriber<A,R> GetIpcSubscriber<A,R>(string n); ICallGateSubscriber<A,B,R> GetIpcSubscriber<A,B,R>(string n); ICallGateSubscriber<A,B,C,R> GetIpcSubscriber<A,B,C,R>(string n); ICallGateSubscriber<A,B,C,D,R> GetIpcSubscriber<A,B,C,D,R>(string n); } }
namespace Dalamud.Plugin.Ipc {
 public interface ICallGateSubscriber<R> { bool HasFunction { get; } bool HasAction { get; } void InvokeAction(); R InvokeFunc(); }
 public interface ICallGateSubscriber<A,R> { bool HasFunction { get; } R InvokeFunc(A a); }
 public interface ICallGateSubscriber<A,B,R> { bool HasFunction { get; } R InvokeFunc(A a,B b); }
 public interface ICallGateSubscriber<A,B,C,R> { bool HasFunction { get; } R InvokeFunc(A a,B b,C c); }
 public interface ICallGateSubscriber<A,B,C,D,R> { bool HasFunction { get; } R InvokeFunc(A a,B b,C c,D d); }
}
namespace Dalamud.Plugin.Ipc.Exceptions { public class IpcNotReadyError : Exception {} }
namespace Dalamud.Plugin.Services { public interface IFramework { event Action<IFramework>? Update; } public interface IPluginLog { void Warning(string m, params object[] a); void Warning(Exception e,string m,params object[] a); void Debug(string m,params object[] a); void Debug(Exception e,string m,params object[] a); void Error(Exception e,string m,params object[] a); } }
namespace YetAnotherFATE.Config { public enum FateCollectionPolicy { BossModDefault = 0, KillOnly = 1 } public class Configuration { public int BossModMaxTargets { get; set; } public bool BossModTargetsByRole { get; set; } public FateCollectionPolicy CollectionPolicy { get; set; } } }
class PlayerState : IPlayerState { public bool IsLoaded => true; public JobRef ClassJob { get; set; } = new(); }
public class JobRef { public bool IsValid => true; public Job Value { get; set; } = new(); }
public class Job { public byte Role { get; set; } }
namespace Dalamud.Plugin.Services { public interface IPlayerState { bool IsLoaded { get; } JobRef ClassJob { get; } } }
namespace YetAnotherFATE.Data.Combat { public enum CombatPluginId { BossMod, BossModReborn } }
namespace YetAnotherFATE.Services.Combat { public class Status { public YetAnotherFATE.Data.Combat.CombatPluginId Id => YetAnotherFATE.Data.Combat.CombatPluginId.BossMod; public bool Installed => true; public bool IsLoaded => true; } public interface ICombatPluginRegistry { void Refresh(); List<Status> Statuses { get; } } }
