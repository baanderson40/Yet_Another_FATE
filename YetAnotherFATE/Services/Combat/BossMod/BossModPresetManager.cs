using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Data.Combat;

namespace YetAnotherFATE.Services.Combat.BossMod;

public sealed class BossModPresetManager : IDisposable
{
    private const string CreateEndpoint = "BossMod.Presets.Create";
    private const string DeleteEndpoint = "BossMod.Presets.Delete";
    private const string SetActiveEndpoint = "BossMod.Presets.SetActive";
    private const string GetActiveListEndpoint = "BossMod.Presets.GetActiveList";
    private const string SetActiveListEndpoint = "BossMod.Presets.SetActiveList";

    private const string RotationPresetName = "YAFate Farming";
    private const string PassivePresetName = "YAFate Passive";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICombatPluginRegistry combatPlugins;
    private readonly IPluginLog log;
    private readonly string rotationPresetPath;
    private readonly string passivePresetPath;

    private ICallGateSubscriber<string, bool, bool>? createPresetSubscriber;
    private ICallGateSubscriber<string, bool>? deletePresetSubscriber;
    private ICallGateSubscriber<string, bool>? setActiveSubscriber;
    private ICallGateSubscriber<System.Collections.Generic.List<string>>? getActiveListSubscriber;
    private ICallGateSubscriber<System.Collections.Generic.List<string>, bool>? setActiveListSubscriber;
    private System.Collections.Generic.List<string>? previousActivePresets;
    private string? activePresetName;
    private bool presetsCreated;
    private bool rotationPresetCreated;
    private bool passivePresetCreated;
    private bool disposed;

    public bool PresetsCreated => presetsCreated && !disposed;

    public BossModPresetManager(
        IDalamudPluginInterface pluginInterface,
        ICombatPluginRegistry combatPlugins,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.combatPlugins = combatPlugins;
        this.log = log;

        var baseDir = pluginInterface.AssemblyLocation.DirectoryName ?? string.Empty;
        rotationPresetPath = Path.Combine(baseDir, "Data", "Combat", "BossMod", "YAFateFarmingPreset.json");
        passivePresetPath = Path.Combine(baseDir, "Data", "Combat", "BossMod", "YAFatePassivePreset.json");
    }

