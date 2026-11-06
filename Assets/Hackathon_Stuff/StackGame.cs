using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>3D-only block stacking game. Cubes with real gravity.</summary>
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

    [Header("Startup")]
    [Tooltip("If true and no KidsSalonGameFlow exists in the scene, the game starts itself on scene load. Leave OFF so nothing is built until RestartGame() is called.")]
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

    public event Action<int> ScoreChanged;
    public event Action<int> GameOver;

    private Transform towerRoot;
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

    // Blocks detached by Drop(). Tracked so a restart removes only these,
    // never unrelated Rigidbodies elsewhere in the scene.
    private readonly List<GameObject> droppedBlocks = new List<GameObject>();

    private Vector3 cameraStartPosition;
    private Quaternion cameraStartRotation;
    private bool cameraStartOrthographic;
    private float cameraStartOrthographicSize;
    private bool cameraCaptured;

    private void Awake()
    {
        if (cam == null) cam = Camera.main;
        CaptureCameraDefaults();
        BindButtons();
    }

    private void OnEnable()
    {
        // Only take over the camera if a game is actually running.
        if (initialized && gameStarted) ConfigureCameraForGame();
    }

    private void Start()
    {
        // Scene load is inert: no rows are built here. The game begins only
        // when RestartGame() is called (by KidsSalonGameFlow or the restart button).
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

        FollowTowerWithCamera();
        RefreshCanvasUI();
    }

    /// <summary>Starts (or restarts) the game. This is the only place Row 0 is built.</summary>
    public void RestartGame()
    {
        EnsureInitialized();

        if (towerRoot != null) Destroy(towerRoot.gameObject);
        towerRoot = new GameObject("Stack Tower").transform;

        ClearDroppedBlocks();

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

    /// <summary>Hands control back to the built-in sweep.</summary>
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
        row.position = new Vector3(cellX, cellY, 0f);
    }

    private Transform BuildRow(int cells, int y, int startCell)
    {
        Transform row = new GameObject("Row " + y).transform;
        Color color = Color.HSVToRGB((y * 0.07f) % 1f, 0.6f, 0.95f);

        row.SetParent(towerRoot, false);
        row.position = new Vector3(startCell, y, 0f);

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
        ApplyFraming(instant: true);
        cam.transform.rotation = Quaternion.identity;
    }

    private void ApplyFraming(bool instant)
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

        if (instant)
        {
            cam.orthographicSize = targetSize;
            cam.transform.position = targetPos;
        }
        else
        {
            float k = Time.deltaTime * cameraFollowSpeed;
            cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, targetSize, k);
            cam.transform.position = Vector3.Lerp(cam.transform.position, targetPos, k);
        }
    }

    private void RestoreCameraDefaults()
    {
        if (!cameraCaptured || cam == null) return;
        cam.orthographic = cameraStartOrthographic;
        if (cameraStartOrthographic) cam.orthographicSize = cameraStartOrthographicSize;
        cam.transform.SetPositionAndRotation(cameraStartPosition, cameraStartRotation);
    }

    private void FollowTowerWithCamera() => ApplyFraming(instant: false);

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

    private void Drop(Transform block)
    {
        block.SetParent(null, true);
        Rigidbody body = block.gameObject.AddComponent<Rigidbody>();
        body.AddTorque(UnityEngine.Random.insideUnitSphere * 2f, ForceMode.Impulse);
        droppedBlocks.Add(block.gameObject);
        Destroy(block.gameObject, 3f);
    }

    /// <summary>Destroys only the blocks this game detached (not other scene Rigidbodies).</summary>
    private void ClearDroppedBlocks()
    {
        for (int i = 0; i < droppedBlocks.Count; i++)
            if (droppedBlocks[i] != null) Destroy(droppedBlocks[i]);
        droppedBlocks.Clear();
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