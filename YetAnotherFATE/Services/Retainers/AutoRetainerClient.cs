using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;
using YetAnotherFATE.Data.Retainers;

namespace YetAnotherFATE.Services.Retainers;

public sealed class AutoRetainerClient : IAutoRetainerClient
{
    private readonly ICallGateSubscriber<List<ulong>> getRegisteredCharacters;
    private readonly ICallGateSubscriber<Dictionary<ulong, HashSet<string>>> getEnabledRetainers;
    private readonly ICallGateSubscriber<ulong, object> getOfflineCharacterData;
    private readonly ICallGateSubscriber<ulong, long?> getClosestRetainer;
    private readonly ICallGateSubscriber<ulong, bool?> areVesselsReady;
    private readonly ICallGateSubscriber<object> getRetainerConfiguration;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<bool> getMultiModeStatus;
    private readonly ICallGateSubscriber<int?, object> enableSingleMultiMode;
    private readonly ICallGateSubscriber<object> singleMultiModeFinished;
    private readonly IPluginLog log;

    public AutoRetainerClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        getRegisteredCharacters = pluginInterface.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs");
        getEnabledRetainers = pluginInterface.GetIpcSubscriber<Dictionary<ulong, HashSet<string>>>("AutoRetainer.PluginState.GetEnabledRetainers");
        getOfflineCharacterData = pluginInterface.GetIpcSubscriber<ulong, object>("AutoRetainer.GetOfflineCharacterData");
        getClosestRetainer = pluginInterface.GetIpcSubscriber<ulong, long?>("AutoRetainer.PluginState.GetClosestRetainerVentureSecondsRemaining");
        areVesselsReady = pluginInterface.GetIpcSubscriber<ulong, bool?>("AutoRetainer.PluginState.AreAnyEnabledVesselsReady");
        getRetainerConfiguration = pluginInterface.GetIpcSubscriber<object>("AutoRetainer.GetConfig.MultiModeRetainerConfiguration");
        isBusy = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy");
        getMultiModeStatus = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.GetMultiModeStatus");
        enableSingleMultiMode = pluginInterface.GetIpcSubscriber<int?, object>("AutoRetainer.PluginState.EnableSingleMultiMode");
        singleMultiModeFinished = pluginInterface.GetIpcSubscriber<object>("AutoRetainer.PluginState.OnSingleMultiModeFinished");
        this.log = log;
        singleMultiModeFinished.Subscribe(OnSingleMultiModeFinished);
    }

    public event Action? SinglePassFinished;

    public bool TryGetState(out bool busy, out bool multiModeActive)
    {
        try
        {
            busy = isBusy.InvokeFunc();
            multiModeActive = getMultiModeStatus.InvokeFunc();
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "AutoRetainer state IPC unavailable");
            busy = false;
            multiModeActive = false;
            return false;
        }
    }

    public bool TryGetReadiness(AutoRetainerMode mode, out AutoRetainerReadinessSnapshot snapshot)
    {
        var observedAt = DateTimeOffset.UtcNow;
        try
        {
            var registered = getRegisteredCharacters.InvokeFunc() ?? new List<ulong>();
            var selectedRetainers = getEnabledRetainers.InvokeFunc() ?? new Dictionary<ulong, HashSet<string>>();
            var (advanceSeconds, waitForAll) = ReadRetainerConfiguration();
            var retainerReadyCount = 0;
            var retainerCandidates = 0;
            long? closestRetainerSeconds = null;
            var deployableReady = false;

            foreach (var contentId in registered)
            {
                if (mode is AutoRetainerMode.Retainers or AutoRetainerMode.Everything)
                {
                    var remaining = getClosestRetainer.InvokeFunc(contentId);
                    if (remaining is { } seconds)
                    {
                        if (closestRetainerSeconds is null || seconds < closestRetainerSeconds)
                            closestRetainerSeconds = seconds;
                        var allRemaining = ReadRetainerRemaining(contentId, selectedRetainers, seconds);
                        retainerCandidates++;
                        if (IsRetainerReady(allRemaining, seconds, waitForAll, advanceSeconds))
                            retainerReadyCount++;
                    }
                }

                if (mode is AutoRetainerMode.Submersibles or AutoRetainerMode.Everything)
                    deployableReady |= areVesselsReady.InvokeFunc(contentId) == true;
            }

            var retainerReady = retainerCandidates > 0 && retainerReadyCount > 0;
            var workReady = mode switch
            {
                AutoRetainerMode.Retainers => retainerReady,
                AutoRetainerMode.Submersibles => deployableReady,
                _ => retainerReady || deployableReady,
            };

            TryGetState(out var busy, out var multiModeActive);
            snapshot = new AutoRetainerReadinessSnapshot(
                true,
                workReady,
                registered.Count,
                retainerReadyCount,
                closestRetainerSeconds,
                retainerReady,
                deployableReady,
                busy,
                multiModeActive,
                observedAt,
                null);
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "AutoRetainer readiness IPC unavailable");
            snapshot = new AutoRetainerReadinessSnapshot(
                false,
                false,
                0,
                0,
                null,
                false,
                false,
                false,
                false,
                observedAt,
                ex.GetType().Name);
            return false;
        }
    }

    public bool TryGetRegisteredCharacters(out IReadOnlyList<AutoRetainerCharacter> characters)
    {
        try
        {
            characters = (getRegisteredCharacters.InvokeFunc() ?? new List<ulong>())
                .Select(cid => (Cid: cid, Data: getOfflineCharacterData.InvokeFunc(cid)))
                .Where(x => x.Data is not null)
                .Select(x => new AutoRetainerCharacter(x.Cid,
                    ReadCharacterString(x.Data!, "Name"), ReadCharacterString(x.Data!, "World")))
                .Where(x => x.ContentId != 0 && !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.World))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.World, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "AutoRetainer character roster IPC unavailable");
            characters = Array.Empty<AutoRetainerCharacter>();
            return false;
        }
    }

    private static string ReadCharacterString(object data, string name)
    {
        if (data is JObject json && json.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var token) &&
            token.Type is not JTokenType.Null and not JTokenType.Undefined)
            return token.ToString();

        var type = data.GetType();
        return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(data)?.ToString() ??
               type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(data)?.ToString() ?? string.Empty;
    }

    public bool TryEnableSinglePass(AutoRetainerMode mode)
    {
        try
        {
            enableSingleMultiMode.InvokeAction((int?)mode);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not enable AutoRetainer single-pass Multi Mode");
            return false;
        }
    }

    public void Dispose() => singleMultiModeFinished.Unsubscribe(OnSingleMultiModeFinished);

    private (long AdvanceSeconds, bool WaitForAll) ReadRetainerConfiguration()
    {
        try
        {
            var configuration = getRetainerConfiguration.InvokeFunc();
            if (configuration == null)
                return (0, false);

            var type = configuration.GetType();
            var advance = ReadMember(type, configuration, "AdvanceTimer", 0L);
            var waitForAll = ReadMember(type, configuration, "MultiWaitForAll", false);
            return (Math.Max(0, advance), waitForAll);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "AutoRetainer retainer configuration unavailable; using immediate any-retainer readiness");
            return (0, false);
        }
    }

    private IReadOnlyList<long> ReadRetainerRemaining(
        ulong contentId,
        IReadOnlyDictionary<ulong, HashSet<string>> selectedRetainers,
        long closestSeconds)
    {
        if (!selectedRetainers.TryGetValue(contentId, out var selected) || selected.Count == 0)
            return [closestSeconds];

        var character = getOfflineCharacterData.InvokeFunc(contentId);
        var dataType = character.GetType();
        var retainerData = dataType.GetField("RetainerData", BindingFlags.Public | BindingFlags.Instance)?.GetValue(character) as System.Collections.IEnumerable;
        if (retainerData is null)
            return [closestSeconds];

        var remaining = new List<long>();
        foreach (var retainer in retainerData)
        {
            if (retainer is null)
                continue;
            var type = retainer.GetType();
            var name = type.GetField("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(retainer) as string;
            var hasVenture = type.GetField("HasVenture", BindingFlags.Public | BindingFlags.Instance)?.GetValue(retainer) as bool?;
            var ventureEndsAt = type.GetField("VentureEndsAt", BindingFlags.Public | BindingFlags.Instance)?.GetValue(retainer);
            if (name is null || !selected.Contains(name) || hasVenture != true || ventureEndsAt is null)
                continue;
            remaining.Add(Convert.ToInt64(ventureEndsAt, CultureInfo.InvariantCulture) - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        return remaining.Count == 0 ? [closestSeconds] : remaining;
    }

    private static bool IsRetainerReady(IReadOnlyList<long> remaining, long closestSeconds, bool waitForAll, long advanceSeconds)
    {
        if (closestSeconds > advanceSeconds)
            return false;
        return !waitForAll || remaining.All(seconds => seconds <= advanceSeconds);
    }

    private static T ReadMember<T>(Type type, object value, string name, T fallback)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        var raw = property?.GetValue(value) ?? field?.GetValue(value);
        if (raw is null)
            return fallback;
        try
        {
            return (T)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private void OnSingleMultiModeFinished() => SinglePassFinished?.Invoke();
}
