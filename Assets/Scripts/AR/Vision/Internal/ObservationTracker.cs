// Role: generic IoU tracker for one ObservationKind. Matching, lifecycle, recognition hook and profile mapping
// all happen inside Update. This is the only place profiles are assigned. ToPublic() only copies them.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AR.Vision
{
    // Supplied by the facade. Called once per active track per frame, after matching and recognition.
    public delegate UnityEngine.Object ProfileResolver(ObservationKind kind, string label, bool identified);

    public sealed class ObservationTracker
    {
        private static int nextTrackId = 1;

        private readonly ObservationKind kind;
        private readonly float iouThreshold;
        private readonly int maxMissedFrames;

        private readonly List<TrackedObservation> active = new List<TrackedObservation>();
        private readonly List<TrackedObservation> appeared = new List<TrackedObservation>();
        private readonly List<TrackedObservation> lost = new List<TrackedObservation>();
        private readonly List<TrackedObservation> matched = new List<TrackedObservation>();

        public ObservationKind Kind => kind;
        public IReadOnlyList<TrackedObservation> Active => active;
        public IReadOnlyList<TrackedObservation> Appeared => appeared;
        public IReadOnlyList<TrackedObservation> Lost => lost;

        public ObservationTracker(ObservationKind kind, float iouThreshold, int maxMissedFrames)
        {
            this.kind = kind;
            this.iouThreshold = iouThreshold;
            this.maxMissedFrames = maxMissedFrames;
        }

        public void Update(
            List<Detection> detections,
            ProfileResolver resolveProfile,
            bool recognitionDue,
            Action<IReadOnlyList<TrackedObservation>> recognize)
        {
            appeared.Clear();
            lost.Clear();
            matched.Clear();

            for (int i = 0; i < active.Count; i++) active[i].ResetFrameFlags();

            // STEP 1: match detections to existing tracks by IoU. Detections arrive sorted by score.
            for (int d = 0; d < detections.Count; d++)
            {
                Detection det = detections[d];
                Rect rect = Geometry.MapToFrame(det.Box, det.InputSize, det.Region);

                TrackedObservation best = null;
                float bestIou = iouThreshold;
                for (int t = 0; t < active.Count; t++)
                {
                    TrackedObservation candidate = active[t];
                    if (candidate.MatchedThisFrame) continue;

                    float iou = Geometry.IoU(rect, candidate.NormalizedRect);
                    if (iou > bestIou)
                    {
                        bestIou = iou;
                        best = candidate;
                    }
                }

                if (best != null)
                {
                    best.ApplyDetection(det, rect);
                    matched.Add(best);
                }
                else
                {
                    var created = new TrackedObservation(nextTrackId++, kind, det, rect);
                    active.Add(created);
                    appeared.Add(created);
                    matched.Add(created);
                }
            }

            // STEP 2: lifecycle. Unmatched tracks accumulate misses and are dropped past the limit.
            for (int t = active.Count - 1; t >= 0; t--)
            {
                TrackedObservation tr = active[t];
                if (tr.MatchedThisFrame) continue;

                tr.RegisterMiss();
                if (tr.MissedFrames > maxMissedFrames)
                {
                    lost.Add(tr);
                    active.RemoveAt(t);
                }
            }

            // STEP 3: recognition hook, run only on tracks that matched this frame.
            if (recognitionDue && recognize != null && matched.Count > 0)
                recognize(matched);

            // STEP 4: profile assignment. The only place a track's Profile is set.
            for (int t = 0; t < active.Count; t++)
            {
                TrackedObservation tr = active[t];
                UnityEngine.Object resolved = resolveProfile != null
                    ? resolveProfile(kind, tr.Label, tr.Identified)
                    : null;
                tr.CommitIdentity(resolved);
            }
        }
    }
}
