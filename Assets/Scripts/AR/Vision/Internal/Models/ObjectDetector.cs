// Role: YOLO-style object detector on a 640x640 full-frame input. Supports channel-first and transposed output.
using System.Collections.Generic;
using UnityEngine;
using Unity.InferenceEngine;

namespace AR.Vision
{
    public sealed class ObjectDetector : System.IDisposable
    {
        public const int InputSize = 640;

        private readonly Worker detector;
        private readonly bool transposedOutput;
        private readonly string[] classNames;
        private readonly float[] input = new float[3 * InputSize * InputSize];

        public ObjectDetector(ModelAsset asset, bool transposedOutput, string[] classNames)
        {
            this.transposedOutput = transposedOutput;
            this.classNames = classNames ?? new string[0];
            detector = new Worker(ModelLoader.Load(asset), BackendType.GPUCompute);
        }

        public List<Detection> Detect(VisionFrame frame, float scoreThreshold, float nmsIouThreshold)
        {
            var results = new List<Detection>();
            int numClasses = classNames.Length;
            if (numClasses == 0) return results;

            frame.FillChw(input, InputSize, Geometry.FullFrame, 1f / 255f, 0f);   // ASSUMPTION: [0,1] RGB input

            using Tensor<float> t = new Tensor<float>(new TensorShape(1, 3, InputSize, InputSize), input);
            detector.Schedule(t);

            float[] data = (detector.PeekOutput(0) as Tensor<float>).DownloadToArray();

            // ASSUMPTION: attribute count is 4 box values followed by one score per class. Class count equals classNames.Length.
            // ASSUMPTION: class scores are already sigmoid probabilities (YOLOv8-style export), so no objectness term is used.
            int attrs = 4 + numClasses;
            int anchors = data.Length / attrs;

            var perClass = new Dictionary<int, List<Detection>>();

            for (int a = 0; a < anchors; a++)
            {
                float best = 0f;
                int cls = -1;
                for (int c = 0; c < numClasses; c++)
                {
                    float s = Read(data, 4 + c, a, anchors, attrs);
                    if (s > best)
                    {
                        best = s;
                        cls = c;
                    }
                }
                if (cls < 0 || best < scoreThreshold) continue;

                float cx = Read(data, 0, a, anchors, attrs);
                float cy = Read(data, 1, a, anchors, attrs);
                float w = Read(data, 2, a, anchors, attrs);
                float h = Read(data, 3, a, anchors, attrs);

                if (!perClass.TryGetValue(cls, out List<Detection> list))
                {
                    list = new List<Detection>();
                    perClass[cls] = list;
                }

                list.Add(new Detection
                {
                    Box = new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h),
                    Keypoints = System.Array.Empty<Vector2>(),
                    Score = best,
                    ClassIndex = cls,
                    Label = classNames[cls],
                    Identified = true,
                    InputSize = InputSize,
                    Region = Geometry.FullFrame
                });
            }

            // Class-aware NMS: boxes of different classes never suppress each other.
            foreach (var pair in perClass)
                results.AddRange(Geometry.Nms(pair.Value, nmsIouThreshold));

            return results;
        }

        // Layout is either [1, attrs, anchors] (channel-first) or [1, anchors, attrs] (transposed).
        private float Read(float[] data, int attr, int anchor, int anchors, int attrs)
        {
            return transposedOutput
                ? data[anchor * attrs + attr]
                : data[attr * anchors + anchor];
        }

        public void Dispose()
        {
            detector?.Dispose();
        }
    }
}
