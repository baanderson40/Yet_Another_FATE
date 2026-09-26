using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace YetAnotherFATE.Services.Fates;

public sealed class TextAdvanceClient : ITextAdvanceClient, IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private ICallGateSubscriber<string, ExternalConfig, bool>? enable;
    private ICallGateSubscriber<string, bool>? disable;
    private string? ownedMode;
    private DateTimeOffset nextAttemptAt;
    private DateTimeOffset nextLogAt;
    private bool releasePending;

    public TextAdvanceClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public bool EnableForCollection() => Enable("collection", true);

    public bool EnableForActivation() => Enable("activation", false);

    private bool Enable(string mode, bool collection)
    {
        if (ownedMode == mode && !releasePending)
            return true;
        if (DateTimeOffset.UtcNow < nextAttemptAt)
            return false;

        nextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(5);
        try
        {
            enable ??= pluginInterface.GetIpcSubscriber<string, ExternalConfig, bool>("TextAdvance.EnableExternalControl");
            var acquired = enable.InvokeFunc("YetAnotherFATE", new ExternalConfig
            {
                EnableTalkSkip = true,
                EnableRequestHandin = collection,
                EnableRequestFill = collection,
                RequestFillQualityPreference = FillQualityPreference.Any,
            });
            if (acquired)
            {
                ownedMode = mode;
                releasePending = false;
                nextAttemptAt = DateTimeOffset.MinValue;
            }
            return acquired;
        }
        catch (Exception ex)
        {
            LogUnavailable(ex);
            return false;
        }
    }

    public void Disable()
    {
        if (ownedMode == null || (releasePending && DateTimeOffset.UtcNow < nextAttemptAt))
            return;

        releasePending = true;
        try
        {
            disable ??= pluginInterface.GetIpcSubscriber<string, bool>("TextAdvance.DisableExternalControl");
            disable.InvokeFunc("YetAnotherFATE");
            // False means another requester owns control; there is nothing of ours to release.
            ownedMode = null;
            releasePending = false;
            nextAttemptAt = DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            nextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(5);
            LogUnavailable(ex);
        }
    }

    public void Dispose()
    {
        nextAttemptAt = DateTimeOffset.MinValue;
        Disable();
    }

    private void LogUnavailable(Exception ex)
    {
        if (DateTimeOffset.UtcNow < nextLogAt)
            return;
        nextLogAt = DateTimeOffset.UtcNow.AddMinutes(1);
        log.Debug(ex, "TextAdvance IPC unavailable; retrying with backoff");
    }

    // Dalamud CallGateChannel converts copied IPC DTOs via Newtonsoft.Json.
    // Field names and numeric enum values match TextAdvance.ExternalTerritoryConfig.
    [Serializable]
    private sealed class ExternalConfig
    {
        public bool? EnableQuestAccept = false;
        public bool? EnableQuestComplete = false;
        public bool? EnableRewardPick = false;
        public bool? EnableRequestHandin = false;
        public bool? EnableCutsceneEsc = false;
        public bool? EnableCutsceneSkipConfirm = false;
        public bool? EnableTalkSkip = false;
        public bool? EnableRequestFill = false;
        public FillQualityPreference? RequestFillQualityPreference = null;
        public bool? EnableAutoInteract = false;
    }

    private enum FillQualityPreference
    {
        NQ = 0,
        HQ = 1,
        Any = 2,
    }
}
