using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 3D block stacking game.
///
/// The tower is treated as being HELD by the barber: its orientation is set
/// by the tilt pivot (barber hands -> director -> TowerRoot), and blocks are
/// stacked in the tower's LOCAL frame. "Down" for the tower is its local -Y,
/// not world -Y. That means tilting the tower does not cause blocks to slide
/// sideways off each other, because gravity in the tower's frame points at
/// the tower's base.
///
/// Detached blocks ("dropped" on a miss) are also simulated in the tower's
/// frame: world gravity applied to them is first rotated by the inverse of
/// the tower's current tilt, so they fall along the tower walls rather than
/// toward world-down. This makes the visual "held by barber" illusion
/// consistent for both stacked and falling blocks.
/// </summary>
public class StackGame : MonoBehaviour
{
    [Header("Grid")]
    [SerializeField, Min(2)] public int columns = 9;
    [SerializeField, Min(1)] public int startWidth = 4;

    [Header("Movement")]
    [SerializeField, Min(0.1f)] public float startSpeed = 4f;
    [SerializeField, Min(0f)] public float speedIncrease = 0.15f;
    [SerializeField, Min(0.1f)] public float maxSpeed = 12f;

    [Header("Input")]
    [SerializeField] public KeyCode stopKey = KeyCode.Space;
    [SerializeField] public KeyCode spawnKey = KeyCode.Return;
    [SerializeField] public KeyCode slowKey = KeyCode.LeftShift;
    [SerializeField] private bool allowMouseOutsideUI = true;

    [Header("Slow mode")]
    [SerializeField, Range(0.1f, 1f)] public float slowMultiplier = 0.4f;

    [Header("Camera")]
    [SerializeField] public Camera cam;
    [SerializeField, Min(0f)] public float cameraFollowSpeed = 3f;

    [Header("Camera fit")]
    [SerializeField] private bool cameraFitEnabled = true;
    [SerializeField, Range(0f, 0.4f)] private float cameraFitPaddingX = 0.10f;
    [SerializeField, Range(0f, 0.4f)] private float cameraFitPaddingY = 0.12f;
    [SerializeField, Min(0f)] private float cameraFitWorldMargin = 0.5f;
    [SerializeField, Min(0.5f)] private float cameraFitMinSize = 3f;
    [SerializeField, Min(0f)] private float cameraFitLerp = 3f;

    [Header("Tilt visual juice")]
    [SerializeField, Min(0.01f)] private float tiltSmoothTime = 0.18f;
    [SerializeField, Min(0f)] private float tiltOvershoot = 4f;
    [SerializeField, Min(0.1f)] private float tiltOvershootDecay = 6f;

    [Header("Tower-as-held physics")]
    [Tooltip("Rotate world gravity into the tower's local frame for " +
             "detached blocks. When true, blocks fall along the tower's " +
             "own -Y axis (barber holding the tower). When false, they " +
             "fall toward world-down.")]
    [SerializeField] private bool gravityFollowsTower = true;
    [Tooltip("Multiplier applied to gravity for detached blocks. 0 = they " +
             "float in place (purely visual drop), 1 = full gravity in the " +
             "tower's frame.")]
    [SerializeField, Min(0f)] private float detachedGravityScale = 1f;

    [Header("Startup")]
    [SerializeField] private bool autoStartIfNoFlow = false;

    [Header("Vision steering")]
    [SerializeField] private bool steerByExternalInput = false;
    [SerializeField, Min(0.1f)] private float steerLerpSpeed = 12f;

    [Header("Canvas UI")]
    [SerializeField] private Text scoreText;
    [SerializeField] private Text instructionText;
    [SerializeField] private Text slowModeText;
    [SerializeField] private Text gameOverText;
    [SerializeField] private GameObject gameOverPanel;
    [SerializeField] private Button spawnButton;
    [SerializeField] private Button stopButton;
    [SerializeField] private Button slowButton;
    [SerializeField] private Button restartButton;

