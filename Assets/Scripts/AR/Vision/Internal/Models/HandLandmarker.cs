// Role: hand landmark model. Takes a 224x224 NHWC (height, width, channel) crop and returns 21 joints,
// presence and handedness. Output decoding is ASSUMED. Verify every ASSUMPTION line in Netron before trusting joints.
using UnityEngine;
using Unity.InferenceEngine;

namespace AR.Vision
{
    public sealed class HandLandmarker : System.IDisposable
    {
        // Matches the model's expected input shape (1, 224, 224, 3).
        public const int InputSize = 224;
        public const int JointCount = 21;

        // Debug output. Set to false to silence the console.
        private const bool DebugLog = true;
        private const int LogEveryNCalls = 60;

        public struct Pose
        {
            public Vector2[] Joints;     // in 224-px crop space, top-down
            public float[] Depth;        // relative z per joint, unused by Observation
            public float Presence;       // 0..1
            public float Handedness;     // 0..1, above 0.5 means Left
        }

        private readonly Worker landmarker;
        private readonly float[] input = new float[InputSize * InputSize * 3];
        private int callCount;

        public HandLandmarker(ModelAsset asset)
        {
            landmarker = new Worker(ModelLoader.Load(asset), BackendType.GPUCompute);
        }

        public Pose Run(VisionFrame frame, Rect cropRegion)
        {
            FillHwc(frame, cropRegion);

            using Tensor<float> t = new Tensor<float>(new TensorShape(1, InputSize, InputSize, 3), input);
            landmarker.Schedule(t);

            // ASSUMPTION: output 0 is [1, 63] holding 21 joints of (x, y, z) in 224-px space.
            float[] xyz = (landmarker.PeekOutput(0) as Tensor<float>).DownloadToArray();
            // ASSUMPTION: output 1 is [1, 1] presence as a logit.
            float[] presence = (landmarker.PeekOutput(1) as Tensor<float>).DownloadToArray();
            // ASSUMPTION: output 2 is [1, 1] handedness as a logit. Sigmoid above 0.5 means Left.
            float[] handed = (landmarker.PeekOutput(2) as Tensor<float>).DownloadToArray();

            var pose = new Pose
            {
                Joints = new Vector2[JointCount],
                Depth = new float[JointCount]
            };

            for (int j = 0; j < JointCount; j++)
            {
                pose.Joints[j] = new Vector2(xyz[j * 3 + 0], xyz[j * 3 + 1]);
                pose.Depth[j] = xyz[j * 3 + 2];
            }

            pose.Presence = Geometry.Sigmoid(presence[0]);
            pose.Handedness = Geometry.Sigmoid(handed[0]);

            if (DebugLog && callCount++ % LogEveryNCalls == 0)
            {
                Debug.Log(
                    $"[Landmark] xyzLen={xyz.Length} presenceLen={presence.Length} handedLen={handed.Length} " +
                    $"rawPresence={presence[0]:0.000} presence={pose.Presence:0.000} " +
                    $"rawHanded={handed[0]:0.000} handed={pose.Handedness:0.000} " +
                    $"joint0=({pose.Joints[0].x:0.0}, {pose.Joints[0].y:0.0}) " +
                    $"crop=({cropRegion.xMin:0.000}, {cropRegion.yMin:0.000}, {cropRegion.width:0.000})");
            }

            return pose;
        }

        // Writes an NHWC (height, width, channel) crop with RGB values in [0, 1].
        // ASSUMPTION: the landmark model expects [0, 1] input. Some exports expect [-1, 1] instead.
        private void FillHwc(VisionFrame frame, Rect cropRegion)
        {
            for (int y = 0; y < InputSize; y++)
            {
                for (int x = 0; x < InputSize; x++)
                {
                    Vector2 uv = Geometry.MapToFrame(new Vector2(x + 0.5f, y + 0.5f), InputSize, cropRegion);
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
            landmarker?.Dispose();
        }
    }
}