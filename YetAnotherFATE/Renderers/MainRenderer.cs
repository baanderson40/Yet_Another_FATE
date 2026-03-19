using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using YetAnotherFATE.Config;

namespace YetAnotherFATE.Renderers;

public sealed class MainRenderer
{
    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;

    public MainRenderer(
        Configuration configuration,
        IPlayerState playerState,
        IClientState clientState,
        IDataManager dataManager)
    {
        this.configuration = configuration;
        this.playerState = playerState;
        this.clientState = clientState;
        this.dataManager = dataManager;
    }

    public void Draw(System.Action openConfigAction)
    {
        ImGui.Text($"The random config bool is {configuration.SomePropertyToBeSavedAndWithADefault}");

        if (ImGui.Button("Show Settings"))
        {
            openConfigAction();
        }

        ImGui.Spacing();

        using var child = ImRaii.Child("YetAnotherFATE.Body", Vector2.Zero, true);
        if (!child.Success)
        {
            return;
        }

        ImGui.Text("Yet Another FATE bootstrap window");
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
    }
}
