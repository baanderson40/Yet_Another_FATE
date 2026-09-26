using System;
using System.Numerics;
using Dalamud.Game.ClientState.Fates;

namespace YetAnotherFATE.Data.Fates;

public readonly record struct ActiveFate(
    uint FateId,
    string Name,
    byte Level,
    uint TerritoryId,
    Vector3 Center,
    float Radius,
    float DistanceFromPlayer,
    bool IsContinuation,
    TimeSpan TimeRemaining,
    FateState State,
    ushort FateChain,
    string Description,
    string Objective,
    byte Progress,
    byte HandInCount,
    bool HasBonus,
    byte MaxLevel,
    uint IconId,
    uint MapIconId,
    int StartTimeEpoch,
    short Duration,
    uint MotivationNpc,
    uint ObjectiveNpc,
    byte Rule,
    ushort FateRuleEx,
    uint EventItem,
    uint RequiredEventItem,
    uint TurnInEventItem)
{
    // Sheet metadata, not inferred from runtime rules, items, or FATEChain.
    public bool HasFollowUp { get; init; }
    public uint LocationId { get; init; }
    public bool HasSheetMetadata { get; init; }
    public bool IsOnMap { get; init; }
    public bool IsPending => !IsOnMap;

    public bool RequiresActivation => State == FateState.Preparing;

    public FateRule FateRule => (FateRule)Rule;

    public bool IsCollect => FateRule == FateRule.Collect;

    public bool HasMotivationNpc => MotivationNpc is not 0 and not 0xE0000000;

    public bool HasObjectiveNpc => ObjectiveNpc is not 0 and not 0xE0000000;
}
