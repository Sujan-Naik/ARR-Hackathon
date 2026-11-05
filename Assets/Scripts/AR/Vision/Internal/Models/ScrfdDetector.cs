// Role: SCRFD face detector. DecodeScrfd, Nms and FillInput are ported from the original script.
using System.Collections.Generic;
using UnityEngine;
using Unity.InferenceEngine;

namespace AR.Vision
{
    public sealed class ScrfdDetector : System.IDisposable
    {
        public const int DetSize = 640;

        // SCRFD output layout: 3 strides, 2 anchors per grid cell
        private static readonly int[] Strides = { 8, 16, 32 };
        private const int NumAnchors = 2;
        private const int NumOutputs = 9;   // scores x3, boxes x3, keypoints x3

        private readonly Worker detector;
        private readonly float[] detInputData = new float[3 * DetSize * DetSize];

        public ScrfdDetector(ModelAsset asset)
        {
            detector = new Worker(ModelLoader.Load(asset), BackendType.GPUCompute);
        }

        public List<Detection> Detect(VisionFrame frame, float detectionThreshold, float nmsIouThreshold)
        {
            FillInput(frame);

            using Tensor<float> input = new Tensor<float>(new TensorShape(1, 3, DetSize, DetSize), detInputData);
            detector.Schedule(input);

            // Outputs: scores[0..2], boxes[3..5], keypoints[6..8], one group per stride
            float[][] raw = new float[NumOutputs][];
            for (int i = 0; i < NumOutputs; i++)
            {
                Tensor<float> t = detector.PeekOutput(i) as Tensor<float>;
                raw[i] = t.DownloadToArray();
            }

            return DecodeScrfd(raw, detectionThreshold, nmsIouThreshold);
        }

        // Converts the frame into a CHW tensor with SCRFD normalization.
        // The frame is already top-down (VisionFrame.CopyFrom flipped it once), so this is a straight copy.
        private void FillInput(VisionFrame frame)
        {
            Color32[] px = frame.Pixels;
            int plane = DetSize * DetSize;

            for (int y = 0; y < DetSize; y++)
            {
                int row = y * DetSize;

                for (int x = 0; x < DetSize; x++)
                {
                    Color32 c = px[row + x];
                    int idx = row + x;
                    detInputData[0 * plane + idx] = (c.r - 127.5f) / 128f;
                    detInputData[1 * plane + idx] = (c.g - 127.5f) / 128f;
                    detInputData[2 * plane + idx] = (c.b - 127.5f) / 128f;
                }
            }
        }

        private List<Detection> DecodeScrfd(float[][] raw, float detectionThreshold, float nmsIouThreshold)
        {
            var candidates = new List<Detection>();

            for (int s = 0; s < Strides.Length; s++)
            {
                int stride = Strides[s];
                int grid = DetSize / stride;
                float[] scores = raw[s];
                float[] boxes = raw[3 + s];
                float[] kps = raw[6 + s];
                int count = grid * grid * NumAnchors;

                for (int idx = 0; idx < count; idx++)
                {
                    float score = scores[idx];
                    if (score < detectionThreshold) continue;

                    // Anchor index is ordered (y, x, anchor)
                    int cell = idx / NumAnchors;
                    float cx = (cell % grid) * stride;
                    float cy = (cell / grid) * stride;

                    // Box offsets are distances to each edge, in stride units
                    int b = idx * 4;
                    float x1 = cx - boxes[b + 0] * stride;
                    float y1 = cy - boxes[b + 1] * stride;
                    float x2 = cx + boxes[b + 2] * stride;
                    float y2 = cy + boxes[b + 3] * stride;

                    // Landmarks: left eye, right eye, nose, left mouth, right mouth
                    int k = idx * 10;
                    Vector2 leftEye = new Vector2(cx + kps[k + 0] * stride, cy + kps[k + 1] * stride);
                    Vector2 rightEye = new Vector2(cx + kps[k + 2] * stride, cy + kps[k + 3] * stride);
                    Vector2 nose = new Vector2(cx + kps[k + 4] * stride, cy + kps[k + 5] * stride);
                    Vector2 mouthLeft = new Vector2(cx + kps[k + 6] * stride, cy + kps[k + 7] * stride);
                    Vector2 mouthRight = new Vector2(cx + kps[k + 8] * stride, cy + kps[k + 9] * stride);

                    candidates.Add(new Detection
                    {
                        Box = new Rect(x1, y1, x2 - x1, y2 - y1),
                        Keypoints = new[] { leftEye, rightEye, nose, mouthLeft, mouthRight },
                        Score = score,
                        ClassIndex = 0,
                        Label = null,
                        Identified = false,
                        InputSize = DetSize,
                        Region = Geometry.FullFrame
                    });
                }
            }

            return Geometry.Nms(candidates, nmsIouThreshold);
        }

        public void Dispose()
        {
            detector?.Dispose();
        }
    }
}
