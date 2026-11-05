// Role: per-track state held by ObservationTracker. Bookkeeping only. Game code receives Observation.
// Profile is stored as a UnityEngine.Object here (this assembly cannot name TrackableProfile);
// VisionPipeline converts it when building the public Observation.
using UnityEngine;

namespace AR.Vision
{
    public sealed class TrackedObservation
    {
        public int TrackId { get; }
        public ObservationKind Kind { get; }
        public Rect NormalizedRect { get; private set; }
        public Vector2[] Keypoints { get; private set; }
        public float Confidence { get; private set; }
        public Detection LastDetection { get; private set; }
        public string Label { get; private set; }
        public bool Identified { get; private set; }
        public float MatchScore { get; private set; }
        public UnityEngine.Object ProfileObject { get; private set; }
        public int FramesTracked { get; private set; }
        public int MissedFrames { get; private set; }
        public bool MatchedThisFrame { get; internal set; }
        public bool IdentityChangedThisFrame { get; private set; }

        private string prevLabel;
        private bool prevIdentified;
        private UnityEngine.Object prevProfile;

        public TrackedObservation(int trackId, ObservationKind kind, Detection detection, Rect normalizedRect)
        {
            TrackId = trackId;
            Kind = kind;
            LastDetection = detection;
            NormalizedRect = normalizedRect;
            Label = detection.Label ?? kind.ToString().ToLowerInvariant();
            Identified = detection.Identified;
            Confidence = detection.Score;
            Keypoints = MapKeypoints(detection);
            FramesTracked = 1;
            MatchedThisFrame = true;

            ProfileObject = null;
            prevLabel = Label;
            prevIdentified = Identified;
            prevProfile = null;
        }

        internal void ApplyDetection(Detection detection, Rect normalizedRect)
        {
            LastDetection = detection;
            NormalizedRect = normalizedRect;
            Keypoints = MapKeypoints(detection);
            Confidence = detection.Score;
            FramesTracked++;
            MissedFrames = 0;
            MatchedThisFrame = true;

            // Faces leave Label null here, so a recognized name survives between recognition passes.
            if (detection.Label != null)
            {
                Label = detection.Label;
                Identified = detection.Identified;
            }
        }

        internal void ResetFrameFlags()
        {
            MatchedThisFrame = false;
        }

        internal void RegisterMiss()
        {
            MissedFrames++;
        }

        // Called by the tracker with the resolved profile. Records whether anything the public view shows has changed.
        internal void CommitIdentity(UnityEngine.Object profile)
        {
            ProfileObject = profile;
            IdentityChangedThisFrame = profile != prevProfile || Label != prevLabel || Identified != prevIdentified;

            prevProfile = profile;
            prevLabel = Label;
            prevIdentified = Identified;
        }

        // Used by recognition modules to write their result onto the track.
        public void SetRecognition(string label, float matchScore, bool identified)
        {
            Label = label;
            MatchScore = matchScore;
            Identified = identified;
        }

        private static Vector2[] MapKeypoints(Detection detection)
        {
            if (detection.Keypoints == null) return System.Array.Empty<Vector2>();

            var result = new Vector2[detection.Keypoints.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = Geometry.MapToFrame(detection.Keypoints[i], detection.InputSize, detection.Region);
            return result;
        }
    }
}
