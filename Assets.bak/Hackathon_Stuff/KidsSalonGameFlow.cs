
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

/// <summary>
/// Main flow controller for the three-person tower stacking game.
///
/// The tower is the main experience:
/// - Parent raises a hand to spawn a block.
/// - Child closes a hand to stop the moving block.
/// - Barber uses both hands to tilt the tower.
///
/// This script creates the game UI at runtime and connects it to StackGame.
/// It does not depend on the salon or haircut system.
/// </summary>
public class KidsSalonGameFlow : MonoBehaviour
{
    [Header("Game references")]
    [SerializeField] private StackGame stackGame;
    [SerializeField] private TowerVisionController towerVisionController;

    [Header("Game settings")]
    [SerializeField] private bool startGameAutomatically = true;
    [SerializeField] private bool showInstructionsAtStart = true;

    [Header("UI appearance")]
    [SerializeField]
    private Color backgroundColor =
        new Color(0.035f, 0.055f, 0.11f, 0.92f);

    [SerializeField]
    private Color panelColor =
        new Color(0.10f, 0.14f, 0.24f, 0.96f);

    [SerializeField]
    private Color primaryColor =
        new Color(0.25f, 0.78f, 0.98f);

    [SerializeField]
    private Color secondaryColor =
        new Color(1f, 0.72f, 0.28f);

    [SerializeField] private Color textColor = Color.white;

    [Header("UI layout")]
    [SerializeField] private float panelWidth = 440f;
    [SerializeField] private float panelHeight = 570f;
    [SerializeField] private int titleFontSize = 38;
    [SerializeField] private int bodyFontSize = 20;

    [Header("Debug")]
    [SerializeField] private bool logFlowChanges;

    private Canvas canvas;
    private Text scoreText;
    private Text statusText;
    private Text instructionText;
    private GameObject instructionPanel;
    private GameObject gameOverPanel;
    private Text gameOverScoreText;
    private Button startButton;

    private bool gameStarted;
    private bool subscribed;

    private void Awake()
    {
        if (stackGame == null)
            stackGame = FindObjectOfType<StackGame>();

        if (towerVisionController == null)
            towerVisionController = FindObjectOfType<TowerVisionController>();

        BuildInterface();
    }

    private void OnEnable()
    {
        SubscribeToGame();
    }

    private void Start()
    {
        SubscribeToGame();

        if (stackGame == null)
        {
            SetStatus("Waiting for tower game...");
            return;
        }

        RefreshScore();

        if (startGameAutomatically)
        {
            StartTowerGame();
        }
        else
        {
            ShowWelcomeScreen();
        }
    }

    private void Update()
    {
        if (stackGame == null)
            return;

        RefreshScore();

        if (stackGame.IsGameOver)
        {
            ShowGameOver();
        }
    }

    // ---------------------------------------------------------
    // UI CREATION
    // ---------------------------------------------------------

    private void BuildInterface()
    {
        GameObject canvasObject = new GameObject("Tower Game UI");

        canvasObject.transform.SetParent(transform, false);

        canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        canvasObject.AddComponent<GraphicRaycaster>();

        BuildMainPanel();
        BuildInstructionsPanel();
        BuildGameOverPanel();
    }

    private void BuildMainPanel()
    {
        GameObject panel = CreatePanel(
            "Tower HUD",
            canvas.transform,
            panelColor,
            new Vector2(0f, 1f),
            new Vector2(0f, 1f),
            new Vector2(0f, 1f),
            new Vector2(0f, 1f),
            new Vector2(24f, -24f),
            new Vector2(panelWidth, 220f));

        AddText(
            "Game Title",
            panel.transform,
            "TOWER CHALLENGE",
            titleFontSize,
            FontStyle.Bold,
            TextAnchor.MiddleLeft,
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(20f, -18f),
            new Vector2(-40f, 60f));

        scoreText = AddText(
            "Score",
            panel.transform,
            "SCORE  0",
            30,
            FontStyle.Bold,
            TextAnchor.MiddleLeft,
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(20f, -86f),
            new Vector2(-40f, 48f));

        statusText = AddText(
            "Status",
            panel.transform,
            "Ready to build",
            bodyFontSize,
            FontStyle.Normal,
            TextAnchor.MiddleLeft,
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(20f, -135f),
            new Vector2(-40f, 42f));

        Button instructionsButton = CreateButton(
            "Instructions Button",
            panel.transform,
            "HOW TO PLAY",
            secondaryColor,
            new Vector2(0f, 0f),
            new Vector2(0.5f, 0f),
            new Vector2(20f, 18f),
            new Vector2(-10f, 54f),
            ShowInstructions);

        startButton = CreateButton(
            "Start Button",
            panel.transform,
            "START GAME",
            primaryColor,
            new Vector2(0.5f, 0f),
            new Vector2(1f, 0f),
            new Vector2(10f, 18f),
            new Vector2(-20f, 54f),
            StartTowerGame);
    }

