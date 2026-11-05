// Role: static helpers that read gestures from hand Observations. Uses MediaPipe 21-joint ordering.
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

        public static bool IsPinching(Observation observation)
        {
            if (observation.Kind != ObservationKind.Hand) return false;
            Vector2[] k = observation.Keypoints;
            if (k == null || k.Length < JointCount) return false;

            float handScale = Vector2.Distance(k[Wrist], k[MiddleMcp]);
            if (handScale <= 0f) return false;

            return Vector2.Distance(k[ThumbTip], k[IndexTip]) < handScale * PinchRatio;
        }
    }
}
