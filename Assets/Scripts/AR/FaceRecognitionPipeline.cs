using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Unity.InferenceEngine;

[RequireComponent(typeof(RawImage))]
public class FaceRecognitionPipeline : MonoBehaviour
{
    [Header("Webcam")]
    [SerializeField] private int requestedWidth = 1920;
    [SerializeField] private int requestedHeight = 1080;
    [SerializeField] private int requestedFps = 30;

    [Header("Models")]
    [SerializeField] private ModelAsset faceDetectorAsset;   // SCRFD .onnx (scrfd_10g.onnx)
    [SerializeField] private ModelAsset faceEmbedderAsset;   // ArcFace .onnx (arcface_w600k_r50.onnx)
    [SerializeField] private float detectionThreshold = 0.5f;
    [SerializeField] private float nmsIouThreshold = 0.4f;
    [SerializeField] private float matchThreshold = 0.45f;
    [SerializeField] private int recognizeEveryNFrames = 10;

    private const int DetSize = 640;
    private const int RecSize = 112;

    // SCRFD output layout: 3 strides, 2 anchors per grid cell
    private static readonly int[] Strides = { 8, 16, 32 };
    private const int NumAnchors = 2;
    private const int NumOutputs = 9;   // scores x3, boxes x3, keypoints x3

    // Canonical ArcFace eye positions inside a 112x112 aligned crop
    private static readonly Vector2 CanonLeftEye = new Vector2(38.2946f, 51.6963f);
    private static readonly Vector2 CanonRightEye = new Vector2(73.5318f, 51.5014f);

    private struct Detection
    {
        public Rect box;
        public Vector2 leftEye;
        public Vector2 rightEye;
        public float score;
        public string label;
    }

    private WebCamTexture webcam;
    private RawImage display;
    private AspectRatioFitter fitter;
    private Material orientMat;
    private RenderTexture oriented;     // upright, unmirrored full frame
    private RenderTexture detInput;     // 640x640 copy for the detector
    private Texture2D detReadback;      // CPU copy of detInput
    private float[] detInputData;       // CHW tensor for the detector
    private Worker detector;
    private Worker embedder;
    private Coroutine startRoutine;
    private int frameCount;

    private readonly List<Detection> latestFaces = new List<Detection>();
    private readonly Dictionary<string, float[]> gallery = new Dictionary<string, float[]>();

    void Awake()
    {
        display = GetComponent<RawImage>();

        RectTransform rt = (RectTransform)transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        fitter = GetComponent<AspectRatioFitter>();
        if (fitter == null) fitter = gameObject.AddComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;

        Shader shader = Shader.Find("Hidden/OrientCorrect");
        if (shader == null) Debug.LogError("Hidden/OrientCorrect shader not found in project.");
        orientMat = new Material(shader);

        detector = new Worker(ModelLoader.Load(faceDetectorAsset), BackendType.GPUCompute);
        embedder = new Worker(ModelLoader.Load(faceEmbedderAsset), BackendType.GPUCompute);

        detInput = new RenderTexture(DetSize, DetSize, 0, RenderTextureFormat.ARGB32);
        detReadback = new Texture2D(DetSize, DetSize, TextureFormat.RGB24, false);
        detInputData = new float[3 * DetSize * DetSize];
    }

    void OnEnable()
    {
        startRoutine = StartCoroutine(StartWebcam());
    }

    void OnDisable()
    {
        if (startRoutine != null)
        {
            StopCoroutine(startRoutine);
            startRoutine = null;
        }
        if (webcam != null && webcam.isPlaying) webcam.Stop();
    }

    void OnDestroy()
    {
        if (webcam != null)
        {
            webcam.Stop();
            Destroy(webcam);
            webcam = null;
        }
        detector?.Dispose();
        embedder?.Dispose();
        if (oriented != null) oriented.Release();
        if (detInput != null) detInput.Release();
    }

