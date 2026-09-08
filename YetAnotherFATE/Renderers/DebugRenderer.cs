using System;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Chains;
using YetAnotherFATE.Data.Combat;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Combat;
using YetAnotherFATE.Services.Movement;
using YetAnotherFATE.Services.Travel;

namespace YetAnotherFATE.Renderers;

public sealed class DebugRenderer
{
    private readonly IFateRepository fateRepository;
    private readonly IFateSelector fateSelector;
    private readonly IPlayerState playerState;
    private readonly IClientState clientState;
    private readonly ITeleportExecutor teleportExecutor;
    private readonly INavmeshClient navmeshClient;
    private readonly IFateActivationService fateActivationService;
    private readonly ICombatPluginRegistry combatPlugins;
    private readonly AutomatorChain automatorChain;
    private int selectedSection;

    public DebugRenderer(
        IFateRepository fateRepository,
        IFateSelector fateSelector,
        IPlayerState playerState,
        IClientState clientState,
        ITeleportExecutor teleportExecutor,
        INavmeshClient navmeshClient,
        IFateActivationService fateActivationService,
        ICombatPluginRegistry combatPlugins,
        AutomatorChain automatorChain)
    {
        this.fateRepository = fateRepository;
        this.fateSelector = fateSelector;
        this.playerState = playerState;
        this.clientState = clientState;
        this.teleportExecutor = teleportExecutor;
        this.navmeshClient = navmeshClient;
        this.fateActivationService = fateActivationService;
        this.combatPlugins = combatPlugins;
        this.automatorChain = automatorChain;
    }

    public void Draw()
    {
        var fates = fateRepository.EnumerateActiveFates();
        var diagnostics = fateSelector.Evaluate(fates);

        var childSize = ImGui.GetContentRegionAvail();
        var navigationWidth = Math.Clamp(childSize.X * 0.22f, 150f, 220f);
        var contentWidth = childSize.X - navigationWidth - ImGui.GetStyle().ItemSpacing.X;

        if (ImGui.BeginChild("##debug-sections", new Vector2(navigationWidth, childSize.Y), true))
        {
            ImGui.TextUnformatted("Sections");
            ImGui.Separator();
            DrawSectionButton("FATE", 0);
            DrawSectionButton("Plugins", 1);
            ImGui.EndChild();
        }

        ImGui.SameLine();
        if (ImGui.BeginChild("##debug-information", new Vector2(contentWidth, childSize.Y), true))
        {
            if (selectedSection == 0)
            {
                DrawFateSection(diagnostics);
            }
            else
            {
                DrawPluginsSection();
            }

            ImGui.EndChild();
        }
    }