    public int Score => Mathf.Max(0, rowIndex - 1);
    public bool IsGameOver => gameOver;
    public bool IsMoving => movingRow != null;
    public bool HasStarted => gameStarted;
    public Transform TowerRoot => towerRoot;
    public Transform TiltPivot => towerPivot;

    /// <summary>Current lean of the tower in degrees (Z).</summary>
    public float CurrentTiltDegrees => towerCurrentTilt;

    public event Action<int> ScoreChanged;
    public event Action<int> GameOver;

    private Transform towerRoot;
    private Transform towerPivot;
    private Transform movingRow;
    private int rowIndex;
    private int prevStart;
    private int prevWidth;
    private int width;
    private float pos;
    private float speed;
    private float dir = 1f;
    private bool gameOver;
    private bool slowed;
    private bool initialized;
    private bool buttonsBound;
    private bool gameStarted;

    private bool hasSteerTarget;
    private float steerTargetPos;

    // Tilt visual state.
    private float towerTargetTilt;
    private float towerCurrentTilt;
    private float towerTiltVelocity;
    private float towerOvershoot;
    private float lastAppliedTargetTilt;

    // Per-drop physics: gravity override state.
    private sealed class DetachedBlock
    {
        public GameObject go;
        public Rigidbody body;
    }
    private readonly List<DetachedBlock> detached = new List<DetachedBlock>();

    // Camera state.
    private Vector3 cameraStartPosition;
    private Quaternion cameraStartRotation;
    private bool cameraStartOrthographic;
    private float cameraStartOrthographicSize;
    private bool cameraCaptured;

    private Vector3 cameraTargetPosition;
    private float cameraTargetSize;
    private bool cameraTargetValid;

    private readonly List<Renderer> rendererScratch = new List<Renderer>();

    private void Awake()
    {
        if (cam == null) cam = Camera.main;
        CaptureCameraDefaults();
        BindButtons();
    }

    private void OnEnable()
    {
        if (initialized && gameStarted) ConfigureCameraForGame();
    }

    private void Start()
    {
        EnsureInitialized();

        if (autoStartIfNoFlow && FindObjectOfType<KidsSalonGameFlow>() == null)
            RestartGame();
        else
            RefreshCanvasUI();
    }

    private void OnDisable() { RestoreCameraDefaults(); }
    private void OnDestroy() { UnbindButtons(); }

    private void Update()
    {
        if (PauseOverlay.IsPaused || !initialized || !gameStarted) return;
        if (gameOver) { RefreshCanvasUI(); return; }

        if (Input.GetKeyDown(slowKey)) ToggleSlow();

        if (movingRow == null)
        {
            if (Input.GetKeyDown(spawnKey) || MousePressedOutsideUI(1)) SpawnBlock();
        }
        else
        {
            float currentSpeed = slowed ? speed * slowMultiplier : speed;
            float max = columns - width;

            if (steerByExternalInput && hasSteerTarget)
                pos = Mathf.Lerp(pos, Mathf.Clamp(steerTargetPos, 0f, max), Time.deltaTime * steerLerpSpeed);
            else
            {
                pos += dir * currentSpeed * Time.deltaTime;
                if (pos > max) { pos = max; dir = -1f; }
                else if (pos < 0f) { pos = 0f; dir = 1f; }
            }

            SetRowCellPosition(movingRow, pos, rowIndex);
            if (Input.GetKeyDown(stopKey) || MousePressedOutsideUI(0)) StopBlock();
        }

        RefreshCanvasUI();
    }

    private void LateUpdate()
    {
        UpdateTowerTiltVisual();
        UpdateDetachedPhysics();
        UpdateCameraFit();
    }

    // ---------------------------------------------------------------------
    // Held-tower tilt
    // ---------------------------------------------------------------------

