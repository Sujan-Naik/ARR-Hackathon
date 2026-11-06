using System.Collections.Generic;
using UnityEngine;
using AR.Vision;

/// <summary>
/// Draws the Barber's hands as abstract line skeletons at their real positions.
/// Not used to drive tilt directly; StackingDirector reads BarberHandsInput
/// for proximity to the child's head.
/// </summary>
[DefaultExecutionOrder(210)]
public class BarberHandsController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ParticipantHandAssociation association;
    [SerializeField] private Camera renderCamera;

    [Header("Hand visibility")]
    [SerializeField] private bool showBarberHands = true;

    [Header("3D positioning")]
    [SerializeField, Min(0.1f)] private float cameraDistance = 4f;
    [SerializeField, Min(0.001f)] private float lineWidth = 0.012f;
    [SerializeField] private float depthOffset = 0f;

    [Header("Appearance")]
    [SerializeField] private Color lineColor = new Color(0.95f, 0.62f, 0.43f, 1f);
    [SerializeField] private Color fingertipColor = new Color(1f, 0.82f, 0.79f, 1f);
    [SerializeField, Min(0f)] private float fingertipRadius = 0.02f;

    [Header("Smoothing")]
    [SerializeField, Min(0.1f)] private float positionSmoothing = 15f;

    private static readonly int[][] BoneChains =
    {
        new[] { 0, 1, 2, 3, 4 },
        new[] { 0, 5, 6, 7, 8 },
        new[] { 0, 9, 10, 11, 12 },
        new[] { 0, 13, 14, 15, 16 },
        new[] { 0, 17, 18, 19, 20 },
        new[] { 5, 9, 13, 17 },
    };

    private const int JointCount = 21;

    private readonly Dictionary<int, HandLines> handModels =
        new Dictionary<int, HandLines>();
    private readonly List<Observation> barberHands = new List<Observation>();
    private readonly HashSet<int> visibleThisFrame = new HashSet<int>();

    private Material lineMaterial;

    private sealed class HandLines
    {
        public GameObject Root;
        public LineRenderer[] Chains;
        public Transform[] Tips;
        public Vector3[] SmoothedJoints;
        public bool Initialized;
    }

    private void Awake()
    {
        if (association == null) association = FindObjectOfType<ParticipantHandAssociation>();
        if (renderCamera == null) renderCamera = Camera.main;
        CreateMaterial();
    }

    private void Update()
    {
        if (!showBarberHands || PauseOverlay.IsPaused || association == null)
        {
            HideAllHands();
            return;
        }

        if (renderCamera == null) renderCamera = Camera.main;
        if (renderCamera == null || !association.RolesAssigned)
        {
            HideAllHands();
            return;
        }

        association.GetHandsForRole(
            ParticipantHandAssociation.ParticipantRole.Barber, barberHands);

        visibleThisFrame.Clear();

        for (int i = 0; i < barberHands.Count; i++)
        {
            Observation observation = barberHands[i];
            visibleThisFrame.Add(observation.TrackId);

            if (!handModels.TryGetValue(observation.TrackId, out HandLines model))
            {
                model = CreateHandLines(observation.TrackId);
                handModels.Add(observation.TrackId, model);
            }

            UpdateHand(model, observation);
        }

        RemoveLostHands();
    }

    private void CreateMaterial()
    {
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null)
        {
            Debug.LogError("Could not find a shader for the barber hand lines.", this);
            return;
        }
        lineMaterial = new Material(shader) { color = lineColor };
    }

    private HandLines CreateHandLines(int trackId)
    {
        HandLines hand = new HandLines
        {
            Root = new GameObject("Barber Hand Lines " + trackId),
            Chains = new LineRenderer[BoneChains.Length],
            Tips = new Transform[5],
            SmoothedJoints = new Vector3[JointCount]
        };

        hand.Root.transform.SetParent(transform, false);

        for (int c = 0; c < BoneChains.Length; c++)
        {
            GameObject chainObject = new GameObject("Chain " + c);
            chainObject.transform.SetParent(hand.Root.transform, false);

            LineRenderer line = chainObject.AddComponent<LineRenderer>();
            line.positionCount = BoneChains[c].Length;
            line.useWorldSpace = true;
            line.startWidth = lineWidth;
            line.endWidth = lineWidth;
            line.numCapVertices = 2;
            line.numCornerVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;

            if (lineMaterial != null) line.sharedMaterial = lineMaterial;
            hand.Chains[c] = line;

            if (c < 5 && fingertipRadius > 0f && fingertipColor.a > 0f)
            {
                GameObject tip = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                tip.name = "Tip " + c;
                tip.transform.SetParent(hand.Root.transform, false);
                tip.transform.localScale = Vector3.one * fingertipRadius * 2f;

                Renderer tipRenderer = tip.GetComponent<Renderer>();
                if (tipRenderer != null)
                {
                    Material tipMaterial = new Material(lineMaterial != null
                        ? lineMaterial.shader : Shader.Find("Standard"));
                    tipMaterial.color = fingertipColor;
                    tipRenderer.sharedMaterial = tipMaterial;
                    tipRenderer.shadowCastingMode =
                        UnityEngine.Rendering.ShadowCastingMode.Off;
                    tipRenderer.receiveShadows = false;
                }

                Collider tipCollider = tip.GetComponent<Collider>();
                if (tipCollider != null) Destroy(tipCollider);

                hand.Tips[c] = tip.transform;
            }
        }

        hand.Root.SetActive(false);
        return hand;
    }

    private void UpdateHand(HandLines model, Observation observation)
    {
        Vector2[] keypoints = observation.Keypoints;
        if (keypoints == null || keypoints.Length < JointCount)
        {
            if (model.Root.activeSelf) model.Root.SetActive(false);
            return;
        }

        float depth = cameraDistance + depthOffset;
        float t = model.Initialized ? Time.deltaTime * positionSmoothing : 1f;

        for (int j = 0; j < JointCount; j++)
        {
            Vector3 viewport = new Vector3(
                Mathf.Clamp01(keypoints[j].x),
                1f - Mathf.Clamp01(keypoints[j].y),
                depth);

            Vector3 target = renderCamera.ViewportToWorldPoint(viewport);

            model.SmoothedJoints[j] = model.Initialized
                ? Vector3.Lerp(model.SmoothedJoints[j], target, t)
                : target;
        }

        model.Initialized = true;

        for (int c = 0; c < BoneChains.Length; c++)
        {
            LineRenderer line = model.Chains[c];
            int[] chain = BoneChains[c];
            for (int k = 0; k < chain.Length; k++)
                line.SetPosition(k, model.SmoothedJoints[chain[k]]);
        }

        for (int f = 0; f < model.Tips.Length; f++)
        {
            Transform tip = model.Tips[f];
            if (tip == null) continue;
            int joint = BoneChains[f][BoneChains[f].Length - 1];
            tip.position = model.SmoothedJoints[joint];
        }

        if (!model.Root.activeSelf) model.Root.SetActive(true);
    }

    private void RemoveLostHands()
    {
        List<int> removeIds = new List<int>();
        foreach (KeyValuePair<int, HandLines> pair in handModels)
            if (!visibleThisFrame.Contains(pair.Key)) removeIds.Add(pair.Key);

        for (int i = 0; i < removeIds.Count; i++)
        {
            int id = removeIds[i];
            if (handModels.TryGetValue(id, out HandLines hand))
            {
                if (hand.Root != null) Destroy(hand.Root);
                handModels.Remove(id);
            }
        }
    }

    private void HideAllHands()
    {
        foreach (KeyValuePair<int, HandLines> pair in handModels)
            if (pair.Value.Root != null) pair.Value.Root.SetActive(false);
    }

    public void SetHandsVisible(bool visible)
    {
        showBarberHands = visible;
        if (!visible) HideAllHands();
    }

    /// <summary>Recolours the barber line material. Useful to sync with a role HUD palette.</summary>
    public void SetLineColor(Color color)
    {
        lineColor = color;
        if (lineMaterial != null) lineMaterial.color = color;
    }

    private void OnDisable() => HideAllHands();

    private void OnDestroy()
    {
        foreach (KeyValuePair<int, HandLines> pair in handModels)
            if (pair.Value.Root != null) Destroy(pair.Value.Root);

        handModels.Clear();
        if (lineMaterial != null) Destroy(lineMaterial);
    }
}