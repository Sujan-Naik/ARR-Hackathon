// Role: Inspector-facing settings for every module. Assets, thresholds and class names live here,
// and VisionPipeline holds one instance of this class.
using System;
using UnityEngine;
using Unity.InferenceEngine;

namespace AR.Vision
{
    [Serializable]
    public class ModelSettings
    {
        [Serializable]
        public class FaceSettings
        {
            [Tooltip("SCRFD detector, for example scrfd_10g.onnx")]
            public ModelAsset detector;
            [Tooltip("ArcFace embedder, for example arcface_w600k_r50.onnx")]
            public ModelAsset embedder;
            public float detectionThreshold = 0.5f;
            public float nmsIouThreshold = 0.4f;
            public float matchThreshold = 0.45f;
            public int recognizeEveryNFrames = 10;
        }

        [Serializable]
        public class HandSettings
        {
            public ModelAsset palmDetector;
            public ModelAsset handLandmarker;
            public float palmScoreThreshold = 0.5f;
            public float palmNmsIouThreshold = 0.3f;
            public float handPresenceThreshold = 0.5f;
        }

        [Serializable]
        public class ObjectSettings
        {
            public ModelAsset detector;
            [Tooltip("Enable when the model outputs [1, anchors, 4 + classes]")]
            public bool transposedOutput = false;
            [Tooltip("Class names in model index order")]
            public string[] classNames = new string[0];
            public float scoreThreshold = 0.4f;
            public float nmsIouThreshold = 0.45f;
        }

        public FaceSettings face = new FaceSettings();
        public HandSettings hand = new HandSettings();
        public ObjectSettings objects = new ObjectSettings();
    }
}
