using System.Collections.Generic;
using UnityEngine;

namespace AR.Vision
{
    /// <summary>
    /// Keyboard-driven manual role assignment for the salon demo.
    ///
    ///   Tab       cycle target role (Child -> Barber -> Parent -> Auto -> ...)
    ///   1..9      bind the Nth visible face (sorted left-to-right) to the current role
    ///   0         clear all manual bindings (return to auto)
    ///   F2        toggle the on-screen hint
    ///
    /// Works with any number of tracked faces (1, 2 or 3), so a solo demo can
    /// just press 1 while looking at the camera.
    ///
    /// The panel only talks to ParticipantHandAssociation's public API:
    /// AssignRole, ClearManualRoles, IsRoleManual, GetFaceIdForRole,
    /// RolesAssigned. It reads VisionPipeline.Observations to enumerate faces.
    ///
    /// A short toast is shown for a couple of seconds after any binding
    /// change so the operator gets confirmation even if the hint is hidden.
    /// </summary>
    [DefaultExecutionOrder(150)]
    public class RoleAssignmentPanel : MonoBehaviour
    {
        [SerializeField] private ParticipantHandAssociation association;
        [SerializeField] private VisionPipeline vision;

        [Header("Input")]
        [SerializeField] private KeyCode cycleKey = KeyCode.Tab;
        [SerializeField] private KeyCode clearKey = KeyCode.Alpha0;
        [SerializeField] private KeyCode toggleHintKey = KeyCode.F2;
        [SerializeField] private bool showHint = true;

        [Header("Debug")]
        [SerializeField] private bool logActions = true;

        [Header("Toast")]
        [SerializeField, Min(0.5f)] private float toastSeconds = 2f;
        [SerializeField] private Color toastColor = new Color(1f, 0.9f, 0.5f);

        private ParticipantHandAssociation.ParticipantRole targetRole =
            ParticipantHandAssociation.ParticipantRole.Child;
        private bool autoMode = true;

        private readonly List<Observation> sortedFaces = new List<Observation>();

        private GUIStyle labelStyle;
        private GUIStyle hintStyle;

        private string toastText;
        private float toastUntil;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            if (association == null) association = FindObjectOfType<ParticipantHandAssociation>();
            if (vision == null) vision = FindObjectOfType<VisionPipeline>();
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleHintKey)) showHint = !showHint;

            if (association == null) return;

            if (Input.GetKeyDown(cycleKey)) CycleMode();
            if (Input.GetKeyDown(clearKey)) ClearManual();

            for (int i = 1; i <= 9; i++)
            {
                KeyCode code = KeyCode.Alpha0 + i;
                if (Input.GetKeyDown(code)) BindNthFace(i - 1);
            }
        }

        // ------------------------------------------------------------------
        // Assignment actions
        // ------------------------------------------------------------------

        private void CycleMode()
        {
            if (autoMode)
            {
                autoMode = false;
                targetRole = ParticipantHandAssociation.ParticipantRole.Child;
            }
            else
            {
                switch (targetRole)
                {
                    case ParticipantHandAssociation.ParticipantRole.Child:
                        targetRole = ParticipantHandAssociation.ParticipantRole.Barber;
                        break;
                    case ParticipantHandAssociation.ParticipantRole.Barber:
                        targetRole = ParticipantHandAssociation.ParticipantRole.Parent;
                        break;
                    default:
                        autoMode = true;
                        break;
                }
            }

            ShowToast("mode: " + ModeLabel());
            if (logActions) Debug.Log("[Roles] mode: " + ModeLabel(), this);
        }

        private void ClearManual()
        {
            association.ClearManualRoles();
            autoMode = true;
            ShowToast("manual bindings cleared");
            if (logActions) Debug.Log("[Roles] manual bindings cleared", this);
        }

        private void BindNthFace(int index)
        {
            if (autoMode || vision == null) return;

            sortedFaces.Clear();
            foreach (Observation o in vision.Observations)
                if (o.Kind == ObservationKind.Face) sortedFaces.Add(o);

            // Sort left-to-right so key 1 is the leftmost face. Matches what
            // the debug overlay draws, and matches the auto-assignment order.
            sortedFaces.Sort((a, b) => a.Center.x.CompareTo(b.Center.x));

            if (index < 0 || index >= sortedFaces.Count)
            {
                ShowToast("no face #" + (index + 1));
                if (logActions) Debug.Log("[Roles] no face #" + (index + 1), this);
                return;
            }

            int faceId = sortedFaces[index].TrackId;
            association.AssignRole(targetRole, faceId);

            ShowToast(targetRole + " <- face #" + faceId);
            if (logActions)
                Debug.Log("[Roles] " + targetRole + " <- face " + faceId, this);
        }

        private string ModeLabel()
        {
            return autoMode ? "Auto" : targetRole.ToString();
        }

        private void ShowToast(string message)
        {
            toastText = message;
            toastUntil = Time.time + toastSeconds;
        }

        // ------------------------------------------------------------------
        // Hint overlay
        // ------------------------------------------------------------------

        private void EnsureStyles()
        {
            if (labelStyle != null) return;

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.UpperLeft,
            };
            labelStyle.normal.textColor = Color.white;

            hintStyle = new GUIStyle(labelStyle)
            {
                fontSize = 11,
                fontStyle = FontStyle.Normal,
            };
            hintStyle.normal.textColor = new Color(1f, 0.9f, 0.6f, 1f);
        }

        private void OnGUI()
        {
            if (!showHint && Time.time >= toastUntil) return;

            EnsureStyles();

            int faces = 0;
            if (vision != null)
                foreach (Observation o in vision.Observations)
                    if (o.Kind == ObservationKind.Face) faces++;

            bool rolesAssigned = association != null && association.RolesAssigned;
            bool showRoles = association != null && rolesAssigned;
            string manual = association != null && !autoMode
                && association.IsRoleManual(targetRole) ? "  (manual)" : "";

            const float margin = 10f;
            const float width = 320f;
            const float lineHeight = 18f;
            const float padding = 8f;

            int lineCount = 3 + (showRoles ? 1 : 0);
            float height = lineCount * lineHeight + padding * 2f;
            Rect panel = new Rect(margin, margin, width, height);

            // Backing panel.
            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = prev;

            float y = panel.y + padding;
            float x = panel.x + padding;

            GUI.Label(new Rect(x, y, width, lineHeight),
                "Roles: " + ModeLabel() + manual, labelStyle);
            y += lineHeight;

            GUI.Label(new Rect(x, y, width, lineHeight),
                "[Tab] next role   [1-9] bind face   [0] clear", hintStyle);
            y += lineHeight;

            GUI.Label(new Rect(x, y, width, lineHeight),
                "faces visible: " + faces, labelStyle);
            y += lineHeight;

            if (showRoles)
            {
                string line =
                    "C#" + association.GetFaceIdForRole(
                        ParticipantHandAssociation.ParticipantRole.Child) +
                    "   B#" + association.GetFaceIdForRole(
                        ParticipantHandAssociation.ParticipantRole.Barber) +
                    "   P#" + association.GetFaceIdForRole(
                        ParticipantHandAssociation.ParticipantRole.Parent);
                GUI.Label(new Rect(x, y, width, lineHeight), line, labelStyle);
            }

            // Toast (always drawn, independent of the hint).
            if (Time.time < toastUntil && !string.IsNullOrEmpty(toastText))
            {
                Color prevToast = GUI.color;
                GUI.color = toastColor;
                GUI.Label(new Rect(margin, Screen.height - 40f, 480f, 24f),
                    "» " + toastText, labelStyle);
                GUI.color = prevToast;
            }
        }
    }
}