    private void UpdateTowerTiltVisual()
    {
        if (towerRoot == null) return;
        EnsureTowerPivot();

        // Read the angle the director wrote this frame, then undo it on
        // TowerRoot and reapply a smoothed version to the pivot.
        float requestedZ = Mathf.DeltaAngle(0f, towerRoot.localEulerAngles.z);
        towerTargetTilt = requestedZ;

        if (tiltOvershoot > 0.01f)
        {
            float delta = Mathf.DeltaAngle(lastAppliedTargetTilt, requestedZ);
            if (Mathf.Abs(delta) > 1f)
                towerOvershoot = Mathf.Clamp(towerOvershoot + delta * 0.25f, -tiltOvershoot, tiltOvershoot);
        }
        lastAppliedTargetTilt = requestedZ;

        towerOvershoot = Mathf.Lerp(towerOvershoot, 0f, 1f - Mathf.Exp(-tiltOvershootDecay * Time.deltaTime));

        float desired = requestedZ + towerOvershoot;
        towerCurrentTilt = Mathf.SmoothDampAngle(
            towerCurrentTilt, desired, ref towerTiltVelocity,
            Mathf.Max(0.001f, tiltSmoothTime), Mathf.Infinity, Time.deltaTime);

        towerRoot.localRotation = Quaternion.identity;
        towerPivot.localRotation = Quaternion.Euler(0f, 0f, towerCurrentTilt);
    }

    private void EnsureTowerPivot()
    {
        if (towerPivot != null && towerPivot.parent == towerRoot) return;

        Transform existing = towerRoot.Find("TiltPivot");
        if (existing != null) { towerPivot = existing; return; }

        GameObject pivotGo = new GameObject("TiltPivot");
        towerPivot = pivotGo.transform;
        towerPivot.SetParent(towerRoot, false);
        towerPivot.localPosition = Vector3.zero;
        towerPivot.localRotation = Quaternion.identity;
        towerPivot.localScale = Vector3.one;

        for (int i = towerRoot.childCount - 1; i >= 0; i--)
        {
            Transform c = towerRoot.GetChild(i);
            if (c == towerPivot) continue;
            c.SetParent(towerPivot, true);
        }
    }

    // ---------------------------------------------------------------------
    // Detached-block physics in the tower's frame
    //
    // "Barber holds the tower": gravity for anything attached to the
    // tower is effectively along the tower's own -Y. Detached blocks
    // are parented to the world so they can fall, but their gravity is
    // overridden each FixedUpdate to be the world-space vector that,
    // in the tower's frame, points along local -Y.
    // ---------------------------------------------------------------------

    private void UpdateDetachedPhysics()
    {
        if (detached.Count == 0) return;

        Quaternion towerRot = towerPivot != null ? towerPivot.rotation : Quaternion.identity;
        Vector3 worldDown = Physics.gravity;

        // In the tower's frame, world-down becomes (towerRot^-1 * worldDown).
        // We want the detached block to fall along tower-local -Y, so:
        //   worldGravityForBlock = towerRot * (local -Y) * |g|
        // Which is -towerRot.up * magnitude.
        Vector3 towerFrameGravity;
        if (gravityFollowsTower)
            towerFrameGravity = -(towerRot * Vector3.up) * Mathf.Abs(Physics.gravity.magnitude) * detachedGravityScale;
        else
            towerFrameGravity = worldDown * detachedGravityScale;

        for (int i = detached.Count - 1; i >= 0; i--)
        {
            DetachedBlock d = detached[i];
            if (d == null || d.go == null) { detached.RemoveAt(i); continue; }

            if (d.body != null)
            {
                // Clear per-body gravity override (Unity doesn't have one
                // built-in for rigidbodies), so we approximate by applying
                // a custom force each FixedUpdate.

                // UseForce: apply force = mass * (desired - Physics.gravity).
                // This cancels the built-in Physics.gravity and replaces it
                // with towerFrameGravity.
                Vector3 cancel = -Physics.gravity;
                Vector3 apply = towerFrameGravity;
                d.body.AddForce((cancel + apply) * d.body.mass, ForceMode.Force);
            }
        }
    }

