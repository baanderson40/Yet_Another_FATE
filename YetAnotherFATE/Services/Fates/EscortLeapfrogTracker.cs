using System;
using System.Numerics;
using YetAnotherFATE.Data.Fates;

namespace YetAnotherFATE.Services.Fates;

public sealed class EscortLeapfrogTracker
{
    private const float LeadDistance = 10f;
    private const float BehindThreshold = 6f;
    private const float ArrivalDistance = 2.5f;
    private const float MaximumSpeed = 8f;
    private const float MaximumSampleGapSeconds = 5f;
    private const float MinimumSampleMovement = 0.35f;
    private static readonly TimeSpan MotionSampleWindow = TimeSpan.FromSeconds(0.75);
    private static readonly TimeSpan StationaryTimeout = TimeSpan.FromSeconds(2.5);
    private ActiveFate occurrence;
    private ulong anchorId;
    private Vector3 previousPosition;
    private DateTimeOffset previousAt;
    private Vector3 sampleOrigin;
    private DateTimeOffset sampleStartedAt;
    private DateTimeOffset lastMotionAt;
    private Vector2 heading;
    private Vector3? destination;

    public Vector3? Destination => destination;
    public Vector2 Heading => heading;
    public float LastSampleDisplacement { get; private set; }
    public float PlayerHeadingOffset { get; private set; }
    public string Status { get; private set; } = "observing anchor motion";

    public void Reset()
    {
        occurrence = default;
        anchorId = 0;
        previousPosition = default;
        previousAt = default;
        sampleOrigin = default;
        sampleStartedAt = default;
        lastMotionAt = default;
        heading = default;
        destination = null;
        LastSampleDisplacement = 0;
        PlayerHeadingOffset = 0;
        Status = "observing anchor motion";
    }

    public void SuspendLeg() => destination = null;

    public Vector3? Tick(ActiveFate fate, ulong currentAnchorId, Vector3 anchorPosition,
        Vector3 playerPosition, DateTimeOffset now)
    {
        if (!IsFinite(anchorPosition) || !IsFinite(playerPosition) || fate.Radius <= 0)
        {
            Reset();
            return null;
        }

        if (!SameOccurrence(occurrence, fate) || anchorId != currentAnchorId)
        {
            Reset();
            occurrence = fate;
            anchorId = currentAnchorId;
        }

        if (previousAt == default)
        {
            previousPosition = sampleOrigin = anchorPosition;
            previousAt = sampleStartedAt = now;
            Status = "observing anchor motion (first sample)";
            return null;
        }

        var elapsed = (now - previousAt).TotalSeconds;
        var stepDistance = FlatDistance(previousPosition, anchorPosition);
        var sampleElapsed = now - sampleStartedAt;
        var sampleDistance = FlatDistance(sampleOrigin, anchorPosition);
        LastSampleDisplacement = sampleDistance;
        if (elapsed <= 0 || elapsed > MaximumSampleGapSeconds || stepDistance > MaximumSpeed * elapsed + 0.25f ||
            sampleElapsed > TimeSpan.FromSeconds(MaximumSampleGapSeconds))
        {
            heading = default;
            destination = null;
            lastMotionAt = default;
            sampleOrigin = anchorPosition;
            sampleStartedAt = now;
            Status = "reacquiring after a stale or implausible anchor sample";
        }
        else if (sampleElapsed >= MotionSampleWindow)
        {
            if (sampleDistance >= MinimumSampleMovement && sampleDistance / sampleElapsed.TotalSeconds <= MaximumSpeed)
            {
                var observed = new Vector2((anchorPosition.X - sampleOrigin.X) / sampleDistance,
                    (anchorPosition.Z - sampleOrigin.Z) / sampleDistance);
                var turn = heading.LengthSquared() >= 0.01f && Vector2.Dot(heading, observed) < 0.4f;
                heading = heading.LengthSquared() < 0.01f || turn
                    ? observed
                    : Vector2.Normalize(Vector2.Lerp(heading, observed, 0.4f));
                lastMotionAt = now;
                if (turn) destination = null;
            }
            sampleOrigin = anchorPosition;
            sampleStartedAt = now;
        }

        previousPosition = anchorPosition;
        previousAt = now;
        if (lastMotionAt != default && now - lastMotionAt > StationaryTimeout)
        {
            heading = default;
            destination = null;
        }
        if (heading.LengthSquared() < 0.5f)
        {
            destination = null;
            Status = lastMotionAt == default
                ? $"observing anchor motion ({LastSampleDisplacement:F1}y / {sampleElapsed.TotalSeconds:F1}s sample)"
                : "waiting: anchor has been stationary";
            return null;
        }

        var playerOffset = new Vector2(playerPosition.X - anchorPosition.X, playerPosition.Z - anchorPosition.Z);
        PlayerHeadingOffset = Vector2.Dot(playerOffset, heading);
        if (destination is { } committed)
        {
            if (FlatDistance(playerPosition, committed) <= ArrivalDistance)
            {
                destination = null;
                Status = "waiting: reached forward point; letting escort pass";
                return null;
            }

            Status = "moving ahead: committed leapfrog leg";
            return committed;
        }

        if (PlayerHeadingOffset >= -BehindThreshold)
        {
            Status = $"waiting: player not behind threshold ({PlayerHeadingOffset:F1}y)";
            return null;
        }

        var lead = MathF.Min(LeadDistance, MathF.Max(0f, fate.Radius - 5f));
        if (lead < 3f)
        {
            Status = "waiting: FATE too small for safe forward point";
            return null;
        }

        var target = new Vector3(anchorPosition.X + heading.X * lead, anchorPosition.Y,
            anchorPosition.Z + heading.Y * lead);
        var fromCenter = new Vector2(target.X - fate.Center.X, target.Z - fate.Center.Z);
        var safeRadius = MathF.Max(0f, fate.Radius - 3f);
        if (fromCenter.Length() > safeRadius)
        {
            if (safeRadius < 3f)
            {
                Status = "waiting: no safe interior destination";
                return null;
            }
            fromCenter = Vector2.Normalize(fromCenter) * safeRadius;
            target.X = fate.Center.X + fromCenter.X;
            target.Z = fate.Center.Z + fromCenter.Y;
        }

        destination = target;
        Status = $"moving ahead: starting {lead:F0}y leapfrog leg";
        return destination;
    }

    private static bool SameOccurrence(ActiveFate left, ActiveFate right)
        => left.FateId == right.FateId && left.TerritoryId == right.TerritoryId &&
           (left.StartTimeEpoch == 0 || right.StartTimeEpoch == 0 || left.StartTimeEpoch == right.StartTimeEpoch);

    private static float FlatDistance(Vector3 left, Vector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
