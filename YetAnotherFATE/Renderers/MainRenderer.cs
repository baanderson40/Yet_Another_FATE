using System;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Fates;
using YetAnotherFATE.Services.Travel;
using YetAnotherFATE.Windows;

namespace YetAnotherFATE.Renderers;

public sealed class MainRenderer
{
    private readonly Configuration configuration;
    private readonly IFateRepository fateRepository;
    private readonly IFateSelector fateSelector;
    private readonly IPluginLog log;
    private readonly ITravelController travelController;
    private readonly IFateRewardTracker rewardTracker;
    private readonly IFateFollowUpTracker followUpTracker;

    public MainRenderer(
        Configuration configuration,
        IFateRepository fateRepository,
        IFateSelector fateSelector,
        IPluginLog log,
        ITravelController travelController,
        IFateRewardTracker rewardTracker,
        IFateFollowUpTracker followUpTracker)
    {
        this.configuration = configuration;
        this.fateRepository = fateRepository;
        this.fateSelector = fateSelector;
        this.log = log;
        this.travelController = travelController;
        this.rewardTracker = rewardTracker;
        this.followUpTracker = followUpTracker;
    }

    public void Draw()
    {
        if (!configuration.AutomatorEnabled)
        {
            if (ImGui.Button("Start")) rewardTracker.Start();
        }
        else
        {
            var stoppingSafely = rewardTracker.PendingStop;
            if (ImGui.Button(stoppingSafely ? "Stopping safely..." : "Stop"))
            {
                if (stoppingSafely || !ImGui.GetIO().KeyCtrl)
                    rewardTracker.Stop();
                else
                    rewardTracker.RequestStopWhenSafe();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(stoppingSafely ? "Click to stop immediately" : "Ctrl+click to stop when safe");
        }

        ImGui.Spacing();
        DrawFateList();
    }

    private void DrawFateList()
    {
        var fates = fateRepository.EnumerateActiveFates();
        var diagnostics = fateSelector.Evaluate(fates);
        var selectedId = diagnostics.Selected?.Fate.FateId;

        if (ImGui.BeginTable("##yafate_table", 8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame
                | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Reorderable
                | ImGuiTableFlags.Hideable | ImGuiTableFlags.Resizable,
                new Vector2(-1f, Math.Max(0f, ImGui.GetContentRegionAvail().Y))))
        {
            ImGui.TableSetupColumn("FATE", ImGuiTableColumnFlags.NoHide);
            ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Distance (y)", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Time Left", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Has Follow-up", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Score", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Eligibility");
            ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthFixed, 120f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            if (fates.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted("No active FATEs");
                ImGui.EndTable();
                return;
            }

            foreach (var candidate in diagnostics.Candidates)
            {
                var fate = candidate.Fate;
                ImGui.TableNextRow();
                if (selectedId.HasValue && selectedId.Value == fate.FateId)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(YafTheme.Selected));
                }

                ImGui.TableSetColumnIndex(0);
                if (fate.HasBonus)
                {
                    using (ImRaii.PushFont(Dalamud.Interface.UiBuilder.IconFont))
                        ImGui.TextColored(YafTheme.Accent, Dalamud.Interface.FontAwesomeIcon.Star.ToIconString());
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
                var eligibility = candidate.Eligible
                    ? followUpTracker.IsPreferred(fate) ? "Eligible follow-up" : "Eligible"
                    : candidate.ExclusionReason ?? "Excluded";
                ImGui.TextColored(YafTheme.ForStatus(candidate.Eligible ? "Eligible" : "Blocked"), eligibility);

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
