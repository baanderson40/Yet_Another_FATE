using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Combat;

namespace YetAnotherFATE.Services.Combat.BossMod;

public sealed class BossModPresetManager : IDisposable
{
    private const string RotationPresetName = "YAFate Farming";
    private const string PassivePresetName = "YAFate Passive";
    private const string AutoTarget = "BossMod.Autorotation.MiscAI.AutoTarget";
    private const string FateUtils = "BossMod.Autorotation.MiscAI.FateUtils";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICombatPluginRegistry combatPlugins;
    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly IPlayerState playerState;
    private readonly HashSet<string> ownedPresets = new();
    private readonly HashSet<string> availablePresets = new();
    private readonly Dictionary<(string Preset, string Module, string Track), string> strategies = new();
    private List<string>? previousActivePresets;
    private bool previousForceDisabled;
    private bool listProvider;
    private string? activePresetName;
    private bool disposed;
    private DateTimeOffset retryAfter;
    private DateTimeOffset strategyRetryAfter;
    private DateTimeOffset nextStrategyReconcile;
    private bool forceStrategyRefresh;

    public bool PresetsCreated => !disposed && availablePresets.Contains(RotationPresetName) && availablePresets.Contains(PassivePresetName);

    public BossModPresetManager(IDalamudPluginInterface pluginInterface, ICombatPluginRegistry combatPlugins, Configuration configuration, IPluginLog log, IPlayerState playerState)
    {
        this.pluginInterface = pluginInterface;
        this.combatPlugins = combatPlugins;
        this.configuration = configuration;
        this.log = log;
        this.playerState = playerState;
    }

    public void EnsurePresets()
    {
        if (disposed || PresetsCreated || DateTimeOffset.UtcNow < retryAfter) return;
        retryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
        combatPlugins.Refresh();
        if (!combatPlugins.Statuses.Any(s => (s.Id == CombatPluginId.BossMod || s.Id == CombatPluginId.BossModReborn) && s.Installed && s.IsLoaded)) return;
        try
        {
            var create = pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");
            if (!create.HasFunction) return;
            EnsureAvailable(RotationPresetName, "YAFateFarmingPreset.json", create);
            EnsureAvailable(PassivePresetName, "YAFatePassivePreset.json", create);
            if (PresetsCreated) retryAfter = DateTimeOffset.MinValue;
        }
        catch (Exception ex) { log.Warning(ex, "BossModPresetManager: preset creation failed"); }
    }

