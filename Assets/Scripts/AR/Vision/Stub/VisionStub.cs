using System.Collections.Generic;
using UnityEngine;

namespace AR.Vision
{
    /// <summary>
    /// Deterministic stand-in for VisionPipeline. Emits synthetic faces/hands so the
    /// stacking demo can be exercised without a camera or ONNX models.
    ///
    /// Step order (matches ParticipantHandAssociation expectations):
    ///   1  warmup            - nothing
    ///   2  child face        - Child face appears (roles can bind with 1)
    ///   3  barber face       - Barber face appears (roles now bind Child+Barber)
    ///   4  parent face       - Parent face appears (optional; can be toggled off)
    ///   5  child raise       - Child hand rises then lowers -> triggers ChildHandsInput.Placed
    ///   6  parent left hold  - Parent's own-left hand raises and STAYS raised -> steer left
    ///   7  parent right hold - Parent's own-right hand raises and STAYS raised -> steer right
    ///   8  barber near       - Barber hands move near child head -> GUIDED
    ///   9  barber away       - Barber hands move away -> HOLDING
    ///  10  child raise again - Child raises again -> commit + respawn
    ///
    /// Controls:  [N] next step   [P] toggle parent present   [Space] pause auto-advance
    /// </summary>
    public class VisionStub : VisionPipeline
    {
        [Header("Stepping")]
        [SerializeField] private bool autoAdvance = true;
        [SerializeField, Min(0.5f)] private float stepSeconds = 2.5f;

        [Header("Participants")]
        [Tooltip("If false, the Parent face and Parent hands are never emitted. " +
                 "The demo should still reach Playing with Child+Barber only.")]
        [SerializeField] private bool parentEnabled = true;

        [Header("Layout (normalized 0..1, top-left origin)")]
        [SerializeField] private Vector2 childFaceCentre = new Vector2(0.30f, 0.35f);
        [SerializeField] private Vector2 barberFaceCentre = new Vector2(0.70f, 0.35f);
        [SerializeField] private Vector2 parentFaceCentre = new Vector2(0.50f, 0.80f);
        [SerializeField, Range(0.05f, 0.4f)] private float faceSize = 0.14f;

        [Header("Child hand")]
        [SerializeField] private float childHandRestDrop = 0.14f; // below face centre
        [SerializeField] private float childHandRaiseBy = 0.10f; // how far up the raise goes
        [SerializeField, Min(0.01f)] private float childRaiseDuration = 0.02f;
        [SerializeField] private float childRaiseHoldSeconds = 0.6f;

        [Header("Parent hands")]
        [SerializeField] private float parentHomeDrop = 0.12f;
        [SerializeField] private float parentRaiseBy = 0.14f;
        [SerializeField, Min(0.01f)] private float parentRaiseDuration = 0.02f;

        [Header("Barber hands")]
        [SerializeField] private float barberHomeDrop = 0.06f;
        [SerializeField] private float barberNearOffset = 0.10f; // distance from barber face toward child
        [SerializeField] private float barberFarOffset = 0.30f;

        [Header("Head roll")]
        [SerializeField] private float headRollSwing = 12f;

        protected override bool WantsWebcam => false;

        public override bool IsReady() => true;
        public override float FrameAspect => 16f / 9f;
        public override IReadOnlyList<Observation> Observations => currentList;

        private readonly List<Observation> currentList = new List<Observation>();
        private readonly HashSet<int> lastIds = new HashSet<int>();
        private float stepTimer;
        private float t;

        private const int ChildFaceId = 1;
        private const int BarberFaceId = 2;
        private const int ParentFaceId = 3;

        private const int ChildHandId = 12;
        private const int BarberLHandId = 10;
        private const int BarberRHandId = 11;
        private const int ParentLHandId = 20;
        private const int ParentRHandId = 21;

        private enum Step
        {
            Warmup,
            ChildFace,
            BarberFace,
            ParentFace,
            ChildRaiseSpawn,
            ParentLeftHold,
            ParentRightHold,
            BarberNear,
            BarberAway,
            ChildRaiseCommit,
        }

