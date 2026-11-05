// Role: one raw detection in a module's input space, plus the frame region that space covers.
// Box and Keypoints are in input pixels (640 for SCRFD and YOLO, 256 for palm, 1 for already-normalized output).
// Region and InputSize let the tracker map them to normalized frame coordinates via Geometry.MapToFrame.
using UnityEngine;

namespace AR.Vision
{
    public struct Detection
    {
        public Rect Box;
        public Vector2[] Keypoints;
        public float Score;
        public int ClassIndex;
        public string Label;
        public bool Identified;
        public float InputSize;
        public Rect Region;
    }
}
