using System;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using YetAnotherFATE.Chains;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Travel;

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
    private readonly ITravelController travelController;
    private readonly IFateRewardTracker rewardTracker;
    private readonly IFateFollowUpTracker followUpTracker;

    public MainRenderer(
        Configuration configuration,
        IPlayerState playerState,
        IClientState clientState,
        IDataManager dataManager,
        IFateRepository fateRepository,
        IFateSelector fateSelector,
        IPluginLog log,
        AutomatorChain automatorChain,
        ITravelController travelController,
        IFateRewardTracker rewardTracker,
        IFateFollowUpTracker followUpTracker)
    {
        this.configuration = configuration;
        this.playerState = playerState;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.fateRepository = fateRepository;
        this.fateSelector = fateSelector;
        this.log = log;
        this.automatorChain = automatorChain;
        this.travelController = travelController;
        this.rewardTracker = rewardTracker;
        this.followUpTracker = followUpTracker;
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

        ImGui.SameLine();
        var automatorEnabled = configuration.AutomatorEnabled;
        if (ImGui.Checkbox("Enable Automator", ref automatorEnabled))
        {
            configuration.AutomatorEnabled = automatorEnabled;
            configuration.Save();
        }

        if (automatorEnabled && !rewardTracker.PendingStop)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop when safe")) rewardTracker.RequestStopWhenSafe();
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

        if (configuration.ZoneRotationEnabled)
            ImGui.TextWrapped($"Zone rotation: {automatorChain.ZoneRotationStatus}");
        ImGui.Text($"Rewards completed: {rewardTracker.CompletedCount}");
        if (rewardTracker.Remaining is { } remaining)
        {
            ImGui.SameLine();
            ImGui.Text($"Remaining: {remaining}");
        }

        if (rewardTracker.PendingStop)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Stop pending: waiting until safe");
        }

        if (followUpTracker.IsWatching)
        {
            ImGui.TextDisabled(followUpTracker.Status);
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

        var diagnostics = fateSelector.Evaluate(fates);
        var selection = diagnostics.Selected;
        var selectedId = selection?.Fate.FateId;

        if (selection != null)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.9f, 0.4f, 1f), $"Tracking: {selection.Value.Fate.Name} (#{selection.Value.Fate.FateId})");
            ImGui.Text($"Diagnostic score: {selection.Value.Score:F1} (selection uses priority order)");
            ImGui.TextWrapped($"Reason: {selection.Value.Reason}");
        }
        else
        {
            ImGui.Text("Tracking: none");
        }

        if (ImGui.BeginTable("##yafate_table", 8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.ScrollX,
                Vector2.Zero, Math.Max(ImGui.GetContentRegionAvail().X, 1000f * ImGuiHelpers.GlobalScale)))
        {
            ImGui.TableSetupColumn("FATE");
            ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Distance (y)", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Time Left", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Has Follow-up", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Score", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Eligibility");
            ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthFixed, 120f * ImGuiHelpers.GlobalScale);
            ImGui.TableHeadersRow();

            foreach (var candidate in diagnostics.Candidates)
            {
                var fate = candidate.Fate;
                ImGui.TableNextRow();
                if (selectedId.HasValue && selectedId.Value == fate.FateId)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.35f, 0.15f, 0.45f)));
                }

                ImGui.TableSetColumnIndex(0);
                if (fate.HasBonus)
                {
                    using (ImRaii.PushFont(Dalamud.Interface.UiBuilder.IconFont))
                        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), Dalamud.Interface.FontAwesomeIcon.Star.ToIconString());
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Bonus FATE");
                    ImGui.SameLine();
                }
                ImGui.TextUnformatted(FormatDisplayName(fate));
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"#{fate.FateId} {fate.Name}\nRule: {fate.FateRule}\nState: {fate.State}\nProgress: {fate.Progress}%\n"
                        + $"On map: {fate.IsOnMap}\nLocation: {(fate.HasSheetMetadata ? fate.LocationId.ToString() : "unknown")}\n"
                        + $"Continuation (sheet location): {fate.IsContinuation}\n"
                        + $"Eligibility: {candidate.ExclusionReason ?? "eligible"}\n"
                        + $"Diagnostic score: {candidate.Score?.ToString() ?? "n/a"}");
                }

                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    ImGui.OpenPopup($"##fate-context-{fate.FateId}");
                }

                if (ImGui.BeginPopup($"##fate-context-{fate.FateId}"))
                {
                    var blacklisted = configuration.FateBlacklist.Contains(fate.FateId);
                    if (ImGui.MenuItem(blacklisted ? "Remove blacklist" : "Blacklist FATE"))
                    {
                        if (blacklisted)
                        {
                            configuration.FateBlacklist.Remove(fate.FateId);
                        }
                        else
                        {
                            configuration.FateBlacklist.Add(fate.FateId);
                        }

                        configuration.Save();
                    }

                    ImGui.EndPopup();
                }

                ImGui.SameLine();
                var movingHere = travelController.CurrentJob is { Kind: TravelRequestKind.DebugForceRun } moving && moving.Selection.Fate.FateId == fate.FateId;
                if ((candidate.Eligible || movingHere) && ImGui.SmallButton($"{(movingHere ? "Stop movement" : "Go")}##{fate.FateId}"))
                {
                    if (movingHere)
                    {
                        travelController.Cancel("Manual movement stopped", TravelRequestKind.DebugForceRun);
                    }
                    else
                    {
                    var recommendation = candidate.Teleport.Recommendation;
                    var manualSelection = new FateSelection(fate, fate.Center, recommendation.ShouldTeleport,
                        recommendation.AetheryteId, candidate.Score?.FinalScore ?? 0, "manual travel", recommendation);
                    travelController.RequestTravel(manualSelection, TravelRequestKind.DebugForceRun);
                    }
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(fate.Level.ToString());

                ImGui.TableSetColumnIndex(2);
                ImGui.Text(fate.DistanceFromPlayer.ToString("F0"));

                ImGui.TableSetColumnIndex(3);
                ImGui.Text(FormatTimeRemaining(fate.TimeRemaining));

                ImGui.TableSetColumnIndex(4);
                ImGui.Text(!fate.HasSheetMetadata ? "Unknown" : fate.HasFollowUp ? "Yes" : "No");

                ImGui.TableSetColumnIndex(5);
                ImGui.Text(candidate.Score?.FinalScore.ToString("F1") ?? "-");

                ImGui.TableSetColumnIndex(6);
                ImGui.TextWrapped(candidate.Eligible
                    ? followUpTracker.IsPreferred(fate) ? "Eligible follow-up" : "Eligible"
                    : candidate.ExclusionReason ?? "Excluded");

                ImGui.TableSetColumnIndex(7);
                DrawProgress(fate.Progress);
            }

            ImGui.EndTable();
        }
    }

    private string FormatDisplayName(ActiveFate fate)
    {
        var format = string.IsNullOrWhiteSpace(configuration.DisplayNameFormat)
            ? Configuration.DefaultDisplayNameFormat : configuration.DisplayNameFormat;
        // Single pass keeps token-like text in a FATE name literal.
        return Regex.Replace(format, @"\{(Level|Name|Id|Progress|TimeRemaining|Distance|State)\}", match => match.Value switch
        {
            "{Level}" => fate.Level.ToString(),
            "{Name}" => fate.Name,
            "{Id}" => fate.FateId.ToString(),
            "{Progress}" => fate.Progress.ToString(),
            "{TimeRemaining}" => FormatTimeRemaining(fate.TimeRemaining),
            "{Distance}" => fate.DistanceFromPlayer.ToString("F1"),
            "{State}" => fate.State.ToString(),
            _ => match.Value,
        });
    }

    private void DrawProgress(byte progress)
    {
        var label = $"{progress}%";
        var size = new Vector2(Math.Max(1f, ImGui.GetContentRegionAvail().X), ImGui.GetFrameHeight());
        var origin = ImGui.GetCursorScreenPos();
        using (ImRaii.PushColor(ImGuiCol.PlotHistogram, configuration.BarColour))
            ImGui.ProgressBar(Math.Clamp(progress / 100f, 0f, 1f), size, "");

        var textSize = ImGui.CalcTextSize(label);
        var position = origin + new Vector2(Math.Max(0f, size.X - textSize.X - 4f * ImGuiHelpers.GlobalScale), (size.Y - textSize.Y) / 2f);
        var drawList = ImGui.GetWindowDrawList();
        // Outlined text stays readable over both the configured fill and empty track.
        drawList.AddText(position + new Vector2(-1, 0), 0xFF000000, label);
        drawList.AddText(position + new Vector2(1, 0), 0xFF000000, label);
        drawList.AddText(position + new Vector2(0, -1), 0xFF000000, label);
        drawList.AddText(position + new Vector2(0, 1), 0xFF000000, label);
        drawList.AddText(position, 0xFFFFFFFF, label);
    }

    private static string FormatTimeRemaining(TimeSpan span)
    {
        if (span.TotalSeconds < 0)
        {
            return "∞";
        }

        return $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
    }
}