        private static readonly string[] StepNames =
        {
            "1/10 warmup",
            "2/10 child face appears",
            "3/10 barber face appears (Child+Barber bound)",
            "4/10 parent face appears (optional)",
            "5/10 child RAISES hand -> place block",
            "6/10 parent LEFT hand raised -> steer left",
            "7/10 parent RIGHT hand raised -> steer right",
            "8/10 barber hands NEAR -> GUIDED",
            "9/10 barber hands AWAY -> HOLDING",
            "10/10 child RAISES hand -> commit + respawn",
        };

        public string Caption { get; private set; } = "";
        public int StepIndex { get; private set; }
        public string StepName { get; private set; } = StepNames[0];
        public bool ParentEnabled => parentEnabled;

        private void Awake()
        {
            Caption = "[N] next  [P] parent  [Space] auto";
        }

        private void Update()
        {
            t += Time.deltaTime;

            if (Input.GetKeyDown(KeyCode.N)) Advance();
            if (Input.GetKeyDown(KeyCode.P)) parentEnabled = !parentEnabled;
            if (Input.GetKeyDown(KeyCode.Space)) autoAdvance = !autoAdvance;

            if (autoAdvance)
            {
                stepTimer += Time.deltaTime;
                if (stepTimer >= stepSeconds) Advance();
            }

            Rebuild();
        }

        private void Advance()
        {
            stepTimer = 0f;
            StepIndex = (StepIndex + 1) % StepNames.Length;
            StepName = StepNames[StepIndex];
        }

        // ---------------------------------------------------------------
        // Emit
        // ---------------------------------------------------------------

        private void Rebuild()
        {
            currentList.Clear();
            Step step = (Step)StepIndex;

            // Faces: Child and Barber appear first; Parent only if enabled.
            if (step >= Step.ChildFace)
                AddFace(ChildFaceId, childFaceCentre, RollNow());

            if (step >= Step.BarberFace)
                AddFace(BarberFaceId, barberFaceCentre, 0f);

            if (step >= Step.ParentFace && parentEnabled)
                AddFace(ParentFaceId, parentFaceCentre, 0f);

            // Child hand: present from ChildRaiseSpawn onward.
            if (step >= Step.ChildRaiseSpawn)
            {
                Vector2 rest = childFaceCentre + new Vector2(0f, childHandRestDrop);
                Vector2 up = rest - new Vector2(0f, childHandRaiseBy);

                bool isRaiseStep = step == Step.ChildRaiseSpawn || step == Step.ChildRaiseCommit;
                float raiseT = isRaiseStep ? Mathf.Clamp01(stepTimer / childRaiseDuration) : 0f;

                Vector2 pos;
                if (isRaiseStep && stepTimer <= childRaiseHoldSeconds)
                    pos = Vector2.Lerp(rest, up, raiseT);
                else
                    pos = rest;

                AddHand(ChildHandId, pos, closed: false);
                Caption = isRaiseStep
                    ? "Child RAISE t=" + stepTimer.ToString("0.00") + "s"
                    : "Child idle";
            }

            // Parent hands: only when parent enabled and we're in a parent step.
            if (parentEnabled && (step == Step.ParentLeftHold || step == Step.ParentRightHold))
            {
                Vector2 homeL = parentFaceCentre + new Vector2(-0.10f, parentHomeDrop);
                Vector2 homeR = parentFaceCentre + new Vector2(0.10f, parentHomeDrop);
                float raiseT = Mathf.Clamp01(stepTimer / parentRaiseDuration);

                if (step == Step.ParentLeftHold)
                {
                    Vector2 up = homeL - new Vector2(0f, parentRaiseBy);
                    AddHand(ParentLHandId, Vector2.Lerp(homeL, up, raiseT), closed: false);
                    AddHand(ParentRHandId, homeR, closed: false);
                    Caption = raiseT >= 1f ? "Parent LEFT raised (hold) -> steer left" : "Parent LEFT raising...";
                }
                else
                {
                    Vector2 up = homeR - new Vector2(0f, parentRaiseBy);
                    AddHand(ParentLHandId, homeL, closed: false);
                    AddHand(ParentRHandId, Vector2.Lerp(homeR, up, raiseT), closed: false);
                    Caption = raiseT >= 1f ? "Parent RIGHT raised (hold) -> steer right" : "Parent RIGHT raising...";
                }
            }
            else if (parentEnabled && (step == Step.ParentFace || step == Step.ChildRaiseSpawn))
            {
                // Parent present but hands at rest.
                Vector2 homeL = parentFaceCentre + new Vector2(-0.10f, parentHomeDrop);
                Vector2 homeR = parentFaceCentre + new Vector2(0.10f, parentHomeDrop);
                AddHand(ParentLHandId, homeL, closed: false);
                AddHand(ParentRHandId, homeR, closed: false);
            }

            // Barber hands: home beside the barber face; NEAR moves them toward the child,
            // but not so close that the child face steals ownership of the barber's hands.
            if (step >= Step.BarberFace)
            {
                Vector2 homeL = barberFaceCentre + new Vector2(-0.06f, barberHomeDrop);
                Vector2 homeR = barberFaceCentre + new Vector2(0.06f, barberHomeDrop);

                if (step == Step.BarberNear)
                {
                    // Close enough to the child head to satisfy guideEnterDistance (0.12),
                    // but still nearer to the barber face than to the child face so the
                    // association keeps these hands bound to Barber.
                    Vector2 nearL = new Vector2(childFaceCentre.x + 0.16f, childFaceCentre.y + 0.02f);
                    Vector2 nearR = new Vector2(childFaceCentre.x + 0.20f, childFaceCentre.y + 0.02f);
                    AddHand(BarberLHandId, nearL, closed: false);
                    AddHand(BarberRHandId, nearR, closed: false);
                    Caption = "Barber NEAR child -> GUIDED";
                }
                else if (step == Step.BarberAway)
                {
                    // Far from child, near barber face.
                    AddHand(BarberLHandId, homeL - new Vector2(barberFarOffset * 0.2f, 0f), closed: false);
                    AddHand(BarberRHandId, homeR + new Vector2(barberFarOffset * 0.2f, 0f), closed: false);
                    Caption = "Barber AWAY from child -> HOLDING";
                }
                else
                {
                    AddHand(BarberLHandId, homeL, closed: false);
                    AddHand(BarberRHandId, homeR, closed: false);
                }
            }

            PublishDiff();
        }

