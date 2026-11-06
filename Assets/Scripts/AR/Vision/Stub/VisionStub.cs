using System.Collections.Generic;
using UnityEngine;
using Stacking;

namespace AR.Vision
{
    /// <summary>
    /// Demo driver + stand-in for VisionPipeline.
    ///
    /// Two jobs:
    ///   1. Emit synthetic Observation records so the debug overlay, the
    ///      association component and any HUD still have something to render.
    ///   2. Drive the game loop DIRECTLY on a fixed script (directDrive = true),
    ///      bypassing the pipeline's downstream adapters. For a demo the
    ///      adapters' geometric checks are too brittle to satisfy reliably
    ///      from synthetic keypoints.
    ///
    /// What the stub calls directly (directDrive):
    ///   - StackingDirector.BeginRound()           once at startup
    ///   - StackGame.SpawnBlock / StopBlock        to place / commit blocks
    ///   - BlockSweepController.Steer / Release    to steer the moving block
    ///   - StackGame.TowerRoot.localRotation       for the head-tilt demo
    ///     (so the tilt is visible even when the pipeline's HeadPose path
    ///      refuses to accept synthetic face keypoints)
    ///
    /// Tilt demo (child head roll):
    ///   The child face rolls left/right on a sine wave (childRollAmplitude).
    ///   The stub mirrors that roll onto the tower so the player sees the
    ///   head-tilt experience end to end. When directDrive is OFF, tilt is
    ///   owned entirely by StackingDirector via the pipeline's HeadTiltSignal.
    ///
    /// Controls:
    ///   [P]     toggle Parent presence
    ///   [Space] pause / resume the demo script
    ///   [R]     restart the demo script from t = 0
    ///   [A]     toggle direct drive vs. pipeline-only
    ///   [T]     toggle the tilt mirror (on by default)
    /// </summary>
    public class VisionStub : VisionPipeline
    {
        [Header("Simulation")]
        [SerializeField] private bool paused = false;
        [SerializeField, Min(0.1f)] private float timeScale = 1f;

        [Header("Participants")]
        [Tooltip("If false, the Parent face and hands are never emitted and " +
                 "the demo script never steers. The demo still plays.")]
        [SerializeField] private bool parentEnabled = true;

        [Header("Direct drive (demo bypass)")]
        [SerializeField] private bool directDrive = true;
        [SerializeField, Min(1f)] private float autoPlaceInterval = 4f;
        [SerializeField, Min(0.5f)] private float autoSteerInterval = 2f;
        [SerializeField, Min(0.1f)] private float autoSteerHold = 0.6f;

        [Header("Faces (normalized 0..1, top-left origin)")]
        [SerializeField] private Vector2 childFaceCentre = new Vector2(0.28f, 0.34f);
        [SerializeField] private Vector2 barberFaceCentre = new Vector2(0.72f, 0.34f);
        [SerializeField] private Vector2 parentFaceCentre = new Vector2(0.50f, 0.80f);
        [SerializeField, Range(0.05f, 0.4f)] private float faceSize = 0.18f;
        [SerializeField] private float faceBobAmplitude = 0.008f;
        [SerializeField] private float faceBobSpeed = 0.7f;

        [Header("Child head roll (tilt demo)")]
        [Tooltip("Peak roll of the child's head, in degrees. The tower " +
                 "mirrors this when tiltMirror is on.")]
        [SerializeField] private float childRollAmplitude = 20f;
        [SerializeField, Min(0.5f)] private float childRollPeriod = 6f;
        [Tooltip("How quickly the mirrored tower rotation chases the head " +
                 "roll. Matches StackingDirector.tiltLerpSpeed by default.")]
        [SerializeField, Min(0.1f)] private float tiltLerpSpeed = 6f;
        [Tooltip("Mirror the child's head roll onto TowerRoot when " +
                 "directDrive is on. Turn off if StackingDirector is " +
                 "already driving tilt via HeadPose.")]
        [SerializeField] private bool tiltMirror = true;

        [Header("Child hands")]
        [SerializeField] private float childHandDrop = 0.12f;
        [SerializeField] private float childHandSpread = 0.10f;
        [SerializeField] private float childRaiseHeight = 0.14f;

