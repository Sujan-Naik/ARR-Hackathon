using System;
using System.Collections.Generic;
using UnityEngine;

namespace AR.Vision
{
    public class VisionStub : VisionPipeline
    {
        [SerializeField] private bool  autoAdvance = true;
        [SerializeField, Min(0.5f)] private float stepSeconds = 3.0f;
        [SerializeField] private bool  parentEnabled = true;

        [SerializeField] private Vector2 childFaceCentre   = new Vector2(0.30f, 0.30f);
        [SerializeField] private Vector2 barberFaceCentre  = new Vector2(0.70f, 0.30f);
        [SerializeField] private Vector2 parentFaceCentre  = new Vector2(0.50f, 0.80f);
        [SerializeField] private float   faceSize          = 0.16f;
        [SerializeField] private float   handOffsetBelow   = 0.14f;
        [SerializeField] private float   headRollSwing     = 12f;
        [SerializeField] private float   barberNearOffset  = 0.04f;
        [SerializeField] private float   barberFarOffset   = 0.40f;
        [SerializeField] private float   parentRaiseHeight = 0.14f;

        protected override bool WantsWebcam => false;

        public override bool  IsReady() => true;
        public override float FrameAspect => 16f / 9f;
        public override IReadOnlyList<Observation> Observations => currentList;

        private readonly List<Observation> currentList = new List<Observation>();
        private readonly HashSet<int> lastIds = new HashSet<int>();
        private float stepTimer, t;

        private const int ChildFaceId = 1, BarberFaceId = 2, ParentFaceId = 3;
        private const int BarberLHandId = 10, BarberRHandId = 11, ChildHandId = 12;
        private const int ParentLHandId = 20, ParentRHandId = 21;

        private enum Step { Warmup, FacesOnly, ChildClosesSpawn, ParentLeft, ParentRight, BarberNear, BarberAway, ChildClosesCommit }
        private static readonly string[] StepNames =
        {
            "1/8 warmup",
            "2/8 faces appear",
            "3/8 child RAISES hand -> commit + respawn",
            "4/8 parent own-LEFT raise -> steer left",
            "5/8 parent own-RIGHT raise -> steer right",
            "6/8 barber hands NEAR -> GUIDED",
            "7/8 barber hands AWAY -> HOLDING",
            "8/8 child RAISES hand -> commit + respawn",
        };
        public string Caption { get; private set; } = "";
        public int StepIndex { get; private set; }
        public string StepName { get; private set; } = StepNames[0];

        private void Awake() { Caption = "[N] advance, [P] parent."; }

        private void Update()
        {
            t += Time.deltaTime;
            if (Input.GetKeyDown(KeyCode.N)) Advance();
            if (Input.GetKeyDown(KeyCode.P)) parentEnabled = !parentEnabled;
            if (autoAdvance) { stepTimer += Time.deltaTime; if (stepTimer >= stepSeconds) Advance(); }
            Rebuild();
        }

        private void Advance()
        {
            stepTimer = 0f;
            StepIndex = (StepIndex + 1) % StepNames.Length;
            StepName = StepNames[StepIndex];
        }

