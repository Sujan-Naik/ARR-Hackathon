// Role: approximate head yaw, pitch and roll from the five SCRFD face keypoints. Geometry only, no model.
// Expect roughly +/-15 degrees of error. The signs and gains below are starting values to calibrate on your camera.
using UnityEngine;

namespace AR.Vision
{
    public readonly struct HeadAngles
    {
        public readonly float Yaw;     // degrees
        public readonly float Pitch;   // degrees
        public readonly float Roll;    // degrees

        public HeadAngles(float yaw, float pitch, float roll)
        {
            Yaw = yaw;
            Pitch = pitch;
            Roll = roll;
        }
    }

    public static class HeadPose
    {
        // Keypoint order from ScrfdDetector: 0 left eye, 1 right eye, 2 nose, 3 left mouth, 4 right mouth.
        private const int LeftEye = 0;
        private const int RightEye = 1;
        private const int Nose = 2;
        private const int LeftMouth = 3;
        private const int RightMouth = 4;

        // Nose position between the eye line and the mouth line when the face points at the camera.
        // ASSUMPTION: about 0.45 for a typical face. Calibrate by looking straight ahead and logging the value.
        public const float NeutralNoseRatio = 0.45f;

        // ASSUMPTION: starting gains. Tune by turning your head to known angles and comparing the logs.
        public const float YawGain = 2f;
        public const float PitchDegreesPerUnit = 90f;

        // frameAspect is the frame's width divided by its height. It is needed because keypoints are normalized
        // separately on each axis, so distances would be distorted without it.
        public static bool TryEstimate(Observation o, float frameAspect, out HeadAngles angles)
        {
            angles = default;
            if (o.Kind != ObservationKind.Face) return false;

            Vector2[] k = o.Keypoints;
            if (k == null || k.Length < 5) return false;

            Vector2 le = ToAspect(k[LeftEye], frameAspect);
            Vector2 re = ToAspect(k[RightEye], frameAspect);
            Vector2 nose = ToAspect(k[Nose], frameAspect);
            Vector2 ml = ToAspect(k[LeftMouth], frameAspect);
            Vector2 mr = ToAspect(k[RightMouth], frameAspect);

            Vector2 eyeMid = (le + re) * 0.5f;
            Vector2 mouthMid = (ml + mr) * 0.5f;

            float eyeDistance = Vector2.Distance(le, re);
            float faceHeight = mouthMid.y - eyeMid.y;
            if (eyeDistance < 1e-4f || Mathf.Abs(faceHeight) < 1e-4f) return false;

            // Yaw: horizontal offset of the nose from the eye midpoint, scaled by eye distance.
            // Positive means the nose has moved toward the image's right. Mirror handling depends on your frame.
            float yawRatio = (nose.x - eyeMid.x) / eyeDistance;
            float yaw = Mathf.Rad2Deg * Mathf.Asin(Mathf.Clamp(yawRatio * YawGain, -1f, 1f));

            // Pitch: where the nose sits between the eye line and the mouth line.
            // Positive means the nose has moved down relative to neutral. Verify the sign against a known head-down pose.
            float noseRatio = (nose.y - eyeMid.y) / faceHeight;
            float pitch = (noseRatio - NeutralNoseRatio) * PitchDegreesPerUnit;

            // Roll: tilt of the eye line. Positive is clockwise in the upright image.
            Vector2 eyeLine = re - le;
            float roll = Mathf.Rad2Deg * Mathf.Atan2(eyeLine.y, eyeLine.x);

            angles = new HeadAngles(yaw, pitch, roll);
            return true;
        }

        private static Vector2 ToAspect(Vector2 normalized, float frameAspect)
        {
            return new Vector2(normalized.x * frameAspect, normalized.y);
        }
    }
}