        private float RollNow()
        {
            Step s = (Step)StepIndex;
            if (s < Step.BarberNear) return 0f;
            if (s == Step.BarberNear) return Mathf.Sin(t * 1.2f) * headRollSwing;
            return Mathf.Sin(t * 1.2f) * headRollSwing * 1.6f;
        }

        private void AddFace(int id, Vector2 c, float rollDeg)
        {
            float half = faceSize * 0.5f;
            float r = rollDeg * Mathf.Deg2Rad;
            Vector2 le = c + Rot(new Vector2(-half * 0.4f, -half * 0.15f), r);
            Vector2 re = c + Rot(new Vector2(half * 0.4f, -half * 0.15f), r);
            Vector2 no = c + Rot(Vector2.zero, r);
            Vector2 ml = c + Rot(new Vector2(-half * 0.3f, half * 0.35f), r);
            Vector2 mr = c + Rot(new Vector2(half * 0.3f, half * 0.35f), r);
            var rect = Rect.MinMaxRect(c.x - half, c.y - half, c.x + half, c.y + half);
            currentList.Add(new Observation(id, ObservationKind.Face, "unknown", null,
                0.9f, 0f, rect, new[] { le, re, no, ml, mr }, 1, false));
        }

        private void AddHand(int id, Vector2 c, bool closed)
        {
            var rect = Rect.MinMaxRect(c.x - 0.06f, c.y - 0.06f, c.x + 0.06f, c.y + 0.06f);
            currentList.Add(new Observation(id, ObservationKind.Hand, "Left", null,
                0.9f, 0f, rect, BuildHandJoints(c, closed), 1, true));
        }

