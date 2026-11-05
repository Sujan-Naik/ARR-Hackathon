// Role: MediaPipe-style palm detector on a 256x256 full-frame input. Decodes boxes and runs NMS.
using System.Collections.Generic;
using UnityEngine;
using Unity.InferenceEngine;

namespace AR.Vision
{
    public sealed class PalmDetector : System.IDisposable
    {
        public const int InputSize = 256;
        private const int PalmKeypoints = 7;

        // ASSUMPTION: each anchor row holds 4 box values (cx, cy, w, h in 256-px space)
        // followed by 7 keypoints of (x, y). That is 4 + 7 * 2 = 18 values per anchor.
        private const int BoxStride = 4 + PalmKeypoints * 2;

        private readonly Worker detector;
        private readonly float[] input = new float[3 * InputSize * InputSize];

        public PalmDetector(ModelAsset asset)
        {
            detector = new Worker(ModelLoader.Load(asset), BackendType.GPUCompute);
        }

        public List<Detection> Detect(VisionFrame frame, float scoreThreshold, float nmsIouThreshold)
        {
            frame.FillChw(input, InputSize, Geometry.FullFrame, 1f / 255f, 0f);   // ASSUMPTION: palm model expects [0,1] RGB

            using Tensor<float> t = new Tensor<float>(new TensorShape(1, 3, InputSize, InputSize), input);
            detector.Schedule(t);

            // ASSUMPTION: output 0 is boxes shaped [1, N, 18] and already decoded from anchors into pixels.
            float[] boxes = (detector.PeekOutput(0) as Tensor<float>).DownloadToArray();
            // ASSUMPTION: output 1 is scores shaped [1, N, 1] holding raw logits, so sigmoid is applied.
            float[] scores = (detector.PeekOutput(1) as Tensor<float>).DownloadToArray();

            int count = scores.Length;   // ASSUMPTION: N equals the score count
            var candidates = new List<Detection>();

            for (int i = 0; i < count; i++)
            {
                float prob = Geometry.Sigmoid(scores[i]);
                if (prob < scoreThreshold) continue;

                int b = i * BoxStride;
                float cx = boxes[b + 0];
                float cy = boxes[b + 1];
                float w = boxes[b + 2];
                float h = boxes[b + 3];

                var keypoints = new Vector2[PalmKeypoints];
                for (int k = 0; k < PalmKeypoints; k++)
                    keypoints[k] = new Vector2(boxes[b + 4 + 2 * k], boxes[b + 5 + 2 * k]);

                candidates.Add(new Detection
                {
                    Box = new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h),
                    Keypoints = keypoints,
                    Score = prob,
                    ClassIndex = 0,
                    Label = null,
                    Identified = false,
                    InputSize = InputSize,
                    Region = Geometry.FullFrame
                });
            }

            return Geometry.Nms(candidates, nmsIouThreshold);
        }

        public void Dispose()
        {
            detector?.Dispose();
        }
    }
}
