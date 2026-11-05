// Role: shared math for the vision layer. One IoU, one NMS, and one coordinate mapping helper.
using System.Collections.Generic;
using UnityEngine;

namespace AR.Vision
{
    public static class Geometry
    {
        public static readonly Rect FullFrame = new Rect(0f, 0f, 1f, 1f);

        public static float IoU(Rect a, Rect b)
        {
            float x1 = Mathf.Max(a.xMin, b.xMin);
            float y1 = Mathf.Max(a.yMin, b.yMin);
            float x2 = Mathf.Min(a.xMax, b.xMax);
            float y2 = Mathf.Min(a.yMax, b.yMax);

            float inter = Mathf.Max(0f, x2 - x1) * Mathf.Max(0f, y2 - y1);
            float union = a.width * a.height + b.width * b.height - inter;
            return union <= 0f ? 0f : inter / union;
        }

        public static List<Detection> Nms(List<Detection> items, float nmsIouThreshold)
        {
            items.Sort((a, b) => b.Score.CompareTo(a.Score));
            var kept = new List<Detection>();

            foreach (var d in items)
            {
                bool overlaps = false;
                foreach (var k in kept)
                {
                    if (IoU(d.Box, k.Box) > nmsIouThreshold)
                    {
                        overlaps = true;
                        break;
                    }
                }
                if (!overlaps) kept.Add(d);
            }
            return kept;
        }

        // The single coordinate mapping helper.
        // Maps a point in a module's input space (inputSize x inputSize, top-left origin)
        // to frame-normalized 0..1 coordinates, through the region that input covers.
        public static Vector2 MapToFrame(Vector2 p, float inputSize, Rect region)
        {
            float s = 1f / inputSize;
            return new Vector2(
                region.xMin + p.x * s * region.width,
                region.yMin + p.y * s * region.height);
        }

        public static Rect MapToFrame(Rect r, float inputSize, Rect region)
        {
            Vector2 min = MapToFrame(new Vector2(r.xMin, r.yMin), inputSize, region);
            Vector2 max = MapToFrame(new Vector2(r.xMax, r.yMax), inputSize, region);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        // Square region centered on r, side = longer edge of r times scale.
        public static Rect SquareAround(Rect r, float scale)
        {
            Vector2 c = r.center;
            float side = Mathf.Max(r.width, r.height) * scale;
            return new Rect(c.x - side * 0.5f, c.y - side * 0.5f, side, side);
        }

        public static void Normalize(float[] v)
        {
            float sum = 0f;
            for (int i = 0; i < v.Length; i++) sum += v[i] * v[i];
            float inv = 1f / Mathf.Sqrt(Mathf.Max(sum, 1e-12f));
            for (int i = 0; i < v.Length; i++) v[i] *= inv;
        }

        public static float Dot(float[] a, float[] b)
        {
            float s = 0f;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        public static float Sigmoid(float x)
        {
            return 1f / (1f + Mathf.Exp(-x));
        }
    }
}
