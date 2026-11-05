using AR.Vision;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

/// <summary>
/// Debug overlay for the salon demo. Shows, per participant role, where they
/// are on screen, what they are doing, and whether the association is bound.
///
/// Read-only: this component never writes to the association, the stack game,
/// or the hand controllers. It only reads the VisionPipeline's published
/// Observations and the public APIs on ParticipantHandAssociation / StackGame.
///
/// Uses OnGUI on purpose. Gameplay code does not; a debug overlay does.
/// </summary>
[DefaultExecutionOrder(300)]
public class DebugParticipantOverlay : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private VisionPipeline vision;
    [SerializeField] private ParticipantHandAssociation association;
    [SerializeField] private StackGame stackGame;
    [SerializeField] private BarberHandsController barberHands;

    [Header("Visibility")]
    [Tooltip("Master switch. Off by default so the demo is clean; bind a key to toggle.")]
    [SerializeField] private bool showOverlay = true;
    [SerializeField] private KeyCode toggleKey = KeyCode.F1;

    [Header("Face boxes")]
    [SerializeField] private bool drawFaceBoxes = true;
    [SerializeField, Min(1f)] private float faceBoxThickness = 3f;
    [Tooltip("Draw a thin box for every tracked face, not just role-bound ones.")]
    [SerializeField] private bool drawAllFaces = false;

    [Header("Hand markers")]
    [SerializeField] private bool drawHandMarkers = true;
    [SerializeField, Min(2f)] private float handMarkerRadius = 7f;
    [Tooltip("Draw a dot for every tracked hand, including unassociated ones.")]
    [SerializeField] private bool drawUnassociatedHands = true;

    [Header("Panel")]
    [SerializeField] private bool drawStatusPanel = true;
    [SerializeField] private int panelWidth = 360;
    [SerializeField] private int panelPadding = 10;

    [Header("Colours")]
    [SerializeField] private Color childColor = new Color(0.25f, 0.75f, 1f);
    [SerializeField] private Color barberColor = new Color(1f, 0.6f, 0.2f);
    [SerializeField] private Color parentColor = new Color(0.55f, 1f, 0.45f);
    [SerializeField] private Color unboundColor = new Color(0.6f, 0.6f, 0.6f);
    [SerializeField] private Color warnColor = new Color(1f, 0.78f, 0.12f);

    // Scratch buffers so we don't allocate per frame.
    private readonly List<Observation> scratchHands = new List<Observation>();
    private readonly Dictionary<int, Observation> faceById = new Dictionary<int, Observation>();
    private readonly Dictionary<int, Observation> handById = new Dictionary<int, Observation>();

    private Texture2D white;
    private GUIStyle labelStyle;
    private GUIStyle headerStyle;
    private GUIStyle panelStyle;
    private int frameCounter;

    private static readonly ParticipantHandAssociation.ParticipantRole[] Roles =
    {
        ParticipantHandAssociation.ParticipantRole.Child,
        ParticipantHandAssociation.ParticipantRole.Barber,
        ParticipantHandAssociation.ParticipantRole.Parent,
    };

    private void Awake()
    {
        if (vision == null) vision = FindObjectOfType<VisionPipeline>();
        if (association == null) association = FindObjectOfType<ParticipantHandAssociation>();
        if (stackGame == null) stackGame = FindObjectOfType<StackGame>();
        if (barberHands == null) barberHands = FindObjectOfType<BarberHandsController>();

        white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        white.SetPixel(0, 0, Color.white);
        white.Apply();
    }

    private void OnDestroy()
    {
        if (white != null) Destroy(white);
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            showOverlay = !showOverlay;

        frameCounter++;
        if (!showOverlay) return;

        RebuildIndexes();
    }

    /// <summary>Rebuilds the per-frame lookups from the pipeline's published list.</summary>
    private void RebuildIndexes()
    {
        faceById.Clear();
        handById.Clear();

        if (vision == null) return;

        IReadOnlyList<Observation> all = vision.Observations;
        for (int i = 0; i < all.Count; i++)
        {
            Observation o = all[i];
            if (o.Kind == ObservationKind.Face) faceById[o.TrackId] = o;
            else if (o.Kind == ObservationKind.Hand) handById[o.TrackId] = o;
        }
    }

    // ------------------------------------------------------------------
    // OnGUI
    // ------------------------------------------------------------------

    private void OnGUI()
    {
        if (!showOverlay || white == null) return;

        EnsureStyles();

        // The camera preview RawImage is a full-screen Canvas child. Its world
        // corners give us the pixel rect to map normalized face/hand coords into.
        Rect preview = GetPreviewScreenRect();

        if (drawFaceBoxes)
            DrawFaces(preview);

        if (drawHandMarkers)
            DrawHands(preview);

        if (drawStatusPanel)
            DrawStatusPanel(preview);
    }

    private void EnsureStyles()
    {
        if (labelStyle != null) return;

        labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            wordWrap = false,
        };
        labelStyle.normal.textColor = Color.white;

        headerStyle = new GUIStyle(labelStyle)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
        };

        panelStyle = new GUIStyle(GUI.skin.box);
    }

    /// <summary>
    /// Returns the screen-pixel rect of the webcam preview. Falls back to the
    /// whole screen if the VisionPipeline's RectTransform can't be found.
    /// Assumes a Screen Space - Overlay canvas (same assumption VisionPipeline makes).
    /// </summary>
    private Rect GetPreviewScreenRect()
    {
        if (vision == null) return new Rect(0f, 0f, Screen.width, Screen.height);

        RectTransform rt = vision.transform as RectTransform;
        if (rt == null) return new Rect(0f, 0f, Screen.width, Screen.height);

        Vector3[] corners = new Vector3[4];
        rt.GetWorldCorners(corners); // BL, TL, TR, BR in screen pixels

        float x = corners[0].x;
        float y = Screen.height - corners[2].y; // GUI y is top-down
        float w = corners[2].x - corners[0].x;
        float h = corners[2].y - corners[0].y;
        return new Rect(x, y, w, h);
    }

    // ------------------------------------------------------------------
    // Face drawing
    // ------------------------------------------------------------------

    private void DrawFaces(Rect preview)
    {
        if (association != null && association.RolesAssigned)
        {
            for (int r = 0; r < Roles.Length; r++)
            {
                ParticipantHandAssociation.ParticipantRole role = Roles[r];
                Color color = RoleColor(role);

                if (association.TryGetFace(role, out Observation face))
                {
                    Rect box = NormalizedToScreen(face.NormalizedRect, preview);
                    DrawRectOutline(box, color, faceBoxThickness);
                    DrawLabel(
                        new Vector2(box.x, box.y - 18f),
                        role.ToString().ToUpperInvariant()
                            + "  conf " + face.Confidence.ToString("0.00")
                            + (face.IsIdentified ? "  id:" + face.Label : ""),
                        color);
                }
            }
        }

        if (drawAllFaces && vision != null)
        {
            foreach (var pair in faceById)
            {
                Observation face = pair.Value;

                // Skip faces already drawn above.
                bool alreadyDrawn = false;
                for (int r = 0; r < Roles.Length; r++)
                {
                    if (association != null
                        && association.TryGetFace(Roles[r], out Observation bound)
                        && bound.TrackId == face.TrackId)
                    {
                        alreadyDrawn = true;
                        break;
                    }
                }
                if (alreadyDrawn) continue;

                Rect box = NormalizedToScreen(face.NormalizedRect, preview);
                DrawRectOutline(box, unboundColor, 1f);
                DrawLabel(new Vector2(box.x, box.y - 16f),
                    "face " + face.TrackId, unboundColor);
            }
        }
    }

    // ------------------------------------------------------------------
    // Hand drawing
    // ------------------------------------------------------------------

    private void DrawHands(Rect preview)
    {
        if (association == null)
        {
            if (drawUnassociatedHands) DrawAllHands(preview);
            return;
        }

        HashSet<int> drawn = new HashSet<int>();

        for (int r = 0; r < Roles.Length; r++)
        {
            ParticipantHandAssociation.ParticipantRole role = Roles[r];
            Color color = RoleColor(role);

            scratchHands.Clear();
            association.GetHandsForRole(role, scratchHands);

            for (int i = 0; i < scratchHands.Count; i++)
            {
                Observation hand = scratchHands[i];
                drawn.Add(hand.TrackId);

                Vector2 screen = NormalizedToScreenPoint(hand.Center, preview);
                DrawCircle(screen, handMarkerRadius, color);

                string state = HandState(hand);
                DrawLabel(
                    new Vector2(screen.x + handMarkerRadius + 4f, screen.y - 8f),
                    role + " hand  " + state,
                    color);
            }
        }

        if (drawUnassociatedHands)
        {
            foreach (var pair in handById)
            {
                if (drawn.Contains(pair.Key)) continue;

                Observation hand = pair.Value;
                Vector2 screen = NormalizedToScreenPoint(hand.Center, preview);
                DrawCircle(screen, handMarkerRadius * 0.7f, unboundColor);
                DrawLabel(
                    new Vector2(screen.x + handMarkerRadius + 2f, screen.y - 8f),
                    "hand " + hand.TrackId + " (unassociated)",
                    unboundColor);
            }
        }
    }

    private void DrawAllHands(Rect preview)
    {
        foreach (var pair in handById)
        {
            Observation hand = pair.Value;
            Vector2 screen = NormalizedToScreenPoint(hand.Center, preview);
            DrawCircle(screen, handMarkerRadius * 0.7f, unboundColor);
        }
    }

    private static string HandState(Observation hand)
    {
        if (HandGestures.IsPinching(hand)) return "PINCH";
        if (HandGestures.IsOpen(hand)) return "open";
        if (HandGestures.IsClosed(hand)) return "closed";
        float openness = HandGestures.Openness(hand);
        return "mid " + openness.ToString("0.00");
    }

    // ------------------------------------------------------------------
    // Status panel
    // ------------------------------------------------------------------

    private void DrawStatusPanel(Rect preview)
    {
        float panelHeight = Screen.height;
        Rect panel = new Rect(
            Screen.width - panelWidth - panelPadding,
            panelPadding,
            panelWidth,
            panelHeight - panelPadding * 2);

        GUI.Box(panel, GUIContent.none, panelStyle);

        StringBuilder sb = new StringBuilder(512);
        float x = panel.x + 8f;
        float y = panel.y + 6f;
        const float lineHeight = 16f;

        DrawLine(ref y, x, "PARTICIPANT DEBUG  (F1 to hide)", lineHeight, headerStyle, Color.white);

        // Pipeline readiness.
        bool ready = vision != null && vision.IsReady();
        DrawLine(ref y, x,
            "vision: " + (ready ? "ready" : "NOT READY")
            + "   frames observed: " + frameCounter,
            lineHeight, labelStyle, ready ? Color.white : warnColor);

        // Role assignment.
        bool rolesAssigned = association != null && association.RolesAssigned;
        DrawLine(ref y, x,
            "roles assigned: " + (rolesAssigned ? "YES" : "no (need 3 faces)"),
            lineHeight, labelStyle, rolesAssigned ? Color.white : warnColor);

        DrawLine(ref y, x,
            "faces tracked: " + faceById.Count
            + "   hands tracked: " + handById.Count,
            lineHeight, labelStyle, Color.white);

        y += 4f;

        // Per-role block.
        for (int r = 0; r < Roles.Length; r++)
        {
            ParticipantHandAssociation.ParticipantRole role = Roles[r];
            Color color = RoleColor(role);

            DrawLine(ref y, x, "--- " + role.ToString().ToUpperInvariant() + " ---",
                lineHeight, labelStyle, color);

            // Face.
            if (association != null && association.TryGetFace(role, out Observation face))
            {
                DrawLine(ref y, x,
                    "  face #" + face.TrackId
                    + "  conf " + face.Confidence.ToString("0.00")
                    + "  centre (" + face.Center.x.ToString("0.00") + ", "
                    + face.Center.y.ToString("0.00") + ")",
                    lineHeight, labelStyle, color);
            }
            else
            {
                DrawLine(ref y, x, "  face: NONE", lineHeight, labelStyle, unboundColor);
            }

            // Hands.
            scratchHands.Clear();
            if (association != null)
                association.GetHandsForRole(role, scratchHands);

            if (scratchHands.Count == 0)
            {
                DrawLine(ref y, x, "  hands: none associated", lineHeight, labelStyle, unboundColor);
            }
            else
            {
                for (int i = 0; i < scratchHands.Count; i++)
                {
                    Observation hand = scratchHands[i];
                    DrawLine(ref y, x,
                        "  hand #" + hand.TrackId
                        + "  " + HandState(hand)
                        + "  (" + hand.Center.x.ToString("0.00") + ", "
                        + hand.Center.y.ToString("0.00") + ")",
                        lineHeight, labelStyle, color);
                }
            }

            // Per-role status line.
            DrawLine(ref y, x, "  status: " + RoleStatus(role), lineHeight, labelStyle, color);
            y += 4f;
        }

        // Tower / game state.
        y += 2f;
        DrawLine(ref y, x, "--- STACK GAME ---", lineHeight, labelStyle, Color.white);

        if (stackGame != null)
        {
            DrawLine(ref y, x,
                "  moving: " + (stackGame.IsMoving ? "YES" : "no")
                + "   gameover: " + (stackGame.IsGameOver ? "YES" : "no"),
                lineHeight, labelStyle, Color.white);
            DrawLine(ref y, x,
                "  score: " + stackGame.Score,
                lineHeight, labelStyle, Color.white);
        }
        else
        {
            DrawLine(ref y, x, "  stackGame: not assigned", lineHeight, labelStyle, unboundColor);
        }

        if (barberHands != null)
        {
            DrawLine(ref y, x,
                "  barber line renderer: " + (barberHands.isActiveAndEnabled ? "on" : "off"),
                lineHeight, labelStyle, Color.white);
        }
    }

    private string RoleStatus(ParticipantHandAssociation.ParticipantRole role)
    {
        switch (role)
        {
            case ParticipantHandAssociation.ParticipantRole.Child:
                if (association == null
                    || !association.TryGetFace(role, out Observation childFace))
                {
                    return "no face -> tower upright";
                }
                if (vision == null || !vision.IsReady())
                    return "vision not ready -> tower upright";
                if (HeadPose.TryEstimate(childFace, vision.FrameAspect, out HeadAngles head))
                {
                    return "head yaw " + head.Yaw.ToString("+0;-0;0")
                        + "\u00B0 pitch " + head.Pitch.ToString("+0;-0;0")
                        + "\u00B0 roll " + head.Roll.ToString("+0;-0;0")
                        + "\u00B0";
                }
                return "head pose unavailable";

            case ParticipantHandAssociation.ParticipantRole.Barber:
                if (association == null
                    || !association.TryGetFace(role, out _))
                {
                    return "no face -> hands hidden";
                }
                scratchHands.Clear();
                association.GetHandsForRole(role, scratchHands);
                return "lines drawn for " + scratchHands.Count + " hand(s)";

            case ParticipantHandAssociation.ParticipantRole.Parent:
                if (association == null
                    || !association.TryGetFace(role, out _))
                {
                    return "no face -> spawn disabled";
                }
                if (stackGame == null) return "stackGame missing";
                if (stackGame.IsGameOver) return "round over";
                if (stackGame.IsMoving) return "moving block -> raise to steer";
                return "idle -> raise to spawn";

            default:
                return "";
        }
    }

    // ------------------------------------------------------------------
    // Drawing primitives
    // ------------------------------------------------------------------

    private void DrawLine(ref float y, float x, string text, float height, GUIStyle style, Color color)
    {
        Color prev = style.normal.textColor;
        style.normal.textColor = color;
        GUI.Label(new Rect(x, y, panelWidth, height), text, style);
        style.normal.textColor = prev;
        y += height;
    }

    private void DrawLabel(Vector2 position, string text, Color color)
    {
        Color prev = labelStyle.normal.textColor;
        labelStyle.normal.textColor = color;
        GUI.Label(new Rect(position.x, position.y, 400f, 18f), text, labelStyle);
        labelStyle.normal.textColor = prev;
    }

    private void DrawRectOutline(Rect rect, Color color, float thickness)
    {
        Color prev = GUI.color;
        GUI.color = color;

        GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, rect.width, thickness), white);
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMax - thickness, rect.width, thickness), white);
        GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, thickness, rect.height), white);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.yMin, thickness, rect.height), white);

        GUI.color = prev;
    }

    private void DrawCircle(Vector2 centre, float radius, Color color)
    {
        Color prev = GUI.color;
        GUI.color = color;

        // Draw a crosshair plus a filled square; simple and crisp.
        GUI.DrawTexture(new Rect(centre.x - radius, centre.y - 1f, radius * 2f, 2f), white);
        GUI.DrawTexture(new Rect(centre.x - 1f, centre.y - radius, 2f, radius * 2f), white);

        GUI.color = prev;
    }

    private Rect NormalizedToScreen(Rect normalized, Rect preview)
    {
        return new Rect(
            preview.x + normalized.xMin * preview.width,
            preview.y + normalized.yMin * preview.height,
            normalized.width * preview.width,
            normalized.height * preview.height);
    }

    private Vector2 NormalizedToScreenPoint(Vector2 normalized, Rect preview)
    {
        return new Vector2(
            preview.x + normalized.x * preview.width,
            preview.y + normalized.y * preview.height);
    }

    private Color RoleColor(ParticipantHandAssociation.ParticipantRole role)
    {
        switch (role)
        {
            case ParticipantHandAssociation.ParticipantRole.Child: return childColor;
            case ParticipantHandAssociation.ParticipantRole.Barber: return barberColor;
            case ParticipantHandAssociation.ParticipantRole.Parent: return parentColor;
            default: return unboundColor;
        }
    }
}