    IEnumerator StartWebcam()
    {
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);

        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            Debug.LogError("Webcam permission denied.");
            yield break;
        }

        if (WebCamTexture.devices.Length == 0)
        {
            Debug.LogError("No webcam devices found.");
            yield break;
        }

        if (webcam == null)
        {
            webcam = new WebCamTexture(WebCamTexture.devices[0].name, requestedWidth, requestedHeight, requestedFps);
        }

        webcam.Play();

        float timeout = 5f;
        while (webcam.width <= 16 && timeout > 0f)
        {
            timeout -= Time.deltaTime;
            yield return null;
        }

        SetupOrientation();
        startRoutine = null;
    }

    void SetupOrientation()
    {
        int angle = webcam.videoRotationAngle;
        bool flip = webcam.videoVerticallyMirrored;
        bool swap = angle == 90 || angle == 270;

        int w = swap ? webcam.height : webcam.width;
        int h = swap ? webcam.width : webcam.height;

        if (oriented == null || oriented.width != w || oriented.height != h)
        {
            if (oriented != null) oriented.Release();
            oriented = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
        }

        orientMat.SetFloat("_Angle", angle);
        orientMat.SetFloat("_FlipY", flip ? 1f : 0f);

        display.texture = oriented;
        display.uvRect = new Rect(0f, 0f, 1f, 1f);
        display.rectTransform.localEulerAngles = Vector3.zero;
        fitter.aspectRatio = (float)w / h;
    }

    void Update()
    {
        if (webcam == null || !webcam.isPlaying || oriented == null) return;
        if (!webcam.didUpdateThisFrame) return;

        // Upright frame for display, then a square 640x640 copy for the detector
        Graphics.Blit(webcam, oriented, orientMat);
        Graphics.Blit(oriented, detInput);

        RunDetection();

        frameCount++;
        if (frameCount % recognizeEveryNFrames == 0 && latestFaces.Count > 0)
            RunRecognition();
    }

    void RefreshReadback()
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = detInput;
        detReadback.ReadPixels(new Rect(0, 0, DetSize, DetSize), 0, 0);
        detReadback.Apply();
        RenderTexture.active = previous;
    }

    void RunDetection()
    {
        RefreshReadback();
        FillDetectorInput();

        using Tensor<float> input = new Tensor<float>(new TensorShape(1, 3, DetSize, DetSize), detInputData);
        detector.Schedule(input);

        // Outputs: scores[0..2], boxes[3..5], keypoints[6..8], one group per stride
        float[][] raw = new float[NumOutputs][];
        for (int i = 0; i < NumOutputs; i++)
        {
            Tensor<float> t = detector.PeekOutput(i) as Tensor<float>;
            raw[i] = t.DownloadToArray();
        }

        latestFaces.Clear();
        latestFaces.AddRange(DecodeScrfd(raw));
    }

    // Converts the readback into a CHW tensor with top-left origin and SCRFD normalization
    void FillDetectorInput()
    {
        Color32[] px = detReadback.GetPixels32();
        int plane = DetSize * DetSize;

        for (int y = 0; y < DetSize; y++)
        {
            // Texture2D rows are bottom-up, so flip to top-down
            int srcRow = (DetSize - 1 - y) * DetSize;
            int dstRow = y * DetSize;

            for (int x = 0; x < DetSize; x++)
            {
                Color32 c = px[srcRow + x];
                int idx = dstRow + x;
                detInputData[0 * plane + idx] = (c.r - 127.5f) / 128f;
                detInputData[1 * plane + idx] = (c.g - 127.5f) / 128f;
                detInputData[2 * plane + idx] = (c.b - 127.5f) / 128f;
            }
        }
    }

    List<Detection> DecodeScrfd(float[][] raw)
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

                candidates.Add(new Detection
                {
                    box = new Rect(x1, y1, x2 - x1, y2 - y1),
                    leftEye = leftEye,
                    rightEye = rightEye,
                    score = score,
                    label = null
                });
            }
        }

        return Nms(candidates);
    }

    List<Detection> Nms(List<Detection> items)
    {
        items.Sort((a, b) => b.score.CompareTo(a.score));
        var kept = new List<Detection>();

        foreach (var d in items)
        {
            bool overlaps = false;
            foreach (var k in kept)
            {
                if (IoU(d.box, k.box) > nmsIouThreshold)
                {
                    overlaps = true;
                    break;
                }
            }
            if (!overlaps) kept.Add(d);
        }
        return kept;
    }

    static float IoU(Rect a, Rect b)
    {
        float x1 = Mathf.Max(a.xMin, b.xMin);
        float y1 = Mathf.Max(a.yMin, b.yMin);
        float x2 = Mathf.Min(a.xMax, b.xMax);
        float y2 = Mathf.Min(a.yMax, b.yMax);

        float inter = Mathf.Max(0f, x2 - x1) * Mathf.Max(0f, y2 - y1);
        float union = a.width * a.height + b.width * b.height - inter;
        return union <= 0f ? 0f : inter / union;
    }

    void RunRecognition()
    {
        // The readback from RunDetection is still current for this frame
        for (int i = 0; i < latestFaces.Count; i++)
        {
            Detection face = latestFaces[i];
            if (Vector2.Distance(face.leftEye, face.rightEye) < 4f) continue;

            float[] emb = EmbedFace(face);
            face.label = Match(emb);
            latestFaces[i] = face;
        }
    }

    float[] EmbedFace(Detection face)
    {
        float[] data = AlignCrop(face);
        using Tensor<float> input = new Tensor<float>(new TensorShape(1, 3, RecSize, RecSize), data);
        embedder.Schedule(input);

        Tensor<float> output = embedder.PeekOutput() as Tensor<float>;
        float[] vec = output.DownloadToArray();
        Normalize(vec);
        return vec;
    }

    // Similarity transform from the two eyes to the canonical ArcFace template, sampled from the 640 frame
    float[] AlignCrop(Detection face)
    {
        Vector2 cv = CanonRightEye - CanonLeftEye;
        Vector2 sv = face.rightEye - face.leftEye;

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

                float px = face.leftEye.x + scale * (cos * dx - sin * dy);
                float py = face.leftEye.y + scale * (sin * dx + cos * dy);

                // py is top-down in detector space; the readback is bottom-up, so flip v
                Color c = detReadback.GetPixelBilinear(px / DetSize, 1f - py / DetSize);
                int idx = y * RecSize + x;

                // ArcFace expects RGB normalized to [-1, 1]
                data[0 * plane + idx] = c.r * 2f - 1f;
                data[1 * plane + idx] = c.g * 2f - 1f;
                data[2 * plane + idx] = c.b * 2f - 1f;
            }
        }
        return data;
    }

    static void Normalize(float[] v)
    {
        float sum = 0f;
        for (int i = 0; i < v.Length; i++) sum += v[i] * v[i];
        float inv = 1f / Mathf.Sqrt(Mathf.Max(sum, 1e-12f));
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    static float Dot(float[] a, float[] b)
    {
        float s = 0f;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    string Match(float[] emb)
    {
        string best = "unknown";
        float bestScore = matchThreshold;

        foreach (var entry in gallery)
        {
            float sim = Dot(emb, entry.Value);
            if (sim > bestScore)
            {
                bestScore = sim;
                best = entry.Key;
            }
        }
        return best;
    }

    // Call from a UI button or other script while a face is visible
    public void EnrollLargestFace(string personName)
    {
        if (latestFaces.Count == 0)
        {
            Debug.LogWarning("No face in view to enroll.");
            return;
        }

        Detection largest = latestFaces[0];
        foreach (var f in latestFaces)
        {
            if (f.box.width * f.box.height > largest.box.width * largest.box.height)
                largest = f;
        }

        RefreshReadback();
        gallery[personName] = EmbedFace(largest);
    }

    void OnGUI()
    {
        if (display == null || oriented == null || latestFaces.Count == 0) return;

        // Assumes a Screen Space - Overlay canvas, where world corners are screen pixels
        Vector3[] corners = new Vector3[4];
        ((RectTransform)transform).GetWorldCorners(corners);

        float left = corners[0].x;
        float width = corners[2].x - corners[0].x;
        float height = corners[2].y - corners[0].y;
        float top = Screen.height - corners[2].y;

        foreach (var face in latestFaces)
        {
            Rect r = new Rect(
                left + (face.box.xMin / DetSize) * width,
                top + (face.box.yMin / DetSize) * height,
                (face.box.width / DetSize) * width,
                (face.box.height / DetSize) * height);

            GUI.Box(r, $"{face.label ?? "face"} {face.score:0.00}");
        }
    }
}