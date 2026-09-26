using System;
using System.Collections.Generic;
using System.Linq;
using YetAnotherFATE.Config;
using YetAnotherFATE.Data.Travel;
using YetAnotherFATE.Services.Zone;

namespace YetAnotherFATE.Services.Travel;

public enum ZoneRotationPhase { Idle, WaitingEmpty, WaitingSafety, Teleporting, Settling, Backoff, Suspended }

// All inputs are current-zone observations, captured on the framework thread.
public readonly record struct ZoneRotationObservation(uint Territory, bool Ready, bool Safe, bool HasTwist, bool Empty);

public sealed class ZoneRotationController(Configuration configuration, IZoneService zones, ITeleportExecutor teleports)
{
    private uint[] selected = [];
    private readonly HashSet<uint> attempted = new();
    private readonly Dictionary<uint, DateTimeOffset> failedUntil = new();
    private DateTimeOffset? emptySince;
    private DateTimeOffset? readySince;
    private DateTimeOffset issuedAt;
    private DateTimeOffset? deadline;
    private bool suspended;
    private DateTimeOffset? scanReadySince;
    private DateTimeOffset contentionUntil;
    private int contentionAttempts;

    public ZoneRotationPhase Phase { get; private set; }
    public uint? Destination { get; private set; }
    public string? DestinationName { get; private set; }
    public string Detail { get; private set; } = "Idle";
    public string? LastFailure { get; private set; }
    public int CompletedCircuits { get; private set; }
    public int AttemptedCount => attempted.Count(id => selected.Contains(id));
    public double Countdown(DateTimeOffset now) => deadline.HasValue ? Math.Max(0, (deadline.Value - now).TotalSeconds) : 0;
    public string Status => $"{Phase}: {Detail} | destination: {DestinationName ?? "none"} | {Countdown(DateTimeOffset.UtcNow):F0}s | circuit {CompletedCircuits + 1}, {AttemptedCount}/{selected.Length} checked | last failure: {LastFailure ?? "none"}";
    public bool ExpectsArrival(uint territory) => Destination == territory && Phase is ZoneRotationPhase.Teleporting or ZoneRotationPhase.Settling;
    public TeleportState TeleportState => teleports.State;
    public bool OwnsLoadingTransit(uint territory, bool loading) => territory == 0 && loading &&
        Destination.HasValue && Phase == ZoneRotationPhase.Teleporting;

    public void InvalidateReadiness()
    {
        scanReadySince = emptySince = readySince = null;
    }

    public void SynchronizeSettings()
    {
        if (Destination.HasValue && (!configuration.ZoneRotationEnabled || !configuration.TeleportEnabled))
            Suspend("Rotation or teleport disabled during outstanding travel");
        if (!configuration.ZoneRotationEnabled && (!suspended || teleports.State is TeleportState.Idle or TeleportState.Completed or TeleportState.Failed))
            Reset();
    }

    public void Reset()
    {
        selected = [];
        attempted.Clear();
        failedUntil.Clear();
        emptySince = readySince = deadline = null;
        Destination = null;
        DestinationName = null;
        LastFailure = null;
        CompletedCircuits = 0;
        suspended = false;
        scanReadySince = null;
        contentionUntil = default;
        contentionAttempts = 0;
        Phase = ZoneRotationPhase.Idle;
        Detail = "Idle";
    }

    // An already-issued game cast cannot be recalled here. Never issue another
    // request after manual travel; toggling automation/rotation rearms it.
    public void Suspend(string reason)
    {
        Reset();
        suspended = true;
        Phase = ZoneRotationPhase.Suspended;
        Detail = reason + "; toggle automation or rotation to resume";
    }

