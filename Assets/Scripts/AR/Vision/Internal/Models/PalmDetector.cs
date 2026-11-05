// Role: palm detector. Takes a 192x192 NHWC (height, width, channel) frame, decodes anchor-relative boxes and scores, and runs NMS.
// Anchor layout and decode are ASSUMED to match the MediaPipe palm model (2016 anchors). Verify in Netron before trusting boxes.
using System.Collections.Generic;
using UnityEngine;
using Unity.InferenceEngine;

namespace AR.Vision
{
    public sealed class PalmDetector : System.IDisposable
    {
        // Matches the model's expected input shape (1, 192, 192, 3).
        public const int InputSize = 192;
        private const int PalmKeypoints = 7;

        // ASSUMPTION: each row holds 4 box values (cx, cy, w, h) followed by 7 keypoints (x, y).
        // Box values are offsets in input pixels relative to the anchor center.
        private const int BoxStride = 4 + PalmKeypoints * 2;

        // ASSUMPTION: anchors are laid out as strides {8, 16, 16, 16}, each with 2 anchors per cell.
        // That gives 24*24*2 + 12*12*2*3 = 1152 + 864 = 2016, matching the model's output count.
        private static readonly int[] AnchorStrides = { 8, 16, 16, 16 };
        private const int AnchorsPerCell = 2;

        // Debug output. Set to false to silence the console.
        private const bool DebugLog = true;
        private const int LogEveryNCalls = 60;

        private readonly Worker detector;
        private readonly float[] input = new float[InputSize * InputSize * 3];
        private readonly Vector2[] anchors;
        private int callCount;

        public PalmDetector(ModelAsset asset)
        {
            detector = new Worker(ModelLoader.Load(asset), BackendType.GPUCompute);
            anchors = BuildAnchors();
        }

        // Anchor centers normalized 0..1, ordered (y, x, anchor) like the model.
        private static Vector2[] BuildAnchors()
        {
            var list = new List<Vector2>();
            foreach (int stride in AnchorStrides)
            {
                int grid = InputSize / stride;
                for (int y = 0; y < grid; y++)
                {
                    for (int x = 0; x < grid; x++)
                    {
                        Vector2 center = new Vector2((x + 0.5f) / grid, (y + 0.5f) / grid);
                        for (int a = 0; a < AnchorsPerCell; a++) list.Add(center);
                    }
                }
            }
            return list.ToArray();
        }

        public List<Detection> Detect(VisionFrame frame, float scoreThreshold, float nmsIouThreshold)
        {
            FillHwc(frame);

            using Tensor<float> t = new Tensor<float>(new TensorShape(1, InputSize, InputSize, 3), input);
            detector.Schedule(t);

            // ASSUMPTION: output 0 is boxes shaped [1, N, 18] with raw offsets in input pixels.
            float[] boxes = (detector.PeekOutput(0) as Tensor<float>).DownloadToArray();

            // ASSUMPTION: output 1 is scores shaped [1, N, 1] holding raw logits, so sigmoid is applied.
            float[] scores = (detector.PeekOutput(1) as Tensor<float>).DownloadToArray();

            int count = Mathf.Min(scores.Length, anchors.Length);
            var candidates = new List<Detection>();

            float maxRaw = float.NegativeInfinity;
            int aboveThreshold = 0;

            for (int i = 0; i < count; i++)
            {
                if (scores[i] > maxRaw) maxRaw = scores[i];

                float prob = Geometry.Sigmoid(scores[i]);
                if (prob < scoreThreshold) continue;
                aboveThreshold++;

                int b = i * BoxStride;
                Vector2 anchor = anchors[i] * InputSize;   // anchor center in input pixels

                float cx = anchor.x + boxes[b + 0];
                float cy = anchor.y + boxes[b + 1];
                float w = boxes[b + 2];
                float h = boxes[b + 3];

                var keypoints = new Vector2[PalmKeypoints];
                for (int k = 0; k < PalmKeypoints; k++)
                {
                    keypoints[k] = new Vector2(
                        anchor.x + boxes[b + 4 + 2 * k],
                        anchor.y + boxes[b + 5 + 2 * k]);
                }

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

            if (DebugLog && callCount++ % LogEveryNCalls == 0)
            {
                Debug.Log(
                    $"[Palm] scores={scores.Length} anchors={anchors.Length} boxes={boxes.Length} " +
                    $"expectedBoxes={scores.Length * BoxStride} " +
                    $"maxRaw={maxRaw:0.000} maxProb={Geometry.Sigmoid(maxRaw):0.000} " +
                    $"aboveThreshold={aboveThreshold} candidates={candidates.Count}");
            }

            return Geometry.Nms(candidates, nmsIouThreshold);
        }

        // Writes an NHWC (height, width, channel) tensor with RGB values in [0, 1].
        // ASSUMPTION: the palm model expects [0, 1] input. Some exports expect [-1, 1] instead.
        private void FillHwc(VisionFrame frame)
        {
            for (int y = 0; y < InputSize; y++)
            {
                for (int x = 0; x < InputSize; x++)
                {
                    Vector2 uv = Geometry.MapToFrame(new Vector2(x + 0.5f, y + 0.5f), InputSize, Geometry.FullFrame);
                    Color c = frame.SampleTopLeft(uv.x, uv.y);
                    int idx = (y * InputSize + x) * 3;
                    input[idx + 0] = c.r;
                    input[idx + 1] = c.g;
                    input[idx + 2] = c.b;
                }
            }
        }

        public void Dispose()
        {
            detector?.Dispose();
        }
    }
}