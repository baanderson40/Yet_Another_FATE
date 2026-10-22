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
            long? closestRetainerSeconds = null;
            var deployableReady = false;

            foreach (var contentId in registered)
            {
                var character = getOfflineCharacterData.InvokeFunc(contentId);
                if (character is null)
                    continue;

                if (mode is AutoRetainerMode.Retainers or AutoRetainerMode.Everything)
                {
                    var remaining = getClosestRetainer.InvokeFunc(contentId);
                    if (remaining is { } seconds)
                    {
                        if (closestRetainerSeconds is null || seconds < closestRetainerSeconds)
                            closestRetainerSeconds = seconds;
                    }

                    var selected = selectedRetainers.TryGetValue(contentId, out var enabledRetainers)
                        ? enabledRetainers
                        : new HashSet<string>();
                    var allRemaining = ReadRetainerRemaining(character, selected, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    var characterEnabled = ReadMember(character.GetType(), character, "Enabled", false);
                    var closest = remaining is { } closestSeconds ? closestSeconds : (long?)null;
                    if (AutoRetainerReadinessPolicy.IsRetainerReady(
                            characterEnabled, allRemaining.Count > 0, allRemaining, closest, waitForAll, advanceSeconds))
                        retainerReadyCount++;
                }

                if (mode is AutoRetainerMode.Submersibles or AutoRetainerMode.Everything)
                    deployableReady |= ReadMember(character.GetType(), character, "WorkshopEnabled", false) &&
                                       areVesselsReady.InvokeFunc(contentId) == true;
            }

            var retainerReady = retainerReadyCount > 0;
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

    private static IReadOnlyList<long> ReadRetainerRemaining(object character, IReadOnlySet<string> selectedRetainers, long nowUnixSeconds)
    {
        if (selectedRetainers.Count == 0)
            return Array.Empty<long>();

        var retainerData = ReadObjectMember(character, "RetainerData") as System.Collections.IEnumerable;
        if (retainerData is null)
            return Array.Empty<long>();

        var remaining = new List<long>();
        foreach (var retainer in retainerData)
        {
            if (retainer is null)
                continue;
            var name = ReadObjectMember(retainer, "Name")?.ToString();
            var hasVenture = ReadMember(retainer.GetType(), retainer, "HasVenture", false);
            var ventureEndsAt = ReadMember(retainer.GetType(), retainer, "VentureEndsAt", 0L);
            if (string.IsNullOrWhiteSpace(name) || !selectedRetainers.Contains(name) || !hasVenture || ventureEndsAt <= 0)
                continue;
            remaining.Add(ventureEndsAt - nowUnixSeconds);
        }

        return remaining;
    }

    private static object? ReadObjectMember(object? value, string name)
    {
        if (value is JObject json && json.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var token))
            return token is JValue jValue ? jValue.Value : token;

        if (value is JToken jsonToken && jsonToken.Type == JTokenType.Object &&
            jsonToken[name] is { } child)
            return child is JValue childValue ? childValue.Value : child;

        if (value is null)
            return null;
        var type = value.GetType();
        return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value) ??
               type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);
    }

    private static T ReadMember<T>(Type type, object value, string name, T fallback)
    {
        var raw = ReadObjectMember(value, name);
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