    private void DrawSectionButton(string label, int section)
    {
        var isSelected = selectedSection == section;
        if (isSelected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.Header]);
        }

        if (ImGui.Button($"{label}##debug-section-{section}", new Vector2(-1f, 0f)))
        {
            selectedSection = section;
        }

        if (isSelected)
        {
            ImGui.PopStyleColor();
        }
    }

    private void DrawFateSection(FateSelectionDiagnostics diagnostics)
    {
        ImGui.Text($"Territory: {clientState.TerritoryType} | Player loaded: {playerState.IsLoaded}");
        ImGui.Text($"Automator: {automatorChain.State} ({automatorChain.StateReason ?? "no reason"})");
        ImGui.Text($"Travel: {automatorChain.TravelPhase} ({automatorChain.TravelPhaseDetail ?? "n/a"})");
        ImGui.Text($"Teleport: {teleportExecutor.State} | Navmesh: {navmeshClient.IsAvailable} ({navmeshClient.Status})");
        ImGui.Text($"Activation: {fateActivationService.Status}");

        if (diagnostics.Candidates.Count == 0)
        {
            ImGui.TextDisabled("No active FATE candidates.");
            return;
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"FATE choices ({diagnostics.Candidates.Count})");
        foreach (var candidate in diagnostics.Candidates.OrderByDescending(c => c.Score?.FinalScore ?? double.MinValue))
        {
            var selected = diagnostics.Selected?.Fate.FateId == candidate.Fate.FateId;
            var label = $"{(selected ? "> " : "")}#{candidate.Fate.FateId} {candidate.Fate.Name}##debug-{candidate.Fate.FateId}";
            if (!ImGui.CollapsingHeader(label, selected ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
            {
                continue;
            }

            ImGui.Text($"Territory {candidate.Fate.TerritoryId} | Level {candidate.Fate.Level} | Distance {candidate.Fate.DistanceFromPlayer:F1}y | Time {candidate.Fate.TimeRemaining:mm\\:ss}");
            ImGui.Text($"State: {candidate.Fate.State} | Eligibility: {(candidate.Eligible ? "Eligible" : candidate.ExclusionReason ?? "Excluded")}");
            ImGui.Text($"Progress: {candidate.Fate.Progress}% | Hand-ins: {candidate.Fate.HandInCount} | Bonus: {candidate.Fate.HasBonus} | Max level: {candidate.Fate.MaxLevel}");
            ImGui.Text($"Motivation NPC: {FormatObjectId(candidate.Fate.MotivationNpc)} | Objective NPC: {FormatObjectId(candidate.Fate.ObjectiveNpc)}");
            ImGui.Text($"Rule: {candidate.Fate.Rule} | Fate rule extension: {candidate.Fate.FateRuleEx} | Event item: {candidate.Fate.EventItem}");
            if (!string.IsNullOrWhiteSpace(candidate.Fate.Description))
            {
                ImGui.TextWrapped($"Description: {candidate.Fate.Description}");
            }

            if (!string.IsNullOrWhiteSpace(candidate.Fate.Objective))
            {
                ImGui.TextWrapped($"Objective: {candidate.Fate.Objective}");
            }

            if (candidate.Score is { } score)
            {
                ImGui.Text($"Score: {score.FinalScore:F1} = {score.BaseScore:F0} - {score.DistancePenalty:F1} + {score.TimeAdjustment:F0} - {score.ActivationPenalty:F0} - {score.LevelPenalty:F1} + {score.TravelAdjustment:F0} + {score.ContinuationBonus:F0}");
                ImGui.Text($"Level difference: {score.LevelDifference}");
            }

            if (ImGui.Button($"Copy FATE Report##copy-{candidate.Fate.FateId}"))
            {
                ImGui.SetClipboardText(BuildFateReport(candidate, selected));
            }

            DrawTeleport(candidate.Teleport);
        }
    }

    private void DrawPluginsSection()
    {
        ImGui.TextUnformatted("Combat plugins");
        if (ImGui.Button("Refresh Plugins"))
        {
            combatPlugins.Refresh();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{combatPlugins.Statuses.Count} known plugins");
        ImGui.Separator();

        if (!ImGui.BeginTable("##debug-plugin-status", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            return;
        }

        ImGui.TableSetupColumn("Plugin");
        ImGui.TableSetupColumn("State");
        ImGui.TableSetupColumn("Version");
        ImGui.TableSetupColumn("Internal name");
        ImGui.TableSetupColumn("Capabilities");
        ImGui.TableHeadersRow();

        foreach (var status in combatPlugins.Statuses)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(status.DisplayName);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(GetPluginStateLabel(status));
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(status.Version ?? "n/a");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(status.InternalName);
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(GetCapabilityLabel(status));
        }

        ImGui.EndTable();
    }

    private static string GetPluginStateLabel(CombatPluginStatus status)
    {
        return !status.Installed ? "Missing" : status.IsLoaded ? "Loaded" : "Installed";
    }

    private static string GetCapabilityLabel(CombatPluginStatus status)
    {
        return status.SupportsRotation && status.SupportsDodge
            ? "Rotation + Dodge"
            : status.SupportsRotation ? "Rotation" : status.SupportsDodge ? "Dodge" : "Unknown";
    }

    private static void DrawTeleport(TeleportAnalysis analysis)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Teleport analysis");
        var recommendation = analysis.Recommendation;
        ImGui.Text($"Decision: {(recommendation.ShouldTeleport ? "TELEPORT" : "RUN")} ({recommendation.Reason})");
        ImGui.Text($"Selected: {recommendation.AetheryteName ?? "none"} (ID {recommendation.AetheryteId?.ToString() ?? "n/a"}, sub-index {recommendation.SubIndex})");
        ImGui.Text($"Run: {recommendation.EstimatedRunDistance:F1}y / {recommendation.RunTimeSeconds:F1}s | Teleport route: {recommendation.EstimatedTeleportDistance:F1}y / {recommendation.TeleportTimeSeconds:F1}s");
        ImGui.Text($"Speed {analysis.RunSpeed:F1}y/s | Penalty {analysis.TeleportPenalty:F1}s | Buffer {analysis.PostTeleportBuffer:F1}y | Advantage {analysis.Advantage:F1}s / threshold {analysis.AdvantageThreshold:F1}s");

        if (analysis.Aetherytes.Count == 0)
        {
            ImGui.TextDisabled($"No aetheryte candidates: {analysis.FailureReason ?? "none"}");
            return;
        }

        if (!ImGui.BeginTable("##aetheryte-analysis", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            return;
        }

        ImGui.TableSetupColumn("Aetheryte");
        ImGui.TableSetupColumn("ID");
        ImGui.TableSetupColumn("Position");
        ImGui.TableSetupColumn("Source");
        ImGui.TableSetupColumn("Distance");
        ImGui.TableSetupColumn("Cost");
        ImGui.TableSetupColumn("Sub");
        ImGui.TableSetupColumn("Result");
        ImGui.TableHeadersRow();
        foreach (var item in analysis.Aetherytes)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(item.Aetheryte.Name);
            ImGui.TableSetColumnIndex(1);
            ImGui.Text(item.Aetheryte.AetheryteId.ToString());
            ImGui.TableSetColumnIndex(2);
            ImGui.Text(FormatVector(item.Aetheryte.Position));
            ImGui.TableSetColumnIndex(3);
            ImGui.Text(item.Aetheryte.PositionSource);
            ImGui.TableSetColumnIndex(4);
            ImGui.Text($"{item.DistanceToFate:F1}y");
            ImGui.TableSetColumnIndex(5);
            ImGui.Text(item.Aetheryte.GilCost.ToString());
            ImGui.TableSetColumnIndex(6);
            ImGui.Text(item.Aetheryte.SubIndex.ToString());
            ImGui.TableSetColumnIndex(7);
            ImGui.Text(item.Selected ? "SELECTED" : item.ExclusionReason ?? "available");
        }

        ImGui.EndTable();
    }

    private static string FormatVector(Vector3 position) => $"{position.X:F1}, {position.Y:F1}, {position.Z:F1}";

    private static string FormatObjectId(uint objectId) => objectId == 0 ? "none" : $"{objectId} (0x{objectId:X})";

    private string BuildFateReport(FateCandidateDiagnostic candidate, bool selected)
    {
        var fate = candidate.Fate;
        var recommendation = candidate.Teleport.Recommendation;
        var report = new StringBuilder();
        report.AppendLine("Yet Another FATE diagnostic report");
        report.AppendLine($"Captured: {DateTimeOffset.UtcNow:O}");
        report.AppendLine($"FATE: #{fate.FateId} {fate.Name}");
        report.AppendLine($"Territory: {fate.TerritoryId}");
        report.AppendLine($"Level: {fate.Level}");
        report.AppendLine($"Center: {FormatVector(fate.Center)}");
        report.AppendLine($"Distance from player: {fate.DistanceFromPlayer:F1}y");
        report.AppendLine($"State: {fate.State}");
        report.AppendLine($"Description: {fate.Description}");
        report.AppendLine($"Objective: {fate.Objective}");
        report.AppendLine($"Progress: {fate.Progress}%");
        report.AppendLine($"Hand-in count: {fate.HandInCount}");
        report.AppendLine($"Bonus: {fate.HasBonus}");
        report.AppendLine($"Max level: {fate.MaxLevel}");
        report.AppendLine($"Icon ID: {fate.IconId}");
        report.AppendLine($"Map icon ID: {fate.MapIconId}");
        report.AppendLine($"Start time epoch: {fate.StartTimeEpoch}");
        report.AppendLine($"Duration: {fate.Duration}s");
        report.AppendLine($"Time remaining: {fate.TimeRemaining}");
        report.AppendLine($"Motivation NPC: {FormatObjectId(fate.MotivationNpc)}");
        report.AppendLine($"Objective NPC: {FormatObjectId(fate.ObjectiveNpc)}");
        report.AppendLine($"Rule: {fate.Rule}");
        report.AppendLine($"FATE rule extension: {fate.FateRuleEx}");
        report.AppendLine($"Event item: {fate.EventItem}");
        report.AppendLine($"Required event item: {fate.RequiredEventItem}");
        report.AppendLine($"Turn-in event item: {fate.TurnInEventItem}");
        report.AppendLine($"Selected: {selected}");
        report.AppendLine($"Eligible: {candidate.Eligible}");
        report.AppendLine($"Exclusion: {candidate.ExclusionReason ?? "none"}");

        if (candidate.Score is { } score)
        {
            report.AppendLine($"Score: {score.FinalScore:F1}");
            report.AppendLine($"Score breakdown: base={score.BaseScore:F1}, distancePenalty={score.DistancePenalty:F1}, timeAdjustment={score.TimeAdjustment:F1}, activationPenalty={score.ActivationPenalty:F1}, levelDifference={score.LevelDifference}, levelPenalty={score.LevelPenalty:F1}, travelAdjustment={score.TravelAdjustment:F1}, continuationBonus={score.ContinuationBonus:F1}");
        }

        report.AppendLine();
        report.AppendLine("Teleport analysis");
        report.AppendLine($"Decision: {(recommendation.ShouldTeleport ? "TELEPORT" : "RUN")}");
        report.AppendLine($"Reason: {recommendation.Reason}");
        report.AppendLine($"Selected aetheryte: {recommendation.AetheryteName ?? "none"} (id={recommendation.AetheryteId?.ToString() ?? "none"}, subIndex={recommendation.SubIndex})");
        report.AppendLine($"Run distance/time: {recommendation.EstimatedRunDistance:F1}y / {recommendation.RunTimeSeconds:F1}s");
        report.AppendLine($"Teleport distance/time: {recommendation.EstimatedTeleportDistance:F1}y / {recommendation.TeleportTimeSeconds:F1}s");
        report.AppendLine($"Run speed: {candidate.Teleport.RunSpeed:F1}y/s");
        report.AppendLine($"Teleport penalty: {candidate.Teleport.TeleportPenalty:F1}s");
        report.AppendLine($"Post-teleport buffer: {candidate.Teleport.PostTeleportBuffer:F1}y");
        report.AppendLine($"Advantage: {candidate.Teleport.Advantage:F1}s, threshold={candidate.Teleport.AdvantageThreshold:F1}s");
        report.AppendLine($"Analysis failure: {candidate.Teleport.FailureReason ?? "none"}");

        report.AppendLine();
        report.AppendLine("Aetherytes");
        foreach (var item in candidate.Teleport.Aetherytes)
        {
            report.AppendLine(
                $"- {item.Aetheryte.Name} id={item.Aetheryte.AetheryteId} territory={item.Aetheryte.TerritoryId} " +
                $"position=({FormatVector(item.Aetheryte.Position)}) source={item.Aetheryte.PositionSource} distance={item.DistanceToFate:F1}y " +
                $"adjustedDistance={item.AdjustedDistance:F1}y runTime={item.EstimatedRunTime:F1}s " +
                $"gil={item.Aetheryte.GilCost} withinCost={item.WithinCostLimit} selected={item.Selected} " +
                $"primary={item.Aetheryte.IsPrimary} favourite={item.Aetheryte.IsFavourite} subIndex={item.Aetheryte.SubIndex} " +
                $"exclusion={item.ExclusionReason ?? "none"}");
        }

        report.AppendLine();
        report.AppendLine($"Automator state: {automatorChain.State} ({automatorChain.StateReason ?? "none"})");
        report.AppendLine($"Travel phase: {automatorChain.TravelPhase} ({automatorChain.TravelPhaseDetail ?? "none"})");
        report.AppendLine($"Teleport executor: {teleportExecutor.State}");
        report.AppendLine($"Navmesh: available={navmeshClient.IsAvailable}, status={navmeshClient.Status}");
        report.AppendLine($"FATE activation: {fateActivationService.Status}");
        return report.ToString();
    }
}