        private void Rebuild()
        {
            currentList.Clear();
            Step step = (Step)StepIndex;

            if (step >= Step.FacesOnly)
            {
                AddFace(ChildFaceId, childFaceCentre, RollNow());
                AddFace(BarberFaceId, barberFaceCentre, 0f);
                AddFace(ParentFaceId, parentFaceCentre, 0f);
            }

            if (step >= Step.ChildClosesSpawn)
            {
                bool raising = (step == Step.ChildClosesSpawn || step == Step.ChildClosesCommit)
                               && stepTimer > 1.0f;
                float yOff = raising ? (handOffsetBelow - 0.14f) : handOffsetBelow;
                AddHand(ChildHandId, childFaceCentre + new Vector2(0f, yOff), false);
            }

            if (step == Step.BarberNear)
            {
                // Between barber face (0.70,0.30) and child head (0.30,0.30):
                //   distance to barber face ~0.13, distance to child head ~0.12
                // -> binds to Barber AND within guideEnterDistance of child.
                AddHand(BarberLHandId, new Vector2(0.42f, 0.30f), false);
                AddHand(BarberRHandId, new Vector2(0.46f, 0.30f), false);
                Caption = "Barber NEAR -> GUIDED";
            }
            else if (step == Step.BarberAway)
            {
                // Near barber face but > guideExitDistance from child head.
                AddHand(BarberLHandId, new Vector2(0.62f, 0.30f), false);
                AddHand(BarberRHandId, new Vector2(0.66f, 0.30f), false);
                Caption = "Barber AWAY -> HOLDING";
            }
            else Caption = "Barber idle";

            if (step == Step.ParentLeft && parentEnabled)
            {
                Vector2 home = new Vector2(0.65f, 0.85f), up = home - new Vector2(0f, parentRaiseHeight);
                bool raised = stepTimer > 1.0f;
                AddHand(ParentLHandId, raised ? up : home, false);
                Caption = "Parent LEFT " + (raised ? "RAISED -> steer left" : "rest");
            }
            else if (step == Step.ParentRight && parentEnabled)
            {
                Vector2 home = new Vector2(0.35f, 0.85f), up = home - new Vector2(0f, parentRaiseHeight);
                bool raised = stepTimer > 1.0f;
                AddHand(ParentRHandId, raised ? up : home, false);
                Caption = "Parent RIGHT " + (raised ? "RAISED -> steer right" : "rest");
            }
            else if ((step == Step.ParentLeft || step == Step.ParentRight) && !parentEnabled)
                Caption = "Parent disabled";

            HashSet<int> nowIds = new HashSet<int>();
            foreach (var o in currentList) nowIds.Add(o.TrackId);
            foreach (var o in currentList)
            {
                if (lastIds.Contains(o.TrackId)) RaiseUpdated(o); else RaiseAppeared(o);
            }
            foreach (var id in lastIds)
                if (!nowIds.Contains(id))
                    RaiseLost(id >= 10 ? ObservationKind.Hand : ObservationKind.Face, id);
            lastIds.Clear();
            foreach (var id in nowIds) lastIds.Add(id);
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
            float half = faceSize * 0.5f, r = rollDeg * Mathf.Deg2Rad;
            Vector2 le = c + Rot(new Vector2(-half * 0.4f, -half * 0.15f), r);
            Vector2 re = c + Rot(new Vector2( half * 0.4f, -half * 0.15f), r);
            Vector2 no = c + Rot(new Vector2( 0f, 0f), r);
            Vector2 ml = c + Rot(new Vector2(-half * 0.3f,  half * 0.35f), r);
            Vector2 mr = c + Rot(new Vector2( half * 0.3f,  half * 0.35f), r);
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
            // mcp, pip, dip, tip for 4 fingers + thumb
            // open: tip ~0.06 above wrist. closed: tip ~0.005 above wrist.
            float mid = closed ? 0.012f : 0.028f;
            float tip = closed ? 0.006f : 0.060f;
            F(kp,  1, 2, 3, 4, wrist, -0.030f, 0.0f, mid, tip, -0.010f);
            F(kp,  5, 6, 7, 8, wrist, -0.020f, 0.0f, mid, tip, -0.020f);
            F(kp,  9,10,11,12, wrist, -0.005f, 0.0f, mid, tip,  0.000f);
            F(kp, 13,14,15,16, wrist,  0.008f, 0.0f, mid, tip,  0.010f);
            F(kp, 17,18,19,20, wrist,  0.020f, 0.0f, mid, tip,  0.020f);
            return kp;
        }

