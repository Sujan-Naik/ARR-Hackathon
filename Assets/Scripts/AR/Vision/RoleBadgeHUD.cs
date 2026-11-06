using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using AR.Vision;

/// <summary>
/// Always-on in-game readout of who is assigned which role.
///
///  • Top-right status strip: one row per role with colour chip, name,
///    bound face id and AUTO/MANUAL tag.
///  • Screen-space rings drawn over each bound face, using the same
///    colour palette as the rest of the demo, so during play the
///    participant can see "I am the BARBER" without F1.
///
/// Everything is uGUI, rebuilt only when a binding changes.
/// Place this on the Canvas (or any child of it).
/// </summary>
[DefaultExecutionOrder(250)]
public class RoleBadgeHUD : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ParticipantHandAssociation association;
    [SerializeField] private VisionPipeline vision;

    [Header("Layout")]
    [SerializeField] private Vector2 panelMargin = new Vector2(12f, 12f);
    [SerializeField] private float rowHeight = 34f;
    [SerializeField] private float panelWidth = 260f;
    [SerializeField] private float faceBadgeRadius = 46f;
    [SerializeField] private bool drawFaceBadges = true;

    [Header("Colours")]
    [SerializeField] private Color childColor = new Color(0.25f, 0.75f, 1f);
    [SerializeField] private Color barberColor = new Color(1f, 0.6f, 0.2f);
    [SerializeField] private Color parentColor = new Color(0.55f, 1f, 0.45f);
    [SerializeField] private Color unboundColor = new Color(0.6f, 0.6f, 0.6f);
    [SerializeField] private Color manualTagColor = new Color(1f, 0.85f, 0.25f);
    [SerializeField] private Color autoTagColor = new Color(0.75f, 0.85f, 1f);

    [Header("Face badge")]
    [SerializeField] private float badgeThickness = 3f;
    [SerializeField] private float labelOffsetY = 6f;

    // ---- runtime ----
    private ParticipantHandAssociation.ParticipantRole[] roles;
    private RoleRow[] rows;
    private FaceBadge[] badges;
    private Canvas canvas;
    private RectTransform canvasRect;
    private Camera uiCamera; // null for Screen Space - Overlay

    private readonly List<ParticipantHandAssociation.RoleBinding> bindings =
        new List<ParticipantHandAssociation.RoleBinding>(3);

    private static readonly ParticipantHandAssociation.ParticipantRole[] RoleOrder =
    {
        ParticipantHandAssociation.ParticipantRole.Child,
        ParticipantHandAssociation.ParticipantRole.Barber,
        ParticipantHandAssociation.ParticipantRole.Parent,
    };

    private sealed class RoleRow
    {
        public GameObject Root;
        public Image Chip;
        public Text Name;
        public Text Binding;
        public Text Tag;
    }

    private sealed class FaceBadge
    {
        public GameObject Root;
        public RectTransform Rect;
        public Image Ring;
        public Text Label;
        public int FaceTrackId = -1;
        public ParticipantHandAssociation.ParticipantRole Role;
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    private void Awake()
    {
        if (association == null) association = FindObjectOfType<ParticipantHandAssociation>();
        if (vision == null) vision = FindObjectOfType<VisionPipeline>();

        canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("RoleBadgeHUD must live under a Canvas.", this);
            enabled = false;
            return;
        }
        canvasRect = canvas.rootCanvas != null
            ? canvas.rootCanvas.transform as RectTransform
            : canvas.transform as RectTransform;
        if (canvas.rootCanvas != null && canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
            uiCamera = canvas.rootCanvas.worldCamera;

        BuildStatusStrip();
        BuildFaceBadges();
    }

    private void OnEnable()
    {
        if (association != null)
        {
            association.RoleChanged += OnRoleChanged;
            association.RolesAssignedChanged += OnRolesAssignedChanged;
            association.HandAssociated += OnHandAssociated;
            association.HandUnassociated += OnHandUnassociated;
        }
    }

    private void OnDisable()
    {
        if (association != null)
        {
            association.RoleChanged -= OnRoleChanged;
            association.RolesAssignedChanged -= OnRolesAssignedChanged;
            association.HandAssociated -= OnHandAssociated;
            association.HandUnassociated -= OnHandUnassociated;
        }
    }

    private void Update()
    {
        // Ring positions are the only thing that needs per-frame work.
        UpdateFaceBadgePositions();
    }

    // ------------------------------------------------------------------
    // Events
    // ------------------------------------------------------------------

    private void OnRolesAssignedChanged(bool assigned)
    {
        RefreshStatusStrip();
        RefreshFaceBadges();
    }

    private void OnRoleChanged(ParticipantHandAssociation.RoleBinding binding)
    {
        RefreshStatusStrip();
        RefreshFaceBadges();
    }

    private void OnHandAssociated(int handId, ParticipantHandAssociation.ParticipantRole role)
    {
        RefreshStatusStrip();
    }

    private void OnHandUnassociated(int handId)
    {
        RefreshStatusStrip();
    }

    // ------------------------------------------------------------------
    // Status strip
    // ------------------------------------------------------------------

    private void BuildStatusStrip()
    {
        roles = RoleOrder;
        rows = new RoleRow[roles.Length];

        GameObject panel = new GameObject("RoleBadgePanel", typeof(RectTransform));
        RectTransform panelRect = (RectTransform)panel.transform;
        panelRect.SetParent(canvasRect, false);
        panelRect.anchorMin = new Vector2(1f, 1f);
        panelRect.anchorMax = new Vector2(1f, 1f);
        panelRect.pivot = new Vector2(1f, 1f);
        panelRect.anchoredPosition = new Vector2(-panelMargin.x, -panelMargin.y);
        panelRect.sizeDelta = new Vector2(panelWidth, rowHeight * roles.Length + 12f);

        Image bg = panel.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.65f);
        bg.raycastTarget = false;

        for (int i = 0; i < roles.Length; i++)
        {
            float y = -6f - i * rowHeight;

            GameObject row = new GameObject("Row_" + roles[i], typeof(RectTransform));
            RectTransform rowRect = (RectTransform)row.transform;
            rowRect.SetParent(panelRect, false);
            rowRect.anchorMin = new Vector2(0f, 1f);
            rowRect.anchorMax = new Vector2(1f, 1f);
            rowRect.pivot = new Vector2(0f, 1f);
            rowRect.anchoredPosition = new Vector2(0f, y);
            rowRect.sizeDelta = new Vector2(0f, rowHeight);

            Image chip = CreateImage("Chip", rowRect,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(8f, 0f), new Vector2(20f, 20f),
                RoleColor(roles[i]));

            Text nameText = CreateText("Name", rowRect,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(36f, 0f), new Vector2(90f, rowHeight),
                roles[i].ToString().ToUpperInvariant(),
                TextAnchor.MiddleLeft, 13, FontStyle.Bold, Color.white);

            Text bindingText = CreateText("Binding", rowRect,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(124f, 0f), new Vector2(80f, rowHeight),
                "", TextAnchor.MiddleLeft, 12, FontStyle.Normal,
                new Color(0.9f, 0.9f, 0.9f));

            Text tag = CreateText("Tag", rowRect,
                new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-8f, 0f), new Vector2(60f, rowHeight),
                "", TextAnchor.MiddleRight, 11, FontStyle.Bold, autoTagColor);

            rows[i] = new RoleRow
            {
                Root = row,
                Chip = chip,
                Name = nameText,
                Binding = bindingText,
                Tag = tag,
            };
        }

        RefreshStatusStrip();
    }

    private void RefreshStatusStrip()
    {
        if (association == null || rows == null) return;

        for (int i = 0; i < rows.Length; i++)
        {
            ParticipantHandAssociation.ParticipantRole role = roles[i];
            ParticipantHandAssociation.RoleBinding binding = association.GetBinding(role);
            RoleRow row = rows[i];

            row.Chip.color = binding.IsBound ? RoleColor(role) : unboundColor;

            if (binding.IsBound)
            {
                string label = string.IsNullOrEmpty(binding.Label)
                    ? ("face #" + binding.FaceTrackId)
                    : (binding.Label + " #" + binding.FaceTrackId);
                row.Binding.text = label;
                row.Binding.color = Color.white;
                row.Tag.text = binding.IsManual ? "MANUAL" : "AUTO";
                row.Tag.color = binding.IsManual ? manualTagColor : autoTagColor;
            }
            else
            {
                row.Binding.text = "waiting…";
                row.Binding.color = new Color(0.7f, 0.7f, 0.7f);
                row.Tag.text = association.IsRoleManual(role) ? "MANUAL" : "AUTO";
                row.Tag.color = association.IsRoleManual(role) ? manualTagColor : autoTagColor;
            }
        }
    }

    // ------------------------------------------------------------------
    // Face badges
    // ------------------------------------------------------------------

    private void BuildFaceBadges()
    {
        badges = new FaceBadge[roles.Length];
        for (int i = 0; i < roles.Length; i++)
        {
            GameObject root = new GameObject("FaceBadge_" + roles[i], typeof(RectTransform));
            RectTransform rect = (RectTransform)root.transform;
            rect.SetParent(canvasRect, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(faceBadgeRadius * 2f, faceBadgeRadius * 2f);
            root.SetActive(false);

            // Ring made from four thin Images so we don't need a sprite asset.
            Image ringTop = CreateEdgeImage("Top", rect, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -badgeThickness), new Vector2(0f, 0f), RoleColor(roles[i]));
            Image ringBottom = CreateEdgeImage("Bottom", rect, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 0f), new Vector2(0f, badgeThickness), RoleColor(roles[i]));
            Image ringLeft = CreateEdgeImage("Left", rect, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(badgeThickness, 0f), RoleColor(roles[i]));
            Image ringRight = CreateEdgeImage("Right", rect, new Vector2(1f, 0f), new Vector2(1f, 1f),
                new Vector2(-badgeThickness, 0f), new Vector2(0f, 0f), RoleColor(roles[i]));

            Text label = CreateText("Label", rect,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, labelOffsetY), new Vector2(160f, 22f),
                roles[i].ToString().ToUpperInvariant(),
                TextAnchor.LowerCenter, 13, FontStyle.Bold, RoleColor(roles[i]));

            badges[i] = new FaceBadge
            {
                Root = root,
                Rect = rect,
                Ring = ringTop, // only used for colour; four edges driven below
                Label = label,
                Role = roles[i],
            };
        }

        RefreshFaceBadges();
    }

    private void RefreshFaceBadges()
    {
        if (association == null || badges == null) return;

        for (int i = 0; i < badges.Length; i++)
        {
            ParticipantHandAssociation.RoleBinding binding =
                association.GetBinding(badges[i].Role);

            FaceBadge badge = badges[i];
            badge.FaceTrackId = binding.FaceTrackId;

            bool visible = drawFaceBadges && binding.IsBound && vision != null;
            if (badge.Root.activeSelf != visible)
                badge.Root.SetActive(visible);

            Color c = RoleColor(badge.Role);
            SetRingColor(badge, c);
        }
    }

    private void UpdateFaceBadgePositions()
    {
        if (badges == null || vision == null) return;

        // Find the webcam preview RectTransform to map normalized -> screen.
        RectTransform previewRect = vision.transform as RectTransform;
        if (previewRect == null) return;

        Vector3[] corners = new Vector3[4];
        previewRect.GetWorldCorners(corners);

        for (int i = 0; i < badges.Length; i++)
        {
            FaceBadge badge = badges[i];
            if (!badge.Root.activeSelf) continue;
            if (!TryGetFace(badge.FaceTrackId, out Observation face)) continue;

            Rect n = face.NormalizedRect;
            // Map normalized centre to preview-local, then to screen, then to canvas local.
            Vector2 top = Vector2.Lerp(corners[1], corners[2], n.center.x);
            Vector2 bottom = Vector2.Lerp(corners[0], corners[3], n.center.x);
            Vector2 centreWorld = Vector2.Lerp(top, bottom, n.center.y);

            // Approximate radius from the face box height in preview pixels.
            float faceHeightPx = Mathf.Abs(corners[2].y - corners[0].y) * n.height;
            float radius = Mathf.Max(faceHeightPx * 0.75f, faceBadgeRadius * 0.5f);

            Vector2 local;
            if (canvas.rootCanvas != null
                && canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                // World corners here are screen pixels; canvas local == screen space.
                local = centreWorld;
            }
            else
            {
                // Convert world position into canvas local space.
                local = canvasRect.InverseTransformPoint(centreWorld);
            }

            badge.Rect.anchoredPosition = local;
            badge.Rect.sizeDelta = new Vector2(radius * 2f, radius * 2f);
        }
    }

    private bool TryGetFace(int faceTrackId, out Observation face)
    {
        if (faceTrackId < 0 || vision == null) { face = default; return false; }

        IReadOnlyList<Observation> all = vision.Observations;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Kind == ObservationKind.Face && all[i].TrackId == faceTrackId)
            {
                face = all[i];
                return true;
            }
        }
        face = default;
        return false;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

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

    private static void SetRingColor(FaceBadge badge, Color color)
    {
        // Recolour the four edge images (children 0..3).
        Transform t = badge.Rect;
        for (int i = 0; i < t.childCount; i++)
        {
            Image img = t.GetChild(i).GetComponent<Image>();
            if (img != null) img.color = color;
        }
        badge.Label.color = color;
    }

    private static Image CreateImage(string name, RectTransform parent,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPos,
        Vector2 size, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = anchoredPos;
        rect.sizeDelta = size;
        Image img = go.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    private static Image CreateEdgeImage(string name, RectTransform parent,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
        Image img = go.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    private static Text CreateText(string name, RectTransform parent,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPos, Vector2 size,
        string content, TextAnchor align, int fontSize, FontStyle style, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Text));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(
            align == TextAnchor.UpperRight || align == TextAnchor.MiddleRight || align == TextAnchor.LowerRight ? 1f : 0f,
            0.5f);
        rect.anchoredPosition = anchoredPos;
        rect.sizeDelta = size;

        Text text = go.GetComponent<Text>();
        text.text = content;
        text.alignment = align;
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.color = color;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        // Use the built-in font so this works without any asset assignment.
        Font builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (builtin != null) text.font = builtin;
        return text;
    }
}