    // Returns true while rotation owns the selecting state (including scan settle).
    public bool Tick(ZoneRotationObservation observation, DateTimeOffset now)
    {
        SynchronizeSettings();
        if (!configuration.ZoneRotationEnabled || !configuration.AutomatorEnabled)
        {
            if (!configuration.AutomatorEnabled) Reset();
            return false;
        }
        if (suspended) return false;

        var list = (configuration.ZoneRotationTerritories ?? []).Where(id => id != 0).Distinct().ToArray();
        if (list.Length == 0 && configuration.ZoneRotationSameExpansionFallback)
            list = zones.GetRotationTerritories(observation.Territory).ToArray();
        if (!selected.SequenceEqual(list))
        {
            selected = list;
            attempted.Clear();
            foreach (var removed in failedUntil.Keys.Where(id => !list.Contains(id)).ToArray())
                failedUntil.Remove(removed);
            emptySince = null;
            CompletedCircuits = 0;
            // Preserve ownership of an issued teleport even if its row is removed.
            if (Destination == null) { deadline = null; Phase = ZoneRotationPhase.Idle; }
        }

        if (!observation.Ready) scanReadySince = null;
        else scanReadySince ??= now;

        if (Phase == ZoneRotationPhase.Teleporting)
        {
            if (observation.Territory == Destination)
            {
                Phase = ZoneRotationPhase.Settling;
                readySince = null;
                deadline = null;
            }
            else if (now - issuedAt >= TimeSpan.FromSeconds(60) ||
                     (now - issuedAt >= TimeSpan.FromSeconds(1) && teleports.State is TeleportState.Idle or TeleportState.Failed))
            {
                FailDestination(now, "Teleport canceled, failed, or arrival timed out");
            }
            else return true;
        }

        if (Phase == ZoneRotationPhase.Settling)
        {
            if (!observation.Ready)
            {
                readySince = deadline = null;
                Detail = "Waiting for destination readiness";
                if (now - issuedAt >= TimeSpan.FromSeconds(90)) Suspend("Destination never became ready");
                return true;
            }
            readySince ??= now;
            deadline = readySince.Value.AddSeconds(Math.Clamp(configuration.ZoneRotationArrivalSettleSeconds, 5, 120));
            Detail = "Settling current-zone FATE scan";
            if (now < deadline) return true;
            Destination = null;
            DestinationName = null;
            readySince = deadline = null;
            Phase = ZoneRotationPhase.Idle;
        }

        if (observation.Ready && !observation.Empty)
        {
            attempted.Clear();
            emptySince = deadline = null;
            CompletedCircuits = 0;
            Phase = ZoneRotationPhase.Idle;
            Detail = "Current-zone FATEs available";
            return false;
        }
        if (!observation.Ready || !observation.Safe || observation.HasTwist || !configuration.TeleportEnabled)
        {
            emptySince = null;
            if (Phase != ZoneRotationPhase.Backoff) deadline = null;
            Phase = Phase == ZoneRotationPhase.Backoff ? Phase : ZoneRotationPhase.WaitingSafety;
            Detail = observation.HasTwist ? "Preserving Twist of Fate; waiting locally" : "Waiting for readiness, safety, or teleports to be enabled";
            return true;
        }
        if (Phase == ZoneRotationPhase.Backoff)
        {
            if (now < deadline) return true;
            attempted.Clear();
            emptySince = null;
            deadline = null;
        }

        emptySince ??= now;
        deadline = emptySince.Value.AddSeconds(Math.Clamp(configuration.ZoneRotationEmptyDelaySeconds, 0, 3600));
        var scanDeadline = scanReadySince!.Value.AddSeconds(Math.Clamp(configuration.ZoneRotationArrivalSettleSeconds, 5, 120));
        if (scanDeadline > deadline) deadline = scanDeadline;
        Phase = ZoneRotationPhase.WaitingEmpty;
        Detail = "Waiting for sustained current-zone emptiness";
        if (now < deadline) return true;
        if (now < contentionUntil)
        {
            deadline = contentionUntil;
            Detail = "Waiting for teleport contention backoff";
            return true;
        }

        attempted.Add(observation.Territory);
        var start = Array.IndexOf(selected, observation.Territory);
        for (var step = 1; step <= selected.Length; step++)
        {
            var id = selected[(start + step) % selected.Length];
            if (id == observation.Territory || !attempted.Add(id)) continue;
            if (failedUntil.TryGetValue(id, out var cooldown) && now < cooldown) continue;
            var zone = zones.GetZone(id);
            var target = zone?.UnlockedAetherytes.Where(a => configuration.TeleportMaxGilCost == 0 || a.GilCost <= configuration.TeleportMaxGilCost)
                .OrderBy(a => a.GilCost).FirstOrDefault();
            if (target is not { AetheryteId: > 0 } aetheryte)
            {
                LastFailure = $"{zone?.Name ?? id.ToString()}: locked, unavailable, or over teleport budget";
                continue;
            }
            Destination = id;
            DestinationName = zone?.Name ?? id.ToString();
            var recommendation = new TeleportRecommendation(true, aetheryte.AetheryteId, "Empty-zone rotation",
                0, 0, 0, 0, 0, aetheryte.GilCost, aetheryte.IsFavourite, aetheryte.Name, aetheryte.SubIndex);
            var result = teleports.RequestTeleport(recommendation);
            if (result is TeleportRequestResult.Busy or TeleportRequestResult.Blocked)
            {
                attempted.Remove(id);
                Destination = null;
                DestinationName = null;
                contentionAttempts = Math.Min(contentionAttempts + 1, 4);
                contentionUntil = now.AddSeconds(Math.Min(10, contentionAttempts * 2));
                deadline = contentionUntil;
                Detail = "Waiting for teleport contention backoff";
                return true;
            }
            contentionAttempts = 0;
            contentionUntil = default;
            if (result != TeleportRequestResult.Issued)
            {
                FailDestination(now, $"Teleport request: {result}");
                // At most one request per framework tick, one per destination per circuit.
                return true;
            }
            issuedAt = now;
            deadline = now.AddSeconds(60);
            emptySince = null;
            Phase = ZoneRotationPhase.Teleporting;
            Detail = "Teleport issued; waiting for expected territory";
            return true;
        }

        CompletedCircuits++;
        Phase = ZoneRotationPhase.Backoff;
        deadline = now.AddSeconds(Math.Clamp(configuration.ZoneRotationCircuitBackoffSeconds, 1, 3600));
        Detail = "Circuit exhausted; no unvisited unlocked destination within budget";
        return true;
    }

    private void FailDestination(DateTimeOffset now, string reason)
    {
        if (Destination is { } id) failedUntil[id] = now.AddSeconds(60);
        LastFailure = $"{DestinationName}: {reason}";
        Destination = null;
        DestinationName = null;
        deadline = null;
        Phase = ZoneRotationPhase.WaitingEmpty;
    }
}
