// Role: barber workstation example. Designates a seated client, reads and smooths head pose, gates cutting tools,
// and draws feedback: a tilting top hat, a wobble meter, a pulsing warning vignette, a banner, and screen shake
// when the client moves too much. Drawing uses OnGUI, like the pipeline overlay, so no extra Canvas is needed.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AR.Vision
{
    public class BarberStation : MonoBehaviour
    {
        public enum CutState
        {
            NoClient,      // no client in the seat
            Reposition,    // head is outside yaw, pitch or roll limits
            Settling,      // within limits but still moving
            SafeToCut      // within limits and still for settleSeconds
        }

        [SerializeField] private VisionPipeline vision;
        [SerializeField] private float grabRadius = 0.08f;   // normalized frame units

        [Header("Client")]
        [Tooltip("Normalized frame area. A face whose center sits inside this rect becomes the client.")]
        [SerializeField] private Rect seatZone = new Rect(0.2f, 0.05f, 0.6f, 0.9f);
        [Tooltip("Frames the client can be missing before the seat is released.")]
        [SerializeField] private int framesBeforeClientRelease = 15;

        [Header("Head guidance")]
        [SerializeField] private float maxYaw = 20f;          // degrees
        [SerializeField] private float maxPitch = 15f;        // degrees
        [SerializeField] private float maxRoll = 10f;         // degrees
        [SerializeField] private float stillSpeed = 8f;       // degrees per second
        [SerializeField] private float settleSeconds = 0.6f;
        [SerializeField, Range(0.05f, 1f)] private float smoothing = 0.35f;
        [Tooltip("Deviation above which the client counts as wiggling. 1 is exactly at a limit.")]
        [SerializeField] private float chaosDeviation = 1.8f;

        [Header("Tools")]
        [SerializeField] private string[] cuttingLabels = { "scissors", "knife", "razor" };

        [Header("Visuals")]
        [SerializeField] private bool showVisuals = true;
        [SerializeField] private bool showHat = true;
        [SerializeField] private bool showWobbleMeter = true;
        [SerializeField] private float shakeMaxPixels = 14f;
        [SerializeField] private Color idleColor = new Color(0.75f, 0.75f, 0.8f);
        [SerializeField] private Color safeColor = new Color(0.2f, 1f, 0.45f);
        [SerializeField] private Color warnColor = new Color(1f, 0.85f, 0.1f);
        [SerializeField] private Color chaosColor = new Color(1f, 0.15f, 0.35f);
        [SerializeField] private Color hatColor = new Color(0.12f, 0.05f, 0.25f);

        public CutState State { get; private set; } = CutState.NoClient;
        public string Guidance { get; private set; } = "Seat a client to begin";
        public HeadAngles SmoothedHead { get; private set; }
        public bool HasHead { get; private set; }
        public bool HasClient { get; private set; }
        public Observation Client { get; private set; }     // valid only when HasClient is true
        public float Deviation { get; private set; }        // 0 centered, 1 at a limit
        public bool InChaos { get; private set; }

        public event Action<CutState> StateChanged;

        private readonly Dictionary<int, Observation> tools = new Dictionary<int, Observation>();
        private readonly Dictionary<int, bool> pinching = new Dictionary<int, bool>();

        private Texture2D pixel;
        private GUIStyle bannerStyle;
        private GUIStyle smallStyle;

        private float frameAspect = 1f;
        private float stillTimer;
        private int activeCutterId = -1;

        private int clientId = -1;
        private int clientMisses;
        private float flash;
        private Vector2 shakeOffset;

        void Awake()
        {
            pixel = new Texture2D(1, 1);
            pixel.SetPixel(0, 0, Color.white);
            pixel.Apply();
        }

        void OnEnable()
        {
            vision.ObservationAppeared += OnAppeared;
            vision.ObservationUpdated += OnUpdated;
            vision.ObservationLost += OnLost;
        }

        void OnDisable()
        {
            vision.ObservationAppeared -= OnAppeared;
            vision.ObservationUpdated -= OnUpdated;
            vision.ObservationLost -= OnLost;
        }

        void OnDestroy()
        {
            if (pixel != null) Destroy(pixel);
        }

        void Update()
        {
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            flash = Mathf.MoveTowards(flash, 0f, dt * 1.5f);

            if (vision == null || !vision.IsReady()) return;
            frameAspect = vision.FrameAspect;

            UpdateClient();

            if (!HasClient)
            {
                ResetClient();
                SetState(CutState.NoClient, "Seat a client to begin");
                return;
            }

            if (!HeadPose.TryEstimate(Client, frameAspect, out HeadAngles raw))
            {
                shakeOffset = Vector2.zero;
                SetState(CutState.Reposition, "Face not clear, ask the client to face the camera");
                return;
            }

            HeadAngles previous = SmoothedHead;
            if (!HasHead)
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
            float speed = AngularDistance(previous, h) / dt;

            // Deviation is the worst axis as a fraction of its limit. It drives color, shake and chaos mode.
            Deviation = Mathf.Max(
                Mathf.Abs(h.Yaw) / maxYaw,
                Mathf.Abs(h.Pitch) / maxPitch,
                Mathf.Abs(h.Roll) / maxRoll);
            InChaos = Deviation > chaosDeviation;

            float shakeAmount = Mathf.Clamp01((Deviation - 1f) * 1.5f);
            shakeOffset = UnityEngine.Random.insideUnitCircle * (shakeMaxPixels * shakeAmount);

            if (Mathf.Abs(h.Yaw) > maxYaw)
            {
                stillTimer = 0f;
                SetState(CutState.Reposition, h.Yaw > 0f
                    ? "Ask the client to turn a little to the left"
                    : "Ask the client to turn a little to the right");
                return;
            }

            if (Mathf.Abs(h.Roll) > maxRoll)
            {
                stillTimer = 0f;
                SetState(CutState.Reposition, h.Roll > 0f
                    ? "Level the head, the client is tilting right"
                    : "Level the head, the client is tilting left");
                return;
            }

            if (Mathf.Abs(h.Pitch) > maxPitch)
            {
                stillTimer = 0f;
                SetState(CutState.Reposition, h.Pitch > 0f
                    ? "Ask the client to lift their chin"
                    : "Ask the client to lower their chin");
                return;
            }

            if (speed > stillSpeed)
            {
                stillTimer = 0f;
                SetState(CutState.Settling, "Hold still");
                return;
            }

            stillTimer += dt;
            if (stillTimer >= settleSeconds)
                SetState(CutState.SafeToCut, "Safe to cut");
            else
                SetState(CutState.Settling, "Hold still");
        }

        // Holds the designated client through brief dropouts, then releases the seat.
        // A new client is picked only from faces whose center lies in seatZone.
        private void UpdateClient()
        {
            if (clientId >= 0)
            {
                if (TryFindFace(clientId, out Observation found))
                {
                    Client = found;
                    HasClient = true;
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
            foreach (Observation o in vision.Observations)
            {
                if (o.Kind != ObservationKind.Face) continue;
                if (!seatZone.Contains(o.Center)) continue;

                float area = o.NormalizedRect.width * o.NormalizedRect.height;
                if (area > bestArea)
                {
                    bestArea = area;
                    clientId = o.TrackId;
                    Client = o;
                    HasClient = true;
                }
            }
        }

        private bool TryFindFace(int trackId, out Observation face)
        {
            foreach (Observation o in vision.Observations)
            {
                if (o.Kind == ObservationKind.Face && o.TrackId == trackId)
                {
                    face = o;
                    return true;
                }
            }
            face = default;
            return false;
        }

        private void OnAppeared(Observation o)
        {
            if (o.Kind == ObservationKind.Object) tools[o.TrackId] = o;
        }

        private void OnUpdated(Observation o)
        {
            if (o.Kind == ObservationKind.Object)
            {
                tools[o.TrackId] = o;
                return;
            }

            if (o.Kind != ObservationKind.Hand) return;

            bool pinchNow = HandGestures.IsPinching(o);
            pinching.TryGetValue(o.TrackId, out bool wasPinching);
            pinching[o.TrackId] = pinchNow;

            if (!pinchNow || wasPinching) return;
            if (!TryFindToolNear(o.Center, out Observation tool)) return;

            if (IsCuttingTool(tool))
            {
                if (State != CutState.SafeToCut)
                {
                    Guidance = "Wait until the client is still before using " + ToolName(tool);
                    flash = Mathf.Max(flash, 0.5f);
                    return;
                }
                activeCutterId = tool.TrackId;
            }

            Debug.Log($"Barber station: picked up {ToolName(tool)}");
        }

        private void OnLost(ObservationKind kind, int trackId)
        {
            tools.Remove(trackId);
            pinching.Remove(trackId);
            if (trackId == activeCutterId) activeCutterId = -1;
        }

        private bool TryFindToolNear(Vector2 point, out Observation tool)
        {
            float best = grabRadius;
            bool found = false;
            tool = default;

            foreach (var pair in tools)
            {
                float d = Vector2.Distance(pair.Value.Center, point);
                if (d < best)
                {
                    best = d;
                    tool = pair.Value;
                    found = true;
                }
            }
            return found;
        }

        private bool IsCuttingTool(Observation tool)
        {
            string label = tool.Profile is ItemProfile item ? item.DetectorLabel : tool.Label;
            if (string.IsNullOrEmpty(label)) return false;

            foreach (string c in cuttingLabels)
                if (string.Equals(c, label, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ToolName(Observation tool)
        {
            return tool.Profile != null ? tool.Profile.DisplayName : tool.Label;
        }

        private static float AngularDistance(HeadAngles a, HeadAngles b)
        {
            float dy = b.Yaw - a.Yaw;
            float dp = b.Pitch - a.Pitch;
            float dr = b.Roll - a.Roll;
            return Mathf.Sqrt(dy * dy + dp * dp + dr * dr);
        }

        private void ResetClient()
        {
            HasHead = false;
            stillTimer = 0f;
            activeCutterId = -1;
            Deviation = 0f;
            InChaos = false;
            shakeOffset = Vector2.zero;
        }

        private void SetState(CutState next, string message)
        {
            Guidance = message;
            if (next == State) return;

            if (next == CutState.Reposition) flash = 1f;
            State = next;
            StateChanged?.Invoke(next);
        }

        // ---------------------------------------------------------------------
        // Visual output
        // ---------------------------------------------------------------------

        void OnGUI()
        {
            if (!showVisuals || vision == null || !vision.IsReady()) return;

            EnsureStyles();

            Matrix4x4 savedMatrix = GUI.matrix;
            Color savedColor = GUI.color;

            // Screen shake applies to everything drawn below.
            GUI.matrix = Matrix4x4.Translate(shakeOffset);

            if (showHat && HasClient && HasHead) DrawHat();
            DrawVignette();
            if (showWobbleMeter && HasHead) DrawWobbleMeter();
            DrawBanner();

            GUI.matrix = savedMatrix;
            GUI.color = savedColor;
        }

        // A top hat that sits on the client's head. Roll tilts it, yaw narrows it as the head turns,
        // and pitch nudges it up or down.
        private void DrawHat()
        {
            Rect face = ToScreen(Client.NormalizedRect);
            float w = face.width;

            float turn = Mathf.Clamp(Mathf.Cos(SmoothedHead.Yaw * Mathf.Deg2Rad), 0.35f, 1f);
            float brimW = w * 1.25f * turn;
            float crownW = w * 0.7f * turn;
            float brimH = Mathf.Max(6f, w * 0.12f);
            float crownH = w * 0.55f;
            float bandH = brimH * 0.8f;

            Vector2 pivot = new Vector2(
                face.center.x,
                face.yMin - w * 0.05f + SmoothedHead.Pitch * 0.5f);

            Matrix4x4 saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(SmoothedHead.Roll, pivot);

            Rect brim = new Rect(pivot.x - brimW * 0.5f, pivot.y - brimH, brimW, brimH);
            Rect crown = new Rect(pivot.x - crownW * 0.5f, brim.y - crownH, crownW, crownH);
            Rect band = new Rect(crown.x, brim.y - bandH, crownW, bandH);

            GUI.color = hatColor;
            GUI.DrawTexture(crown, pixel);
            GUI.DrawTexture(brim, pixel);

            GUI.color = StateColor();
            GUI.DrawTexture(band, pixel);

            GUI.matrix = saved;
        }

        // Red or yellow edges that pulse when the client moves. Strength follows the flash timer and deviation.
        private void DrawVignette()
        {
            float deviationPart = State == CutState.Reposition
                ? Mathf.Clamp01((Deviation - 1f) * 0.6f)
                : 0f;
            float intensity = Mathf.Clamp01(flash * 0.7f + deviationPart);
            if (intensity <= 0.01f) return;

            float pulse = InChaos ? 0.8f + 0.2f * Mathf.Sin(Time.time * 14f) : 1f;
            Color c = InChaos ? chaosColor : warnColor;
            c.a = 0.6f * intensity * pulse;
            GUI.color = c;

            float t = 20f + 70f * intensity;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, t), pixel);
            GUI.DrawTexture(new Rect(0, Screen.height - t, Screen.width, t), pixel);
            GUI.DrawTexture(new Rect(0, 0, t, Screen.height), pixel);
            GUI.DrawTexture(new Rect(Screen.width - t, 0, t, Screen.height), pixel);
        }

        // Horizontal meter. The green zone is the allowed yaw range. The needle is the client's smoothed yaw.
        private void DrawWobbleMeter()
        {
            float barW = 340f;
            float barH = 22f;
            Rect bar = new Rect(Screen.width * 0.5f - barW * 0.5f, Screen.height - 70f, barW, barH);

            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(bar, pixel);

            float zoneLeft = ToBar(bar, -maxYaw);
            float zoneRight = ToBar(bar, maxYaw);
            GUI.color = new Color(safeColor.r, safeColor.g, safeColor.b, 0.5f);
            GUI.DrawTexture(new Rect(zoneLeft, bar.y, zoneRight - zoneLeft, barH), pixel);

            float needleX = ToBar(bar, SmoothedHead.Yaw);
            GUI.color = StateColor();
            GUI.DrawTexture(new Rect(needleX - 3f, bar.y - 6f, 6f, barH + 12f), pixel);

            string label = $"HEAD WOBBLE  yaw {SmoothedHead.Yaw:+0;-0;0} deg";
            DrawShadowedLabel(new Rect(bar.x, bar.y - 28f, barW, 24f), label, smallStyle, Color.white);
        }

        // Maps a yaw value to an x position on the wobble meter, which spans twice the yaw limit each way.
        private float ToBar(Rect bar, float yaw)
        {
            float clamped = Mathf.Clamp(yaw, -2f * maxYaw, 2f * maxYaw);
            float t = clamped / (4f * maxYaw) + 0.5f;
            return bar.x + t * bar.width;
        }

        // Big banner at the top with a playful line for each state, plus the guidance text below.
        private void DrawBanner()
        {
            string title;
            Color color;

            switch (State)
            {
                case CutState.NoClient:
                    title = "Take a seat, the chair is waiting";
                    color = idleColor;
                    break;

                case CutState.Reposition:
                    if (InChaos)
                    {
                        title = "CHAOS MODE!! Please hold still!";
                        color = Color.HSVToRGB(Mathf.Repeat(Time.time * 0.6f, 1f), 0.85f, 1f);
                    }
                    else
                    {
                        title = "Whoa, easy there!";
                        color = warnColor;
                    }
                    break;

                case CutState.Settling:
                    title = "Hold still...";
                    color = warnColor;
                    break;

                default:
                    title = "Looking sharp! Cut away.";
                    color = safeColor;
                    break;
            }

            DrawShadowedLabel(new Rect(0, 20f, Screen.width, 60f), title, bannerStyle, color);
            DrawShadowedLabel(new Rect(0, 84f, Screen.width, 30f), Guidance, smallStyle, Color.white);
        }

        private void DrawShadowedLabel(Rect rect, string text, GUIStyle style, Color color)
        {
            Color saved = GUI.color;
            GUI.color = Color.white;

            style.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 3f, rect.y + 3f, rect.width, rect.height), text, style);

            style.normal.textColor = color;
            GUI.Label(rect, text, style);

            GUI.color = saved;
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

        private void EnsureStyles()
        {
            if (bannerStyle == null)
            {
                bannerStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 44,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter
                };
            }
            if (smallStyle == null)
            {
                smallStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter
                };
            }
        }

        // Converts a normalized frame rect to screen pixels, using the same mapping as VisionPipeline.OnGUI.
        private Rect ToScreen(Rect n)
        {
            Vector3[] corners = new Vector3[4];
            ((RectTransform)vision.transform).GetWorldCorners(corners);

            float left = corners[0].x;
            float width = corners[2].x - corners[0].x;
            float height = corners[2].y - corners[0].y;
            float top = Screen.height - corners[2].y;

            return new Rect(
                left + n.xMin * width,
                top + n.yMin * height,
                n.width * width,
                n.height * height);
        }
    }
}