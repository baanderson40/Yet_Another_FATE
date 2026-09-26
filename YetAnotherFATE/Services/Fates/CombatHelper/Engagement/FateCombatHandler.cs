using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Services.Fates.CombatHelper.Targeter;

namespace YetAnotherFATE.Services.Fates.CombatHelper.Engagement;

public sealed class FateCombatHandler : IFateCombatHandler
{
    private readonly IFateTargetTracker targetTracker;
    private readonly IPluginLog log;
    private ulong? lastTargetObjectId;
    private nint? lastTargetAddress;
    private DateTimeOffset nextFailureLogAt = DateTimeOffset.MinValue;

    public FateCombatHandler(
        IFateTargetTracker targetTracker,
        IPluginLog log)
    {
        this.targetTracker = targetTracker;
        this.log = log;
    }

    public FateTargetSnapshot? Execute(FateSelection selection)
    {
        var snapshot = targetTracker.Refresh(selection);
        if (snapshot is { } value)
        {
            EnsureHardTarget(value);
        }
        else
        {
            ClearOwnedHardTarget();
            lastTargetObjectId = null;
            lastTargetAddress = null;
        }

        return snapshot;
    }

    public void Reset(string reason)
    {
        ClearOwnedHardTarget();
        targetTracker.Clear(reason);
        lastTargetObjectId = null;
        lastTargetAddress = null;
    }

    private unsafe void ClearOwnedHardTarget()
    {
        if (lastTargetAddress is not { } ownedAddress)
            return;
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return;
        var current = targetSystem->GetHardTarget();
        if (current != null && ownedAddress == (nint)current)
            targetSystem->SetHardTarget(null, true);
    }

    private unsafe void EnsureHardTarget(FateTargetSnapshot snapshot)
    {
        if (snapshot.GameObjectAddress == nint.Zero)
        {
            return;
        }

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
        {
            return;
        }

        var desired = (GameObject*)snapshot.GameObjectAddress;
        if (desired == null)
        {
            return;
        }

        var current = targetSystem->GetHardTarget();
        if (current == desired && lastTargetObjectId == snapshot.ObjectId)
        {
            return;
        }

        if (targetSystem->SetHardTarget(desired, true))
        {
            lastTargetObjectId = snapshot.ObjectId;
            lastTargetAddress = snapshot.GameObjectAddress;
            log.Debug("FateCombatHandler: targeting #{ObjectId} {Name}", snapshot.ObjectId, snapshot.Name);
            return;
        }

        if (DateTimeOffset.UtcNow >= nextFailureLogAt)
        {
            log.Debug(
                "FateCombatHandler: failed to set target #{ObjectId} {Name}",
                snapshot.ObjectId,
                snapshot.Name);
            nextFailureLogAt = DateTimeOffset.UtcNow.AddSeconds(3);
        }
    }
}
