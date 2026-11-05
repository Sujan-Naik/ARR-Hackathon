// Role: base contract for a detection module. Modules report raw detections in input space
// and optionally run recognition on tracks that matched this frame. Tensors never leave this layer.
using System;
using System.Collections.Generic;

namespace AR.Vision
{
    public abstract class VisionModule : IDisposable
    {
        public abstract ObservationKind Kind { get; }
        public abstract bool IsReady { get; }

        // Modules that run recognition (faces) override this to schedule it on a frame index.
        public virtual bool RecognitionDue(int frameIndex)
        {
            return false;
        }

        // Returns NMS-filtered detections for this frame. Called once per frame.
        public abstract List<Detection> Detect(VisionFrame frame);

        // Runs on tracks that matched a detection this frame. Writes label, match score and identified only.
        public virtual void Recognize(VisionFrame frame, IReadOnlyList<TrackedObservation> tracks)
        {
        }

        public virtual void Dispose()
        {
        }
    }
}
