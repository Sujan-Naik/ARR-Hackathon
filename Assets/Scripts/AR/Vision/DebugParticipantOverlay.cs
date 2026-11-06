using AR.Vision;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

[DefaultExecutionOrder(300)]
public class DebugParticipantOverlay : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private VisionPipeline vision;
    [SerializeField] private ParticipantHandAssociation association;
    [SerializeField] private StackGame stackGame;
    [SerializeField] private BarberHandsController barberHands;

    [Header("Visibility")]
    [SerializeField] private bool showOverlay = true;
    [SerializeField] private KeyCode toggleKey = KeyCode.F1;

    [Header("Face boxes")]
    [SerializeField] private bool drawFaceBoxes = true;
    [SerializeField, Min(1f)] private float faceBoxThickness = 3f;
    [SerializeField] private bool drawAllFaces = true;

    [Header("Hand markers")]
    [SerializeField] private bool drawHandMarkers = true;
    [SerializeField, Min(2f)] private float handMarkerRadius = 7f;
    [SerializeField] private bool drawUnassociatedHands = true;

    [Header("Panel")]
    [SerializeField] private bool drawStatusPanel = true;
    [SerializeField] private int panelWidth = 360;
    [SerializeField] private int panelPadding = 10;

    [Header("Role menu")]
    [SerializeField] private bool enableRoleMenu = true;
    [SerializeField] private KeyCode fallbackMenuKey = KeyCode.M;

    [Header("Colours")]
    [SerializeField] private Color childColor = new Color(0.25f, 0.75f, 1f);
    [SerializeField] private Color barberColor = new Color(1f, 0.6f, 0.2f);
    [SerializeField] private Color parentColor = new Color(0.55f, 1f, 0.45f);
    [SerializeField] private Color unboundColor = new Color(0.6f, 0.6f, 0.6f);
    [SerializeField] private Color warnColor = new Color(1f, 0.78f, 0.12f);

    private readonly List<Observation> scratchHands = new List<Observation>();
    private readonly Dictionary<int, Observation> faceById = new Dictionary<int, Observation>();
    private readonly Dictionary<int, Observation> handById = new Dictionary<int, Observation>();
    private readonly List<Rect> faceScreenRects = new List<Rect>();

    private Texture2D white;
    private GUIStyle labelStyle;
    private GUIStyle headerStyle;
    private GUIStyle panelStyle;
    private GUIStyle menuItemStyle;
    private GUIStyle menuHeaderStyle;
    private int frameCounter;

    // Menu state: active face (TrackId), target screen rect, hit test rects.
    private int activeFaceId = -1;
    private Rect activeFaceScreenRect;
    private readonly List<KeyValuePair<Rect, ParticipantHandAssociation.ParticipantRole?>> menuHitRects =
        new List<KeyValuePair<Rect, ParticipantHandAssociation.ParticipantRole?>>();

    private static readonly ParticipantHandAssociation.ParticipantRole[] Roles =
    {
        ParticipantHandAssociation.ParticipantRole.Child,
        ParticipantHandAssociation.ParticipantRole.Barber,
        ParticipantHandAssociation.ParticipantRole.Parent,
    };

    private void Awake()
    {
        if (vision == null) vision = FindObjectOfType<VisionStub>() ?? FindObjectOfType<VisionPipeline>();
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

        Rect preview = GetPreviewScreenRect();

        if (drawFaceBoxes)
            DrawFaces(preview);

        if (drawHandMarkers)
            DrawHands(preview);

        if (drawStatusPanel)
            DrawStatusPanel(preview);

        if (enableRoleMenu)
            HandleRoleMenu(preview);
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

        menuHeaderStyle = new GUIStyle(labelStyle)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft,
        };

        menuItemStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(6, 6, 2, 2),
        };
        menuItemStyle.normal.textColor = Color.white;
    }

    private Rect GetPreviewScreenRect()
    {
        if (vision == null) return new Rect(0f, 0f, Screen.width, Screen.height);

        RectTransform rt = vision.transform as RectTransform;
        if (rt == null) return new Rect(0f, 0f, Screen.width, Screen.height);

        Vector3[] corners = new Vector3[4];
        rt.GetWorldCorners(corners);

        float x = corners[0].x;
        float y = Screen.height - corners[2].y;
        float w = corners[2].x - corners[0].x;
        float h = corners[2].y - corners[0].y;

        if (w <= 1f || h <= 1f)
            return new Rect(0f, 0f, Screen.width, Screen.height);

        return new Rect(x, y, w, h);
    }

    // ------------------------------------------------------------------
    // Face drawing
    // ------------------------------------------------------------------

    private void DrawFaces(Rect preview)
    {
        faceScreenRects.Clear();

        // 1) Role-bound faces (draw first so they sit under the unbound boxes if overlapping).
        if (association != null)
        {
            for (int r = 0; r < Roles.Length; r++)
            {
                ParticipantHandAssociation.ParticipantRole role = Roles[r];
                if (!association.TryGetFace(role, out Observation face)) continue;

                Rect box = NormalizedToScreen(face.NormalizedRect, preview);
                faceScreenRects.Add(box);

                Color color = RoleColor(role);
                DrawRectOutline(box, color, faceBoxThickness);
                DrawLabel(
                    new Vector2(box.x, box.y - 18f),
                    role.ToString().ToUpperInvariant()
                        + "  conf " + face.Confidence.ToString("0.00")
                        + "  right-click to change",
                    color);
            }
        }

        // 2) Every other tracked face, in grey, so the operator can right-click it too.
        if (drawAllFaces)
        {
            foreach (var pair in faceById)
            {
                Observation face = pair.Value;

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
                faceScreenRects.Add(box);

                DrawRectOutline(box, unboundColor, 1f);
                DrawLabel(new Vector2(box.x, box.y - 16f),
                    "face " + face.TrackId + "  right-click to assign",
                    unboundColor);
            }
        }
    }

    // ------------------------------------------------------------------
    // Hand drawing (unchanged behaviour)
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

                DrawLabel(
                    new Vector2(screen.x + handMarkerRadius + 4f, screen.y - 8f),
                    role + " hand  " + HandState(hand),
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
    // Right-click role menu
    // ------------------------------------------------------------------

    private void HandleRoleMenu(Rect preview)
    {
        Event e = Event.current;
        if (e == null) return;

        // Open the menu on right-click over a face box.
        if (e.type == EventType.MouseDown && e.button == 1)
        {
            for (int i = 0; i < faceScreenRects.Count; i++)
            {
                if (faceScreenRects[i].Contains(e.mousePosition))
                {
                    // Match by rect back to a face TrackId.
                    int trackId = -1;
                    foreach (var pair in faceById)
                    {
                        Rect box = NormalizedToScreen(pair.Value.NormalizedRect, preview);
                        if (box.Contains(e.mousePosition)) { trackId = pair.Key; break; }
                    }
                    if (trackId >= 0)
                    {
                        activeFaceId = trackId;
                        activeFaceScreenRect = faceScreenRects[i];
                        BuildMenuHitRects();
                        e.Use();
                        return;
                    }
                }
            }
            // Click outside: close menu.
            activeFaceId = -1;
            menuHitRects.Clear();
        }

        // Fallback key: opens menu on the first tracked face (useful if right-click is captured elsewhere).
        if (Input.GetKeyDown(fallbackMenuKey) && activeFaceId < 0 && faceById.Count > 0)
        {
            foreach (var pair in faceById) { activeFaceId = pair.Key; break; }
            Rect box = NormalizedToScreen(faceById[activeFaceId].NormalizedRect, preview);
            activeFaceScreenRect = box;
            BuildMenuHitRects();
        }

        if (activeFaceId < 0) return;

        // Draw the menu.
        DrawRoleMenu();

        // Handle clicks inside the menu.
        if (e.type == EventType.MouseDown && e.button == 0)
        {
            for (int i = 0; i < menuHitRects.Count; i++)
            {
                if (!menuHitRects[i].Key.Contains(e.mousePosition)) continue;

                ParticipantHandAssociation.ParticipantRole? chosen = menuHitRects[i].Value;
                ApplyMenuChoice(chosen);
                activeFaceId = -1;
                menuHitRects.Clear();
                e.Use();
                return;
            }
        }

        // Escape closes the menu.
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            activeFaceId = -1;
            menuHitRects.Clear();
            e.Use();
        }
    }

    private void BuildMenuHitRects()
    {
        menuHitRects.Clear();

        float width = 200f;
        float lineHeight = 22f;
        float pad = 6f;
        float headerHeight = 22f;

        // Anchor below the face box, clamped to screen.
        float x = Mathf.Clamp(activeFaceScreenRect.x,
                              0f, Screen.width - width);
        float y = activeFaceScreenRect.yMax + 4f;
        if (y + headerHeight + lineHeight * 4f > Screen.height)
            y = Mathf.Max(0f, activeFaceScreenRect.y - (headerHeight + lineHeight * 4f) - 4f);

        float cy = y + headerHeight;

        // "Clear" entry
        menuHitRects.Add(new KeyValuePair<Rect, ParticipantHandAssociation.ParticipantRole?>(
            new Rect(x + pad, cy + pad, width - pad * 2f, lineHeight),
            null));
        cy += lineHeight;

        for (int i = 0; i < Roles.Length; i++)
        {
            menuHitRects.Add(new KeyValuePair<Rect, ParticipantHandAssociation.ParticipantRole?>(
                new Rect(x + pad, cy + pad, width - pad * 2f, lineHeight),
                Roles[i]));
            cy += lineHeight;
        }
    }

    private void DrawRoleMenu()
    {
        if (activeFaceId < 0) return;

        float width = 200f;
        float lineHeight = 22f;
        float pad = 6f;
        float headerHeight = 22f;

        Rect header = new Rect(
            menuHitRects[0].Key.x - pad,
            menuHitRects[0].Key.y - pad - headerHeight,
            width,
            headerHeight + lineHeight * menuHitRects.Count + pad * 2f);

        Color prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.88f);
        GUI.DrawTexture(header, white);
        GUI.color = prev;

        Color prevText = menuHeaderStyle.normal.textColor;
        menuHeaderStyle.normal.textColor = Color.white;
        GUI.Label(new Rect(header.x + 6f, header.y + 2f, header.width - 12f, headerHeight),
            "face " + activeFaceId + " -> assign role", menuHeaderStyle);
        menuHeaderStyle.normal.textColor = prevText;

        for (int i = 0; i < menuHitRects.Count; i++)
        {
            Rect r = menuHitRects[i].Key;
            ParticipantHandAssociation.ParticipantRole? role = menuHitRects[i].Value;

            bool hover = r.Contains(Event.current.mousePosition);
            if (hover)
            {
                prev = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.12f);
                GUI.DrawTexture(r, white);
                GUI.color = prev;
            }

            string text;
            if (role.HasValue)
            {
                // Mark current binding.
                bool current = association != null
                    && association.TryGetFace(role.Value, out Observation f)
                    && f.TrackId == activeFaceId;
                text = (current ? "\u2713 " : "   ") + role.Value.ToString();
                menuItemStyle.normal.textColor = RoleColor(role.Value);
            }
            else
            {
                text = "   Clear manual binding";
                menuItemStyle.normal.textColor = unboundColor;
            }

            GUI.Label(r, text, menuItemStyle);
        }
    }

    private void ApplyMenuChoice(ParticipantHandAssociation.ParticipantRole? chosen)
    {
        if (association == null) return;

        if (!chosen.HasValue)
        {
            // Clear: unbind whichever role currently holds this face.
            for (int i = 0; i < Roles.Length; i++)
            {
                if (association.TryGetFace(Roles[i], out Observation f)
                    && f.TrackId == activeFaceId)
                {
                    association.AssignRole(Roles[i], -1);
                    break;
                }
            }
            return;
        }

        association.AssignRole(chosen.Value, activeFaceId);
    }

    // ------------------------------------------------------------------
    // Status panel (unchanged)
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

        bool ready = vision != null && vision.IsReady();
        DrawLine(ref y, x,
            "vision: " + (ready ? "ready" : "NOT READY")
            + "   frames observed: " + frameCounter,
            lineHeight, labelStyle, ready ? Color.white : warnColor);

        bool rolesAssigned = association != null && association.RolesAssigned;
        DrawLine(ref y, x,
            "roles assigned: " + (rolesAssigned ? "YES" : "no (need 2 faces: Child+Barber)"),
            lineHeight, labelStyle, rolesAssigned ? Color.white : warnColor);

        DrawLine(ref y, x,
            "faces tracked: " + faceById.Count
            + "   hands tracked: " + handById.Count,
            lineHeight, labelStyle, Color.white);

        y += 4f;

        for (int r = 0; r < Roles.Length; r++)
        {
            ParticipantHandAssociation.ParticipantRole role = Roles[r];
            Color color = RoleColor(role);

            DrawLine(ref y, x, "--- " + role.ToString().ToUpperInvariant() + " ---",
                lineHeight, labelStyle, color);

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

            DrawLine(ref y, x, "  status: " + RoleStatus(role), lineHeight, labelStyle, color);
            y += 4f;
        }

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

            var directorForHud = FindObjectOfType<Stacking.StackingDirector>();
            DrawLine(ref y, x,
                "  armed: " + (directorForHud != null && directorForHud.IsArmed ? "YES" : "no"),
                lineHeight, labelStyle, Color.white);

            var sweepForHud = FindObjectOfType<Stacking.BlockSweepController>();
            if (sweepForHud != null)
            {
                DrawLine(ref y, x,
                    "  steer hand: " + sweepForHud.SteerHand
                    + "   target " + sweepForHud.SteerTarget.ToString("0.00")
                    + "   sweeping " + (sweepForHud.IsSweeping ? "yes" : "no"),
                    lineHeight, labelStyle, Color.white);
            }
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
                    return "optional -> auto-sweep";
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