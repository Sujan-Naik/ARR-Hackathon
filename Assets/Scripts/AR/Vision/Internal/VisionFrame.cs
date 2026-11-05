// Role: one captured frame as a CPU buffer in top-down order with a top-left origin.
// THE ONLY VERTICAL FLIP IN THE VISION LAYER happens in CopyFrom. Texture2D rows are bottom-up.
using UnityEngine;

namespace AR.Vision
{
    public sealed class VisionFrame
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public Color32[] Pixels { get; private set; }

        public void CopyFrom(Texture2D bottomUp)
        {
            int w = bottomUp.width;
            int h = bottomUp.height;
            if (Pixels == null || Width != w || Height != h)
            {
                Width = w;
                Height = h;
                Pixels = new Color32[w * h];
            }

            Color32[] src = bottomUp.GetPixels32();
            for (int y = 0; y < h; y++)
            {
                int srcRow = (h - 1 - y) * w;   // bottom-up source row
                int dstRow = y * w;             // top-down destination row
                System.Array.Copy(src, srcRow, Pixels, dstRow, w);
            }
        }

        // Bilinear sample. u and v are normalized with a top-left origin. No flip happens here.
        public Color SampleTopLeft(float u, float v)
        {
            float x = Mathf.Clamp(u * Width - 0.5f, 0f, Width - 1f);
            float y = Mathf.Clamp(v * Height - 0.5f, 0f, Height - 1f);

            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            int x1 = Mathf.Min(x0 + 1, Width - 1);
            int y1 = Mathf.Min(y0 + 1, Height - 1);
            float fx = x - x0;
            float fy = y - y0;

            Color top = Color.Lerp(Pixels[y0 * Width + x0], Pixels[y0 * Width + x1], fx);
            Color bottom = Color.Lerp(Pixels[y1 * Width + x0], Pixels[y1 * Width + x1], fx);
            return Color.Lerp(top, bottom, fy);
        }

        // Writes a CHW float tensor of size x size pixels sampled from region.
        // Each value is channel * scale + bias. Fast path when the region is the full frame at the same size.
        public void FillChw(float[] dst, int size, Rect region, float scale, float bias)
        {
            int plane = size * size;

            bool identity = size == Width && size == Height
                && region.xMin == 0f && region.yMin == 0f && region.width == 1f && region.height == 1f;

            if (identity)
            {
                for (int i = 0; i < plane; i++)
                {
                    Color32 c = Pixels[i];
                    dst[i] = c.r * scale + bias;
                    dst[plane + i] = c.g * scale + bias;
                    dst[2 * plane + i] = c.b * scale + bias;
                }
                return;
            }

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 uv = Geometry.MapToFrame(new Vector2(x + 0.5f, y + 0.5f), size, region);
                    Color c = SampleTopLeft(uv.x, uv.y);
                    int idx = y * size + x;
                    dst[idx] = c.r * scale + bias;
                    dst[plane + idx] = c.g * scale + bias;
                    dst[2 * plane + idx] = c.b * scale + bias;
                }
            }
        }
    }
}
