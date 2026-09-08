using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using YetAnotherFATE.Chains;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Fates;

namespace YetAnotherFATE.Renderers;

public sealed class MainRenderer
{
    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IFateRepository fateRepository;
    private readonly IFateSelector fateSelector;
    private readonly IPluginLog log;
    private readonly AutomatorChain automatorChain;

    public MainRenderer(
        Configuration configuration,
        IPlayerState playerState,
        IClientState clientState,
        IDataManager dataManager,
        IFateRepository fateRepository,
        IFateSelector fateSelector,
        IPluginLog log,
        AutomatorChain automatorChain)
    {
        this.configuration = configuration;
        this.playerState = playerState;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.fateRepository = fateRepository;
        this.fateSelector = fateSelector;
        this.log = log;
        this.automatorChain = automatorChain;
    }

    public void Draw(System.Action openConfigAction, System.Action openDebugAction)
    {
        if (ImGui.Button("Show Settings"))
        {
            openConfigAction();
        }

        ImGui.SameLine();
        if (ImGui.Button("Debug"))
        {
            openDebugAction();
        }

        var automatorEnabled = configuration.AutomatorEnabled;
        if (ImGui.Checkbox("Enable Automator", ref automatorEnabled))
        {
            configuration.AutomatorEnabled = automatorEnabled;
            configuration.Save();
        }

        ImGui.Text($"Automator: {automatorChain.State}");
        if (!string.IsNullOrWhiteSpace(automatorChain.StateReason))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(automatorChain.StateReason);
        }

        if (automatorChain.CurrentSelection is { } selection)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"FATE #{selection.Fate.FateId} {selection.Fate.Name}");
        }

        if (automatorChain.TravelPhase != TravelPhase.Idle)
        {
            ImGui.TextDisabled($"Travel: {automatorChain.TravelPhase} {automatorChain.TravelPhaseDetail}");
        }

        ImGui.Spacing();

        using var child = ImRaii.Child("YetAnotherFATE.Body", Vector2.Zero, true);
        if (!child.Success)
        {
            return;
        }

        ImGui.Text("Active FATEs");
        ImGuiHelpers.ScaledDummy(20.0f);

        if (!playerState.IsLoaded)
        {
            ImGui.Text("Local player is not logged in.");
            return;
        }

        if (!playerState.ClassJob.IsValid)
        {
            ImGui.Text("Current job information unavailable.");
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.Text("Current job:");
        ImGui.SameLine(120 * ImGuiHelpers.GlobalScale);

        ImGui.Text(playerState.ClassJob.Value.Abbreviation.ToString());
        ImGui.SameLine();
        ImGui.Text($" [Level {playerState.Level}]");

        var territoryId = clientState.TerritoryType;
        if (dataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territoryRow))
        {
            ImGui.Text("Current location:");
            ImGui.SameLine(120 * ImGuiHelpers.GlobalScale);
            ImGui.Text(territoryRow.PlaceName.Value.Name.ToString());
        }
        else
        {
            ImGui.Text("Current location unknown.");
        }

        ImGuiHelpers.ScaledDummy(10f);
        DrawFateList();
    }

    private void DrawFateList()
    {
        var fates = fateRepository.EnumerateActiveFates();

        if (fates.Count == 0)
        {
            ImGui.Text("No active FATEs detected.");
            return;
        }

        var ordered = fates
            .OrderBy(f => f.DistanceFromPlayer)
            .ThenBy(f => f.TimeRemaining)
            .ToList();

        var selection = fateSelector.SelectBest(fates);
        var selectedId = selection?.Fate.FateId;

        if (selection != null)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.9f, 0.4f, 1f), $"Tracking: {selection.Value.Fate.Name} (#{selection.Value.Fate.FateId})");
            ImGui.Text($"Score: {selection.Value.Score:F1}");
            ImGui.TextWrapped($"Reason: {selection.Value.Reason}");
        }
        else
        {
            ImGui.Text("Tracking: none");
        }

        if (ImGui.BeginTable("##yafate_table", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("FATE");
            ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Distance (y)", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Time Left", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Continuation", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Score", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();

            foreach (var fate in ordered)
            {
                ImGui.TableNextRow();
                if (selectedId.HasValue && selectedId.Value == fate.FateId)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.35f, 0.15f, 0.45f)));
                }

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted($"#{fate.FateId} {fate.Name}");

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(fate.Level.ToString());

                ImGui.TableSetColumnIndex(2);
                ImGui.Text(fate.DistanceFromPlayer.ToString("F0"));

                ImGui.TableSetColumnIndex(3);
                ImGui.Text(FormatTimeRemaining(fate.TimeRemaining));

                ImGui.TableSetColumnIndex(4);
                ImGui.Text(fate.IsContinuation ? "Yes" : "No");

                ImGui.TableSetColumnIndex(5);
                if (selectedId.HasValue && selectedId.Value == fate.FateId && selection != null)
                {
                    ImGui.Text(selection.Value.Score.ToString("F1"));
                }
                else
                {
                    ImGui.Text("-");
                }
            }

            ImGui.EndTable();
        }
    }

    private static string FormatTimeRemaining(TimeSpan span)
    {
        if (span.TotalSeconds <= 0)
        {
            return "0:00";
        }

        span = TimeSpan.FromSeconds(Math.Min(span.TotalSeconds, TimeSpan.FromMinutes(59).TotalSeconds));
        return span.ToString("m\\:ss");
    }
}
