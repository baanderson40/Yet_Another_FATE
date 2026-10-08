using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Combat;
using YetAnotherFATE.Data.Companions;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Retainers;
using YetAnotherFATE.Services.Combat;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Services.Zone;

namespace YetAnotherFATE.Windows;

public class ConfigWindow : Window, IDisposable
{
    private enum ConfigSection
    {
        AutoRetainer,
        Combat,
        Companion,
        Display,
        FateSelection,
        Teleport,
        ZoneRotation,
    }

    private const float ControlWidth = 150f;
    private readonly Configuration configuration;
    private readonly ICombatPluginRegistry combatPlugins;
    private readonly IZoneService zones;
    private (uint Id, string Name)[]? rotationZones;
    private bool initialRefreshDone;
    private ConfigSection selectedSection = ConfigSection.Display;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Configuration configuration, ICombatPluginRegistry combatPlugins, IZoneService zones) : base("Yet Another FATE Config")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(760, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.configuration = configuration;
        this.combatPlugins = combatPlugins;
        this.zones = zones;
    }

    public void Dispose() { }

    public override void Draw()
    {
        EnsureProvidersDetected();
        var available = ImGui.GetContentRegionAvail();
        var navigationWidth = Math.Clamp(available.X * 0.22f, 150f, 220f);
        var contentWidth = Math.Max(0f, available.X - navigationWidth - ImGui.GetStyle().ItemSpacing.X);

        if (ImGui.BeginChild("##config-sections", new Vector2(navigationWidth, available.Y), true))
        {
            ImGui.TextUnformatted("Sections");
            ImGui.Separator();
            DrawSectionButton("AutoRetainer", ConfigSection.AutoRetainer);
            DrawSectionButton("Combat", ConfigSection.Combat);
            DrawSectionButton("Companion", ConfigSection.Companion);
            DrawSectionButton("FATE Display", ConfigSection.Display);
            DrawSectionButton("FATE Selection", ConfigSection.FateSelection);
            DrawSectionButton("Teleport", ConfigSection.Teleport);
            DrawSectionButton("Zone Rotation", ConfigSection.ZoneRotation);
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("##config-content", new Vector2(contentWidth, available.Y), true))
        {
            switch (selectedSection)
            {
                case ConfigSection.AutoRetainer:
                    DrawAutoRetainerSection();
                    break;
                case ConfigSection.Combat:
                    DrawCombatSection();
                    break;
                case ConfigSection.Companion:
                    DrawCompanionSection();
                    break;
                case ConfigSection.Display:
                    DrawDisplaySection();
                    break;
                case ConfigSection.FateSelection:
                    DrawFateSection();
                    break;
                case ConfigSection.Teleport:
                    DrawTeleportSection();
                    break;
                case ConfigSection.ZoneRotation:
                    DrawZoneRotationSection();
                    break;
            }
        }
        ImGui.EndChild();
    }

    private void DrawSectionButton(string label, ConfigSection section)
    {
        var selected = selectedSection == section;
        if (selected)
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.Header]);

        if (ImGui.Button($"{label}##config-section-{section}", new Vector2(-1f, 0f)))
            selectedSection = section;

        if (selected)
            ImGui.PopStyleColor();
    }

    private void DrawZoneRotationSection()
    {
        ImGui.TextUnformatted("Empty-zone rotation");
        var enabled = configuration.ZoneRotationEnabled;
        if (ImGui.Checkbox("Rotate empty zones", ref enabled))
        {
            configuration.ZoneRotationEnabled = enabled;
            configuration.Save();
        }
        var returnIdle = configuration.ReturnToNearestAetheryteWhenIdle;
        ImGui.BeginDisabled(enabled);
        if (ImGui.Checkbox("Return to nearest aetheryte when idle", ref returnIdle))
        {
            configuration.ReturnToNearestAetheryteWhenIdle = returnIdle;
            configuration.Save();
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped("When zone rotation is disabled and no eligible FATE remains, teleport to the nearest unlocked main aetheryte after the shared no-FATE delay.");
        ImGui.TextWrapped("Only the current zone's FATEs are visible. Travel visits the ordered list; it does not scan remote zones. Teleport enable and max gil cost apply. Twist of Fate keeps you in the current zone.");
        var fallback = configuration.ZoneRotationSameExpansionFallback;
        if (ImGui.Checkbox("Use same-expansion overworld zones when list is empty", ref fallback))
        {
            configuration.ZoneRotationSameExpansionFallback = fallback;
            configuration.Save();
        }
        var noEligible = configuration.ZoneRotationWhenNoEligibleFates;
        if (ImGui.Checkbox("Also rotate when no FATE meets selection filters", ref noEligible))
        {
            configuration.ZoneRotationWhenNoEligibleFates = noEligible;
            configuration.Save();
        }
        DrawIntegerSetting("No-FATE delay (s)", configuration.ZoneRotationEmptyDelaySeconds, 0, 3600, v => configuration.ZoneRotationEmptyDelaySeconds = v);
        DrawIntegerSetting("Arrival scan settle (s)", configuration.ZoneRotationArrivalSettleSeconds, 5, 120, v => configuration.ZoneRotationArrivalSettleSeconds = v);
        DrawIntegerSetting("Full-circuit backoff (s)", configuration.ZoneRotationCircuitBackoffSeconds, 1, 3600, v => configuration.ZoneRotationCircuitBackoffSeconds = v);
        configuration.ZoneRotationTerritories ??= new();
        var list = configuration.ZoneRotationTerritories;
        for (var i = 0; i < list.Count; i++)
        {
            var zone = zones.GetZone(list[i]);
            ImGui.PushID($"rotation-{i}");
            ImGui.TextUnformatted($"{i + 1}. {zone?.Name ?? $"Territory {list[i]}"} [{(zone?.HasUnlockedAetheryte == true ? "Unlocked" : "Locked / unavailable")}]");
            ImGui.SameLine();
            ImGui.BeginDisabled(i == 0);
            var up = ImGui.SmallButton("Up");
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(i == list.Count - 1);
            var down = ImGui.SmallButton("Down");
            ImGui.EndDisabled();
            ImGui.SameLine();
            var remove = ImGui.SmallButton("Remove");
            ImGui.PopID();
            if (remove) list.RemoveAt(i);
            else if (up) (list[i - 1], list[i]) = (list[i], list[i - 1]);
            else if (down) (list[i + 1], list[i]) = (list[i], list[i + 1]);
            if (remove || up || down) { configuration.Save(); break; }
        }
        if (ImGui.BeginCombo("Add rotation zone", "Select a zone"))
        {
            // Metadata only; unlock state is refreshed by ZoneService each draw.
            rotationZones ??= zones.GetRotationTerritories()
                .Select(id => (Id: id, Name: zones.GetZone(id)?.Name ?? $"Territory {id}"))
                .OrderBy(z => z.Name).ToArray();
            foreach (var entry in rotationZones.Where(z => !list.Contains(z.Id)))
            {
                var unlocked = zones.GetZone(entry.Id)?.HasUnlockedAetheryte == true;
                if (ImGui.Selectable($"{entry.Name} [{(unlocked ? "Unlocked" : "Locked")}]##{entry.Id}"))
                {
                    list.Add(entry.Id);
                    configuration.Save();
                    break;
                }
            }
            ImGui.EndCombo();
        }
    }

    private void DrawAutoRetainerSection()
    {
        ImGui.TextUnformatted("AutoRetainer handoff");
        var enabled = configuration.AutoRetainerHandoffEnabled;
        if (ImGui.Checkbox("Pause farming for AutoRetainer", ref enabled))
        {
            configuration.AutoRetainerHandoffEnabled = enabled;
            configuration.Save();
        }

        ImGui.SetNextItemWidth(ControlWidth);
        var modeLabel = configuration.AutoRetainerMode.ToString();
        if (ImGui.BeginCombo("Single-pass mode", modeLabel))
        {
            foreach (var mode in Enum.GetValues<AutoRetainerMode>())
            {
                var selected = mode == configuration.AutoRetainerMode;
                if (ImGui.Selectable(mode.ToString(), selected))
                {
                    configuration.AutoRetainerMode = mode;
                    configuration.Save();
                }
            }
            ImGui.EndCombo();
        }

        DrawIntegerSetting("Readiness check (s)", configuration.AutoRetainerReadinessIntervalSeconds, 15, 600,
            value => configuration.AutoRetainerReadinessIntervalSeconds = value);
        DrawIntegerSetting("Retry cooldown (s)", configuration.AutoRetainerRetryCooldownSeconds, 30, 1800,
            value => configuration.AutoRetainerRetryCooldownSeconds = value);
        ImGui.TextWrapped("YAF returns to the nearest unlocked main aetheryte before every YAF-started AutoRetainer pass. Teleport, gil, Twist of Fate, and safety restrictions can hold the handoff. YAF resumes after returning to the original character.");
    }

    private void DrawDisplaySection()
    {
        ImGui.TextUnformatted("FATE Display");
        var format = configuration.DisplayNameFormat ?? Configuration.DefaultDisplayNameFormat;
        ImGui.SetNextItemWidth(ControlWidth + 150f);
        if (ImGui.InputText("Display name template", ref format, 1024))
        {
            configuration.DisplayNameFormat = format;
            configuration.Save();
        }

        ImGui.TextWrapped("Tokens (case-sensitive): {Level}, {Name}, {Id}, {Progress}, {TimeRemaining}, {Distance}, {State}");
        ImGui.TextWrapped("Progress is a number (add % yourself); time is mm:ss or ∞; distance is yalms with one decimal. Unknown tokens stay literal. Blank uses the default.");
        var colour = configuration.BarColour;
        if (ImGui.ColorEdit4("Progress bar colour", ref colour))
        {
            configuration.BarColour = colour;
            configuration.Save();
        }

        if (ImGui.Button("Reset display defaults"))
        {
            configuration.DisplayNameFormat = Configuration.DefaultDisplayNameFormat;
            configuration.BarColour = Configuration.DefaultBarColour;
            configuration.Save();
        }
    }

    private void DrawCombatSection()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Combat Providers");
        ImGui.Separator();

        if (ImGui.Button("Refresh Providers"))
        {
            combatPlugins.Refresh();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Detected plugins: " + combatPlugins.Statuses.Count);

        ImGui.NewLine();

        DrawProviderCombo(
            "Rotation Provider",
            configuration.PreferredRotationProvider,
            status => status.SupportsRotation,
            id => configuration.PreferredRotationProvider = id);

        var rotation = configuration.PreferredRotationProvider;
        var disableDodge = rotation is CombatPluginId.BossMod or CombatPluginId.BossModReborn;

        if (disableDodge)
        {
            ImGui.BeginDisabled();
        }

        DrawProviderCombo(
            "Dodge Provider",
            configuration.PreferredDodgeProvider,
            status => status.SupportsDodge,
            id => configuration.PreferredDodgeProvider = id);

        if (disableDodge)
        {
            ImGui.EndDisabled();
        }

        ImGui.NewLine();

    }

    private void DrawCompanionSection()
    {
        ImGui.TextUnformatted("Chocobo Companion");
        ImGui.Separator();
        ImGui.SetNextItemWidth(ControlWidth);
        if (ImGui.BeginCombo("Combat stance", configuration.ChocoboStance.ToString()))
        {
            foreach (var stance in Enum.GetValues<ChocoboStance>())
            {
                var selected = stance == configuration.ChocoboStance;
                if (ImGui.Selectable(stance.ToString(), selected) && !selected)
                {
                    configuration.ChocoboStance = stance;
                    configuration.Save();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextWrapped("YAF uses Gysahl Greens already in normal inventory and refreshes the companion below three minutes between FATEs. If the companion is defeated during combat, YAF resummons it and restores the selected stance without pausing the combat provider. It does not purchase greens.");
        ImGui.TextWrapped("Disabled leaves chocobo summon and stance control off.");
    }

    private void EnsureProvidersDetected()
    {
        if (initialRefreshDone)
        {
            return;
        }

        combatPlugins.Refresh();
        initialRefreshDone = true;
    }

    private void DrawFateSection()
    {
        ImGui.TextUnformatted("FATE Selection");
        ImGui.Separator();
        DrawIntegerSetting("Min time remaining (s)", configuration.FateMinTimeRemainingSeconds, 0, 900, value => configuration.FateMinTimeRemainingSeconds = value);
        DrawIntegerSetting("Max duration (s)", configuration.FateMaxDurationSeconds, 1, 3600, value => configuration.FateMaxDurationSeconds = value);
        DrawIntegerSetting("Max progress (%)", configuration.FateMaxProgress, 0, 100, value => configuration.FateMaxProgress = value);
        DrawIntegerSetting("Min level", configuration.FateMinLevel, 1, 100, value => configuration.FateMinLevel = value);
        DrawIntegerSetting("Max level", configuration.FateMaxLevel, 1, 100, value => configuration.FateMaxLevel = value);

        var followUp = configuration.WaitForFollowUpFates;
        if (ImGui.Checkbox("Wait for follow-up FATEs", ref followUp))
        {
            configuration.WaitForFollowUpFates = followUp;
            configuration.Save();
        }

        var collection = configuration.CollectionPolicy;
        ImGui.SetNextItemWidth(ControlWidth + 100f);
        if (ImGui.BeginCombo("Collection policy", collection == FateCollectionPolicy.BossModDefault ? "BossMod defaults" : "Kill only"))
        {
            if (ImGui.Selectable("BossMod defaults", collection == FateCollectionPolicy.BossModDefault))
            {
                configuration.CollectionPolicy = FateCollectionPolicy.BossModDefault;
                configuration.Save();
            }

            if (ImGui.Selectable("Kill only", collection == FateCollectionPolicy.KillOnly))
            {
                configuration.CollectionPolicy = FateCollectionPolicy.KillOnly;
                configuration.Save();
            }

            ImGui.EndCombo();
        }

        var byRole = configuration.BossModTargetsByRole;
        if (ImGui.Checkbox("BossMod target count by role (tank: unlimited, DPS: 3, healer: 5, other: 1)", ref byRole))
        {
            configuration.BossModTargetsByRole = byRole;
            configuration.Save();
        }
        ImGui.BeginDisabled(byRole);
        DrawIntegerSetting("BossMod FATE max targets (0 = unlimited)", configuration.BossModMaxTargets, 0, 20, value => configuration.BossModMaxTargets = value);
        ImGui.EndDisabled();

        ImGui.TextUnformatted("Blacklisted FATE rules");
        foreach (var rule in Enum.GetValues<FateRule>().Where(r => r != FateRule.None))
        {
            var blacklisted = configuration.BlacklistedFateRules.Contains(rule);
            if (ImGui.Checkbox(rule.ToString(), ref blacklisted))
            {
                if (blacklisted)
                {
                    configuration.BlacklistedFateRules.Add(rule);
                }
                else
                {
                    configuration.BlacklistedFateRules.Remove(rule);
                }

                configuration.Save();
            }
        }

        ImGui.TextUnformatted("Priority order");
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset priorities"))
        {
            configuration.FateSortOrder = Configuration.CreateDefaultFateSortOrder();
            configuration.Save();
        }
        ImGui.TextDisabled("Top criterion wins; lower rows break ties.");
        var usedCriteria = configuration.FateSortOrder.Select(item => item.Criteria).ToHashSet();
        var moveFrom = -1;
        var moveTo = -1;
        var removeAt = -1;
        for (var index = 0; index < configuration.FateSortOrder.Count; index++)
        {
            var item = configuration.FateSortOrder[index];
            ImGui.PushID(index);
            ImGui.SetNextItemWidth(ControlWidth + 40f);
            if (ImGui.BeginCombo("##criteria", item.Criteria.ToString()))
            {
                foreach (var criteria in Enum.GetValues<FateSortCriteria>())
                {
                    if (criteria != item.Criteria && usedCriteria.Contains(criteria))
                        continue;

                    if (ImGui.Selectable(criteria.ToString(), criteria == item.Criteria))
                    {
                        usedCriteria.Remove(item.Criteria);
                        usedCriteria.Add(criteria);
                        item.Criteria = criteria;
                        configuration.FateSortOrder[index] = item;
                        configuration.Save();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(item.Descending ? "DESC" : "ASC"))
            {
                item.Descending = !item.Descending;
                configuration.FateSortOrder[index] = item;
                configuration.Save();
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(index == 0);
            if (ImGui.SmallButton("Up"))
            {
                moveFrom = index;
                moveTo = index - 1;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(index == configuration.FateSortOrder.Count - 1);
            if (ImGui.SmallButton("Down"))
            {
                moveFrom = index;
                moveTo = index + 1;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                removeAt = index;
            }

            ImGui.PopID();
            if (removeAt >= 0)
                break;
        }

        if (removeAt >= 0)
        {
            configuration.FateSortOrder.RemoveAt(removeAt);
            configuration.Save();
        }

        if (moveFrom >= 0)
        {
            (configuration.FateSortOrder[moveFrom], configuration.FateSortOrder[moveTo]) =
                (configuration.FateSortOrder[moveTo], configuration.FateSortOrder[moveFrom]);
            configuration.Save();
        }

        var availableCriteria = Enum.GetValues<FateSortCriteria>()
            .Where(criteria => !usedCriteria.Contains(criteria))
            .ToArray();
        ImGui.BeginDisabled(availableCriteria.Length == 0);
        if (ImGui.Button("Add priority criterion") && availableCriteria.Length > 0)
        {
            configuration.FateSortOrder.Add(new FateSortOrder { Criteria = availableCriteria[0], Descending = false });
            configuration.Save();
        }
        ImGui.EndDisabled();
    }

    private void DrawIntegerSetting(string label, int value, int min, int max, Action<int> assign)
    {
        ImGui.SetNextItemWidth(ControlWidth);
        var next = value;
        if (ImGui.InputInt(label, ref next))
        {
            assign(Math.Clamp(next, min, max));
            configuration.Save();
        }
    }

    private void DrawTeleportSection()
    {
        ImGui.TextUnformatted("Teleport Heuristics");
        ImGui.Separator();
        var enabled = configuration.TeleportEnabled;
        if (ImGui.Checkbox("Enable Teleport Advisor", ref enabled))
        {
            configuration.TeleportEnabled = enabled;
            configuration.Save();
        }

        var runSpeed = configuration.TeleportMountedRunSpeed;
        DrawFloatSetting("Mounted run speed (y/s)", ref runSpeed, 1.0f, 20.0f, v => configuration.TeleportMountedRunSpeed = v);
        var penalty = configuration.TeleportPenaltySeconds;
        DrawFloatSetting("Teleport penalty (s)", ref penalty, 0.0f, 60.0f, v => configuration.TeleportPenaltySeconds = v);
        var buffer = configuration.TeleportPostTeleportRunBufferYalms;
        DrawFloatSetting("Post-teleport buffer (y)", ref buffer, 0.0f, 1000.0f, v => configuration.TeleportPostTeleportRunBufferYalms = v);
        var minDistance = configuration.TeleportMinimumDistanceYalms;
        DrawFloatSetting("Min teleport distance (y)", ref minDistance, 0.0f, 10000.0f, v => configuration.TeleportMinimumDistanceYalms = v);
        var advantage = configuration.TeleportAdvantageThresholdSeconds;
        DrawFloatSetting("Required advantage (s)", ref advantage, 0.0f, 60.0f, v => configuration.TeleportAdvantageThresholdSeconds = v);

        var maxCost = (int)configuration.TeleportMaxGilCost;
        ImGui.SetNextItemWidth(ControlWidth);
        if (ImGui.InputInt("Max teleport cost (gil)", ref maxCost))
        {
            configuration.TeleportMaxGilCost = (uint)Math.Max(0, maxCost);
            configuration.Save();
        }
    }

    private void DrawFloatSetting(string label, ref float value, float min, float max, Action<float> assign)
    {
        ImGui.SetNextItemWidth(ControlWidth);
        var temp = value;
        if (ImGui.InputFloat(label, ref temp))
        {
            var clamped = Math.Clamp(temp, min, max);
            assign(clamped);
            configuration.Save();
        }
    }

    private void DrawProviderCombo(string label, CombatPluginId current, Func<CombatPluginStatus, bool> predicate, Action<CombatPluginId> assign)
    {
        var statuses = combatPlugins.Statuses
            .Where(s => predicate(s) && s.Installed && s.IsLoaded)
            .ToList();
        var currentLabel = GetProviderDisplayName(current);
        ImGui.SetNextItemWidth(ControlWidth + 100f);
        if (ImGui.BeginCombo(label, currentLabel))
        {
            var autoSelected = current == CombatPluginId.None;
            if (ImGui.Selectable("Auto-select", autoSelected) && !autoSelected)
            {
                assign(CombatPluginId.None);
                configuration.Save();
            }

            foreach (var status in statuses)
            {
                var selected = current == status.Id;
                if (ImGui.Selectable(status.DisplayName, selected) && !selected)
                {
                    assign(status.Id);
                    configuration.Save();
                }
            }

            ImGui.EndCombo();
        }
    }

    private string GetProviderDisplayName(CombatPluginId provider)
    {
        if (provider == CombatPluginId.None)
        {
            return "Auto-select";
        }

        var status = combatPlugins.GetStatus(provider);
        if (status == null)
        {
            return provider.ToString();
        }

        if (!status.Installed || !status.IsLoaded)
        {
            return "Auto-select";
        }

        return status.DisplayName;
    }

}