    public void EnsurePresets()
    {
        if (disposed || presetsCreated)
        {
            return;
        }

        combatPlugins.Refresh();
        if (!IsBossModAvailable())
        {
            log.Debug("BossModPresetManager: BossMod/BMR not detected, skipping preset creation");
            return;
        }

        if (!EnsureIpc())
        {
            log.Debug("BossModPresetManager: IPC unavailable; cannot create presets");
            return;
        }

        var rotationDefinition = LoadPreset(rotationPresetPath);
        var passiveDefinition = LoadPreset(passivePresetPath);
        if (rotationDefinition == null || passiveDefinition == null)
        {
            log.Warning("BossModPresetManager: preset definitions not found; skipping creation");
            return;
        }

        try
        {
            rotationPresetCreated = createPresetSubscriber!.InvokeFunc(rotationDefinition, false);
            passivePresetCreated = createPresetSubscriber.InvokeFunc(passiveDefinition, false);
            presetsCreated = rotationPresetCreated && passivePresetCreated;

            if (!presetsCreated)
            {
                // We only clean up names this process created; pre-existing user presets are never overwritten or deleted.
                if (rotationPresetCreated)
                {
                    TryDeletePreset(RotationPresetName);
                    rotationPresetCreated = false;
                }

                if (passivePresetCreated)
                {
                    TryDeletePreset(PassivePresetName);
                    passivePresetCreated = false;
                }
            }

            log.Debug("BossModPresetManager: created presets rotation={RotationResult} passive={PassiveResult}", rotationPresetCreated, passivePresetCreated);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "BossModPresetManager: failed to create presets");
            if (rotationPresetCreated)
            {
                TryDeletePreset(RotationPresetName);
                rotationPresetCreated = false;
            }

            if (passivePresetCreated)
            {
                TryDeletePreset(PassivePresetName);
                passivePresetCreated = false;
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ReleasePresets();
    }

    public void ReleasePresets()
    {
        DeactivatePreset();

        if (!presetsCreated)
        {
            return;
        }

        combatPlugins.Refresh();
        if (!IsBossModAvailable() || !EnsureIpc())
        {
            log.Debug("BossModPresetManager: presets were created but BossMod/BMR unavailable during cleanup");
            return;
        }

        if (rotationPresetCreated)
        {
            TryDeletePreset(RotationPresetName);
        }

        if (passivePresetCreated)
        {
            TryDeletePreset(PassivePresetName);
        }

        presetsCreated = false;
        rotationPresetCreated = false;
        passivePresetCreated = false;
    }

    public void ActivateRotationPreset()
    {
        ActivatePreset(RotationPresetName);
    }

    public void ActivatePassivePreset()
    {
        ActivatePreset(PassivePresetName);
    }

    public void DeactivateActivePreset()
    {
        DeactivatePreset();
    }

    private void ActivatePreset(string presetName)
    {
        if (!PresetsCreated)
        {
            EnsurePresets();
        }

        if (!PresetsCreated || !EnsureActivationIpc())
        {
            return;
        }

        if (activePresetName == presetName)
        {
            return;
        }

        if (activePresetName != null)
        {
            DeactivatePreset();
        }

        try
        {
            previousActivePresets = getActiveListSubscriber?.InvokeFunc();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "BossModPresetManager: failed to capture active preset list");
        }

        try
        {
            var result = setActiveSubscriber!.InvokeFunc(presetName);
            if (result)
            {
                activePresetName = presetName;
            }
            log.Debug("BossModPresetManager: SetActive {Preset} => {Result}", presetName, result);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "BossModPresetManager: failed to activate preset {Preset}", presetName);
        }
    }

    private void DeactivatePreset()
    {
        if (activePresetName == null || !EnsureActivationIpc())
        {
            return;
        }

        var restored = false;
        if (previousActivePresets is { Count: > 0 } list)
        {
            try
            {
                restored = setActiveListSubscriber?.InvokeFunc(new System.Collections.Generic.List<string>(list)) ?? false;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "BossModPresetManager: failed to restore previous preset list");
            }
        }

        if (!restored)
        {
            try
            {
                restored = setActiveListSubscriber?.InvokeFunc(new System.Collections.Generic.List<string>()) ?? false;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "BossModPresetManager: failed to clear preset list");
            }
        }

        activePresetName = null;
        previousActivePresets = null;
        log.Debug("BossModPresetManager: preset released (restored={Restored})", restored);
    }

    private void TryDeletePreset(string name)
    {
        try
        {
            var result = deletePresetSubscriber!.InvokeFunc(name);
            log.Debug("BossModPresetManager: deleted preset {Name} => {Result}", name, result);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "BossModPresetManager: failed to delete preset {Name}", name);
        }
    }

    private string? LoadPreset(string path)
    {
        if (!File.Exists(path))
        {
            log.Warning("BossModPresetManager: preset file missing {Path}", path);
            return null;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "BossModPresetManager: failed to read preset file {Path}", path);
            return null;
        }
    }

    private bool EnsureIpc()
    {
        try
        {
            createPresetSubscriber ??= pluginInterface.GetIpcSubscriber<string, bool, bool>(CreateEndpoint);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "BossModPresetManager: failed to hook {Endpoint}", CreateEndpoint);
        }

        try
        {
            deletePresetSubscriber ??= pluginInterface.GetIpcSubscriber<string, bool>(DeleteEndpoint);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "BossModPresetManager: failed to hook {Endpoint}", DeleteEndpoint);
        }

        return createPresetSubscriber != null && deletePresetSubscriber != null;
    }

    private bool EnsureActivationIpc()
    {
        if (setActiveSubscriber != null && getActiveListSubscriber != null && setActiveListSubscriber != null)
        {
            return true;
        }

        try
        {
            setActiveSubscriber ??= pluginInterface.GetIpcSubscriber<string, bool>(SetActiveEndpoint);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "BossModPresetManager: failed to hook {Endpoint}", SetActiveEndpoint);
        }

        try
        {
            getActiveListSubscriber ??= pluginInterface.GetIpcSubscriber<System.Collections.Generic.List<string>>(GetActiveListEndpoint);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "BossModPresetManager: failed to hook {Endpoint}", GetActiveListEndpoint);
        }

        try
        {
            setActiveListSubscriber ??= pluginInterface.GetIpcSubscriber<System.Collections.Generic.List<string>, bool>(SetActiveListEndpoint);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "BossModPresetManager: failed to hook {Endpoint}", SetActiveListEndpoint);
        }

        return setActiveSubscriber != null && getActiveListSubscriber != null && setActiveListSubscriber != null;
    }

    private bool IsBossModAvailable()
    {
        return combatPlugins.Statuses.Any(s =>
            (s.Id == CombatPluginId.BossMod || s.Id == CombatPluginId.BossModReborn) &&
            s.Installed && s.IsLoaded);
    }
}
