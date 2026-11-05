// Role: object module. YOLO-style detector on the full frame. Labels are class names from ModelSettings.
using System.Collections.Generic;
using UnityEngine;

namespace AR.Vision
{
    public sealed class ObjectModule : VisionModule
    {
        private readonly ModelSettings.ObjectSettings settings;
        private readonly ObjectDetector detector;

        public override ObservationKind Kind => ObservationKind.Object;
        public override bool IsReady => detector != null;

        public ObjectModule(ModelSettings.ObjectSettings settings)
        {
            this.settings = settings;

            if (settings.detector == null)
                Debug.LogError("ObjectModule: detector asset is not assigned.");
            else
                detector = new ObjectDetector(settings.detector, settings.transposedOutput, settings.classNames);
        }

        public override List<Detection> Detect(VisionFrame frame)
        {
            if (!IsReady) return new List<Detection>();
            return detector.Detect(frame, settings.scoreThreshold, settings.nmsIouThreshold);
        }

        public override void Dispose()
        {
            detector?.Dispose();
        }
    }
}
