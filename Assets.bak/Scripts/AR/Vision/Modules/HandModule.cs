// Role: hand module. Palm detection on the full frame, then 21-joint landmarks on a square crop around each palm.
// Output detections are already in normalized frame space (InputSize 1, full Region).
using System.Collections.Generic;
using UnityEngine;

namespace AR.Vision
{
    public sealed class HandModule : VisionModule
    {
        // ASSUMPTION: MediaPipe-style hand crop scale around the palm box.
        private const float CropScale = 2.6f;

        private readonly ModelSettings.HandSettings settings;
        private readonly PalmDetector palm;
        private readonly HandLandmarker landmarker;

        public override ObservationKind Kind => ObservationKind.Hand;
        public override bool IsReady => palm != null && landmarker != null;

        public HandModule(ModelSettings.HandSettings settings)
        {
            this.settings = settings;

            if (settings.palmDetector != null) palm = new PalmDetector(settings.palmDetector);
            if (settings.handLandmarker != null) landmarker = new HandLandmarker(settings.handLandmarker);

            if (palm == null || landmarker == null)
                Debug.LogError("HandModule: palmDetector and handLandmarker assets must both be assigned.");
        }

        public override List<Detection> Detect(VisionFrame frame)
        {
            var result = new List<Detection>();
            if (!IsReady) return result;

            List<Detection> palms = palm.Detect(frame, settings.palmScoreThreshold, settings.palmNmsIouThreshold);

            for (int i = 0; i < palms.Count; i++)
            {
                Detection p = palms[i];

                // Palm box comes back in 256-pixel space. Map it to frame-normalized space first.
                Rect palmFrame = Geometry.MapToFrame(p.Box, p.InputSize, p.Region);
                Rect crop = Geometry.SquareAround(palmFrame, CropScale);

                HandLandmarker.Pose pose = landmarker.Run(frame, crop);
                if (pose.Presence < settings.handPresenceThreshold) continue;

                // Joints are in 224-pixel crop space. Map them to frame-normalized space through the crop.
                Vector2[] joints = new Vector2[pose.Joints.Length];
                for (int j = 0; j < joints.Length; j++)
                    joints[j] = Geometry.MapToFrame(pose.Joints[j], HandLandmarker.InputSize, crop);

                bool left = pose.Handedness >= 0.5f;

                result.Add(new Detection
                {
                    Box = BoundsOf(joints),
                    Keypoints = joints,
                    Score = p.Score,
                    ClassIndex = left ? 0 : 1,
                    Label = left ? "Left" : "Right",
                    Identified = true,
                    InputSize = 1f,
                    Region = Geometry.FullFrame
                });
            }

            return result;
        }

        private static Rect BoundsOf(Vector2[] points)
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < points.Length; i++)
            {
                minX = Mathf.Min(minX, points[i].x);
                minY = Mathf.Min(minY, points[i].y);
                maxX = Mathf.Max(maxX, points[i].x);
                maxY = Mathf.Max(maxY, points[i].y);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        public override void Dispose()
        {
            palm?.Dispose();
            landmarker?.Dispose();
        }
    }
}
