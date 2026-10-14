using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin.Services;
using ECommons.Reflection;

namespace YetAnotherFATE.Services.Retainers;

public sealed class WahToolsFeeshStatusReader(IPluginLog log)
{
    private const string ModuleTypeName = "WahTools.Modules.Feesh.FeeshModule";
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Dictionary<(Type Type, string Name), MemberInfo> memberCache = [];
    private Type? cachedModuleType;
    private WeakReference<object>? currentModule;
    private string lastUnavailableMessage = string.Empty;
    private long generation;

    public WahToolsFeeshStatus Read()
    {
        var now = DateTime.UtcNow;
        try
        {
            if (!DalamudReflector.TryGetDalamudPlugin("WahTools", out var plugin, suppressErrors: true, ignoreCache: true) || plugin is null)
            {
                ClearCache();
                return Report(WahToolsStatusAvailability.NotInstalled, "WahTools is not loaded", now);
            }

            var modules = ReadMember(ReadMember(plugin, "Modules"), "Modules") as IEnumerable;
            var module = modules?.Cast<object?>().FirstOrDefault(x => x?.GetType().FullName == ModuleTypeName);
            if (module is null)
            {
                ClearCache();
                return Report(WahToolsStatusAvailability.Incompatible, "WahTools Feesh module not found", now);
            }
            if (cachedModuleType != module.GetType())
            {
                ClearCache();
                cachedModuleType = module.GetType();
            }
            if (currentModule is null || !currentModule.TryGetTarget(out var previous) || !ReferenceEquals(previous, module))
            {
                currentModule = new WeakReference<object>(module);
                generation++;
            }
            if (ReadRequired<bool>(module, "IsLoaded") != true)
            {
                ClearCache();
                return Report(WahToolsStatusAvailability.NotInstalled, "WahTools Feesh module is not loaded", now);
            }

            var trip = ReadPrivateField(module, "trip");
            var cleaner = ReadRequiredObject(module, "Cleaner");
            var desynther = ReadRequiredObject(module, "Desynther");
            var discarder = ReadRequiredObject(module, "Discarder");
            var buyer = ReadRequiredObject(module, "Buyer");
            var ocean = ReadMember(module, "Ocean");
            var status = new WahToolsFeeshStatus(
                WahToolsStatusAvailability.Available, now, string.Empty,
                trip is not null, trip is null ? "Idle" : ReadOptionalString(trip, "State"),
                trip is null ? "Trip not initialized" : ReadOptionalString(trip, "StatusText"),
                trip is null ? false : ReadOptional<bool>(trip, "IsRunning"),
                ReadRequired<bool>(ocean, "OnBoat"), ReadRequired<bool>(module, "Cleaning"),
                ReadOptionalString(cleaner, "State"), ReadOptionalString(cleaner, "StatusText"),
                ReadRequired<bool>(cleaner, "Running"), ReadRequired<bool>(desynther, "Running"),
                ReadOptionalString(desynther, "Status"), ReadRequired<bool>(discarder, "Running"),
                ReadOptionalString(discarder, "Status"), ReadRequired<bool>(buyer, "Busy")) with { Generation = generation };
            if (lastUnavailableMessage.Length > 0)
            {
                log.Information("WahTools Feesh status reflection recovered.");
                lastUnavailableMessage = string.Empty;
            }
            return status;
        }
        catch (MissingMemberException ex)
        {
            ClearCache();
            return Report(WahToolsStatusAvailability.Incompatible, $"WahTools status layout incompatible: {ex.Message}", now, ex);
        }
        catch (Exception ex)
        {
            ClearCache();
            return Report(WahToolsStatusAvailability.ReadFailed, $"WahTools status read failed: {ex.GetBaseException().Message}", now, ex);
        }
    }

    private object ReadRequiredObject(object target, string member) => ReadMember(target, member) ?? throw new MissingMemberException($"WahTools Feesh member {member} is null");
    private T ReadRequired<T>(object? target, string member) => ReadMember(target, member) is T value ? value : throw new MissingMemberException($"WahTools Feesh member {member} missing or wrong type");
    private T? ReadOptional<T>(object? target, string member) where T : struct => ReadMember(target, member) is T value ? value : null;
    private string? ReadOptionalString(object? target, string member) => ReadMember(target, member)?.ToString();
    private object? ReadPrivateField(object target, string name) => GetMember(target.GetType(), name, true) is FieldInfo field ? field.GetValue(target) : throw new MissingFieldException(target.GetType().FullName, name);

    private object? ReadMember(object? target, string name)
    {
        if (target is null) throw new MissingMemberException($"WahTools Feesh target null while reading {name}");
        return GetMember(target.GetType(), name) switch
        {
            PropertyInfo property => property.GetValue(target),
            FieldInfo field => field.GetValue(target),
            _ => throw new MissingMemberException(target.GetType().FullName, name),
        };
    }

    private MemberInfo GetMember(Type type, string name, bool privateFieldOnly = false)
    {
        if (memberCache.TryGetValue((type, name), out var cached)) return cached;
        MemberInfo? member = privateFieldOnly
            ? type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            : type.GetProperty(name, InstanceMembers) ?? (MemberInfo?)type.GetField(name, InstanceMembers);
        if (member is null) throw new MissingMemberException(type.FullName, name);
        memberCache[(type, name)] = member;
        return member;
    }

    private WahToolsFeeshStatus Report(WahToolsStatusAvailability availability, string message, DateTime now, Exception? ex = null)
    {
        var key = $"{availability}:{message}";
        if (lastUnavailableMessage != key)
        {
            if (ex is null) log.Warning("{Message}", message); else log.Warning(ex, "{Message}", message);
            lastUnavailableMessage = key;
        }
        return WahToolsFeeshStatus.Unavailable(availability, message, now) with { Generation = generation };
    }

    private void ClearCache()
    {
        memberCache.Clear();
        cachedModuleType = null;
        if (currentModule is not null) generation++;
        currentModule = null;
    }
}