    private void EnsureAvailable(string name, string filename, ICallGateSubscriber<string, bool, bool> create)
    {
        if (availablePresets.Contains(name)) return;
        try
        {
            var get = pluginInterface.GetIpcSubscriber<string, string?>("BossMod.Presets.Get");
            if (get.HasFunction && get.InvokeFunc(name) != null)
            {
                // Existing preset may belong to the user. It is usable, but never delete it.
                availablePresets.Add(name);
                return;
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "BossModPresetManager: existing-preset lookup failed for {Preset}", name);
        }

        var path = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "Data", "Combat", "BossMod", filename);
        // Keep provider defaults (Hand-in, Chocobo) and explicit Sync=Enable intact.
        // Collection is explicitly disabled in both YAF presets and at runtime.
        // Policy changes are reversible transient overrides, not mutations of the base definition.
        if (create.InvokeFunc(File.ReadAllText(path), false))
        {
            ownedPresets.Add(name);
            availablePresets.Add(name);
        }
        else log.Warning("BossModPresetManager: could not create {Preset}; existing presets are not overwritten", name);
    }

    public void ActivateRotationPreset() => ActivatePreset(RotationPresetName);
    public void ActivatePassivePreset() => ActivatePreset(PassivePresetName);
    public void DeactivateActivePreset() => DeactivatePreset();

    private void ActivatePreset(string name)
    {
        if (disposed) return;
        if (!PresetsCreated) EnsurePresets();
        if (!PresetsCreated) return;
        if (activePresetName == name && IsProviderPresetActive(name))
        {
            if (DateTimeOffset.UtcNow >= nextStrategyReconcile)
            {
                strategies.Keys.Where(k => k.Preset == name).ToList().ForEach(k => strategies.Remove(k));
                strategyRetryAfter = DateTimeOffset.MinValue;
                nextStrategyReconcile = DateTimeOffset.UtcNow.AddSeconds(5);
            }
            ApplyFateTargetLimit();
            return;
        }
        if (activePresetName == name && !IsProviderPresetActive(name))
        {
            if (HasExternalActivePreset())
            {
                // Another user/plugin took control. Do not overwrite it or restore
                // the snapshot captured before that external change.
                ClearOwnedStrategies(name);
                activePresetName = null;
                previousActivePresets = null;
                log.Debug("BossModPresetManager: relinquishing ownership after external preset change");
                return;
            }
            if (!ReapplyActivePreset(name))
                return;
        }
        if (activePresetName == name)
        {
            ApplyFateTargetLimit();
            return;
        }
        if (DateTimeOffset.UtcNow < retryAfter) return;
        if (activePresetName != null && !DeactivatePreset()) return;
        try
        {
            var getList = pluginInterface.GetIpcSubscriber<List<string>>("BossMod.Presets.GetActiveList");
            var setList = pluginInterface.GetIpcSubscriber<List<string>, bool>("BossMod.Presets.SetActiveList");
            listProvider = getList.HasFunction && setList.HasFunction;
            // Capture must succeed before changing global state. BMR has only single-preset IPC.
            if (listProvider)
            {
                previousActivePresets = new(getList.InvokeFunc());
            }
            else
            {
                var active = pluginInterface.GetIpcSubscriber<string?>("BossMod.Presets.GetActive").InvokeFunc();
                previousForceDisabled = pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.GetForceDisabled").InvokeFunc();
                previousActivePresets = active == null || previousForceDisabled ? new() : new() { active };
            }
            try
            {
                var force = pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.GetForceDisabled");
                if (force.HasFunction)
                    previousForceDisabled = force.InvokeFunc();
            }
            catch { previousForceDisabled = false; }
            var activated = listProvider ? setList.InvokeFunc(new() { name })
                : pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive").InvokeFunc(name);
            if (!activated) throw new InvalidOperationException($"Activation rejected for {name}");
            activePresetName = name;
            strategyRetryAfter = DateTimeOffset.MinValue;
            nextStrategyReconcile = DateTimeOffset.UtcNow.AddSeconds(5);
            forceStrategyRefresh = true;
            ApplyFateTargetLimit();
        }
        catch (Exception ex)
        {
            retryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
            log.Warning(ex, "BossModPresetManager: activation failed");
        }
    }

    private bool IsProviderPresetActive(string name)
    {
        try
        {
            if (listProvider)
            {
                var getList = pluginInterface.GetIpcSubscriber<List<string>>("BossMod.Presets.GetActiveList");
                return getList.HasFunction && getList.InvokeFunc().Contains(name, StringComparer.Ordinal);
            }

            var get = pluginInterface.GetIpcSubscriber<string?>("BossMod.Presets.GetActive");
            return get.HasFunction && string.Equals(get.InvokeFunc(), name, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "BossModPresetManager: active preset lookup failed");
            return false;
        }
    }

    private bool ReapplyActivePreset(string name)
    {
        try
        {
            var setList = pluginInterface.GetIpcSubscriber<List<string>, bool>("BossMod.Presets.SetActiveList");
            if (listProvider && setList.HasFunction)
                return setList.InvokeFunc(new() { name });
            return pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive").InvokeFunc(name);
        }
        catch (Exception ex)
        {
            retryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
            log.Warning(ex, "BossModPresetManager: active preset reconciliation failed for {Preset}", name);
            return false;
        }
    }

    private bool HasExternalActivePreset()
    {
        try
        {
            if (listProvider)
            {
                var list = pluginInterface.GetIpcSubscriber<List<string>>("BossMod.Presets.GetActiveList");
                return list.HasFunction && list.InvokeFunc().Count > 0;
            }

            var active = pluginInterface.GetIpcSubscriber<string?>("BossMod.Presets.GetActive");
            return active.HasFunction && active.InvokeFunc() != null;
        }
        catch
        {
            return false;
        }
    }

    private void ClearOwnedStrategies(string preset)
    {
        foreach (var key in strategies.Keys.Where(k => k.Preset == preset).ToList())
        {
            try
            {
                pluginInterface.GetIpcSubscriber<string, string, string, bool>("BossMod.Presets.ClearTransientStrategy")
                    .InvokeFunc(key.Preset, key.Module, key.Track);
                strategies.Remove(key);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "BossModPresetManager: failed to clear abandoned transient strategy {Module}/{Track}", key.Module, key.Track);
            }
        }
    }

    public void ApplyFateTargetLimit()
    {
        if (activePresetName == null || DateTimeOffset.UtcNow < strategyRetryAfter) return;
        try
        {
            var success = true;
            var killOnly = configuration.CollectionPolicy == FateCollectionPolicy.KillOnly;
            if (activePresetName == RotationPresetName)
            {
                var limit = configuration.BossModTargetsByRole
                    ? TargetsForRole(playerState.IsLoaded && playerState.ClassJob.IsValid ? playerState.ClassJob.Value.Role : (byte)0)
                    : Math.Clamp(configuration.BossModMaxTargets, 0, 20);
                success &= SetStrategy(AutoTarget, "MaxTargets", limit.ToString(CultureInfo.InvariantCulture));
                success &= SetStrategy(AutoTarget, "CollectFATE", killOnly ? "Disabled" : null);
            }
            // YAF owns collection targeting and interaction. Keep FateUtils collection disabled
            // even when the user allows collection, preventing BossMod from fighting YAF's target.
            success &= SetStrategy(FateUtils, "Collect", "Disabled");
            if (!success)
            {
                strategyRetryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
                log.Debug("BossModPresetManager: transient strategy rejected; retrying later");
            }
            else
            {
                forceStrategyRefresh = false;
            }
        }
        catch (Exception ex)
        {
            strategyRetryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
            log.Debug(ex, "BossModPresetManager: transient strategy unavailable");
        }
    }

    private bool SetStrategy(string module, string track, string? value)
    {
        var key = (activePresetName!, module, track);
        if (value == null)
        {
            if (!strategies.ContainsKey(key)) return true;
            // False also means the track is already absent; a missing endpoint throws.
            pluginInterface.GetIpcSubscriber<string, string, string, bool>("BossMod.Presets.ClearTransientStrategy").InvokeFunc(key.Item1, module, track);
            strategies.Remove(key);
            return true;
        }
        if (!forceStrategyRefresh && strategies.TryGetValue(key, out var applied) && applied == value) return true;
        if (!pluginInterface.GetIpcSubscriber<string, string, string, string, bool>("BossMod.Presets.AddTransientStrategy").InvokeFunc(key.Item1, module, track, value)) return false;
        strategies[key] = value;
        return true;
    }

    // Lumina ClassJob.Role: 1 tank, 2 healer, 3 melee DPS, 4 physical-ranged DPS,
    // 5 magical-ranged DPS. BossMod uses zero for unlimited tank pulls.
    public static int TargetsForRole(byte role) => role switch { 1 => 0, 2 => 5, 3 or 4 or 5 => 3, _ => 1 };

    private bool DeactivatePreset()
    {
        if (activePresetName == null) return true;
        if (DateTimeOffset.UtcNow < retryAfter) return false;
        try
        {
            foreach (var key in strategies.Keys.Where(k => k.Preset == activePresetName).ToList())
            {
                // Provider false means the transient track is already absent.
                // Either result invalidates our local cache; exceptions retain it
                // through the outer catch for a later retry.
                pluginInterface.GetIpcSubscriber<string, string, string, bool>("BossMod.Presets.ClearTransientStrategy").InvokeFunc(key.Preset, key.Module, key.Track);
                strategies.Remove(key);
            }
            bool restored;
            if (listProvider)
            {
                restored = pluginInterface.GetIpcSubscriber<List<string>, bool>("BossMod.Presets.SetActiveList").InvokeFunc(new(previousActivePresets!));
                if (restored)
                {
                    if (previousForceDisabled)
                    {
                        var setForce = pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.SetForceDisabled");
                        restored = setForce.InvokeFunc() || pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.GetForceDisabled").InvokeFunc();
                    }
                }
            }
            else if (previousForceDisabled)
            {
                var force = pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.GetForceDisabled");
                restored = force.InvokeFunc() || pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.SetForceDisabled").InvokeFunc();
            }
            else if (previousActivePresets is { Count: > 0 })
                restored = pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive").InvokeFunc(previousActivePresets[0]);
            else
            {
                var get = pluginInterface.GetIpcSubscriber<string?>("BossMod.Presets.GetActive");
                restored = get.InvokeFunc() == null || pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive").InvokeFunc();
            }
            if (!restored) throw new InvalidOperationException("Previous preset restore rejected; retaining snapshot for retry");
            activePresetName = null;
            previousActivePresets = null;
            return true;
        }
        catch (Exception ex)
        {
            retryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
            log.Warning(ex, "BossModPresetManager: restore failed; owned preset and previous state retained");
            return false;
        }
    }

    public void ReleasePresets()
    {
        if (DateTimeOffset.UtcNow < retryAfter) return;
        if (!DeactivatePreset()) return;
        foreach (var name in ownedPresets.ToList())
        {
            try
            {
                // False means the named user preset is already absent.
                var deleted = pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.Delete").InvokeFunc(name);
                if (!deleted)
                {
                    // Provider reports absent as false; treat it as completed cleanup.
                    ownedPresets.Remove(name);
                    availablePresets.Remove(name);
                    continue;
                }
                ownedPresets.Remove(name);
                availablePresets.Remove(name);
            }
            catch (Exception ex)
            {
                retryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
                log.Warning(ex, "BossModPresetManager: deletion failed for {Preset}", name);
            }
        }
        if (ownedPresets.Count == 0) retryAfter = DateTimeOffset.MinValue;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        retryAfter = DateTimeOffset.MinValue;
        ReleasePresets();
    }
}
