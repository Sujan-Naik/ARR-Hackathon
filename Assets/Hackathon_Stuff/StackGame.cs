using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Block stacking game. Blocks are generated at runtime.
///
/// Two rendering modes:
///   • 3D mode  - blocks are world-space cubes. Uses a hijacked orthographic
///                Camera.main side view (legacy behaviour, kept for the
///                desktop-only demo scene).
///   • Canvas mode - blocks are UI Images parented to canvasTowerRoot, mapped
///                from cell units to pixels. This is the mode used when the
///                game is played "over the webcam": the tower lives in the
///                same RectTransform space as the vision preview, so the
///                child's head, the monster's hands and the tower all share
///                one coordinate system.
///
/// The game LOGIC (rows, overlap, scoring, steering) always works in integer
/// cell units. Only the renderer knows about world space or pixels.
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

    [Header("Camera (3D mode only)")]
    [SerializeField] public Camera cam;
    [SerializeField, Min(0f)] public float cameraFollowSpeed = 3f;

    [Header("Vision steering")]
    [Tooltip("When true, the moving row ignores its automatic sweep and is steered directly by SteerMovingBlock.")]
    [SerializeField] private bool steerByExternalInput = false;
    [Tooltip("How quickly the moving row eases toward the externally steered column.")]
    [SerializeField, Min(0.1f)] private float steerLerpSpeed = 12f;

    [Header("Canvas rendering")]
    [Tooltip("Assign a RectTransform anchored to the webcam preview to render the " +
             "tower as UI Images. Leave empty for legacy 3D cubes.")]
    [SerializeField] private RectTransform canvasTowerRoot;
    [Tooltip("Optional sprite template for a block. A 1x1 white square is used if empty.")]
    [SerializeField] private Image canvasBlockPrefab;
    [Tooltip("Pixel gap between adjacent blocks.")]
    [SerializeField, Min(0f)] private float cellGapPixels = 2f;
    [Tooltip("Bottom padding, in cell units, between the canvas bottom and row 0.")]
    [SerializeField, Min(0f)] private float canvasBottomPaddingCells = 0.5f;

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

    public int Score => rowIndex - 1;
    public bool IsGameOver => gameOver;
    public bool IsMoving => movingRow != null;
    public Transform TowerRoot => towerRoot;
    public RectTransform CanvasTowerRoot => canvasTowerRoot;
    public bool UsesCanvasRendering => canvasTowerRoot != null;

    public event Action<int> ScoreChanged;
    public event Action<int> GameOver;

    private Transform towerRoot;              // parent of rows (either world or Canvas)
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

    // External steering state.
    private bool hasSteerTarget;
    private float steerTargetPos;

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
        if (initialized) ConfigureCameraForGame();
    }

    private void Start()
    {
        RestartGame();
    }

    private void OnDisable()
    {
        RestoreCameraDefaults();
    }

    private void OnDestroy()
    {
        UnbindButtons();
    }

    private void Update()
    {
        if (PauseOverlay.IsPaused || !initialized) return;

        if (gameOver)
        {
            RefreshCanvasUI();
            return;
        }

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
            {
                pos = Mathf.Lerp(pos, Mathf.Clamp(steerTargetPos, 0f, max),
                                 Time.deltaTime * steerLerpSpeed);
            }
            else
            {
                pos += dir * currentSpeed * Time.deltaTime;

                if (pos > max) { pos = max; dir = -1f; }
                else if (pos < 0f) { pos = 0f; dir = 1f; }
            }

            SetRowCellPosition(movingRow, pos, rowIndex);
            if (Input.GetKeyDown(stopKey) || MousePressedOutsideUI(0)) StopBlock();
        }

        if (!UsesCanvasRendering) FollowTowerWithCamera();
        RefreshCanvasUI();
    }

    /// <summary>Starts a fresh round. Safe to call from a Canvas Button or game-flow script.</summary>
    public void RestartGame()
    {
        EnsureInitialized();

        if (towerRoot != null)
            Destroy(towerRoot.gameObject);

        towerRoot = new GameObject(UsesCanvasRendering ? "Canvas Stack Tower" : "Stack Tower").transform;

        if (UsesCanvasRendering)
        {
            RectTransform rt = towerRoot.gameObject.AddComponent<RectTransform>();
            rt.SetParent(canvasTowerRoot, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(0f, canvasBottomPaddingCells * CellPixels);
            rt.sizeDelta = Vector2.zero;
        }

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

        if (!UsesCanvasRendering) ConfigureCameraForGame();

        if (gameOverPanel != null) gameOverPanel.SetActive(false);
        ScoreChanged?.Invoke(Score);
        RefreshCanvasUI();
    }

    public void SpawnBlock()
    {
        if (PauseOverlay.IsPaused || !initialized || gameOver || movingRow != null) return;

        pos = 0f;
        dir = 1f;
        hasSteerTarget = false;
        steerTargetPos = 0f;
        movingRow = BuildRow(width, rowIndex, 0);
        RefreshCanvasUI();
    }

    /// <summary>
    /// Steers the currently moving row to a normalized X in [0, 1] across the
    /// playfield width. Only effective while a row is moving. When
    /// steerByExternalInput is false this enables it for the rest of the round.
    /// </summary>
    public void SteerMovingBlock(float normalizedX)
    {
        if (movingRow == null || gameOver) return;

        steerByExternalInput = true;
        float max = columns - width;
        steerTargetPos = Mathf.Clamp01(normalizedX) * max;
        hasSteerTarget = true;
    }

    /// <summary>
    /// Discrete variant: advances the steering target one cell right (dir=+1) or
    /// left (dir=-1). Useful for "raise hand to step" inputs. Clamped to playfield.
    /// </summary>
    public void NudgeMovingBlock(int direction)
    {
        if (movingRow == null || gameOver) return;

        steerByExternalInput = true;
        float max = columns - width;
        float basePos = hasSteerTarget ? steerTargetPos : pos;
        steerTargetPos = Mathf.Clamp(Mathf.Round(basePos) + direction, 0f, max);
        hasSteerTarget = true;
    }

    public void StopBlock()
    {
        if (PauseOverlay.IsPaused || !initialized || gameOver || movingRow == null) return;
        PlaceMovingRow();
        RefreshCanvasUI();
    }

    public void ToggleSlow()
    {
        if (PauseOverlay.IsPaused || !initialized || gameOver) return;
        slowed = !slowed;
        RefreshCanvasUI();
    }

    // ------------------------------------------------------------------
    // Rendering: cell units -> world or pixels
    // ------------------------------------------------------------------

    private float CellPixels
    {
        get
        {
            if (canvasTowerRoot == null) return 1f;
            float w = canvasTowerRoot.rect.width;
            return w > 0.001f ? w / Mathf.Max(1, columns) : 1f;
        }
    }

    private void SetRowCellPosition(Transform row, float cellX, int cellY)
    {
        if (UsesCanvasRendering)
        {
            RectTransform rt = row as RectTransform;
            if (rt != null)
                rt.anchoredPosition = new Vector2(cellX * CellPixels, cellY * CellPixels);
        }
        else
        {
            row.position = new Vector3(cellX, cellY, 0f);
        }
    }

    private Transform BuildRow(int cells, int y, int startCell)
    {
        Transform row = new GameObject("Row " + y).transform;
        Color color = Color.HSVToRGB((y * 0.07f) % 1f, 0.6f, 0.95f);

        if (UsesCanvasRendering)
        {
            RectTransform rowRect = row.gameObject.AddComponent<RectTransform>();
            rowRect.SetParent(towerRoot, false);
            rowRect.anchorMin = Vector2.zero;
            rowRect.anchorMax = Vector2.zero;
            rowRect.pivot = Vector2.zero;
            rowRect.sizeDelta = new Vector2(cells * CellPixels, CellPixels);
            rowRect.anchoredPosition = new Vector2(startCell * CellPixels, y * CellPixels);

            for (int i = 0; i < cells; i++)
            {
                Image block = SpawnCanvasBlock(rowRect, color);
                RectTransform blockRect = block.rectTransform;
                blockRect.anchorMin = Vector2.zero;
                blockRect.anchorMax = Vector2.zero;
                blockRect.pivot = Vector2.zero;
                blockRect.sizeDelta = new Vector2(
                    Mathf.Max(1f, CellPixels - cellGapPixels),
                    Mathf.Max(1f, CellPixels - cellGapPixels));
                blockRect.anchoredPosition = new Vector2(i * CellPixels, 0f);
            }
        }
        else
        {
            row.SetParent(towerRoot, false);
            row.position = new Vector3(startCell, y, 0f);

            for (int i = 0; i < cells; i++)
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.SetParent(row, false);
                cube.transform.localPosition = new Vector3(i, 0f, 0f);
                cube.GetComponent<Renderer>().material.color = color;
                Collider cubeCollider = cube.GetComponent<Collider>();
                if (cubeCollider != null) Destroy(cubeCollider);
            }
        }

        return row;
    }

    private Image SpawnCanvasBlock(RectTransform parent, Color color)
    {
        Image img;
        if (canvasBlockPrefab != null)
        {
            img = Instantiate(canvasBlockPrefab, parent);
        }
        else
        {
            GameObject go = new GameObject("Block", typeof(RectTransform), typeof(Image));
            img = go.GetComponent<Image>();
            go.transform.SetParent(parent, false);
        }

        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

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
        // Canvas mode leaves the camera alone: the tower is on the Canvas.
        if (UsesCanvasRendering || cam == null) return;

        cam.orthographic = true;
        cam.orthographicSize = Mathf.Max(7f, columns * 0.6f / Mathf.Max(0.1f, cam.aspect));
        cam.transform.rotation = Quaternion.identity;
        cam.transform.position = new Vector3((columns - 1) / 2f, 5f, -10f);
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
        Vector3 cameraPosition = cam.transform.position;
        float targetY = Mathf.Max(5f, rowIndex - 2f);
        cameraPosition.y = Mathf.Lerp(cameraPosition.y, targetY, Time.deltaTime * cameraFollowSpeed);
        cam.transform.position = cameraPosition;
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
            for (int i = movingRow.childCount - 1; i >= 0; i--)
                Drop(movingRow.GetChild(i));

            Destroy(movingRow.gameObject);
            movingRow = null;
            FinishGame();
            return;
        }

        for (int i = movingRow.childCount - 1; i >= 0; i--)
        {
            int cell = snap + i;
            if (cell < overlapStart || cell >= overlapEnd)
                Drop(movingRow.GetChild(i));
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
        // Canvas blocks have no physics: just fade them out with a quick tween.
        if (UsesCanvasRendering)
        {
            Image img = block.GetComponent<Image>();
            if (img != null) StartCoroutine(FadeAndDestroy(img));
            else Destroy(block.gameObject);
            return;
        }

        block.SetParent(null, true);
        Rigidbody body = block.gameObject.AddComponent<Rigidbody>();
        body.AddTorque(UnityEngine.Random.insideUnitSphere * 2f, ForceMode.Impulse);
        Destroy(block.gameObject, 3f);
    }

    private System.Collections.IEnumerator FadeAndDestroy(Image img)
    {
        img.transform.SetParent(transform, true);
        float t = 0f;
        Color start = img.color;
        while (t < 0.35f && img != null)
        {
            t += Time.deltaTime;
            float a = 1f - Mathf.Clamp01(t / 0.35f);
            img.color = new Color(start.r, start.g, start.b, a);
            yield return null;
        }
        if (img != null) Destroy(img.gameObject);
    }

    private void FinishGame()
    {
        if (gameOver) return;
        gameOver = true;
        if (gameOverPanel != null) gameOverPanel.SetActive(true);
        GameOver?.Invoke(Score);
        RefreshCanvasUI();
    }

    private bool PointerIsOverUI()
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }

    private bool MousePressedOutsideUI(int button)
    {
        return allowMouseOutsideUI && Input.GetMouseButtonDown(button) && !PointerIsOverUI();
    }

    // ------------------------------------------------------------------
    // UI hooks
    // ------------------------------------------------------------------

    /// <summary>
    /// Allows KidsSalonGameFlow (or another runtime UI builder) to supply the
    /// Canvas controls without requiring any Inspector assignments.
    /// </summary>
    public void ConfigureRuntimeCanvasUI(
        Text runtimeScoreText,
        Text runtimeInstructionText,
        Text runtimeSlowModeText,
        Text runtimeGameOverText,
        GameObject runtimeGameOverPanel,
        Button runtimeSpawnButton,
        Button runtimeStopButton,
        Button runtimeSlowButton,
        Button runtimeRestartButton)
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

    /// <summary>
    /// Assigns the RectTransform that tower blocks will render into. Pass null
    /// to fall back to 3D cube rendering on the next RestartGame.
    /// </summary>
    public void ConfigureCanvasTowerRoot(RectTransform root)
    {
        canvasTowerRoot = root;
        if (initialized) RestartGame();
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
            if (gameOver) instructionText.text = "Round over! Try again or return to the salon.";
            else if (movingRow == null) instructionText.text = "Parent: raise a hand to send the next block";
            else instructionText.text = "Parent: raise a hand to steer \u2022 Child: close a hand to stop";
        }

        if (gameOverText != null) gameOverText.text = "Tower score: " + Score + "\nGreat stacking!";
        if (spawnButton != null) spawnButton.interactable = !gameOver && movingRow == null;
        if (stopButton != null) stopButton.interactable = !gameOver && movingRow != null;
        if (slowButton != null) slowButton.interactable = !gameOver;
    }
}