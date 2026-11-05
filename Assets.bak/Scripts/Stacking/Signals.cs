using UnityEngine;

namespace Stacking
{
    /// <summary>
    /// What the child is telling us. Roll drives tower lean; yaw is optional
    /// camera-facing torque. hasFace is false when the child is not tracked,
    /// in which case the building should settle upright.
    /// </summary>
    public readonly struct HeadTiltSignal
    {
        public readonly float RollDegrees;
        public readonly float YawDegrees;
        public readonly bool HasFace;
        public readonly int TrackId;

        public HeadTiltSignal(float rollDegrees, float yawDegrees, bool hasFace, int trackId)
        {
            RollDegrees = rollDegrees;
            YawDegrees = yawDegrees;
            HasFace = hasFace;
            TrackId = trackId;
        }

        public static readonly HeadTiltSignal None = new HeadTiltSignal(0f, 0f, false, -1);
    }

    /// <summary>One monster hand in normalized screen space.</summary>
    public readonly struct MonsterHand
    {
        public readonly int TrackId;
        public readonly Vector2 Center;
        public readonly bool IsOpen;
        public readonly bool IsClosed;
        public readonly bool IsPinching;

        public MonsterHand(int trackId, Vector2 center, bool isOpen, bool isClosed, bool isPinching)
        {
            TrackId = trackId;
            Center = center;
            IsOpen = isOpen;
            IsClosed = isClosed;
            IsPinching = isPinching;
        }
    }

    /// <summary>
    /// The monster's two hands. Grab is true while any hand is closed.
    /// Each hand may be unbound (TrackId -1, Center zero).
    /// </summary>
    public readonly struct MonsterHandsSignal
    {
        public readonly MonsterHand Left;
        public readonly MonsterHand Right;
        public readonly bool HasAny;

        public MonsterHandsSignal(MonsterHand left, MonsterHand right, bool hasAny)
        {
            Left = left;
            Right = right;
            HasAny = hasAny;
        }

        public static readonly MonsterHandsSignal None =
            new MonsterHandsSignal(default, default, false);
    }

    /// <summary>
    /// What the parent is telling us. SteeredX is the crane's horizontal aim
    /// in 0..1. Raised is edge-triggered: true on the frame a raise was seen.
    /// Released is true on the frame the raise gesture ended.
    /// </summary>
    public readonly struct CraneSignal
    {
        public readonly float SteeredX;
        public readonly bool RaisedThisFrame;
        public readonly bool HasHand;

        public CraneSignal(float steeredX, bool raisedThisFrame, bool hasHand)
        {
            SteeredX = steeredX;
            RaisedThisFrame = raisedThisFrame;
            HasHand = hasHand;
        }

        public static readonly CraneSignal None = new CraneSignal(0.5f, false, false);
    }
}