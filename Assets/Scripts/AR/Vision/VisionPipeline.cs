// Role: game-facing facade. Owns the webcam, orientation, the single frame readback,
// module slots, per-kind trackers and the public events. Game code never sees tensors.
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AR.Vision
{
    [RequireComponent(typeof(RawImage))]
    public class VisionPipeline : MonoBehaviour
    {
        [Header("Webcam")]
        [SerializeField] private int requestedWidth = 1920;
        [SerializeField] private int requestedHeight = 1080;
        [SerializeField] private int requestedFps = 30;

        [Header("Models")]
        [SerializeField] private ModelSettings models = new ModelSettings();

        [Header("Modules")]
        [SerializeField] private bool enableFaces = true;
        [SerializeField] private bool enableHands = false;
        [SerializeField] private bool enableObjects = false;

        [Header("Tracking")]
        [SerializeField] private float trackIouThreshold = 0.3f;
        [SerializeField] private int maxMissedFrames = 5;

        [Header("Profiles")]
        [SerializeField] private TrackableRegistry registry;

        [Header("Debug")]
        [SerializeField] private bool drawOverlay = true;

        // Frame size used by every module: SCRFD runs on the full 640x640 frame.
        private const int FrameSize = ScrfdDetector.DetSize;

        public event Action<Observation> ObservationAppeared;
        public event Action<Observation> ObservationUpdated;
        public event Action<ObservationKind, int> ObservationLost;
        public event Action<Observation, TrackableProfile> IdentityChanged;

        // Observations from the most recent completed frame.
        public IReadOnlyList<Observation> Observations => current;

        private sealed class Slot
        {
            public VisionModule Module;
            public ObservationTracker Tracker;
        }

        private readonly List<Slot> slots = new List<Slot>();
        private readonly List<Observation> current = new List<Observation>();
        private readonly VisionFrame frame = new VisionFrame();
        private ObservationTracker faceTracker;
        private FaceModule faceModule;
        private ProfileResolver resolver;

        private WebCamTexture webcam;
        private RawImage display;
        private AspectRatioFitter fitter;
        private Material orientMat;
        private RenderTexture oriented;     // upright, unmirrored full frame
        private RenderTexture detInput;     // 640x640 copy of the frame for the modules
        private Texture2D detReadback;      // CPU copy of detInput (bottom-up, Unity convention)
        private Coroutine startRoutine;
        private int frameCount;


	public float FrameAspect => oriented != null ? oriented.width / (float)oriented.height : 1f;

      

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

            detInput = new RenderTexture(FrameSize, FrameSize, 0, RenderTextureFormat.ARGB32);
            detReadback = new Texture2D(FrameSize, FrameSize, TextureFormat.RGB24, false);

            resolver = ResolveProfile;
            BuildModules();
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
            for (int i = 0; i < slots.Count; i++) slots[i].Module.Dispose();
            slots.Clear();

            if (oriented != null) oriented.Release();
            if (detInput != null) detInput.Release();
            if (detReadback != null) Destroy(detReadback);
        }

        void BuildModules()
        {
            if (enableFaces)
            {
                faceModule = new FaceModule(models.face);
                faceTracker = AddSlot(faceModule);
            }
            if (enableHands) AddSlot(new HandModule(models.hand));
            if (enableObjects) AddSlot(new ObjectModule(models.objects));
        }

        ObservationTracker AddSlot(VisionModule module)
        {
            var tracker = new ObservationTracker(module.Kind, trackIouThreshold, maxMissedFrames);
            slots.Add(new Slot { Module = module, Tracker = tracker });
            return tracker;
        }

        public bool IsReady()
        {
            return webcam != null && webcam.isPlaying && oriented != null && detReadback != null;
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
                oriented = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            }

            orientMat.SetFloat("_Angle", angle);
            orientMat.SetFloat("_FlipY", flip ? 1f : 0f);


            //display.texture = oriented;
            display.uvRect = new Rect(0f, 0f, 1f, 1f);
            display.rectTransform.localEulerAngles = Vector3.zero;
            fitter.aspectRatio = (float)w / h;
        }

        void Update()
        {
            current.Clear();
            if (!IsReady() || !webcam.didUpdateThisFrame) return;

            // Upright frame for display, then a square 640x640 copy for the modules
            Graphics.Blit(webcam, oriented, orientMat);
            Graphics.Blit(oriented, detInput);
            RefreshReadback();

            // Single flip of the frame happens inside CopyFrom. Everything after reads top-down.
            frame.CopyFrom(detReadback);
            frameCount++;

            for (int s = 0; s < slots.Count; s++)
            {
                Slot slot = slots[s];
                if (!slot.Module.IsReady) continue;

                List<Detection> detections = slot.Module.Detect(frame);
                bool recognitionDue = slot.Module.RecognitionDue(frameCount);

                // Tracking update: matching, lifecycle, recognition hook, then profile mapping.
                slot.Tracker.Update(
                    detections,
                    resolver,
                    recognitionDue,
                    tracks => slot.Module.Recognize(frame, tracks));

                PublishEvents(slot.Tracker);
            }
        }

        // Copies the detector input into the readback texture. Unity leaves it bottom-up;
        // VisionFrame.CopyFrom is the one place that turns it top-down.
        void RefreshReadback()
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = detInput;
            detReadback.ReadPixels(new Rect(0, 0, FrameSize, FrameSize), 0, 0);
            detReadback.Apply();
            RenderTexture.active = previous;
        }

        // Profile mapping. Called once per active track per frame from ObservationTracker.Update.
        // The tracker is the only caller; ToPublic() copies the result and never computes it.
        private UnityEngine.Object ResolveProfile(ObservationKind kind, string label, bool identified)
        {
            if (registry == null || !identified || string.IsNullOrEmpty(label)) return null;

            switch (kind)
            {
                case ObservationKind.Face:
                    return registry.FindCharacterByName(label);
                case ObservationKind.Object:
                    return registry.FindItemByDetectorLabel(label);
                default:
                    return null;
            }
        }

        private void PublishEvents(ObservationTracker tracker)
        {
            IReadOnlyList<TrackedObservation> lost = tracker.Lost;
            for (int i = 0; i < lost.Count; i++)
                ObservationLost?.Invoke(tracker.Kind, lost[i].TrackId);

            IReadOnlyList<TrackedObservation> appeared = tracker.Appeared;
            for (int i = 0; i < appeared.Count; i++)
                ObservationAppeared?.Invoke(ToPublic(appeared[i]));

            IReadOnlyList<TrackedObservation> active = tracker.Active;
            for (int i = 0; i < active.Count; i++)
            {
                TrackedObservation tr = active[i];
                Observation obs = ToPublic(tr);
                current.Add(obs);

                if (tr.IdentityChangedThisFrame)
                    IdentityChanged?.Invoke(obs, obs.Profile);

                ObservationUpdated?.Invoke(obs);
            }
        }

        private static Observation ToPublic(TrackedObservation t)
        {
            return new Observation(
                t.TrackId,
                t.Kind,
                t.Label,
                t.ProfileObject as TrackableProfile,
                t.Confidence,
                t.MatchScore,
                t.NormalizedRect,
                t.Keypoints,
                t.FramesTracked,
                t.Identified);
        }

        // Call from a UI button or other script while a face is visible.
        public void EnrollLargestFace(string personName)
        {
            if (faceModule == null || !faceModule.IsReady || faceTracker == null)
            {
                Debug.LogWarning("Face module is not ready.");
                return;
            }

            IReadOnlyList<TrackedObservation> faces = faceTracker.Active;
            if (faces.Count == 0)
            {
                Debug.LogWarning("No face in view to enroll.");
                return;
            }

            TrackedObservation largest = faces[0];
            for (int i = 1; i < faces.Count; i++)
            {
                Rect a = faces[i].NormalizedRect;
                Rect b = largest.NormalizedRect;
                if (a.width * a.height > b.width * b.height) largest = faces[i];
            }

            faceModule.Enroll(personName, frame, largest);
        }

        void OnGUI()
        {
            if (!drawOverlay || display == null || oriented == null || current.Count == 0) return;

            // Assumes a Screen Space - Overlay canvas, where world corners are screen pixels
            Vector3[] corners = new Vector3[4];
            ((RectTransform)transform).GetWorldCorners(corners);

            float left = corners[0].x;
            float width = corners[2].x - corners[0].x;
            float height = corners[2].y - corners[0].y;
            float top = Screen.height - corners[2].y;

            for (int i = 0; i < current.Count; i++)
            {
                Observation o = current[i];
                Rect n = o.NormalizedRect;
                Rect r = new Rect(
                    left + n.xMin * width,
                    top + n.yMin * height,
                    n.width * width,
                    n.height * height);

                GUI.Box(r, $"{o.Label ?? o.Kind.ToString()} {o.Confidence:0.00}");
            }
        }
    }
}