    private void BuildInstructionsPanel()
    {
        instructionPanel = CreatePanel(
            "Instructions Panel",
            canvas.transform,
            backgroundColor,
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            Vector2.zero,
            new Vector2(600f, 590f));

        AddText(
            "Instructions Title",
            instructionPanel.transform,
            "THREE PLAYERS. ONE TOWER.",
            34,
            FontStyle.Bold,
            TextAnchor.MiddleCenter,
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(20f, -20f),
            new Vector2(-40f, 70f));

        instructionText = AddText(
            "Instructions",
            instructionPanel.transform,
            "",
            bodyFontSize,
            FontStyle.Normal,
            TextAnchor.UpperLeft,
            new Vector2(0f, 0f),
            new Vector2(1f, 1f),
            new Vector2(36f, -105f),
            new Vector2(-72f, -180f));

        Button closeButton = CreateButton(
            "Close Instructions",
            instructionPanel.transform,
            "LET'S PLAY",
            primaryColor,
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 0f),
            new Vector2(-110f, 24f),
            new Vector2(220f, 60f),
            CloseInstructions);

        instructionPanel.SetActive(false);
    }

    private void BuildGameOverPanel()
    {
        gameOverPanel = CreatePanel(
            "Game Over Panel",
            canvas.transform,
            backgroundColor,
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            Vector2.zero,
            new Vector2(520f, 400f));

        AddText(
            "Game Over Title",
            gameOverPanel.transform,
            "TOWER COMPLETE!",
            38,
            FontStyle.Bold,
            TextAnchor.MiddleCenter,
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(20f, -25f),
            new Vector2(-40f, 80f));

        gameOverScoreText = AddText(
            "Final Score",
            gameOverPanel.transform,
            "FINAL SCORE  0",
            30,
            FontStyle.Bold,
            TextAnchor.MiddleCenter,
            new Vector2(0f, 0.5f),
            new Vector2(1f, 0.5f),
            new Vector2(20f, -20f),
            new Vector2(-40f, 70f));

        CreateButton(
            "Play Again",
            gameOverPanel.transform,
            "BUILD AGAIN",
            primaryColor,
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 0f),
            new Vector2(-120f, 28f),
            new Vector2(240f, 64f),
            RestartTowerGame);

        gameOverPanel.SetActive(false);
    }

    // ---------------------------------------------------------
    // GAME FLOW
    // ---------------------------------------------------------

    public void StartTowerGame()
    {
        if (stackGame == null)
        {
            SetStatus("Tower game reference missing");
            return;
        }

        if (gameStarted && !stackGame.IsGameOver)
        {
            CloseInstructions();
            return;
        }

        stackGame.RestartGame();

        gameStarted = true;

        gameOverPanel.SetActive(false);

        if (startButton != null)
            startButton.gameObject.SetActive(false);

        RefreshScore();
        SetStatus("Build your tower together!");

        if (showInstructionsAtStart)
            ShowInstructions();
        else
            CloseInstructions();

        if (logFlowChanges)
            Debug.Log("Tower game started.", this);
    }

    public void RestartTowerGame()
    {
        if (stackGame == null)
            return;

        stackGame.RestartGame();

        gameStarted = true;

        gameOverPanel.SetActive(false);

        if (startButton != null)
            startButton.gameObject.SetActive(false);

        CloseInstructions();

        RefreshScore();
        SetStatus("New tower! Work together!");

        if (logFlowChanges)
            Debug.Log("Tower game restarted.", this);
    }

    private void ShowWelcomeScreen()
    {
        gameStarted = false;

        if (startButton != null)
            startButton.gameObject.SetActive(true);

        SetStatus("Ready for a team challenge!");

        if (showInstructionsAtStart)
            ShowInstructions();
    }

    private void ShowGameOver()
    {
        if (!gameStarted || gameOverPanel.activeSelf)
            return;

        gameOverPanel.SetActive(true);

        if (gameOverScoreText != null)
        {
            gameOverScoreText.text =
                "FINAL SCORE  " + stackGame.Score;
        }

        SetStatus("The tower has fallen!");

        if (logFlowChanges)
            Debug.Log("Tower game over.", this);
    }

    // ---------------------------------------------------------
    // INSTRUCTIONS
    // ---------------------------------------------------------

    public void ShowInstructions()
    {
        if (instructionPanel == null)
            return;

        if (instructionText != null)
        {
            instructionText.text =
                "PARENT - BUILD\n" +
                "Raise either hand to send the next block into the tower.\n\n" +

                "CHILD - DROP\n" +
                "Close your hand to stop the moving block and place it.\n\n" +

                "BARBER - BALANCE\n" +
                "Move your two hands to tilt and balance the tower.\n\n" +

                "TEAM GOAL\n" +
                "Work together to stack as many blocks as possible. " +
                "Keep the tower standing for the highest score!";
        }

        instructionPanel.SetActive(true);
    }

    public void CloseInstructions()
    {
        if (instructionPanel != null)
            instructionPanel.SetActive(false);
    }

    // ---------------------------------------------------------
    // SCORE AND STATUS
    // ---------------------------------------------------------

    private void RefreshScore()
    {
        if (stackGame == null || scoreText == null)
            return;

        scoreText.text = "SCORE  " + stackGame.Score;
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
            statusText.text = message;
    }

    // ---------------------------------------------------------
    // UI HELPERS
    // ---------------------------------------------------------

    private GameObject CreatePanel(
        string objectName,
        Transform parent,
        Color color,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 pivot,
        Vector2 unused,
        Vector2 position,
        Vector2 size)
    {
        GameObject panel = new GameObject(objectName);

        panel.transform.SetParent(parent, false);

        RectTransform rect = panel.AddComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        Image image = panel.AddComponent<Image>();
        image.color = color;

        return panel;
    }

    private Text AddText(
        string objectName,
        Transform parent,
        string content,
        int fontSize,
        FontStyle fontStyle,
        TextAnchor alignment,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 position,
        Vector2 size)
    {
        GameObject textObject = new GameObject(objectName);

        textObject.transform.SetParent(parent, false);

        RectTransform rect = textObject.AddComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        Text text = textObject.AddComponent<Text>();
        text.text = content;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = fontSize;
        text.fontStyle = fontStyle;
        text.alignment = alignment;
        text.color = textColor;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        return text;
    }

    private Button CreateButton(
        string objectName,
        Transform parent,
        string label,
        Color buttonColor,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 position,
        Vector2 size,
        UnityAction action)
    {
        GameObject buttonObject = new GameObject(objectName);

        buttonObject.transform.SetParent(parent, false);

        RectTransform rect = buttonObject.AddComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        Image image = buttonObject.AddComponent<Image>();
        image.color = buttonColor;

        Button button = buttonObject.AddComponent<Button>();
        button.targetGraphic = image;

        ColorBlock colors = button.colors;
        colors.normalColor = buttonColor;
        colors.highlightedColor = Color.Lerp(buttonColor, Color.white, 0.15f);
        colors.pressedColor = Color.Lerp(buttonColor, Color.black, 0.15f);
        button.colors = colors;

        Text buttonText = AddText(
            "Label",
            buttonObject.transform,
            label,
            20,
            FontStyle.Bold,
            TextAnchor.MiddleCenter,
            Vector2.zero,
            Vector2.one,
            Vector2.zero,
            Vector2.zero);

        buttonText.rectTransform.offsetMin = Vector2.zero;
        buttonText.rectTransform.offsetMax = Vector2.zero;

        if (action != null)
            button.onClick.AddListener(action);

        return button;
    }

    // ---------------------------------------------------------
    // EVENTS AND CLEANUP
    // ---------------------------------------------------------

    private void SubscribeToGame()
    {
        if (subscribed || stackGame == null)
            return;

        stackGame.ScoreChanged += OnScoreChanged;
        //stackGame.GameOver += OnGameOver;

        subscribed = true;
    }

    private void OnScoreChanged(int newScore)
    {
        RefreshScore();
    }

    private void OnGameOver()
    {
        ShowGameOver();
    }

    private void OnDisable()
    {
        if (stackGame != null && subscribed)
        {
            stackGame.ScoreChanged -= OnScoreChanged;
            //stackGame.GameOver -= OnGameOver;
        }

        subscribed = false;
    }
}
