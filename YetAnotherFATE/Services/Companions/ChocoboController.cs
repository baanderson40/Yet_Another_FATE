using System;
using Dalamud.Plugin.Services;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Companions;

namespace YetAnotherFATE.Services.Companions;

public sealed class ChocoboController : IChocoboController
{
    private enum PendingAction
    {
        None,
        Dismount,
        Summon,
        Stance,
    }

    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private const float RefreshThresholdSeconds = 180;

    private readonly Configuration configuration;
    private readonly IChocoboCompanionApi api;
    private readonly IPluginLog log;
    private readonly Func<DateTimeOffset> clock;
    private PendingAction pending;
    private DateTimeOffset nextPoll;
    private DateTimeOffset deadline;
    private DateTimeOffset retryAfter;
    private bool summonWasActive;
    private float summonBaselineTime;
    private uint summonBaselineHealth;
    private bool pendingCombat;
    private string status = "Disabled";

    public ChocoboController(Configuration configuration, IChocoboCompanionApi api, IPluginLog log)
        : this(configuration, api, log, () => DateTimeOffset.UtcNow)
    {
    }

    internal ChocoboController(Configuration configuration, IChocoboCompanionApi api, IPluginLog log, Func<DateTimeOffset> clock)
    {
        this.configuration = configuration;
        this.api = api;
        this.log = log;
        this.clock = clock;
    }

    public string Status => status;

    public bool Tick()
        => TickCore(allowRoutineRefresh: true, allowDismount: true, combatMode: false);

    public void TickDuringCombat()
        => TickCore(allowRoutineRefresh: false, allowDismount: false, combatMode: true);

    private bool TickCore(bool allowRoutineRefresh, bool allowDismount, bool combatMode)
    {
        if (configuration.ChocoboStance == ChocoboStance.Disabled)
        {
            Reset();
            return false;
        }

        var now = clock();
        if (pending != PendingAction.None)
        {
            if (now < nextPoll)
                return true;
            return PollPending(now, allowRoutineRefresh, allowDismount);
        }

        if (now < retryAfter)
        {
            status = "Backoff";
            return false;
        }

        var snapshot = api.Read();
        if (!snapshot.PlayerReady)
        {
            status = "Waiting for player";
            return false;
        }
        if (snapshot.IsStabled)
        {
            status = "Chocobo stabled";
            return false;
        }
        if (snapshot.IsMounted && !allowDismount)
        {
            status = "Mounted; combat summon deferred";
            return false;
        }
        if (snapshot.IsMounted)
            return BeginDismount(now);

        if (!snapshot.IsAlive || (allowRoutineRefresh && snapshot.TimeLeftSeconds <= RefreshThresholdSeconds))
        {
            if (snapshot.GreensCount <= 0)
            {
                status = "No Gysahl Greens";
            }
            else
            {
                var result = api.TryUseGreens();
                if (result == ChocoboActionResult.Issued)
                {
                    summonWasActive = snapshot.IsActive;
                    summonBaselineTime = snapshot.TimeLeftSeconds;
                    summonBaselineHealth = snapshot.CurrentHealth;
                    Begin(PendingAction.Summon, now, combatMode, combatMode ? "Combat resummon" : "Summoning chocobo");
                    return true;
                }

                HandleResult(result, "Summon unavailable");
            }
        }

        if (!snapshot.IsAlive)
            return false;

        if (snapshot.ActiveCommand != (byte)configuration.ChocoboStance)
        {
            var result = api.TrySetStance(configuration.ChocoboStance);
            if (result == ChocoboActionResult.Issued)
            {
                Begin(PendingAction.Stance, now, combatMode, combatMode ? "Applying combat stance" : "Applying chocobo stance");
                return true;
            }

            if (result == ChocoboActionResult.AlreadySatisfied)
            {
                status = $"Ready ({snapshot.TimeLeftSeconds:F0}s, {configuration.ChocoboStance})";
                return false;
            }

            HandleResult(result, "Stance unavailable");
            return false;
        }

        status = $"Ready ({snapshot.TimeLeftSeconds:F0}s, {configuration.ChocoboStance})";
        return false;
    }

    public void Reset()
    {
        pending = PendingAction.None;
        nextPoll = DateTimeOffset.MinValue;
        deadline = DateTimeOffset.MinValue;
        retryAfter = DateTimeOffset.MinValue;
        summonWasActive = false;
        summonBaselineTime = 0;
        summonBaselineHealth = 0;
        pendingCombat = false;
        status = configuration.ChocoboStance == ChocoboStance.Disabled ? "Disabled" : "Ready";
    }

    private bool BeginDismount(DateTimeOffset now)
    {
        var result = api.TryDismount();
        if (result == ChocoboActionResult.AlreadySatisfied)
            return false;
        if (result != ChocoboActionResult.Issued)
        {
            HandleResult(result, "Waiting to dismount");
            return false;
        }

        Begin(PendingAction.Dismount, now, false, "Dismounting before chocobo action");
        return true;
    }

    private bool PollPending(DateTimeOffset now, bool allowRoutineRefresh, bool allowDismount)
    {
        var snapshot = api.Read();
        var complete = pending switch
        {
            PendingAction.Dismount => !snapshot.IsMounted,
            PendingAction.Summon => snapshot.IsAlive && (!summonWasActive ||
                snapshot.TimeLeftSeconds > summonBaselineTime + 1 ||
                (summonBaselineHealth == 0 && snapshot.CurrentHealth > 0)),
            PendingAction.Stance => snapshot.ActiveCommand == (byte)configuration.ChocoboStance,
            _ => true,
        };
        if (complete)
        {
            var completed = pending;
            var completedInCombat = pendingCombat;
            pending = PendingAction.None;
            pendingCombat = false;
            summonWasActive = false;
            summonBaselineHealth = 0;
            status = completed == PendingAction.Stance ? $"Ready ({configuration.ChocoboStance})" : "Ready";
            return TickCore(allowRoutineRefresh, allowDismount, completedInCombat);
        }

        if (now >= deadline)
        {
            var timedOut = pending;
            log.Debug("ChocoboController: {Action} confirmation timed out", timedOut);
            pending = PendingAction.None;
            pendingCombat = false;
            summonWasActive = false;
            summonBaselineHealth = 0;
            retryAfter = now + RetryDelay;
            status = $"{timedOut} timed out";
            return false;
        }

        nextPoll = now + PollDelay;
        return true;
    }

    private void Begin(PendingAction action, DateTimeOffset now, bool combatMode, string message)
    {
        pending = action;
        pendingCombat = combatMode;
        nextPoll = now + PollDelay;
        deadline = now + ActionTimeout;
        status = message;
    }

    private void HandleResult(ChocoboActionResult result, string fallback)
    {
        status = result switch
        {
            ChocoboActionResult.NoGreens => "No Gysahl Greens",
            ChocoboActionResult.Busy => "Waiting for safe state",
            ChocoboActionResult.Mounted => "Mounted",
            ChocoboActionResult.InStable => "Chocobo stabled",
            ChocoboActionResult.NotReady => "Waiting for player",
            _ => fallback,
        };
        if (result is ChocoboActionResult.Rejected or ChocoboActionResult.Unavailable)
            retryAfter = clock() + RetryDelay;
    }
}
