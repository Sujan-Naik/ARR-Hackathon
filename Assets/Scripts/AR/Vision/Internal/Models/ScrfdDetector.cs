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

        // Debug output. Set to false to silence.
        private const bool DebugLog = true;
        private const int LogEveryNCalls = 30;

        private readonly Worker detector;
        private readonly float[] detInputData = new float[3 * DetSize * DetSize];
        private int callCount;

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
                if (t == null)
                {
                    if (DebugLog && callCount == 0)
                        Debug.LogError($"[SCRFD] PeekOutput({i}) returned null or non-float tensor. " +
                                       $"Check output count / types in Netron.");
                    return new List<Detection>();
                }
                raw[i] = t.DownloadToArray();
            }

            if (DebugLog && callCount++ % LogEveryNCalls == 0)
            {
                // Aggregate max score across all three strides (SCRFD outputs are probabilities,
                // not logits — no sigmoid needed).
                float maxScore = float.NegativeInfinity;
                int above = 0;
                for (int s = 0; s < Strides.Length; s++)
                {
                    float[] scores = raw[s];
                    if (scores == null) continue;
                    for (int i = 0; i < scores.Length; i++)
                    {
                        float v = scores[i];
                        if (v > maxScore) maxScore = v;
                        if (v >= detectionThreshold) above++;
                    }
                }

                Debug.Log($"[SCRFD] scores0={raw[0].Length} scores1={raw[1].Length} scores2={raw[2].Length} " +
                          $"boxes0={raw[3].Length} kps0={raw[6].Length} " +
                          $"maxScore={maxScore:0.000} aboveThreshold={above} threshold={detectionThreshold:0.00} " +
                          $"inSize={DetSize}");

                // One-time input-sample diagnostic on the first call. Confirms the frame
                // reaching the model is a real image and not black / uniform / flipped.
                if (callCount == 1)
                {
                    Color cC = frame.SampleTopLeft(0.5f, 0.5f);
                    Color cTL = frame.SampleTopLeft(0.02f, 0.02f);
                    Color cBR = frame.SampleTopLeft(0.98f, 0.98f);
                    Color cTR = frame.SampleTopLeft(0.98f, 0.02f);

                    float sum = 0f;
                    for (int i = 0; i < detInputData.Length; i++) sum += detInputData[i];
                    float mean = sum / detInputData.Length;

                    Debug.Log($"[SCRFD-input] tensorMean={mean:0.000} " +
                              $"C=({cC.r:0.00},{cC.g:0.00},{cC.b:0.00}) " +
                              $"TL=({cTL.r:0.00},{cTL.g:0.00},{cTL.b:0.00}) " +
                              $"TR=({cTR.r:0.00},{cTR.g:0.00},{cTR.b:0.00}) " +
                              $"BR=({cBR.r:0.00},{cBR.g:0.00},{cBR.b:0.00})");
                }
            }

            return DecodeScrfd(raw, detectionThreshold, nmsIouThreshold);
        }

        // Converts the frame into a CHW tensor with SCRFD normalization, sampled through the
        // same path as PalmDetector / ArcFaceEmbedder so all modules see identical pixels.
        // Frame is already top-down (VisionFrame.CopyFrom flipped it once), so a straight copy.
        private void FillInput(VisionFrame frame)
        {
            int plane = DetSize * DetSize;

            for (int y = 0; y < DetSize; y++)
            {
                int row = y * DetSize;

                for (int x = 0; x < DetSize; x++)
                {
                    // Match the SampleTopLeft path used by PalmDetector and ArcFace.
                    Vector2 uv = Geometry.MapToFrame(
                        new Vector2(x + 0.5f, y + 0.5f), DetSize, Geometry.FullFrame);
                    Color c = frame.SampleTopLeft(uv.x, uv.y);

                    int idx = row + x;
                    // Equivalent to (byte - 127.5) / 128, but from [0,1] float color.
                    detInputData[0 * plane + idx] = (c.r - 0.5f) / 0.5f;
                    detInputData[1 * plane + idx] = (c.g - 0.5f) / 0.5f;
                    detInputData[2 * plane + idx] = (c.b - 0.5f) / 0.5f;
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

                // Guard against a model output that doesn't match our expected grid/stride layout.
                // If this fires, check the model's output shapes in Netron.
                if (scores.Length < count)
                {
                    if (DebugLog && callCount % LogEveryNCalls == 0)
                        Debug.LogWarning($"[SCRFD] stride {stride}: scores.Length={scores.Length} < expected {count}. " +
                                         $"Decode layout mismatch.");
                    count = scores.Length;
                }

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
                    Vector2 mouthRt = new Vector2(cx + kps[k + 8] * stride, cy + kps[k + 9] * stride);

                    candidates.Add(new Detection
                    {
                        Box = new Rect(x1, y1, x2 - x1, y2 - y1),
                        Keypoints = new[] { leftEye, rightEye, nose, mouthLeft, mouthRt },
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