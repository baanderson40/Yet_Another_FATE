using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YetAnotherFATE.Data.Combat;
using YetAnotherFATE.Data.Companions;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Retainers;

namespace YetAnotherFATE.Config;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool AutomatorEnabled { get; set; } = false;
    public bool DiagnosticLoggingEnabled { get; set; } = false;
    public const string DefaultDisplayNameFormat = "[{Level}] {Name}";
    public static readonly Vector4 DefaultBarColour = new(0.404f, 0.259f, 0.541f, 1f);
    public string DisplayNameFormat { get; set; } = DefaultDisplayNameFormat;
    public Vector4 BarColour { get; set; } = DefaultBarColour;
    public List<uint> FateBlacklist { get; set; } = new();
    public List<FateRule> BlacklistedFateRules { get; set; } = new();
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public List<FateSortOrder> FateSortOrder { get; set; } = CreateDefaultFateSortOrder();
    public int FateMaxDurationSeconds { get; set; } = 900;
    public int FateMinTimeRemainingSeconds { get; set; } = 120;
    public int FateMaxProgress { get; set; } = 90;
    public int FateMinLevel { get; set; } = 1;
    public int FateMaxLevel { get; set; } = 100;
    public bool WaitForFollowUpFates { get; set; } = true;
    public int FollowUpWaitSeconds { get; set; } = 15;
    public FateCollectionPolicy CollectionPolicy { get; set; } = FateCollectionPolicy.BossModDefault;
    public int BossModMaxTargets { get; set; } = 3;
    public bool BossModTargetsByRole { get; set; } = false;
    public ChocoboStance ChocoboStance { get; set; } = ChocoboStance.Disabled;

    public bool TeleportEnabled { get; set; } = true;
    public float TeleportMountedRunSpeed { get; set; } = 6.0f;
    public float TeleportPenaltySeconds { get; set; } = 15.0f;
    public float TeleportPostTeleportRunBufferYalms { get; set; } = 200.0f;
    public float TeleportMinimumDistanceYalms { get; set; } = 300.0f;
    public float TeleportAdvantageThresholdSeconds { get; set; } = 5.0f;
    public uint TeleportMaxGilCost { get; set; } = 9999;
    public bool ZoneRotationEnabled { get; set; } = false;
    public bool ZoneRotationSameExpansionFallback { get; set; } = false;
    public List<uint> ZoneRotationTerritories { get; set; } = new();
    public int ZoneRotationEmptyDelaySeconds { get; set; } = 30;
    public int ZoneRotationArrivalSettleSeconds { get; set; } = 5;
    public int ZoneRotationCircuitBackoffSeconds { get; set; } = 60;
    public bool ZoneRotationWhenNoEligibleFates { get; set; } = false;
    public CombatPluginId PreferredRotationProvider { get; set; } = CombatPluginId.None;
    public CombatPluginId PreferredDodgeProvider { get; set; } = CombatPluginId.None;

    public bool AutoRetainerHandoffEnabled { get; set; } = false;
    public AutoRetainerMode AutoRetainerMode { get; set; } = AutoRetainerMode.Retainers;
    public int AutoRetainerReadinessIntervalSeconds { get; set; } = 60;
    public int AutoRetainerRetryCooldownSeconds { get; set; } = 120;
    public AutoRetainerHandoffSession? AutoRetainerHandoff { get; set; }

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface dalamudPluginInterface)
    {
        pluginInterface = dalamudPluginInterface;
        if (Version < 1)
        {
            // The initial port accidentally used the urgency threshold as the
            // exclusion default, making the entire urgent bucket unreachable.
            if (FateMinTimeRemainingSeconds == 240)
                FateMinTimeRemainingSeconds = 120;
            Version = 1;
            Save();
        }
        if (Version < 2)
        {
            AutoRetainerMode = Enum.IsDefined(typeof(AutoRetainerMode), AutoRetainerMode)
                ? AutoRetainerMode
                : YetAnotherFATE.Data.Retainers.AutoRetainerMode.Retainers;
            AutoRetainerReadinessIntervalSeconds = Math.Clamp(AutoRetainerReadinessIntervalSeconds, 15, 600);
            AutoRetainerRetryCooldownSeconds = Math.Clamp(AutoRetainerRetryCooldownSeconds, 30, 1800);
            Version = 2;
            Save();
        }
        var normalizedSortOrder = NormalizeFateSortOrder(FateSortOrder);
        var sortOrderChanged = !AreSortOrdersEqual(FateSortOrder, normalizedSortOrder);
        FateSortOrder = normalizedSortOrder;
        if (Version < 3 || sortOrderChanged)
        {
            Version = Math.Max(Version, 3);
            Save();
        }
        if (Version < 4)
        {
            if (!Enum.IsDefined(ChocoboStance))
                ChocoboStance = ChocoboStance.Disabled;
            Version = 4;
            Save();
        }
        else if (!Enum.IsDefined(ChocoboStance))
        {
            ChocoboStance = ChocoboStance.Disabled;
            Save();
        }
    }

    public static List<FateSortOrder> CreateDefaultFateSortOrder() =>
    [
        new() { Criteria = FateSortCriteria.HasBonusWithTwist, Descending = true },
        new() { Criteria = FateSortCriteria.Progress, Descending = true },
        new() { Criteria = FateSortCriteria.HasBonus, Descending = true },
        new() { Criteria = FateSortCriteria.TimeRemainingUrgent, Descending = true },
        new() { Criteria = FateSortCriteria.TimeRemaining, Descending = false },
        new() { Criteria = FateSortCriteria.Distance, Descending = false },
    ];

    private static List<FateSortOrder> NormalizeFateSortOrder(List<FateSortOrder>? sortOrder)
    {
        if (sortOrder == null)
            return CreateDefaultFateSortOrder();

        var seen = new HashSet<FateSortCriteria>();
        return sortOrder
            .Where(item => Enum.IsDefined(item.Criteria) && seen.Add(item.Criteria))
            .Select(item => new FateSortOrder { Criteria = item.Criteria, Descending = item.Descending })
            .ToList();
    }

    private static bool AreSortOrdersEqual(IReadOnlyList<FateSortOrder>? left, IReadOnlyList<FateSortOrder> right)
        => left != null && left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Criteria == pair.Second.Criteria && pair.First.Descending == pair.Second.Descending);

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}

public enum FateCollectionPolicy
{
    BossModDefault = 0,
    KillOnly = 1,
}
