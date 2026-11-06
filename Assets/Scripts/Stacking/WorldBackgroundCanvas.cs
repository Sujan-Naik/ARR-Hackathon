using UnityEngine;

namespace Stacking
{
    [RequireComponent(typeof(Canvas))]
    [RequireComponent(typeof(RectTransform))]
    public class WorldBackgroundCanvas : MonoBehaviour
    {
        [Tooltip("Must be the SAME camera that renders your cubes. Do not leave null unless Camera.main is that camera.")]
        [SerializeField] private Camera viewCamera;
        [SerializeField, Min(0.1f)] private float distanceFromCamera = 100f;

        private Canvas canvas;
        private RectTransform rect;
        private float lastAspect;
        private int lastW, lastH;

        private void Awake()
        {
            canvas = GetComponent<Canvas>();
            rect = (RectTransform)transform;

            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = null;
            canvas.overrideSorting = false;
            canvas.sortingOrder = -1000;

            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            if (viewCamera == null) viewCamera = Camera.main;
            Fit();
        }

        private void LateUpdate()
        {
            if (viewCamera == null) viewCamera = Camera.main;
            if (viewCamera == null) return;

            if (!Mathf.Approximately(viewCamera.aspect, lastAspect)
                || viewCamera.pixelWidth != lastW
                || viewCamera.pixelHeight != lastH)
                Fit();
        }

        private void Fit()
        {
            if (viewCamera == null) return;

            float worldHeight;
            if (viewCamera.orthographic)
                worldHeight = viewCamera.orthographicSize * 2f;
            else
                worldHeight = 2f * distanceFromCamera * Mathf.Tan(viewCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);

            int pixelW = Mathf.Max(1, viewCamera.pixelWidth);
            int pixelH = Mathf.Max(1, viewCamera.pixelHeight);
            float unitsPerPixel = worldHeight / pixelH;

            rect.sizeDelta = new Vector2(pixelW, pixelH);
            rect.localScale = new Vector3(unitsPerPixel, unitsPerPixel, unitsPerPixel);

            Transform ct = viewCamera.transform;
            // Use the camera's world-space up and right so the plane is ALWAYS
            // parallel to the screen, regardless of camera rotation.
            Vector3 pos = ct.position
                        + ct.forward * distanceFromCamera;
            rect.position = pos;
            // Match the camera's orientation so the RectTransform's +Z faces the
            // camera (WorldSpace canvases face +Z away from their surface).
            rect.rotation = Quaternion.LookRotation(ct.forward, ct.up);
        }
    }
}
