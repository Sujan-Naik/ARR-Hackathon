using UnityEngine;

namespace Stacking
{
    /// <summary>
    /// The child's head angles, in absolute degrees. When the barber is guiding,
    /// this directly drives the tower lean. When the barber is not guiding, this
    /// is compared against the tower's held lean to decide "matched / mismatched".
    /// </summary>
    public readonly struct HeadTiltSignal
    {
        public readonly float RollDegrees;
        public readonly float YawDegrees;
        public readonly Vector2 Center;
        public readonly bool HasFace;
        public readonly int TrackId;

        public HeadTiltSignal(float rollDegrees, float yawDegrees, Vector2 center, bool hasFace, int trackId)
        {
            RollDegrees = rollDegrees;
            YawDegrees = yawDegrees;
            Center = center;
            HasFace = hasFace;
            TrackId = trackId;
        }

        public static readonly HeadTiltSignal None =
            new HeadTiltSignal(0f, 0f, Vector2.zero, false, -1);
    }

    /// <summary>One monster hand in normalized screen space.</summary>
    public readonly struct MonsterHand
    {
        public readonly int TrackId;
        public readonly Vector2 Center;
        public readonly bool IsOpen;
        public readonly bool IsClosed;
        public readonly bool IsPinching;
        public readonly bool IsBound;

        public MonsterHand(int trackId, Vector2 center, bool isOpen, bool isClosed, bool isPinching)
        {
            TrackId = trackId;
            Center = center;
            IsOpen = isOpen;
            IsClosed = isClosed;
            IsPinching = isPinching;
            IsBound = trackId > 0;
        }

        public static readonly MonsterHand Unbound =
            new MonsterHand(-1, Vector2.zero, false, false, false);
    }

    /// <summary>The barber's two hands. Either one may be unbound.</summary>
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
            new MonsterHandsSignal(MonsterHand.Unbound, MonsterHand.Unbound, false);
    }

    /// <summary>
    /// Parent input. A raise is an edge-triggered toggle of the block's sweep.
    /// </summary>
    public readonly struct CraneSignal
    {
        public enum Hand { None, Left, Right }

        public readonly bool RaisedThisFrame;
        public readonly bool HasHand;
        public readonly Hand RaisingHand;

        public CraneSignal(bool raisedThisFrame, bool hasHand, Hand raisingHand = Hand.None)
        {
            RaisedThisFrame = raisedThisFrame;
            HasHand = hasHand;
            RaisingHand = raisingHand;
        }

        public static readonly CraneSignal None = new CraneSignal(false, false);
    }

    /// <summary>How well the child's head matches the tower's held lean.</summary>
    public enum TiltMatch
    {
        /// <summary>Cannot evaluate: barber guiding, or child/monster not tracked.</summary>
        Unknown,
        /// <summary>Child is mirroring the tower's lean within tolerance.</summary>
        Matched,
        /// <summary>Child is not mirroring the tower's lean.</summary>
        Mismatched,
    }
}