        [Header("Barber hands")]
        [SerializeField] private float barberHandDrop = 0.10f;
        [SerializeField] private float barberHandSpread = 0.12f;
        [SerializeField] private float barberApproachDist = 0.14f;
        [SerializeField] private float barberCyclePeriod = 7.0f;

        [Header("Parent hands")]
        [SerializeField] private float parentHandDrop = 0.12f;
        [SerializeField] private float parentHandSpread = 0.16f;
        [SerializeField] private float parentRaiseHeight = 0.16f;

        protected override bool WantsWebcam => false;

        public override bool IsReady() => true;
        public override float FrameAspect => 16f / 9f;
        public override IReadOnlyList<Observation> Observations => currentList;

        private readonly List<Observation> currentList = new List<Observation>();
        private readonly HashSet<int> lastIds = new HashSet<int>();
        private float t;

        private const int ChildFaceId = 1;
        private const int BarberFaceId = 2;
        private const int ParentFaceId = 3;

        private const int ChildLHandId = 12;
        private const int ChildRHandId = 13;
        private const int BarberLHandId = 10;
        private const int BarberRHandId = 11;
        private const int ParentLHandId = 20;
        private const int ParentRHandId = 21;

        private StackingDirector director;
        private StackGame stackGame;
        private BlockSweepController sweep;

        private bool armed;
        private float nextAutoPlace;
        private float nextAutoSteer;
        private float steerReleaseAt = -1f;
        private CraneSignal.Hand nextSteerHand = CraneSignal.Hand.Left;

        private float currentTilt;

        public bool ParentEnabled => parentEnabled;
        public bool Paused => paused;
        public bool DirectDrive => directDrive;
        public bool TiltMirror => tiltMirror;
        public float CurrentTilt => currentTilt;

        public string Caption { get; private set; } = "";
        public string StepName { get; private set; } = "simulating 3 participants";

        private void Awake()
        {
            Caption = "[P] parent  [Space] pause  [R] reset  [A] drive  [T] tilt";
            CacheRefs();
        }

        private void CacheRefs()
        {
            if (director == null) director = FindObjectOfType<StackingDirector>();
            if (stackGame == null) stackGame = FindObjectOfType<StackGame>();
            if (sweep == null) sweep = FindObjectOfType<BlockSweepController>();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.P)) parentEnabled = !parentEnabled;
            if (Input.GetKeyDown(KeyCode.Space)) paused = !paused;
            if (Input.GetKeyDown(KeyCode.R))
            {
                t = 0f;
                lastIds.Clear();
                armed = false;
                currentTilt = 0f;
                nextAutoPlace = 0f;
                nextAutoSteer = 0f;
                steerReleaseAt = -1f;
            }
            if (Input.GetKeyDown(KeyCode.A)) directDrive = !directDrive;
            if (Input.GetKeyDown(KeyCode.T)) tiltMirror = !tiltMirror;

            if (!paused) t += Time.deltaTime * timeScale;

            if (director == null || stackGame == null || sweep == null)
                CacheRefs();

