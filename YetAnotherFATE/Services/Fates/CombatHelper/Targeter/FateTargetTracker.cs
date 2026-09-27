using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates.CombatHelper.Targeter;

public sealed class FateTargetTracker : IFateTargetTracker
{
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private FateTargetSnapshot? currentTarget;
    private DateTimeOffset nextLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextExclusionLogAt = DateTimeOffset.MinValue;

    public FateTargetTracker(
        IObjectTable objectTable,
        IClientState clientState,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.log = log;
    }

    public FateTargetSnapshot? CurrentTarget => currentTarget;

    public event EventHandler<FateTargetChangedEventArgs>? TargetChanged;

    public FateTargetSnapshot? Refresh(FateSelection selection)
    {
        if (!clientState.IsLoggedIn)
        {
            ClearInternal("Client not logged in", false);
            return null;
        }

        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            ClearInternal("Local player missing", false);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var candidates = new List<FateTargetSnapshot>();

        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc npc)
            {
                continue;
            }

            var exclusionReason = FateTargetPolicy.GetExclusionReason(npc, selection.Fate);
            if (exclusionReason != null)
            {
                if (currentTarget is { } previous && previous.ObjectId == npc.GameObjectId)
                    EmitExclusion(previous, npc, selection.Fate, exclusionReason);
                continue;
            }

            var position = npc.Position;
            var distanceFromPlayer = Vector3.Distance(player.Position, position);
            var dx = selection.Fate.Center.X - position.X;
            var dz = selection.Fate.Center.Z - position.Z;
            var distanceFromFateCenter = MathF.Sqrt(dx * dx + dz * dz);
            var allowedRadius = selection.Fate.Radius + 20f;
            if (distanceFromFateCenter > allowedRadius)
            {
                continue;
            }

            var hpRatio = npc.MaxHp > 0 ? (double)npc.CurrentHp / npc.MaxHp : 1.0;
            var score = ComputeScore(distanceFromPlayer, hpRatio);
            var forlornPriority = FateTargetPolicy.GetForlornPriority(npc.NameId);

            var snapshot = new FateTargetSnapshot(
                npc.GameObjectId,
                npc.BaseId,
                npc.NameId,
                forlornPriority,
                DetermineName(npc),
                position,
                distanceFromPlayer,
                npc.CurrentHp,
                npc.MaxHp,
                score,
                npc.IsDead,
                now,
                npc.Address);

            candidates.Add(snapshot);
        }

        if (candidates.Count == 0)
        {
            ClearInternal("No candidates", true);
            return null;
        }

        var next = candidates
            .OrderByDescending(c => c.ForlornPriority)
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.DistanceFromPlayer)
            .First();
        if (currentTarget is null || currentTarget.Value.ObjectId != next.ObjectId)
        {
            var previous = currentTarget;
            currentTarget = next;
            RaiseTargetChanged(previous, next, "Target swapped");
        }
        else
        {
            currentTarget = next;
        }

        EmitStatus(next, selection);
        return currentTarget;
    }

    public void Clear(string reason)
    {
        ClearInternal(reason, true);
    }

    private void ClearInternal(string reason, bool emitEvent)
    {
        if (currentTarget == null)
        {
            return;
        }

        var previous = currentTarget;
        currentTarget = null;
        if (emitEvent)
        {
            RaiseTargetChanged(previous, null, reason);
        }
    }

    private void RaiseTargetChanged(FateTargetSnapshot? previous, FateTargetSnapshot? current, string reason)
    {
        TargetChanged?.Invoke(this, new FateTargetChangedEventArgs(previous, current, reason));
    }

    private static double ComputeScore(float distance, double hpRatio)
    {
        var score = 1000.0;
        score -= distance;
        score -= hpRatio * 200.0;

        return score;
    }

    private void EmitStatus(FateTargetSnapshot snapshot, FateSelection selection)
    {
        if (DateTimeOffset.UtcNow < nextLogAt)
        {
            return;
        }

        log.Debug(
            "FateTargetTracker: fate #{FateId} target #{ObjectId} {Name} nameId={NameId} forlornPriority={ForlornPriority} dist={Distance:F1}y hp={Hp}/{MaxHp}",
            selection.Fate.FateId,
            snapshot.ObjectId,
            snapshot.Name,
            snapshot.NameId,
            snapshot.ForlornPriority,
            snapshot.DistanceFromPlayer,
            snapshot.CurrentHp,
            snapshot.MaxHp);
        nextLogAt = DateTimeOffset.UtcNow.AddSeconds(5);
    }

    private static string DetermineName(IBattleNpc npc)
    {
        var name = npc.Name?.TextValue;
        return string.IsNullOrWhiteSpace(name) ? "Unknown Target" : name;
    }

    private void EmitExclusion(FateTargetSnapshot previous, IBattleNpc npc, ActiveFate fate, string reason)
    {
        if (DateTimeOffset.UtcNow < nextExclusionLogAt)
            return;

        log.Debug("FateTargetTracker: rejected current target #{ObjectId} {Name} for fate #{FateId}; reason={Reason} baseId={BaseId} kind={Kind} nameplate={Nameplate}",
            previous.ObjectId,
            previous.Name,
            fate.FateId,
            reason,
            npc.BaseId,
            npc.BattleNpcKind,
            FateTargetPolicy.GetNameplateIcon(npc));
        nextExclusionLogAt = DateTimeOffset.UtcNow.AddSeconds(5);
    }
}
