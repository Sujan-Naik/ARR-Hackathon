// Role: face module. SCRFD detection, ArcFace embedding, gallery matching. Recognition runs every N frames.
using System.Collections.Generic;
using UnityEngine;

namespace AR.Vision
{
    public sealed class FaceModule : VisionModule
    {
        public const string UnknownLabel = "unknown";

        private readonly ModelSettings.FaceSettings settings;
        private readonly ScrfdDetector detector;
        private readonly ArcFaceEmbedder embedder;
        private readonly Dictionary<string, float[]> gallery = new Dictionary<string, float[]>();

        public override ObservationKind Kind => ObservationKind.Face;
        public override bool IsReady => detector != null && embedder != null;

        public FaceModule(ModelSettings.FaceSettings settings)
        {
            this.settings = settings;

            if (settings.detector != null) detector = new ScrfdDetector(settings.detector);
            if (settings.embedder != null) embedder = new ArcFaceEmbedder(settings.embedder);

            if (detector == null || embedder == null)
                Debug.LogError("FaceModule: detector and embedder assets must both be assigned.");
        }

        public override List<Detection> Detect(VisionFrame frame)
        {
            if (!IsReady) return new List<Detection>();
            return detector.Detect(frame, settings.detectionThreshold, settings.nmsIouThreshold);
        }

        public override bool RecognitionDue(int frameIndex)
        {
            int every = Mathf.Max(1, settings.recognizeEveryNFrames);
            return frameIndex % every == 0;
        }

        public override void Recognize(VisionFrame frame, IReadOnlyList<TrackedObservation> tracks)
        {
            if (!IsReady) return;

            for (int i = 0; i < tracks.Count; i++)
            {
                TrackedObservation track = tracks[i];
                Detection face = track.LastDetection;
                if (face.Keypoints == null || face.Keypoints.Length < 2) continue;
                if (Vector2.Distance(face.Keypoints[0], face.Keypoints[1]) < 4f) continue;

                float[] emb = embedder.Embed(frame, face);
                string name = Match(emb, out float score);
                track.SetRecognition(name, score, name != UnknownLabel);
            }
        }

        // Enrolls the given face track under personName. Returns false if the module cannot embed it.
        public bool Enroll(string personName, VisionFrame frame, TrackedObservation face)
        {
            if (!IsReady || face == null || string.IsNullOrEmpty(personName)) return false;

            gallery[personName] = embedder.Embed(frame, face.LastDetection);
            return true;
        }

        public void ClearGallery()
        {
            gallery.Clear();
        }

        private string Match(float[] emb, out float bestScore)
        {
            bestScore = 0f;
            if (gallery.Count == 0) return UnknownLabel;

            float top = float.NegativeInfinity;
            string topName = UnknownLabel;
            foreach (var entry in gallery)
            {
                float sim = Geometry.Dot(emb, entry.Value);
                if (sim > top)
                {
                    top = sim;
                    topName = entry.Key;
                }
            }

            bestScore = top;
            return top > settings.matchThreshold ? topName : UnknownLabel;
        }

        public override void Dispose()
        {
            detector?.Dispose();
            embedder?.Dispose();
        }
    }
}
