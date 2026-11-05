using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AR.Vision
{
    /// <summary>
    /// ML-powered kids' barber station.
    /// Uses face/head-pose tracking to guide the seated client and hand/object tracking
    /// to register safe, toy-scissors snips. All visual output is rendered by uGUI
    /// Canvas components; this script does not use OnGUI or IMGUI.
    ///
    /// Existing project dependencies retained:
    /// VisionPipeline, Observation, ObservationKind, HeadPose, HeadAngles,
    /// HandGestures, ItemProfile and PauseOverlay.
    /// </summary>
    public class BarberStation : MonoBehaviour
    {
        public enum CutState
        {
            NoClient,
            Reposition,
            Settling,
            SafeToCut
        }

        [Header("Vision")]
        [SerializeField] private VisionPipeline vision;
        [SerializeField, Min(0.001f)] private float grabRadius = 0.08f;

        [Header("Client")]
        [Tooltip("A face is selected as the client when its centre is inside this normalized frame rectangle.")]
        [SerializeField] private Rect seatZone = new Rect(0.2f, 0.05f, 0.6f, 0.9f);
        [Tooltip("Keep the current client through this many missed vision frames.")]
        [SerializeField, Min(0)] private int framesBeforeClientRelease = 15;

        [Header("Head guidance")]
        [SerializeField, Min(1f)] private float maxYaw = 20f;
        [SerializeField, Min(1f)] private float maxPitch = 15f;
        [SerializeField, Min(1f)] private float maxRoll = 10f;
        [SerializeField, Min(0.1f)] private float stillSpeed = 8f;
        [SerializeField, Min(0.05f)] private float settleSeconds = 0.6f;
        [SerializeField, Range(0.05f, 1f)] private float smoothing = 0.35f;
        [Tooltip("Deviation above this multiple of the allowed head range activates the playful chaos warning.")]
        [SerializeField, Min(1f)] private float chaosDeviation = 1.8f;

        [Header("Detected tools")]
        [Tooltip("Only these labels count as cutting tools. Keep this to child-safe toy/hair scissors labels.")]
        [SerializeField] private string[] cuttingLabels = { "scissors", "toy scissors", "toy_scissors", "hair scissors" };

        [Header("Haircut game")]
        [SerializeField, Min(1)] private int snipsToComplete = 8;
        [SerializeField] private bool resetProgressWhenClientLeaves = true;

        [Header("Canvas UI")]
        [Tooltip("Canvas RectTransform that directly parents the hat overlay. It should cover the same area as the camera preview.")]
        [SerializeField] private RectTransform overlayRoot;
        [Tooltip("Optional root containing the hat and warning-edge images. It can be shaken independently of the HUD.")]
        [SerializeField] private RectTransform effectsRoot;
        [SerializeField] private RectTransform hatRoot;
        [SerializeField] private Image hatBrim;
        [SerializeField] private Image hatCrown;
        [SerializeField] private Image hatBand;
        [SerializeField] private Text bannerText;
        [SerializeField] private Text guidanceText;
        [SerializeField] private Text wobbleText;
        [SerializeField] private Slider yawSlider;
        [SerializeField] private Slider haircutSlider;
        [SerializeField] private Text haircutProgressText;
        [Tooltip("Assign four UI Images: top, bottom, left and right edges.")]
        [SerializeField] private Image[] vignetteEdges = new Image[4];
        [SerializeField] private GameObject visualsRoot;

        [Header("Canvas colours")]
        [SerializeField] private Color idleColor = new Color(0.75f, 0.75f, 0.82f, 1f);
        [SerializeField] private Color safeColor = new Color(0.2f, 1f, 0.45f, 1f);
        [SerializeField] private Color warnColor = new Color(1f, 0.78f, 0.12f, 1f);
        [SerializeField] private Color chaosColor = new Color(1f, 0.15f, 0.35f, 1f);
        [SerializeField] private Color hatColor = new Color(0.12f, 0.05f, 0.25f, 1f);
        [SerializeField, Min(0f)] private float shakeMaxCanvasUnits = 8f;

        public CutState State { get; private set; } = CutState.NoClient;
        public string Guidance { get; private set; } = "Seat a client to begin";
        public HeadAngles SmoothedHead { get; private set; }
        public bool HasHead { get; private set; }
        public bool HasClient { get; private set; }
        public Observation Client { get; private set; }
        public float Deviation { get; private set; }
        public bool InChaos { get; private set; }
        public int SnipsCompleted { get; private set; }
        public int SnipsRequired => Mathf.Max(1, snipsToComplete);
        public bool HaircutComplete { get; private set; }
        public string LastToolName { get; private set; } = string.Empty;

        public event Action<CutState> StateChanged;
        public event Action<string> ToolPickedUp;
        public event Action<int, int> HaircutProgressChanged;
        public event Action HaircutCompleted;

        private readonly Dictionary<int, Observation> tools = new Dictionary<int, Observation>();
        private readonly Dictionary<int, bool> pinching = new Dictionary<int, bool>();

        private float frameAspect = 1f;
        private float stillTimer;
        private int clientId = -1;
        private int clientMisses;
        private bool clientSeenThisFrame;
        private float flash;
        private Vector2 effectsBasePosition;
        private bool effectsBaseCaptured;
        private bool visionSubscribed;

        private void Awake()
        {
            ConfigureHatPieces();
            ConfigureSliders();

            if (effectsRoot != null)
            {
                effectsBasePosition = effectsRoot.anchoredPosition;
                effectsBaseCaptured = true;
            }
        }

        private void OnEnable()
        {
            TrySubscribeToVision();
        }

        private void OnDisable()
        {
            UnsubscribeFromVision();
            if (hatRoot != null) hatRoot.gameObject.SetActive(false);
            if (effectsRoot != null && effectsBaseCaptured)
                effectsRoot.anchoredPosition = effectsBasePosition;
        }

        private void Update()
        {
            if (PauseOverlay.IsPaused) return;

            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            flash = Mathf.MoveTowards(flash, 0f, dt * 1.5f);
            TrySubscribeToVision();

            if (vision == null || !vision.IsReady())
            {
                HasClient = false;
                ResetHeadTracking();
                if (resetProgressWhenClientLeaves) ResetHaircutProgress();
                SetState(CutState.NoClient, "Waiting for the camera and vision model...");
                RefreshCanvasUI();
                return;
            }

            frameAspect = vision.FrameAspect;
            UpdateClient();

            if (!HasClient)
            {
                ResetHeadTracking();
                if (resetProgressWhenClientLeaves) ResetHaircutProgress();
                SetState(CutState.NoClient, "Take a seat in the barber chair!");
                RefreshCanvasUI();
                return;
            }

            // Keep the client selected through brief detector dropouts, but never allow
            // a snip from a stale face observation. Require a fresh face each frame.
            if (!clientSeenThisFrame)
            {
                ResetHeadTracking();
                SetState(CutState.Settling, "Keep your face in the camera view");
                RefreshCanvasUI();
                return;
            }

            if (!HeadPose.TryEstimate(Client, frameAspect, out HeadAngles raw))
            {
                stillTimer = 0f;
                HasHead = false;
                SetState(CutState.Reposition, "Face the camera so I can see your hair");
                RefreshCanvasUI();
                return;
            }

            bool hadPreviousHead = HasHead;
            HeadAngles previous = SmoothedHead;
            if (!hadPreviousHead)
            {
                SmoothedHead = raw;
                HasHead = true;
            }
            else
            {
                SmoothedHead = new HeadAngles(
                    Mathf.Lerp(previous.Yaw, raw.Yaw, smoothing),
                    Mathf.Lerp(previous.Pitch, raw.Pitch, smoothing),
                    Mathf.Lerp(previous.Roll, raw.Roll, smoothing));
            }

            HeadAngles h = SmoothedHead;
            float angularSpeed = hadPreviousHead ? AngularDistance(previous, h) / dt : 0f;
            Deviation = Mathf.Max(
                Mathf.Abs(h.Yaw) / Mathf.Max(maxYaw, 1f),
                Mathf.Abs(h.Pitch) / Mathf.Max(maxPitch, 1f),
                Mathf.Abs(h.Roll) / Mathf.Max(maxRoll, 1f));
            InChaos = Deviation > chaosDeviation;

            if (Mathf.Abs(h.Yaw) > maxYaw)
            {
                stillTimer = 0f;
                SetState(CutState.Reposition, h.Yaw > 0f
                    ? "Turn your face a little to the left"
                    : "Turn your face a little to the right");
            }
            else if (Mathf.Abs(h.Roll) > maxRoll)
            {
                stillTimer = 0f;
                SetState(CutState.Reposition, h.Roll > 0f
                    ? "Try to keep your head level"
                    : "A tiny tilt the other way, please");
            }
            else if (Mathf.Abs(h.Pitch) > maxPitch)
            {
                stillTimer = 0f;
                SetState(CutState.Reposition, h.Pitch > 0f
                    ? "Lift your chin a little"
                    : "Lower your chin a little");
            }
            else if (angularSpeed > stillSpeed)
            {
                stillTimer = 0f;
                SetState(CutState.Settling, "Hold still like a superstar");
            }
            else
            {
                stillTimer += dt;
                SetState(stillTimer >= settleSeconds
                    ? CutState.SafeToCut
                    : CutState.Settling,
                    stillTimer >= settleSeconds ? "Lovely! Ready for a snip" : "Nearly ready... hold still");
            }

            RefreshCanvasUI();
        }

        /// <summary>Can be called by a UI button to clear the current haircut and start again.</summary>
        public void RestartHaircut()
        {
            ResetHaircutProgress();
            stillTimer = 0f;
            Guidance = HasClient ? "Let's start a fresh haircut!" : "Seat a client to begin";
            RefreshCanvasUI();
        }

        private void TrySubscribeToVision()
        {
            if (visionSubscribed || vision == null) return;
            vision.ObservationAppeared += OnAppeared;
            vision.ObservationUpdated += OnUpdated;
            vision.ObservationLost += OnLost;
            visionSubscribed = true;
        }

        private void UnsubscribeFromVision()
        {
            if (!visionSubscribed || vision == null) return;
            vision.ObservationAppeared -= OnAppeared;
            vision.ObservationUpdated -= OnUpdated;
            vision.ObservationLost -= OnLost;
            visionSubscribed = false;
        }

        // Retain the seated client through brief detection dropouts. New clients are only
        // selected when their face centre is inside seatZone.
        private void UpdateClient()
        {
            clientSeenThisFrame = false;
            int previousClientId = clientId;

            if (clientId >= 0)
            {
                if (TryFindFace(clientId, out Observation found))
                {
                    Client = found;
                    HasClient = true;
                    clientSeenThisFrame = true;
                    clientMisses = 0;
                    return;
                }

                clientMisses++;
                if (HasClient && clientMisses <= framesBeforeClientRelease) return;
                clientId = -1;
            }

            HasClient = false;
            clientMisses = 0;

            float bestArea = 0f;
            foreach (Observation observation in vision.Observations)
            {
                if (observation.Kind != ObservationKind.Face) continue;
                if (!seatZone.Contains(observation.Center)) continue;

                float area = observation.NormalizedRect.width * observation.NormalizedRect.height;
                if (area <= bestArea) continue;

                bestArea = area;
                clientId = observation.TrackId;
                Client = observation;
                HasClient = true;
                clientSeenThisFrame = true;
            }

            if (HasClient && clientId != previousClientId)
            {
                ResetHeadTracking();
                ResetHaircutProgress();
            }
        }

        private bool TryFindFace(int trackId, out Observation face)
        {
            foreach (Observation observation in vision.Observations)
            {
                if (observation.Kind == ObservationKind.Face && observation.TrackId == trackId)
                {
                    face = observation;
                    return true;
                }
            }

            face = default;
            return false;
        }

        private void OnAppeared(Observation observation)
        {
            if (observation.Kind == ObservationKind.Object)
                tools[observation.TrackId] = observation;
        }

        private void OnUpdated(Observation observation)
        {
            if (PauseOverlay.IsPaused) return;

            if (observation.Kind == ObservationKind.Object)
            {
                tools[observation.TrackId] = observation;
                return;
            }

            if (observation.Kind != ObservationKind.Hand) return;

            bool isPinching = HandGestures.IsPinching(observation);
            pinching.TryGetValue(observation.TrackId, out bool wasPinching);
            pinching[observation.TrackId] = isPinching;

            // Register only the start of a pinch, not every frame while the hand is closed.
            if (!isPinching || wasPinching) return;
            if (!TryFindToolNear(observation.Center, out Observation tool)) return;

            LastToolName = ToolName(tool);
            ToolPickedUp?.Invoke(LastToolName);

            if (!IsCuttingTool(tool))
            {
                Guidance = "You picked up " + LastToolName + ". Find the toy scissors for a snip!";
                return;
            }

            if (!HasClient || State != CutState.SafeToCut)
            {
                Guidance = "Let's wait until our customer is sitting still before snipping";
                flash = Mathf.Max(flash, 0.65f);
                return;
            }

            if (HaircutComplete) return;

            SnipsCompleted = Mathf.Min(SnipsCompleted + 1, Mathf.Max(1, snipsToComplete));
            HaircutProgressChanged?.Invoke(SnipsCompleted, Mathf.Max(1, snipsToComplete));

            if (SnipsCompleted >= Mathf.Max(1, snipsToComplete))
            {
                HaircutComplete = true;
                Guidance = "Amazing haircut! You did it!";
                HaircutCompleted?.Invoke();
            }
            else
            {
                int remaining = snipsToComplete - SnipsCompleted;
                Guidance = remaining == 1
                    ? "One more gentle snip!"
                    : "Snip! " + remaining + " more to finish the haircut";
            }
        }

        private void OnLost(ObservationKind kind, int trackId)
        {
            if (kind == ObservationKind.Object) tools.Remove(trackId);
            if (kind == ObservationKind.Hand) pinching.Remove(trackId);
        }

        private bool TryFindToolNear(Vector2 point, out Observation tool)
        {
            float bestDistance = grabRadius;
            bool found = false;
            tool = default;

            foreach (KeyValuePair<int, Observation> pair in tools)
            {
                float distance = Vector2.Distance(pair.Value.Center, point);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                tool = pair.Value;
                found = true;
            }

            return found;
        }

        private bool IsCuttingTool(Observation tool)
        {
            string label = tool.Profile is ItemProfile item ? item.DetectorLabel : tool.Label;
            if (string.IsNullOrWhiteSpace(label)) return false;

            string normalized = NormalizeLabel(label);
            foreach (string candidate in cuttingLabels)
            {
                if (string.Equals(normalized, NormalizeLabel(candidate), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string NormalizeLabel(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Replace('_', ' ').Trim();
        }

        private static string ToolName(Observation tool)
        {
            if (tool.Profile != null)
            {
                string displayName = tool.Profile.DisplayName;
                if (!string.IsNullOrWhiteSpace(displayName)) return displayName;
            }
            return string.IsNullOrWhiteSpace(tool.Label) ? "tool" : tool.Label;
        }

        private static float AngularDistance(HeadAngles a, HeadAngles b)
        {
            float yaw = b.Yaw - a.Yaw;
            float pitch = b.Pitch - a.Pitch;
            float roll = b.Roll - a.Roll;
            return Mathf.Sqrt(yaw * yaw + pitch * pitch + roll * roll);
        }

        private void ResetHeadTracking()
        {
            HasHead = false;
            stillTimer = 0f;
            Deviation = 0f;
            InChaos = false;
        }

        private void ResetHaircutProgress()
        {
            if (SnipsCompleted == 0 && !HaircutComplete) return;
            SnipsCompleted = 0;
            HaircutComplete = false;
            HaircutProgressChanged?.Invoke(0, Mathf.Max(1, snipsToComplete));
        }

        private void SetState(CutState next, string message)
        {
            Guidance = message;
            if (next == State) return;

            if (next == CutState.Reposition) flash = 1f;
            State = next;
            StateChanged?.Invoke(next);
        }

        // ------------------------------ Canvas UI ------------------------------

        private void ConfigureSliders()
        {
            if (yawSlider != null)
            {
                yawSlider.minValue = -2f;
                yawSlider.maxValue = 2f;
                yawSlider.interactable = false;
            }

            if (haircutSlider != null)
            {
                haircutSlider.minValue = 0f;
                haircutSlider.maxValue = Mathf.Max(1, snipsToComplete);
                haircutSlider.interactable = false;
            }
        }

        private void ConfigureHatPieces()
        {
            if (hatRoot != null)
            {
                hatRoot.anchorMin = new Vector2(0.5f, 0.5f);
                hatRoot.anchorMax = new Vector2(0.5f, 0.5f);
                hatRoot.pivot = new Vector2(0.5f, 0.5f);
                hatRoot.gameObject.SetActive(false);
            }

            ConfigureHatPiece(hatBrim, new Vector2(0f, 0f), new Vector2(1f, 0.24f), hatColor);
            ConfigureHatPiece(hatCrown, new Vector2(0.17f, 0.22f), new Vector2(0.83f, 1f), hatColor);
            ConfigureHatPiece(hatBand, new Vector2(0.17f, 0.23f), new Vector2(0.83f, 0.39f), safeColor);
        }

        private static void ConfigureHatPiece(Image image, Vector2 anchorMin, Vector2 anchorMax, Color color)
        {
            if (image == null) return;
            RectTransform rect = image.rectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            image.color = color;
            image.raycastTarget = false;
        }

        private void RefreshCanvasUI()
        {
            if (visualsRoot != null && visualsRoot.activeSelf != isActiveAndEnabled)
                visualsRoot.SetActive(isActiveAndEnabled);

            Color stateColor = StateColor();
            if (bannerText != null)
            {
                bannerText.text = BannerTitle();
                bannerText.color = State == CutState.Reposition && InChaos
                    ? Color.HSVToRGB(Mathf.Repeat(Time.time * 0.6f, 1f), 0.85f, 1f)
                    : stateColor;
            }

            if (guidanceText != null) guidanceText.text = Guidance;

            if (wobbleText != null)
            {
                wobbleText.text = HasHead
                    ? "HEAD WOBBLE   yaw " + SmoothedHead.Yaw.ToString("+0;-0;0") + "°"
                    : "HEAD WOBBLE   waiting for face";
                wobbleText.color = stateColor;
            }

            if (yawSlider != null)
                yawSlider.value = HasHead ? Mathf.Clamp(SmoothedHead.Yaw / Mathf.Max(1f, maxYaw), -2f, 2f) : 0f;

            if (haircutSlider != null)
            {
                haircutSlider.maxValue = Mathf.Max(1, snipsToComplete);
                haircutSlider.value = SnipsCompleted;
            }

            if (haircutProgressText != null)
            {
                haircutProgressText.text = HaircutComplete
                    ? "Haircut complete!"
                    : "Haircut: " + SnipsCompleted + " / " + Mathf.Max(1, snipsToComplete) + " snips";
            }

            UpdateHat();
            UpdateVignette();
            UpdateEffectsShake();
        }

        private string BannerTitle()
        {
            switch (State)
            {
                case CutState.NoClient: return "The barber chair is waiting!";
                case CutState.Reposition: return InChaos ? "Wiggle alert! Hold still!" : "Let's get comfy";
                case CutState.Settling: return "Almost ready...";
                default: return HaircutComplete ? "Fabulous new look!" : "Looking sharp! Ready to snip";
            }
        }

        private Color StateColor()
        {
            switch (State)
            {
                case CutState.SafeToCut: return safeColor;
                case CutState.Reposition: return InChaos ? chaosColor : warnColor;
                case CutState.Settling: return warnColor;
                default: return idleColor;
            }
        }

        private void UpdateHat()
        {
            if (hatRoot == null) return;

            bool visible = HasClient && HasHead && overlayRoot != null && vision != null;
            if (hatRoot.gameObject.activeSelf != visible)
                hatRoot.gameObject.SetActive(visible);
            if (!visible) return;

            RectTransform frameRect = vision.transform as RectTransform;
            if (frameRect == null) return;

            Vector3[] corners = new Vector3[4];
            frameRect.GetWorldCorners(corners);

            // Unity corner order: bottom-left, top-left, top-right, bottom-right.
            Rect face = Client.NormalizedRect;
            Vector2 topLeft = NormalizedToOverlayLocal(corners, face.xMin, face.yMin);
            Vector2 topRight = NormalizedToOverlayLocal(corners, face.xMax, face.yMin);
            Vector2 faceTopCentre = (topLeft + topRight) * 0.5f;
            float faceWidth = Vector2.Distance(topLeft, topRight);

            hatRoot.anchoredPosition = faceTopCentre + new Vector2(0f, faceWidth * 0.12f);
            hatRoot.sizeDelta = new Vector2(faceWidth * 1.28f, faceWidth * 0.72f);
            hatRoot.localRotation = Quaternion.Euler(0f, 0f, -SmoothedHead.Roll);

            if (hatBand != null) hatBand.color = StateColor();
        }

        private Vector2 NormalizedToOverlayLocal(Vector3[] corners, float x, float yFromTop)
        {
            Vector3 top = Vector3.Lerp(corners[1], corners[2], x);
            Vector3 bottom = Vector3.Lerp(corners[0], corners[3], x);
            Vector3 world = Vector3.Lerp(top, bottom, yFromTop);
            return overlayRoot.InverseTransformPoint(world);
        }

        private void UpdateVignette()
        {
            float deviationPart = State == CutState.Reposition
                ? Mathf.Clamp01((Deviation - 1f) * 0.6f)
                : 0f;
            float intensity = Mathf.Clamp01(flash * 0.7f + deviationPart);
            float pulse = InChaos ? 0.8f + 0.2f * Mathf.Sin(Time.time * 14f) : 1f;
            Color warningColor = InChaos ? chaosColor : warnColor;
            warningColor.a = 0.6f * intensity * pulse;

            if (vignetteEdges == null) return;
            foreach (Image edge in vignetteEdges)
            {
                if (edge == null) continue;
                edge.color = warningColor;
                edge.raycastTarget = false;
            }
        }

        private void UpdateEffectsShake()
        {
            if (effectsRoot == null || !effectsBaseCaptured) return;
            float amount = Mathf.Clamp01((Deviation - 1f) * 1.5f);
            Vector2 offset = UnityEngine.Random.insideUnitCircle * (shakeMaxCanvasUnits * amount);
            effectsRoot.anchoredPosition = effectsBasePosition + offset;
        }
    }
}
