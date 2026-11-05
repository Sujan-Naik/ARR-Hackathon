// Role: static helpers that read gestures from hand Observations. Uses the 21-joint MediaPipe ordering.
using UnityEngine;

namespace AR.Vision
{
    public static class HandGestures
    {
        public const int Wrist = 0;
        public const int ThumbTip = 4;
        public const int IndexTip = 8;
        public const int MiddleMcp = 9;
        public const int JointCount = 21;

        // Pinch distance as a fraction of the wrist to middle-knuckle length
        public const float PinchRatio = 0.25f;

        // A finger counts as extended when its tip is this many times farther from the wrist than its PIP joint.
        // ASSUMPTION: 1.1 is a starting value. Tune it by holding a fist and an open hand and logging Openness.
        public const float ExtendedRatio = 1.1f;

        // Open means at least this fraction of the four fingers is extended. Closed means at most the opposite.
        public const float OpenThreshold = 0.75f;
        public const float ClosedThreshold = 0.25f;

        // PIP and tip joints for index, middle, ring, and pinky. Thumb is excluded from openness.
        private static readonly int[] FingerPips = { 6, 10, 14, 18 };
        private static readonly int[] FingerTips = { 8, 12, 16, 20 };

        public static bool IsPinching(Observation observation)
        {
            if (observation.Kind != ObservationKind.Hand) return false;
            Vector2[] k = observation.Keypoints;
            if (k == null || k.Length < JointCount) return false;

            float handScale = Vector2.Distance(k[Wrist], k[MiddleMcp]);
            if (handScale <= 0f) return false;

            return Vector2.Distance(k[ThumbTip], k[IndexTip]) < handScale * PinchRatio;
        }

        // Fraction of the four non-thumb fingers that are extended, from 0 (fist) to 1 (fully open).
        // frameAspect is the frame's width divided by its height, used to undo the per-axis normalization.
        public static float Openness(Observation observation, float frameAspect = 1f)
        {
            if (observation.Kind != ObservationKind.Hand) return 0f;
            Vector2[] k = observation.Keypoints;
            if (k == null || k.Length < JointCount) return 0f;

            Vector2 wrist = ToAspect(k[Wrist], frameAspect);
            int extended = 0;

            for (int f = 0; f < FingerTips.Length; f++)
            {
                float tipDistance = Vector2.Distance(ToAspect(k[FingerTips[f]], frameAspect), wrist);
                float pipDistance = Vector2.Distance(ToAspect(k[FingerPips[f]], frameAspect), wrist);
                if (tipDistance > pipDistance * ExtendedRatio) extended++;
            }

            return extended / (float)FingerTips.Length;
        }

        public static bool IsOpen(Observation observation, float frameAspect = 1f)
        {
            return Openness(observation, frameAspect) >= OpenThreshold;
        }

        public static bool IsClosed(Observation observation, float frameAspect = 1f)
        {
            return Openness(observation, frameAspect) <= ClosedThreshold;
        }

        private static Vector2 ToAspect(Vector2 normalized, float frameAspect)
        {
            return new Vector2(normalized.x * frameAspect, normalized.y);
        }
    }
}