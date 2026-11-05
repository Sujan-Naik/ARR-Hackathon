// Role: 21-joint hand landmark model on a 224x224 crop. Outputs joints, depth, presence and handedness.
using UnityEngine;
using Unity.InferenceEngine;

namespace AR.Vision
{
    public sealed class HandLandmarker : System.IDisposable
    {
        public const int InputSize = 224;
        public const int JointCount = 21;

        public struct Pose
        {
            public Vector2[] Joints;     // in 224-px crop space, top-down
            public float[] Depth;        // relative z per joint, unused by Observation
            public float Presence;       // 0..1
            public float Handedness;     // 0..1, above 0.5 means Left
        }

        private readonly Worker landmarker;
        private readonly float[] input = new float[3 * InputSize * InputSize];

        public HandLandmarker(ModelAsset asset)
        {
            landmarker = new Worker(ModelLoader.Load(asset), BackendType.GPUCompute);
        }

        public Pose Run(VisionFrame frame, Rect cropRegion)
        {
            frame.FillChw(input, InputSize, cropRegion, 1f / 255f, 0f);   // ASSUMPTION: [0,1] RGB input

            using Tensor<float> t = new Tensor<float>(new TensorShape(1, 3, InputSize, InputSize), input);
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
            return pose;
        }

        public void Dispose()
        {
            landmarker?.Dispose();
        }
    }
}