    /// <summary>Starts (or restarts) the game.</summary>
    public void RestartGame()
    {
        EnsureInitialized();

        if (towerRoot != null) Destroy(towerRoot.gameObject);
        towerRoot = new GameObject("Stack Tower").transform;
        towerPivot = null;
        towerCurrentTilt = 0f;
        towerTargetTilt = 0f;
        towerTiltVelocity = 0f;
        towerOvershoot = 0f;
        lastAppliedTargetTilt = 0f;
        EnsureTowerPivot();

        ClearDetached();

        gameStarted = true;
        movingRow = null;
        rowIndex = 1;
        prevWidth = Mathf.Clamp(startWidth, 1, Mathf.Max(1, columns));
        prevStart = (columns - prevWidth) / 2;
        width = prevWidth;
        pos = 0f;
        speed = Mathf.Max(0.1f, startSpeed);
        dir = 1f;
        gameOver = false;
        slowed = false;
        hasSteerTarget = false;

        BuildRow(prevWidth, 0, prevStart);
        ConfigureCameraForGame();

        if (gameOverPanel != null) gameOverPanel.SetActive(false);
        ScoreChanged?.Invoke(Score);
        RefreshCanvasUI();
    }

    public void SpawnBlock()
    {
        EnsureInitialized();
        if (!gameStarted || PauseOverlay.IsPaused || gameOver || movingRow != null) return;
        pos = 0f;
        dir = 1f;
        hasSteerTarget = false;
        steerTargetPos = 0f;
        movingRow = BuildRow(width, rowIndex, 0);
        RefreshCanvasUI();
    }

    public void SteerMovingBlock(float normalizedX)
    {
        if (!gameStarted || movingRow == null || gameOver) return;
        steerByExternalInput = true;
        float max = columns - width;
        steerTargetPos = Mathf.Clamp01(normalizedX) * max;
        hasSteerTarget = true;
    }

    public void ReleaseSteering()
    {
        steerByExternalInput = false;
        hasSteerTarget = false;
    }

    public void NudgeMovingBlock(int direction)
    {
        if (!gameStarted || movingRow == null || gameOver) return;
        steerByExternalInput = true;
        float max = columns - width;
        float basePos = hasSteerTarget ? steerTargetPos : pos;
        steerTargetPos = Mathf.Clamp(Mathf.Round(basePos) + direction, 0f, max);
        hasSteerTarget = true;
    }

    public void StopBlock()
    {
        if (!gameStarted || PauseOverlay.IsPaused || !initialized || gameOver || movingRow == null) return;
        PlaceMovingRow();
        RefreshCanvasUI();
    }

    public void ToggleSlow()
    {
        if (!gameStarted || PauseOverlay.IsPaused || !initialized || gameOver) return;
        slowed = !slowed;
        RefreshCanvasUI();
    }

    private void SetRowCellPosition(Transform row, float cellX, int cellY)
    {
        row.localPosition = new Vector3(cellX, cellY, 0f);
    }

    private Transform BuildRow(int cells, int y, int startCell)
    {
        EnsureTowerPivot();

        Transform row = new GameObject("Row " + y).transform;
        Color color = Color.HSVToRGB((y * 0.07f) % 1f, 0.6f, 0.95f);

        row.SetParent(towerPivot, false);
        row.localPosition = new Vector3(startCell, y, 0f);

        for (int i = 0; i < cells; i++)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(row, false);
            cube.transform.localPosition = new Vector3(i, 0f, 0f);
            cube.GetComponent<Renderer>().material.color = color;
            Collider cc = cube.GetComponent<Collider>();
            if (cc != null) Destroy(cc);
        }

