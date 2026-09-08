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
    public bool RequiresActivation => State == FateState.Preparing;
}
