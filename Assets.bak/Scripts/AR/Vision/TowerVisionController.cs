using System.Collections.Generic;
using UnityEngine;
using AR.Vision;

[DefaultExecutionOrder(200)]
public class TowerVisionController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ParticipantHandAssociation association;
    [SerializeField] private StackGame stackGame;
    [SerializeField] private VisionPipeline vision;
    [SerializeField] private Camera renderCamera;

    [Tooltip("Optional explicit tower transform. If empty, StackGame.TowerRoot is used.")]
    [SerializeField] private Transform towerTiltTarget;

    [Header("Parent: raise hand to send / steer block")]
    [Tooltip("Minimum upward movement in normalized screen coordinates.")]
    [SerializeField, Range(0.01f, 0.3f)]
    private float parentRaiseDistance = 0.09f;

    [Tooltip("Hand must be open to trigger a spawn/steer.")]
    [SerializeField]
    private bool parentRequiresOpenHand = true;

    [Tooltip("Minimum time between raise gestures.")]
    [SerializeField, Min(0.1f)]
    private float parentGestureCooldown = 0.8f;

    [Tooltip("If true, a raise starts a new block and the parent's hand X steers it continuously. If false, each raise nudges the moving block one cell to the right.")]
    [SerializeField]
    private bool parentSteersContinuously = true;

    [Header("Child: close hand to stop")]
    [SerializeField, Min(0.1f)]
    private float childGestureCooldown = 0.5f;

    [Header("Child: head orientation tilts tower")]
    [Tooltip("Maximum tower tilt in degrees at the edge of the child's head range.")]
    [SerializeField, Range(0f, 35f)]
    private float maximumTiltDegrees = 18f;

    [Tooltip("Maximum yaw applied to the tower (set 0 to ignore yaw).")]
    [SerializeField, Range(0f, 35f)]
    private float maximumYawDegrees = 0f;

    [SerializeField, Min(0.1f)]
    private float tiltSmoothing = 5f;

    [SerializeField]
    private bool invertTilt;

    [Tooltip("Ignore head angles below this many degrees.")]
    [SerializeField, Range(0f, 10f)]
    private float headDeadZone = 2f;

    [Header("Debug")]
    [SerializeField]
    private bool showDebugMessages;

    private readonly List<Observation> parentHands = new List<Observation>();
    private readonly List<Observation> childHands = new List<Observation>();

    private readonly Dictionary<int, Vector2> previousParentPositions =
        new Dictionary<int, Vector2>();
    private readonly Dictionary<int, bool> previousChildClosed =
        new Dictionary<int, bool>();

    private readonly HashSet<int> activeParentHands = new HashSet<int>();
    private readonly HashSet<int> activeChildHands = new HashSet<int>();

    private float lastParentGesture = -100f;
    private float lastChildGesture = -100f;

    // A "raise" latches the parent into steering mode until the child stops
    // the block. Subsequent raises just re-aim, they don't spawn a second row.
    private bool parentArmed;

    private float currentTilt;
    private float targetTilt;
    private float currentYaw;
    private float targetYaw;

    private void Awake()
    {
        if (stackGame == null) stackGame = FindObjectOfType<StackGame>();
        if (association == null) association = FindObjectOfType<ParticipantHandAssociation>();
        if (vision == null) vision = FindObjectOfType<VisionPipeline>();
        if (renderCamera == null) renderCamera = Camera.main;
    }

    private void OnEnable()
    {
        if (association != null)
            association.HandUnassociated += OnHandUnassociated;

        if (stackGame != null)
            stackGame.ScoreChanged += OnStackScoreChanged;
    }

    private void Start()
    {
        // Catch references assigned after Awake.
        if (association == null) association = FindObjectOfType<ParticipantHandAssociation>();
        if (stackGame == null) stackGame = FindObjectOfType<StackGame>();
        if (vision == null) vision = FindObjectOfType<VisionPipeline>();

        if (association != null)
        {
            association.HandUnassociated -= OnHandUnassociated;
            association.HandUnassociated += OnHandUnassociated;
        }

        if (stackGame != null)
        {
            stackGame.ScoreChanged -= OnStackScoreChanged;
            stackGame.ScoreChanged += OnStackScoreChanged;
        }
    }

    private void Update()
    {
        if (PauseOverlay.IsPaused)
            return;

        if (association == null || stackGame == null)
            return;

        if (!association.RolesAssigned)
        {
            targetTilt = 0f;
            targetYaw = 0f;
            parentArmed = false;
            UpdateTowerRotation();
            return;
        }

        association.GetHandsForRole(
            ParticipantHandAssociation.ParticipantRole.Parent,
            parentHands);

        association.GetHandsForRole(
            ParticipantHandAssociation.ParticipantRole.Child,
            childHands);

        UpdateParentControls();
        UpdateChildControls();
        UpdateChildHeadTilt();
        UpdateTowerRotation();
    }

    // ---------------------------------------------------------
    // PARENT CONTROL
    // ---------------------------------------------------------

    private void UpdateParentControls()
    {
        activeParentHands.Clear();

        bool raiseDetected = false;

        for (int i = 0; i < parentHands.Count; i++)
        {
            Observation hand = parentHands[i];
            int id = hand.TrackId;
            activeParentHands.Add(id);

            Vector2 currentPosition = hand.Center;

            if (previousParentPositions.TryGetValue(id, out Vector2 previousPosition))
            {
                // Normalized Y increases downward; a negative delta is upward motion.
                float upwardMovement = previousPosition.y - currentPosition.y;
                bool isOpen = HandGestures.IsOpen(hand);
                bool gestureValid = !parentRequiresOpenHand || isOpen;

                if (upwardMovement >= parentRaiseDistance && gestureValid)
                    raiseDetected = true;
            }

            previousParentPositions[id] = currentPosition;
        }

        RemoveMissingParentHands();

        bool raiseEdge = raiseDetected
            && Time.time - lastParentGesture >= parentGestureCooldown;

        if (raiseEdge)
            lastParentGesture = Time.time;

        // If the block isn't moving, a raise spawns the next one and arms steering.
        if (raiseEdge && !stackGame.IsGameOver && !stackGame.IsMoving)
        {
            stackGame.SpawnBlock();
            parentArmed = true;

            if (showDebugMessages)
                Debug.Log("Parent raised a hand: spawning next block.", this);
        }
        else if (raiseEdge && stackGame.IsMoving)
        {
            // Already moving: a raise re-arms steering (no second spawn).
            parentArmed = true;
        }

        if (!parentArmed || !stackGame.IsMoving)
            return;

        // Steer using the average X of every visible parent hand.
        float sum = 0f;
        int n = 0;
        for (int i = 0; i < parentHands.Count; i++)
        {
            sum += parentHands[i].Center.x;
            n++;
        }
        if (n == 0)
            return;

        float normalizedX = sum / n;

        if (parentSteersContinuously)
        {
            stackGame.SteerMovingBlock(normalizedX);
        }
        else if (raiseEdge)
        {
            stackGame.NudgeMovingBlock(1);
        }
    }

    private void RemoveMissingParentHands()
    {
        List<int> remove = new List<int>();

        foreach (int id in previousParentPositions.Keys)
        {
            if (!activeParentHands.Contains(id))
                remove.Add(id);
        }

        for (int i = 0; i < remove.Count; i++)
            previousParentPositions.Remove(remove[i]);
    }

    // ---------------------------------------------------------
    // CHILD CONTROL
    // ---------------------------------------------------------

    private void UpdateChildControls()
    {
        activeChildHands.Clear();

        for (int i = 0; i < childHands.Count; i++)
        {
            Observation hand = childHands[i];
            int id = hand.TrackId;
            activeChildHands.Add(id);

            bool closed = HandGestures.IsClosed(hand);

            previousChildClosed.TryGetValue(id, out bool wasClosed);

            // Trigger only on the open -> closed edge.
            if (closed && !wasClosed)
            {
                if (Time.time - lastChildGesture >= childGestureCooldown)
                {
                    if (stackGame.IsMoving && !stackGame.IsGameOver)
                    {
                        stackGame.StopBlock();
                        parentArmed = false;

                        lastChildGesture = Time.time;

                        if (showDebugMessages)
                            Debug.Log("Child closed a hand: stopping block.", this);
                    }
                }
            }

            previousChildClosed[id] = closed;
        }

        RemoveMissingChildHands();
    }

    private void RemoveMissingChildHands()
    {
        List<int> remove = new List<int>();

        foreach (int id in previousChildClosed.Keys)
        {
            if (!activeChildHands.Contains(id))
                remove.Add(id);
        }

        for (int i = 0; i < remove.Count; i++)
            previousChildClosed.Remove(remove[i]);
    }

    // ---------------------------------------------------------
    // CHILD HEAD TILT
    // ---------------------------------------------------------

    private void UpdateChildHeadTilt()
    {
        if (vision == null || !vision.IsReady() || association == null)
        {
            targetTilt = 0f;
            targetYaw = 0f;
            return;
        }

        if (!association.TryGetFace(
                ParticipantHandAssociation.ParticipantRole.Child,
                out Observation face))
        {
            targetTilt = 0f;
            targetYaw = 0f;
            return;
        }

        if (!HeadPose.TryEstimate(face, vision.FrameAspect, out HeadAngles head))
        {
            targetTilt = 0f;
            targetYaw = 0f;
            return;
        }

        float roll = Mathf.Abs(head.Roll) < headDeadZone ? 0f : head.Roll;
        float yaw = Mathf.Abs(head.Yaw) < headDeadZone ? 0f : head.Yaw;

        float normalizedRoll = Mathf.Clamp(roll / Mathf.Max(1f, maximumTiltDegrees), -1f, 1f);
        float normalizedYaw = maximumYawDegrees > 0.01f
            ? Mathf.Clamp(yaw / Mathf.Max(1f, maximumYawDegrees), -1f, 1f)
            : 0f;

        targetTilt = normalizedRoll * maximumTiltDegrees;
        targetYaw = normalizedYaw * maximumYawDegrees;

        if (invertTilt)
        {
            targetTilt *= -1f;
            targetYaw *= -1f;
        }
    }

    // ---------------------------------------------------------
    // TOWER ROTATION
    // ---------------------------------------------------------

    private void UpdateTowerRotation()
    {
        currentTilt = Mathf.Lerp(
            currentTilt, targetTilt, Time.deltaTime * tiltSmoothing);
        currentYaw = Mathf.Lerp(
            currentYaw, targetYaw, Time.deltaTime * tiltSmoothing);

        Transform tower = GetTowerTransform();
        if (tower == null)
            return;

        tower.localRotation = Quaternion.Euler(0f, currentYaw, currentTilt);
    }

    private Transform GetTowerTransform()
    {
        if (towerTiltTarget != null)
            return towerTiltTarget;

        if (stackGame != null)
            return stackGame.TowerRoot;

        return null;
    }

    // ---------------------------------------------------------
    // EVENTS
    // ---------------------------------------------------------

    private void OnHandUnassociated(int trackId)
    {
        previousParentPositions.Remove(trackId);
        previousChildClosed.Remove(trackId);
    }

    private void OnStackScoreChanged(int _)
    {
        // A new row is in play again, so the parent needs to re-raise.
        parentArmed = false;
    }

    // ---------------------------------------------------------
    // PUBLIC CONTROL
    // ---------------------------------------------------------

    public void ResetTilt()
    {
        targetTilt = 0f;
        currentTilt = 0f;
        targetYaw = 0f;
        currentYaw = 0f;

        Transform tower = GetTowerTransform();
        if (tower != null)
            tower.localRotation = Quaternion.identity;
    }

    private void OnDisable()
    {
        if (association != null)
            association.HandUnassociated -= OnHandUnassociated;

        if (stackGame != null)
            stackGame.ScoreChanged -= OnStackScoreChanged;

        ResetTilt();

        previousParentPositions.Clear();
        previousChildClosed.Clear();
        activeParentHands.Clear();
        activeChildHands.Clear();

        parentArmed = false;
    }
}