        return row;
    }

    private void EnsureInitialized()
    {
        if (initialized) return;
        if (cam == null) cam = Camera.main;
        CaptureCameraDefaults();
        initialized = true;
    }

    private void CaptureCameraDefaults()
    {
        if (cameraCaptured || cam == null) return;
        cameraStartPosition = cam.transform.position;
        cameraStartRotation = cam.transform.rotation;
        cameraStartOrthographic = cam.orthographic;
        cameraStartOrthographicSize = cam.orthographicSize;
        cameraCaptured = true;
    }

    private void ConfigureCameraForGame()
    {
        if (cam == null) return;
        cam.orthographic = true;
        cam.transform.rotation = Quaternion.identity;
        cameraTargetValid = false;
        ApplyFraming(instant: true);
    }

    private void UpdateCameraFit()
    {
        if (!cameraFitEnabled) { FollowTowerWithCamera(); return; }
        ApplyFraming(instant: false);
    }

    private void ApplyFraming(bool instant)
    {
        if (cam == null) return;

        if (!TryComputeTowerBounds(out Bounds worldBounds))
        {
            ComputeColumnsFallback(out Vector3 fbPos, out float fbSize);
            worldBounds = new Bounds(fbPos, new Vector3(columns, Mathf.Max(1f, rowIndex) + 2f, 1f));
            if (instant)
            {
                cam.orthographicSize = Mathf.Max(fbSize, cameraFitMinSize);
                cam.transform.position = new Vector3(fbPos.x, fbPos.y, -10f);
            }
            else
            {
                float k0 = Time.deltaTime * cameraFollowSpeed;
                cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, Mathf.Max(fbSize, cameraFitMinSize), k0);
                Vector3 target0 = new Vector3(fbPos.x, fbPos.y, -10f);
                cam.transform.position = Vector3.Lerp(cam.transform.position, target0, k0);
            }
            cameraTargetValid = true;
            return;
        }

        worldBounds.Expand(new Vector3(
            cameraFitWorldMargin * 2f,
            cameraFitWorldMargin * 2f,
            cameraFitWorldMargin * 2f));

        float aspect = Mathf.Max(0.1f, cam.aspect);
        float planeZ = worldBounds.center.z;
        float currentZ = cam.transform.position.z;
        float distance = Mathf.Abs(currentZ - planeZ);
        if (distance < 0.1f) distance = 10f;

        if (cam.orthographic)
        {
            float halfHeight = worldBounds.extents.y;
            float halfWidth = worldBounds.extents.x;
            float sizeFromHeight = halfHeight / Mathf.Max(0.01f, (1f - cameraFitPaddingY));
            float sizeFromWidth = halfWidth / Mathf.Max(0.01f, aspect * (1f - cameraFitPaddingX));
            cameraTargetSize = Mathf.Max(sizeFromHeight, sizeFromWidth, cameraFitMinSize);
            cameraTargetPosition = new Vector3(worldBounds.center.x, worldBounds.center.y, currentZ);
        }
        else
        {
            float vFov = cam.fieldOfView * Mathf.Deg2Rad;
            float hFov = 2f * Mathf.Atan(Mathf.Tan(vFov * 0.5f) * aspect);

            float fitHeightDist = worldBounds.extents.y / Mathf.Tan(vFov * 0.5f * Mathf.Max(0.01f, (1f - cameraFitPaddingY)));
            float fitWidthDist = worldBounds.extents.x / Mathf.Tan(hFov * 0.5f * Mathf.Max(0.01f, (1f - cameraFitPaddingX)));
            float targetDist = Mathf.Max(fitHeightDist, fitWidthDist) + worldBounds.extents.z;

            Vector3 forward = cam.transform.forward;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            cameraTargetPosition = worldBounds.center - forward * targetDist;
        }

        if (instant)
        {
            if (cam.orthographic) cam.orthographicSize = cameraTargetSize;
            cam.transform.position = cameraTargetPosition;
        }
        else
        {
            float k = 1f - Mathf.Exp(-Mathf.Max(0.01f, cameraFitLerp) * Time.deltaTime);
            if (cam.orthographic)
                cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, cameraTargetSize, k);
            cam.transform.position = Vector3.Lerp(cam.transform.position, cameraTargetPosition, k);
        }

        cameraTargetValid = true;
    }

    private bool TryComputeTowerBounds(out Bounds bounds)
    {
        bounds = default;
        if (towerRoot == null) return false;

        rendererScratch.Clear();
        towerRoot.GetComponentsInChildren(false, rendererScratch);

        bool any = false;
        for (int i = 0; i < rendererScratch.Count; i++)
        {
            Renderer r = rendererScratch[i];
            if (r == null) continue;
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (!any) { bounds = r.bounds; any = true; }
            else bounds.Encapsulate(r.bounds);
        }

        return any;
    }

    private void ComputeColumnsFallback(out Vector3 position, out float orthoSize)
    {
        float aspect = cam != null ? Mathf.Max(0.1f, cam.aspect) : 1.777f;
        float halfWidth = columns * 0.5f;
        float sizeForWidth = halfWidth / aspect;
        float topRow = Mathf.Max(1f, rowIndex);
        float verticalSpan = topRow + 2f;
        float sizeForHeight = verticalSpan * 0.5f;
        orthoSize = Mathf.Max(sizeForWidth, sizeForHeight, cameraFitMinSize);
        position = new Vector3((columns - 1) * 0.5f, (topRow - 1f) * 0.5f, -10f);
    }

    private void RestoreCameraDefaults()
    {
        if (!cameraCaptured || cam == null) return;
        cam.orthographic = cameraStartOrthographic;
        if (cameraStartOrthographic) cam.orthographicSize = cameraStartOrthographicSize;
        cam.transform.SetPositionAndRotation(cameraStartPosition, cameraStartRotation);
    }

    private void FollowTowerWithCamera()
    {
        if (cam == null) return;
        float aspect = Mathf.Max(0.1f, cam.aspect);
        float halfWidth = columns * 0.5f;
        float sizeForWidth = halfWidth / aspect;
        float topRow = Mathf.Max(1f, rowIndex);
        float verticalSpan = topRow + 2f;
        float sizeForHeight = verticalSpan * 0.5f;
        float targetSize = Mathf.Max(sizeForWidth, sizeForHeight);
        float targetX = (columns - 1) * 0.5f;
        float targetY = (topRow - 1f) * 0.5f;
        Vector3 targetPos = new Vector3(targetX, Mathf.Max(0f, targetY), -10f);

        float k = Time.deltaTime * cameraFollowSpeed;
        cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, targetSize, k);
        cam.transform.position = Vector3.Lerp(cam.transform.position, targetPos, k);
    }

    private void PlaceMovingRow()
    {
        int snap = Mathf.RoundToInt(pos);
        int overlapStart = Mathf.Max(snap, prevStart);
        int overlapEnd = Mathf.Min(snap + width, prevStart + prevWidth);
        int overlap = overlapEnd - overlapStart;

        SetRowCellPosition(movingRow, snap, rowIndex);

        if (overlap <= 0)
        {
            for (int i = movingRow.childCount - 1; i >= 0; i--) Drop(movingRow.GetChild(i));
            Destroy(movingRow.gameObject);
            movingRow = null;
            FinishGame();
            return;
        }

        for (int i = movingRow.childCount - 1; i >= 0; i--)
        {
            int cell = snap + i;
            if (cell < overlapStart || cell >= overlapEnd) Drop(movingRow.GetChild(i));
        }

        prevStart = overlapStart;
        prevWidth = overlap;
        width = overlap;
        rowIndex++;
        speed = Mathf.Min(speed + speedIncrease, maxSpeed);
        movingRow = null;
        hasSteerTarget = false;
        ScoreChanged?.Invoke(Score);
    }

    /// <summary>
    /// Detach a block so it can fall. Because the tower is being "held" by
    /// the barber, we detach into world space but override the block's
    /// effective gravity each frame (see UpdateDetachedPhysics) so it falls
    /// along the tower's -Y instead of world -Y.
    /// </summary>
    private void Drop(Transform block)
    {
        block.SetParent(null, true);
        Rigidbody body = block.gameObject.AddComponent<Rigidbody>();
        body.useGravity = true; // Unity's built-in gravity is cancelled in UpdateDetachedPhysics
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.AddTorque(UnityEngine.Random.insideUnitSphere * 1.5f, ForceMode.Impulse);
        detached.Add(new DetachedBlock { go = block.gameObject, body = body });
        Destroy(block.gameObject, 3f);
    }

    private void ClearDetached()
    {
        for (int i = 0; i < detached.Count; i++)
            if (detached[i].go != null) Destroy(detached[i].go);
        detached.Clear();
    }

    private void FinishGame()
    {
        if (gameOver) return;
        gameOver = true;
        if (gameOverPanel != null) gameOverPanel.SetActive(true);
        GameOver?.Invoke(Score);
        RefreshCanvasUI();
    }

    private bool PointerIsOverUI() => EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    private bool MousePressedOutsideUI(int button)
    {
        return allowMouseOutsideUI && Input.GetMouseButtonDown(button) && !PointerIsOverUI();
    }

    public void ConfigureRuntimeCanvasUI(
        Text runtimeScoreText, Text runtimeInstructionText, Text runtimeSlowModeText,
        Text runtimeGameOverText, GameObject runtimeGameOverPanel,
        Button runtimeSpawnButton, Button runtimeStopButton,
        Button runtimeSlowButton, Button runtimeRestartButton)
    {
        UnbindButtons();
        scoreText = runtimeScoreText;
        instructionText = runtimeInstructionText;
        slowModeText = runtimeSlowModeText;
        gameOverText = runtimeGameOverText;
        gameOverPanel = runtimeGameOverPanel;
        spawnButton = runtimeSpawnButton;
        stopButton = runtimeStopButton;
        slowButton = runtimeSlowButton;
        restartButton = runtimeRestartButton;
        BindButtons();
        RefreshCanvasUI();
    }

    private void BindButtons()
    {
        if (buttonsBound) return;
        if (spawnButton != null) spawnButton.onClick.AddListener(SpawnBlock);
        if (stopButton != null) stopButton.onClick.AddListener(StopBlock);
        if (slowButton != null) slowButton.onClick.AddListener(ToggleSlow);
        if (restartButton != null) restartButton.onClick.AddListener(RestartGame);
        buttonsBound = true;
    }

    private void UnbindButtons()
    {
        if (!buttonsBound) return;
        if (spawnButton != null) spawnButton.onClick.RemoveListener(SpawnBlock);
        if (stopButton != null) stopButton.onClick.RemoveListener(StopBlock);
        if (slowButton != null) slowButton.onClick.RemoveListener(ToggleSlow);
        if (restartButton != null) restartButton.onClick.RemoveListener(RestartGame);
        buttonsBound = false;
    }

    private void RefreshCanvasUI()
    {
        if (scoreText != null) scoreText.text = "Score: " + Score;
        if (slowModeText != null) slowModeText.text = slowed ? "Slow mode: ON" : "Slow mode: OFF";

        if (instructionText != null)
        {
            if (!gameStarted) instructionText.text = "";
            else if (gameOver) instructionText.text = "Round over! Try again or return to the salon.";
            else if (movingRow == null) instructionText.text = "Raise a hand to send the next block";
            else instructionText.text = "Parent: raise to steer \u2022 Child: raise to place";
        }

        if (gameOverText != null) gameOverText.text = "Tower score: " + Score + "\nGreat stacking!";
        if (spawnButton != null) spawnButton.interactable = gameStarted && !gameOver && movingRow == null;
        if (stopButton != null) stopButton.interactable = gameStarted && !gameOver && movingRow != null;
        if (slowButton != null) slowButton.interactable = gameStarted && !gameOver;
    }
}