            Rebuild();
            DriveDemo();
            DriveTiltMirror();
        }

        private void DriveDemo()
        {
            if (!directDrive || director == null) return;

            if (!armed)
            {
                director.BeginRound();
                armed = true;
                nextAutoPlace = t + autoPlaceInterval * 0.5f;
                nextAutoSteer = t + autoSteerInterval;
            }

            if (stackGame == null) return;
            if (stackGame.IsGameOver) return;
            if (PauseOverlay.IsPaused) return;

            if (t >= nextAutoPlace)
            {
                if (stackGame.IsMoving)
                    stackGame.StopBlock();
                else
                    stackGame.SpawnBlock();
                nextAutoPlace = t + autoPlaceInterval;
            }

            if (parentEnabled && sweep != null)
            {
                if (steerReleaseAt > 0f && t >= steerReleaseAt)
                {
                    sweep.ReleaseSteer();
                    steerReleaseAt = -1f;
                }

                if (t >= nextAutoSteer && steerReleaseAt < 0f)
                {
                    sweep.Steer(nextSteerHand);
                    steerReleaseAt = t + autoSteerHold;
                    nextSteerHand = nextSteerHand == CraneSignal.Hand.Left
                        ? CraneSignal.Hand.Right
                        : CraneSignal.Hand.Left;
                    nextAutoSteer = t + autoSteerInterval;
                }
            }
        }

        /// <summary>
        /// Mirror the child's head roll onto the tower so the tilt demo is
        /// visible even when the pipeline's HeadPose refuses to accept the
        /// synthetic face. Skipped when directDrive is off (the director
        /// owns tilt in that case) or when tiltMirror is off.
        /// </summary>
        private void DriveTiltMirror()
        {
            if (!directDrive || !tiltMirror) return;
            if (stackGame == null || stackGame.TowerRoot == null) return;
            if (PauseOverlay.IsPaused) return;

            float headRoll = ComputeChildRoll();
            currentTilt = Mathf.Lerp(currentTilt, headRoll, Time.deltaTime * tiltLerpSpeed);
            stackGame.TowerRoot.localRotation = Quaternion.Euler(0f, 0f, currentTilt);
        }

        private float ComputeChildRoll()
        {
            // Matches the roll baked into the child's synthetic face keypoints.
            return Mathf.Sin(t * (2f * Mathf.PI / Mathf.Max(0.1f, childRollPeriod))) * childRollAmplitude;
        }

        private void Rebuild()
        {
            currentList.Clear();

            float bob = Mathf.Sin(t * faceBobSpeed) * faceBobAmplitude;
            Vector2 childFace = childFaceCentre + new Vector2(0f, bob);
            Vector2 barberFace = barberFaceCentre + new Vector2(0f, -bob);
            Vector2 parentFace = parentFaceCentre + new Vector2(0f, bob * 0.5f);

            float childRoll = ComputeChildRoll();
            AddFace(ChildFaceId, childFace, childRoll);
            AddFace(BarberFaceId, barberFace, 0f);
            if (parentEnabled)
                AddFace(ParentFaceId, parentFace, 0f);

            float raiseT = (nextAutoPlace - t < autoPlaceInterval * 0.5f) ? 1f : 0f;
            Vector2 childLHome = childFace + new Vector2(-childHandSpread * 0.5f, childHandDrop);
            Vector2 childRHome = childFace + new Vector2(childHandSpread * 0.5f, childHandDrop);
            AddHand(ChildLHandId, childLHome + new Vector2(0f, -childRaiseHeight * raiseT), false);
            AddHand(ChildRHandId, childRHome + new Vector2(0f, -childRaiseHeight * raiseT), false);

            float barberPhase = (Mathf.Sin(t * (2f * Mathf.PI / Mathf.Max(0.1f, barberCyclePeriod))) + 1f) * 0.5f;
            Vector2 barberLHome = barberFace + new Vector2(-barberHandSpread * 0.5f, barberHandDrop);
            Vector2 barberRHome = barberFace + new Vector2(barberHandSpread * 0.5f, barberHandDrop);
            Vector2 barberLTarget = Vector2.Lerp(barberLHome, childFace, Mathf.Clamp01(barberApproachDist / Mathf.Max(0.01f, Vector2.Distance(barberLHome, childFace))));
            Vector2 barberRTarget = Vector2.Lerp(barberRHome, childFace, Mathf.Clamp01(barberApproachDist / Mathf.Max(0.01f, Vector2.Distance(barberRHome, childFace))));
            AddHand(BarberLHandId, Vector2.Lerp(barberLHome, barberLTarget, barberPhase), false);
            AddHand(BarberRHandId, Vector2.Lerp(barberRHome, barberRTarget, barberPhase), false);

            if (parentEnabled)
            {
                Vector2 parentLHome = parentFace + new Vector2(-parentHandSpread * 0.5f, parentHandDrop);
                Vector2 parentRHome = parentFace + new Vector2(parentHandSpread * 0.5f, parentHandDrop);

                bool leftSteering = steerReleaseAt > 0f && nextSteerHand == CraneSignal.Hand.Right;
                bool rightSteering = steerReleaseAt > 0f && nextSteerHand == CraneSignal.Hand.Left;

                AddHand(ParentLHandId, parentLHome + new Vector2(0f, -parentRaiseHeight * (leftSteering ? 1f : 0f)), false);
                AddHand(ParentRHandId, parentRHome + new Vector2(0f, -parentRaiseHeight * (rightSteering ? 1f : 0f)), false);
            }

            Caption =
                (directDrive ? "[DIRECT DRIVE] " : "[PIPELINE ONLY] ") +
                (parentEnabled ? "3 participants" : "2 participants (Parent OFF)") +
                "  tilt=" + currentTilt.ToString("0.0") + "\u00B0" +
                "  t=" + t.ToString("0.0") + "s" +
                (paused ? "  PAUSED" : "");

            PublishDiff();
        }

        private void AddFace(int id, Vector2 c, float rollDeg)
        {
            float half = faceSize * 0.5f;
            float r = rollDeg * Mathf.Deg2Rad;
            Vector2 le = c + Rot(new Vector2(-half * 0.35f, -half * 0.15f), r);
            Vector2 re = c + Rot(new Vector2(half * 0.35f, -half * 0.15f), r);
            Vector2 no = c;
            Vector2 ml = c + Rot(new Vector2(-half * 0.30f, half * 0.35f), r);
            Vector2 mr = c + Rot(new Vector2(half * 0.30f, half * 0.35f), r);
            var rect = Rect.MinMaxRect(c.x - half, c.y - half, c.x + half, c.y + half);
            currentList.Add(new Observation(id, ObservationKind.Face, "unknown", null,
                0.95f, 0f, rect, new[] { le, re, no, ml, mr }, 1, false));
        }

        private void AddHand(int id, Vector2 c, bool closed)
        {
            var rect = Rect.MinMaxRect(c.x - 0.06f, c.y - 0.06f, c.x + 0.06f, c.y + 0.06f);
            currentList.Add(new Observation(id, ObservationKind.Hand, "Left", null,
                0.95f, 0f, rect, BuildHandJoints(c, closed), 1, true));
        }

        private static Vector2[] BuildHandJoints(Vector2 wrist, bool closed)
        {
            var kp = new Vector2[21];
            kp[0] = wrist;

            float[] xoff = { -0.030f, -0.018f, -0.004f, 0.012f, 0.026f };
            float[] drift = { -0.010f, -0.005f, 0.000f, 0.005f, 0.010f };

            for (int f = 0; f < 5; f++)
            {
                int b = 1 + f * 4;
                Vector2 basePos = wrist + new Vector2(xoff[f], -0.005f);

                if (closed)
                {
                    kp[b + 0] = basePos;
                    kp[b + 1] = basePos + new Vector2(drift[f] * 0.3f, -0.012f);
                    kp[b + 2] = basePos + new Vector2(drift[f] * 0.5f, -0.008f);
                    kp[b + 3] = basePos + new Vector2(drift[f] * 0.4f, -0.002f);
                }
                else
                {
                    kp[b + 0] = basePos;
                    kp[b + 1] = basePos + new Vector2(drift[f] * 0.3f, -0.030f);
                    kp[b + 2] = basePos + new Vector2(drift[f] * 0.5f, -0.060f);
                    kp[b + 3] = basePos + new Vector2(drift[f] * 0.6f, -0.090f);
                }
            }
            return kp;
        }

        private static Vector2 Rot(Vector2 v, float r)
        {
            float cs = Mathf.Cos(r), sn = Mathf.Sin(r);
            return new Vector2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
        }

        private void PublishDiff()
        {
            var nowIds = new HashSet<int>();
            for (int i = 0; i < currentList.Count; i++) nowIds.Add(currentList[i].TrackId);

            for (int i = 0; i < currentList.Count; i++)
            {
                Observation o = currentList[i];
                if (lastIds.Contains(o.TrackId)) RaiseUpdated(o);
                else RaiseAppeared(o);
            }

            foreach (int id in lastIds)
                if (!nowIds.Contains(id))
                    RaiseLost(id >= 10 ? ObservationKind.Hand : ObservationKind.Face, id);

            lastIds.Clear();
            foreach (int id in nowIds) lastIds.Add(id);
        }

        private void OnGUI() { DrawBoxes(); DrawHud(); DrawTiltIndicator(); }

        private void DrawBoxes()
        {
            var prev = GUI.color;
            for (int i = 0; i < currentList.Count; i++)
            {
                Observation o = currentList[i];
                Color c;
                if (o.Kind == ObservationKind.Face)
                {
                    switch (o.TrackId)
                    {
                        case ChildFaceId: c = new Color(0.3f, 0.8f, 1f, 0.9f); break;
                        case BarberFaceId: c = new Color(0.2f, 1f, 0.5f, 0.9f); break;
                        default: c = new Color(0.9f, 0.9f, 0.9f, 0.6f); break;
                    }
                }
                else if (o.TrackId == ParentLHandId || o.TrackId == ParentRHandId)
                    c = new Color(1f, 0.6f, 0.2f, 0.9f);
                else if (o.TrackId == ChildLHandId || o.TrackId == ChildRHandId)
                    c = new Color(0.6f, 1f, 0.9f, 0.9f);
                else
                    c = new Color(0.2f, 1f, 0.5f, 0.9f);

                var r = new Rect(
                    o.NormalizedRect.x * Screen.width,
                    o.NormalizedRect.y * Screen.height,
                    o.NormalizedRect.width * Screen.width,
                    o.NormalizedRect.height * Screen.height);

                GUI.color = c;
                Outline(r, 2f);

                if (o.Keypoints != null && o.Keypoints.Length >= 21)
                {
                    for (int k = 0; k < 21; k++)
                    {
                        Vector2 kp = o.Keypoints[k];
                        GUI.DrawTexture(
                            new Rect(kp.x * Screen.width - 1.5f,
                                     kp.y * Screen.height - 1.5f, 3f, 3f),
                            Texture2D.whiteTexture);
                    }
                }
            }
            GUI.color = prev;
        }

        private static void Outline(Rect r, float th)
        {
            GUI.DrawTexture(new Rect(r.xMin, r.yMin, r.width, th), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.xMin, r.yMax - th, r.width, th), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.xMin, r.yMin, th, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.xMax - th, r.yMin, th, r.height), Texture2D.whiteTexture);
        }

        /// <summary>
        /// Small compass-like dial on the left side that shows the live roll
        /// angle, so the tilt demo is legible even if the tower is off-screen
        /// or occluded by the HUD.
        /// </summary>
        private void DrawTiltIndicator()
        {
            const float R = 44f;
            float cx = 70f;
            float cy = Screen.height * 0.5f;

            var prev = GUI.color;

            // Backing disc.
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            var disc = new Rect(cx - R, cy - R, R * 2f, R * 2f);
            GUI.DrawTexture(disc, Texture2D.whiteTexture);

            // Horizontal reference line.
            GUI.color = new Color(1f, 1f, 1f, 0.35f);
            GUI.DrawTexture(new Rect(cx - R, cy - 1f, R * 2f, 2f), Texture2D.whiteTexture);

            // Tilted line showing the live roll.
            float rad = currentTilt * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(rad + Mathf.PI * 0.5f), Mathf.Sin(rad + Mathf.PI * 0.5f));
            Vector2 a = new Vector2(cx, cy) - dir * (R - 6f);
            Vector2 b = new Vector2(cx, cy) + dir * (R - 6f);
            DrawThickLine(a, b, 3f, new Color(1f, 0.85f, 0.3f, 1f));

            // Tick marks every 30 degrees.
            GUI.color = new Color(1f, 1f, 1f, 0.25f);
            for (int i = 0; i < 12; i++)
            {
                float a2 = i * 30f * Mathf.Deg2Rad;
                Vector2 outer = new Vector2(cx + Mathf.Cos(a2) * (R - 4f), cy + Mathf.Sin(a2) * (R - 4f));
                Vector2 inner = new Vector2(cx + Mathf.Cos(a2) * (R - 10f), cy + Mathf.Sin(a2) * (R - 10f));
                DrawThickLine(inner, outer, 1f, GUI.color);
            }

            GUI.color = prev;

            // Label under the dial.
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            _style.fontStyle = FontStyle.Bold;
            _style.alignment = TextAnchor.MiddleCenter;
            _style.normal.textColor = Color.white;
            GUI.Label(new Rect(cx - R, cy + R + 4f, R * 2f, 18f),
                "tilt " + currentTilt.ToString("+0.0;-0.0;0.0") + "\u00B0", _style);
            _style.alignment = TextAnchor.UpperLeft;
            _style.fontStyle = FontStyle.Normal;
        }

        private static void DrawThickLine(Vector2 a, Vector2 b, float thickness, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;

            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) { GUI.color = prev; return; }
            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;

            Matrix4x4 m = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, a);
            GUI.DrawTexture(new Rect(a.x, a.y - thickness * 0.5f, len, thickness), Texture2D.whiteTexture);
            GUI.matrix = m;

            GUI.color = prev;
        }

        private void DrawHud()
        {
            const float w = 780f, h = 300f, pad = 12f;
            var panel = new Rect(pad, Screen.height - h - pad, w, h);

            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = prev;

            float y = panel.y + 6, x = panel.x + 10;

            var childIn = FindObjectOfType<ChildHandsInput>();
            var parentIn = FindObjectOfType<ParentHandsInput>();

            L(ref y, x, w, "VISION STUB - " + StepName, true, Color.white);
            L(ref y, x, w, "Caption: " + Caption, false, new Color(0.85f, 0.9f, 1f));
            L(ref y, x, w,
                "[P] parent=" + (parentEnabled ? "ON" : "OFF") +
                "  [Space] paused=" + paused +
                "  [A] drive=" + (directDrive ? "DIRECT" : "PIPELINE") +
                "  [T] tilt=" + (tiltMirror ? "ON" : "OFF") +
                "  [R] reset  t=" + t.ToString("0.00") + "s",
                false, Color.white);
            L(ref y, x, w,
                "obs=" + currentList.Count +
                "  headRoll=" + ComputeChildRoll().ToString("+0.0;-0.0;0.0") + "\u00B0" +
                "  towerTilt=" + currentTilt.ToString("+0.0;-0.0;0.0") + "\u00B0",
                false, new Color(1f, 0.9f, 0.6f));

            if (director != null)
            {
                L(ref y, x, w,
                    "Phase=" + director.CurrentPhase +
                    "  armed=" + director.IsArmed +
                    "  GUIDED=" + director.BarberGuiding +
                    "  HeldTilt=" + director.HeldTilt.ToString("0.0") +
                    "  DirectorTilt=" + director.CurrentTilt.ToString("0.0") +
                    "  Match=" + director.Match,
                    false, Color.white);
            }

            if (childIn != null)
                L(ref y, x, w,
                    "ChildHasHand=" + childIn.HasHand +
                    "  RaisedThisFrame=" + childIn.RaisedThisFrame,
                    false, new Color(0.6f, 1f, 0.9f));

            if (parentIn != null)
                L(ref y, x, w,
                    "ParentSignal: Raised=" + parentIn.Current.RaisedThisFrame +
                    "  HasHand=" + parentIn.Current.HasHand +
                    "  Hand=" + parentIn.Current.RaisingHand,
                    false, new Color(1f, 0.8f, 0.5f));

            if (sweep != null)
                L(ref y, x, w,
                    "SteerTarget=" + sweep.SteerTarget.ToString("0.00") +
                    "  SteerHand=" + sweep.SteerHand +
                    "  IsSweeping=" + sweep.IsSweeping,
                    false, new Color(0.8f, 1f, 0.7f));

            if (stackGame != null)
                L(ref y, x, w,
                    "Moving=" + stackGame.IsMoving +
                    "  Score=" + stackGame.Score +
                    "  GameOver=" + stackGame.IsGameOver,
                    false, Color.white);
        }

        private static void L(ref float y, float x, float w, string s, bool bold, Color c)
        {
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            _style.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            _style.normal.textColor = c;
            GUI.Label(new Rect(x, y, w, 18), s, _style);
            y += 18;
        }

        private static GUIStyle _style;
    }
}