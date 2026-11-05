// Role: the game-facing, read-only view of one tracked thing (face, hand or object).
// All positions are normalized 0..1 with origin top-left, keypoints included.
using UnityEngine;

namespace AR.Vision
{
    public readonly struct Observation
    {
        public int TrackId { get; }
        public ObservationKind Kind { get; }
        public string Label { get; }
        public TrackableProfile Profile { get; }
        public float Confidence { get; }
        public float MatchScore { get; }
        public Rect NormalizedRect { get; }
        public Vector2[] Keypoints { get; }
        public int FramesTracked { get; }
        public bool IsIdentified { get; }
        public Vector2 Center => NormalizedRect.center;

        public Observation(
            int trackId,
            ObservationKind kind,
            string label,
            TrackableProfile profile,
            float confidence,
            float matchScore,
            Rect normalizedRect,
            Vector2[] keypoints,
            int framesTracked,
            bool identified)
        {
            TrackId = trackId;
            Kind = kind;
            Label = label;
            Profile = profile;
            Confidence = confidence;
            MatchScore = matchScore;
            NormalizedRect = normalizedRect;
            Keypoints = keypoints;
            FramesTracked = framesTracked;
            IsIdentified = identified;
        }
    }
}