        private static void F(Vector2[] kp, int a, int b, int c, int d,
                             Vector2 w, float xoff, float yoff, float mid, float tip, float drift)
        {
            // Detect "closed" by tip < mid: when closed, the tip must be CLOSER to
            // the wrist than the PIP, otherwise HandGestures.IsClosed never fires.
            bool closed = tip < mid;

            Vector2 p0 = w + new Vector2(xoff, yoff);                       // MCP
            Vector2 p1 = p0 + new Vector2(drift * 0.3f, -mid);              // PIP
            Vector2 p2 = p1 + new Vector2(drift * 0.3f, -mid * 0.6f);       // DIP
            // Open: tip continues away from wrist. Closed: tip curls back toward wrist.
            Vector2 p3 = closed
                ? p0 + new Vector2(drift * 0.2f, -mid * 0.3f)               // tip back near wrist
                : p2 + new Vector2(drift * 0.4f, -tip * 0.6f);              // further away
            kp[a] = p0; kp[b] = p1; kp[c] = p2; kp[d] = p3;
        }

        private static Vector2 Rot(Vector2 v, float r)
        {
            float cs = Mathf.Cos(r), sn = Mathf.Sin(r);
            return new Vector2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
        }

        private void OnGUI() { DrawBoxes(); DrawHud(); }

        private void DrawBoxes()
        {
            var prev = GUI.color;
            foreach (var o in currentList)
            {
                Color c = o.Kind == ObservationKind.Face
                    ? (o.TrackId == ChildFaceId ? new Color(0.3f, 0.8f, 1f, 0.9f) : new Color(0.9f, 0.9f, 0.9f, 0.6f))
                    : (o.TrackId == ParentLHandId || o.TrackId == ParentRHandId
                        ? new Color(1f, 0.6f, 0.2f, 0.9f) : new Color(0.2f, 1f, 0.5f, 0.9f));
                var r = new Rect(o.NormalizedRect.x * Screen.width, o.NormalizedRect.y * Screen.height,
                                 o.NormalizedRect.width * Screen.width, o.NormalizedRect.height * Screen.height);
                GUI.color = c; Outline(r, 2f);
                if (o.Keypoints != null && o.Keypoints.Length >= 21)
                    for (int i = 0; i < 21; i++)
                    {
                        var kp = o.Keypoints[i];
                        GUI.DrawTexture(new Rect(kp.x * Screen.width - 1.5f, kp.y * Screen.height - 1.5f, 3f, 3f), Texture2D.whiteTexture);
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
            const float w = 760f, h = 250f, pad = 12f;
            var panel = new Rect(pad, Screen.height - h - pad, w, h);
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = prev;

            float y = panel.y + 6, x = panel.x + 10;
            var director = FindObjectOfType<Stacking.StackingDirector>();
            var sweep = FindObjectOfType<Stacking.BlockSweepController>();
            var childIn = FindObjectOfType<Stacking.ChildHandsInput>();
            var game = FindObjectOfType<StackGame>();

            L(ref y, x, w, "VISION STUB - " + StepName, true, Color.white);
            L(ref y, x, w, "Caption: " + Caption, false, new Color(0.85f, 0.9f, 1f));
            L(ref y, x, w, "[N] advance  [P] parent=" + (parentEnabled ? "ON" : "OFF") + "  auto=" + autoAdvance, false, Color.white);
            L(ref y, x, w, "obs=" + currentList.Count, false, Color.white);
            if (director != null)
            {
                L(ref y, x, w, "GUIDED=" + director.BarberGuiding + "  HeldTilt=" + director.HeldTilt.ToString("0.0") +
                    "  CurrentTilt=" + director.CurrentTilt.ToString("0.0") + "  Match=" + director.Match, false, Color.white);
                L(ref y, x, w, "ParentRaisingHand=" + director.ParentRaisingHand, false, new Color(1f, 0.8f, 0.5f));
            }
            if (childIn != null)
                L(ref y, x, w, "ChildHasHand=" + childIn.HasHand + "  RaisedThisFrame=" + childIn.RaisedThisFrame, false, new Color(0.6f, 1f, 0.9f));
            if (sweep != null)
                L(ref y, x, w, "SteerTarget=" + sweep.SteerTarget.ToString("0.00") + "  SteerHand=" + sweep.SteerHand, false, new Color(0.8f, 1f, 0.7f));
            if (game != null)
                L(ref y, x, w, "Moving=" + game.IsMoving + "  Score=" + game.Score + "  GameOver=" + game.IsGameOver, false, Color.white);
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