        private static Vector2[] BuildHandJoints(Vector2 wrist, bool closed)
        {
            var kp = new Vector2[21];
            kp[0] = wrist;

            float mid = closed ? 0.012f : 0.028f;
            float tip = closed ? 0.006f : 0.060f;
            F(kp, 1, 2, 3, 4, wrist, -0.030f, 0f, mid, tip, -0.010f);
            F(kp, 5, 6, 7, 8, wrist, -0.020f, 0f, mid, tip, -0.020f);
            F(kp, 9, 10, 11, 12, wrist, -0.005f, 0f, mid, tip, 0.000f);
            F(kp, 13, 14, 15, 16, wrist, 0.008f, 0f, mid, tip, 0.010f);
            F(kp, 17, 18, 19, 20, wrist, 0.020f, 0f, mid, tip, 0.020f);
            return kp;
        }

        private static void F(Vector2[] kp, int a, int b, int c, int d,
                             Vector2 w, float xoff, float yoff, float mid, float tip, float drift)
        {
            bool closed = tip < mid;

            Vector2 p0 = w + new Vector2(xoff, yoff);
            Vector2 p1 = p0 + new Vector2(drift * 0.3f, -mid);
            Vector2 p2 = p1 + new Vector2(drift * 0.3f, -mid * 0.6f);
            Vector2 p3 = closed
                ? p0 + new Vector2(drift * 0.2f, -mid * 0.3f)
                : p2 + new Vector2(drift * 0.4f, -tip * 0.6f);
            kp[a] = p0; kp[b] = p1; kp[c] = p2; kp[d] = p3;
        }

        private static Vector2 Rot(Vector2 v, float r)
        {
            float cs = Mathf.Cos(r), sn = Mathf.Sin(r);
            return new Vector2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
        }

        // ---------------------------------------------------------------
        // Diff (appeared/updated/lost events)
        // ---------------------------------------------------------------

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

        // ---------------------------------------------------------------
        // Debug HUD
        // ---------------------------------------------------------------

        private void OnGUI() { DrawBoxes(); DrawHud(); }

        private void DrawBoxes()
        {
            var prev = GUI.color;
            for (int i = 0; i < currentList.Count; i++)
            {
                Observation o = currentList[i];
                Color c;
                if (o.Kind == ObservationKind.Face)
                    c = o.TrackId == ChildFaceId
                        ? new Color(0.3f, 0.8f, 1f, 0.9f)
                        : new Color(0.9f, 0.9f, 0.9f, 0.6f);
                else if (o.TrackId == ParentLHandId || o.TrackId == ParentRHandId)
                    c = new Color(1f, 0.6f, 0.2f, 0.9f);
                else if (o.TrackId == ChildHandId)
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

        private void DrawHud()
        {
            const float w = 780f, h = 260f, pad = 12f;
            var panel = new Rect(pad, Screen.height - h - pad, w, h);

            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = prev;

            float y = panel.y + 6, x = panel.x + 10;

            var director = FindObjectOfType<Stacking.StackingDirector>();
            var sweep = FindObjectOfType<Stacking.BlockSweepController>();
            var childIn = FindObjectOfType<Stacking.ChildHandsInput>();
            var parentIn = FindObjectOfType<Stacking.ParentHandsInput>();
            var game = FindObjectOfType<StackGame>();

            L(ref y, x, w, "VISION STUB - " + StepName, true, Color.white);
            L(ref y, x, w, "Caption: " + Caption, false, new Color(0.85f, 0.9f, 1f));
            L(ref y, x, w,
                "[N] next  [P] parent=" + (parentEnabled ? "ON" : "OFF") +
                "  auto=" + autoAdvance + " (" + stepTimer.ToString("0.00") + "s)",
                false, Color.white);
            L(ref y, x, w, "obs=" + currentList.Count, false, Color.white);

            if (director != null)
            {
                L(ref y, x, w,
                    "Phase=" + director.CurrentPhase +
                    "  GUIDED=" + director.BarberGuiding +
                    "  HeldTilt=" + director.HeldTilt.ToString("0.0") +
                    "  CurrentTilt=" + director.CurrentTilt.ToString("0.0") +
                    "  Match=" + director.Match,
                    false, Color.white);
                L(ref y, x, w,
                    "ParentRaisingHand=" + director.ParentRaisingHand,
                    false, new Color(1f, 0.8f, 0.5f));
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

            if (game != null)
                L(ref y, x, w,
                    "Moving=" + game.IsMoving +
                    "  Score=" + game.Score +
                    "  GameOver=" + game.IsGameOver,
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