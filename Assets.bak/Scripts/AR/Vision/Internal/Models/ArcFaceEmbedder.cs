// Role: ArcFace embedder. Eye-based similarity alignment to the canonical 112x112 template, then a unit-length embedding.
using UnityEngine;
using Unity.InferenceEngine;

namespace AR.Vision
{
    public sealed class ArcFaceEmbedder : System.IDisposable
    {
        public const int RecSize = 112;

        // Canonical ArcFace eye positions inside a 112x112 aligned crop
        private static readonly Vector2 CanonLeftEye = new Vector2(38.2946f, 51.6963f);
        private static readonly Vector2 CanonRightEye = new Vector2(73.5318f, 51.5014f);

        private readonly Worker embedder;

        public ArcFaceEmbedder(ModelAsset asset)
        {
            embedder = new Worker(ModelLoader.Load(asset), BackendType.GPUCompute);
        }

        public float[] Embed(VisionFrame frame, Detection face)
        {
            float[] data = AlignCrop(frame, face);
            using Tensor<float> input = new Tensor<float>(new TensorShape(1, 3, RecSize, RecSize), data);
            embedder.Schedule(input);

            Tensor<float> output = embedder.PeekOutput() as Tensor<float>;
            float[] vec = output.DownloadToArray();
            Geometry.Normalize(vec);
            return vec;
        }

        // Similarity transform from the two eyes to the canonical ArcFace template, sampled from the input frame.
        private static float[] AlignCrop(VisionFrame frame, Detection face)
        {
            Vector2 leftEye = face.Keypoints[0];
            Vector2 rightEye = face.Keypoints[1];

            Vector2 cv = CanonRightEye - CanonLeftEye;
            Vector2 sv = rightEye - leftEye;

            float scale = sv.magnitude / cv.magnitude;
            float angle = Mathf.Atan2(sv.y, sv.x) - Mathf.Atan2(cv.y, cv.x);
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);

            int plane = RecSize * RecSize;
            float[] data = new float[3 * plane];

            for (int y = 0; y < RecSize; y++)
            {
                for (int x = 0; x < RecSize; x++)
                {
                    float dx = x - CanonLeftEye.x;
                    float dy = y - CanonLeftEye.y;

                    float px = leftEye.x + scale * (cos * dx - sin * dy);
                    float py = leftEye.y + scale * (sin * dx + cos * dy);

                    // px and py are in the detector's input pixels, top-down. The frame is top-down too,
                    // so no flip happens here. The flip lives only in VisionFrame.CopyFrom.
                    Color c = frame.SampleTopLeft(px / face.InputSize, py / face.InputSize);
                    int idx = y * RecSize + x;

                    // ArcFace expects RGB normalized to [-1, 1]
                    data[0 * plane + idx] = c.r * 2f - 1f;
                    data[1 * plane + idx] = c.g * 2f - 1f;
                    data[2 * plane + idx] = c.b * 2f - 1f;
                }
            }
            return data;
        }

        public void Dispose()
        {
            embedder?.Dispose();
        }
    }
}
