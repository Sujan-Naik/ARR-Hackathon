using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using Stacking;

/// <summary>
/// Main flow controller for the three-person tower stacking game.
///
/// Phases:  Welcome (centered card, pre-game only)
///       -> Playing (HUD bottom-left, help button bottom-right, nothing in the middle)
///       -> GameOver (centered card, drawn on top of gameplay)
///
/// It is the ONLY script that tells StackingDirector to start spawning blocks
/// (via BeginRound()).
/// </summary>
public class KidsSalonGameFlow : MonoBehaviour
{
    private enum Phase { Welcome, Playing, GameOver }

    [Header("Game references")]
    [SerializeField] private StackGame stackGame;
    [SerializeField] private StackingDirector stackingDirector;

    [Header("Game settings")]
    [SerializeField] private bool startGameAutomatically = true;
    [Tooltip("Seconds after game over before the keyboard restart key is accepted (avoids accidental restarts).")]
    [SerializeField, Min(0f)] private float restartKeyDelay = 0.75f;

    [Header("HUD behaviour")]
    [SerializeField] private bool autoFadeCompactHud = true;
    [SerializeField, Range(0f, 1f)] private float idleOpacity = 0.25f;
    [SerializeField, Range(0f, 1f)] private float activeOpacity = 0.92f;
    [SerializeField, Min(0f)] private float idleDelaySeconds = 2.5f;
    [SerializeField, Min(0.1f)] private float fadeSpeed = 4f;
    [SerializeField] private bool allowTabToHide = true;

    [Header("UI appearance")]
    [SerializeField] private Color backgroundColor = new Color(0.035f, 0.055f, 0.11f, 0.94f);
    [SerializeField] private Color panelColor = new Color(0.10f, 0.14f, 0.24f, 0.96f);
    [SerializeField] private Color compactHudColor = new Color(0.06f, 0.09f, 0.16f, 0.72f);
    [SerializeField] private Color primaryColor = new Color(0.25f, 0.78f, 0.98f);
    [SerializeField] private Color secondaryColor = new Color(1f, 0.72f, 0.28f);
    [SerializeField] private Color textColor = Color.white;
    [SerializeField] private Color warnColor = new Color(1f, 0.35f, 0.35f, 1f);
    [SerializeField] private Color matchColor = new Color(0.55f, 1f, 0.55f, 1f);
    [SerializeField] private Color helpButtonColor = new Color(0.25f, 0.78f, 0.98f, 1f);

    [Header("UI sizes")]
    [SerializeField] private float screenMargin = 24f;
    [SerializeField] private float welcomePanelWidth = 640f;
    [SerializeField] private float helpPanelWidth = 420f;
    [SerializeField] private float gameOverPanelWidth = 380f;
    [SerializeField] private int titleFontSize = 38;
    [SerializeField] private int bodyFontSize = 20;
    [SerializeField] private int helpFontSize = 18;
    [SerializeField] private int compactFontSize = 20;
    [SerializeField] private float compactHudHeight = 46f;
    [SerializeField] private float helpButtonSize = 56f;

    [Header("Keyboard")]
    [Tooltip("Starts the game on the welcome screen and restarts it on the game-over screen.")]
    [SerializeField] private KeyCode startKey = KeyCode.Space;
    [SerializeField] private KeyCode toggleHelpKey = KeyCode.H;
    [SerializeField] private KeyCode closeHelpKey = KeyCode.Escape;
    [SerializeField] private KeyCode toggleCompactHudKey = KeyCode.Tab;

    [Header("Debug")]
    [SerializeField] private bool logFlowChanges;

    private const string InstructionsBody =
        "PARENT - SEND\n" +
        "Raise either hand once to send the next block sweeping across. " +
        "Raise it again to lock it in place.\n\n" +

        "BARBER - GUIDE\n" +
        "Reach your hands toward the child's head to tilt their head. " +
        "While you guide them, the tower follows their head.\n\n" +

        "CHILD - HOLD\n" +
        "When the barber lets go, the tower keeps its lean. Mirror the " +
        "tower with your head to keep it steady.\n\n" +

        "RED FLASH\n" +
        "If the screen flashes red, your head doesn't match the tower. " +
        "Tilt your head until it stops.\n\n" +

        "TEAM GOAL\n" +
        "Stack as many blocks as possible without the tower falling!";

    // ---- runtime UI ----
    private Canvas canvas;
    private GameObject welcomePanel;
    private GameObject compactHudRoot;
    private CanvasGroup compactHudGroup;
    private Text compactScoreText;
    private Text compactModeText;
    private GameObject helpButton;
    private GameObject helpPanel;
    private GameObject gameOverPanel;
    private Text gameOverScoreText;
    private Text gameOverBestText;

    // ---- runtime state ----
    private Phase phase = Phase.Welcome;
    private bool subscribed;
    private bool interfaceBuilt;
    private bool helpOpen;
    private bool compactHudHidden;
    private int bestScore;
    private float gameOverTime;

    private int lastScore = -1;
    private TiltMatch lastMatch = TiltMatch.Unknown;
    private bool lastGuiding;
    private float lastActivityTime;
    private float currentOpacity = 1f;

    // ---------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------

    private void Awake()
    {
        if (stackGame == null)
            stackGame = FindObjectOfType<StackGame>();

        if (stackingDirector == null)
            stackingDirector = FindObjectOfType<StackingDirector>();

        // Disarm the director the moment we come online so nothing spawns
        // before the player starts the game.
        if (stackingDirector != null)
            stackingDirector.EndRound();

        BuildInterface();
    }

    private void OnEnable() => SubscribeToGame();

    private void Start()
    {
        SubscribeToGame();

        if (stackGame == null)
            return;

        if (startGameAutomatically)
            StartTowerGame();
        else
            ShowWelcomeScreen();
    }

    private void Update()
    {
        HandleKeyboard();

        if (stackGame == null || phase != Phase.Playing)
            return;

        RefreshScore();
        RefreshCompactHud();
        UpdateCompactHudFade();
    }

    private void OnDisable()
    {
        if (stackGame != null && subscribed)
        {
            stackGame.ScoreChanged -= OnScoreChanged;
            stackGame.GameOver -= OnGameOver;
        }

        subscribed = false;

        if (stackingDirector != null)
            stackingDirector.EndRound();
    }

    // ---------------------------------------------------------
    // Keyboard
    // ---------------------------------------------------------

    private void HandleKeyboard()
    {
        switch (phase)
        {
            case Phase.Welcome:
                if (Input.GetKeyDown(startKey))
                    StartTowerGame();
                break;

            case Phase.Playing:
                if (Input.GetKeyDown(toggleHelpKey))
                    SetHelpOpen(!helpOpen);
                else if (helpOpen && Input.GetKeyDown(closeHelpKey))
                    SetHelpOpen(false);

                if (allowTabToHide && Input.GetKeyDown(toggleCompactHudKey))
                {
                    compactHudHidden = !compactHudHidden;
                    if (compactHudRoot != null)
                        compactHudRoot.SetActive(!compactHudHidden);
                }
                break;

            case Phase.GameOver:
                if (Time.unscaledTime - gameOverTime >= restartKeyDelay && Input.GetKeyDown(startKey))
                    RestartTowerGame();
                break;
        }
    }

    // ---------------------------------------------------------
    // GAME FLOW
    // ---------------------------------------------------------

    public void StartTowerGame()
    {
        if (phase == Phase.Playing) return;
        BeginRound();
    }

    public void RestartTowerGame() => BeginRound();

    private void BeginRound()
    {
        if (stackGame == null) return;

        // Disarm, reset, then re-arm so the director can't spawn mid-reset.
        if (stackingDirector != null) stackingDirector.EndRound();

        stackGame.RestartGame();
        phase = Phase.Playing;

        if (stackingDirector != null) stackingDirector.BeginRound();

        lastScore = -1;
        lastMatch = TiltMatch.Unknown;
        lastGuiding = false;

        SetHelpOpen(false);
        ApplyPhaseVisibility();
        MarkActivity();
        currentOpacity = activeOpacity;
        if (compactHudGroup != null) compactHudGroup.alpha = activeOpacity;

        RefreshScore();

        if (logFlowChanges)
            Debug.Log("Tower round started.", this);
    }

    private void ShowWelcomeScreen()
    {
        phase = Phase.Welcome;
        if (stackingDirector != null) stackingDirector.EndRound();
        SetHelpOpen(false);
        ApplyPhaseVisibility();
    }

    private void OnGameOver(int finalScore)
    {
        if (phase != Phase.Playing) return;

        phase = Phase.GameOver;
        gameOverTime = Time.unscaledTime;

        if (stackingDirector != null) stackingDirector.EndRound();

        bestScore = Mathf.Max(bestScore, finalScore);
        if (gameOverScoreText != null) gameOverScoreText.text = "SCORE  " + finalScore;
        if (gameOverBestText != null) gameOverBestText.text = "BEST  " + bestScore;

        SetHelpOpen(false);
        ApplyPhaseVisibility();

        if (logFlowChanges)
            Debug.Log("Tower game over. Score " + finalScore, this);
    }

    /// <summary>Single place that decides what is visible in each phase.</summary>
    private void ApplyPhaseVisibility()
    {
        if (welcomePanel != null) welcomePanel.SetActive(phase == Phase.Welcome);
        if (compactHudRoot != null)
        {
            compactHudRoot.SetActive(phase == Phase.Playing && !compactHudHidden);
            if (compactHudRoot.activeSelf)
                compactHudRoot.transform.SetAsLastSibling();
        }
        if (helpButton != null) helpButton.SetActive(phase == Phase.Playing);
        if (gameOverPanel != null)
        {
            gameOverPanel.SetActive(phase == Phase.GameOver);
            if (phase == Phase.GameOver)
                gameOverPanel.transform.SetAsLastSibling();
        }
        if (helpPanel != null && phase != Phase.Playing) helpPanel.SetActive(false);
    }

    // ---------------------------------------------------------
    // HELP (non-blocking, never pauses the game)
    // ---------------------------------------------------------

    public void ToggleHelp() => SetHelpOpen(!helpOpen);

    private void SetHelpOpen(bool open)
    {
        helpOpen = open && phase == Phase.Playing;
        if (helpPanel != null) helpPanel.SetActive(helpOpen);
    }

    // ---------------------------------------------------------
    // UI CREATION
    // ---------------------------------------------------------

    private void BuildInterface()
    {
        if (interfaceBuilt) return;
        interfaceBuilt = true;

        GameObject canvasObject = new GameObject("Tower Game UI");
        // Parent to the root so it isn't affected by this object's transform/scale.
        canvasObject.transform.SetParent(null, false);

        canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Draw above gameplay canvases and world geometry.
        canvas.overrideSorting = true;
        canvas.sortingOrder = 32000;

        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        canvasObject.AddComponent<GraphicRaycaster>();

        BuildWelcomePanel();
        BuildCompactHud();
        BuildHelpPanel();
        BuildHelpButton();
        BuildGameOverPanel();

        welcomePanel.SetActive(false);
        compactHudRoot.SetActive(false);
        helpButton.SetActive(false);
        helpPanel.SetActive(false);
        gameOverPanel.SetActive(false);
    }

    /// <summary>Centered card. Only ever visible BEFORE a round starts.</summary>
    private void BuildWelcomePanel()
    {
        welcomePanel = CreatePanel(
            "Welcome Panel", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            Vector2.zero, welcomePanelWidth, backgroundColor,
            new RectOffset(32, 32, 24, 24), 14f, TextAnchor.UpperCenter);

        AddText("Title", welcomePanel.transform, "TOWER CHALLENGE",
            titleFontSize, FontStyle.Bold, TextAnchor.MiddleCenter, 56f);

        AddText("Subtitle", welcomePanel.transform, "THREE PLAYERS. ONE TOWER.",
            bodyFontSize + 2, FontStyle.Bold, TextAnchor.MiddleCenter, 36f)
            .color = secondaryColor;

        Text body = AddText("Instructions", welcomePanel.transform, InstructionsBody,
            helpFontSize + 1, FontStyle.Normal, TextAnchor.UpperLeft, 0f);
        body.verticalOverflow = VerticalWrapMode.Overflow;

        GameObject row = CreateRow("Button Row", welcomePanel.transform, 60f);
        CreateButton("Start Button", row.transform, "START GAME  (SPACE)", primaryColor, StartTowerGame);
    }

    /// <summary>Score + mode readout, bottom-left, kept above gameplay.</summary>
    private void BuildCompactHud()
    {
        compactHudRoot = new GameObject("Compact HUD");
        compactHudRoot.transform.SetParent(canvas.transform, false);

        RectTransform rect = compactHudRoot.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 0f);
        rect.pivot = new Vector2(0f, 0f);
        rect.anchoredPosition = new Vector2(screenMargin, screenMargin);
        rect.sizeDelta = new Vector2(0f, compactHudHeight);

        Image bg = compactHudRoot.AddComponent<Image>();
        bg.color = compactHudColor;
        bg.raycastTarget = false;

        HorizontalLayoutGroup layout = compactHudRoot.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(14, 16, 0, 0);
        layout.spacing = 10f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;

        ContentSizeFitter fitter = compactHudRoot.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

        compactHudGroup = compactHudRoot.AddComponent<CanvasGroup>();
        compactHudGroup.alpha = activeOpacity;
        compactHudGroup.blocksRaycasts = false;
        compactHudGroup.interactable = false;

        compactScoreText = AddInlineText("Score", compactHudRoot.transform, "SCORE 0",
            compactFontSize, FontStyle.Bold, textColor);

        AddInlineText("Sep", compactHudRoot.transform, "·",
            compactFontSize, FontStyle.Normal, new Color(1f, 1f, 1f, 0.35f));

        compactModeText = AddInlineText("Mode", compactHudRoot.transform, "WAITING",
            compactFontSize, FontStyle.Bold, new Color(0.85f, 0.9f, 1f, 1f));

        // Keep HUD above gameplay.
        compactHudRoot.transform.SetAsLastSibling();
    }

    /// <summary>Round "?" button, bottom-right.</summary>
    private void BuildHelpButton()
    {
        helpButton = new GameObject("Help Button");
        helpButton.transform.SetParent(canvas.transform, false);

        RectTransform rect = helpButton.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(1f, 0f);
        rect.anchoredPosition = new Vector2(-screenMargin, screenMargin);
        rect.sizeDelta = new Vector2(helpButtonSize, helpButtonSize);

        Image bg = helpButton.AddComponent<Image>();
        bg.color = helpButtonColor;

        Button button = helpButton.AddComponent<Button>();
        button.targetGraphic = bg;
        ApplyButtonColors(button, helpButtonColor);
        button.onClick.AddListener(ToggleHelp);

        Text label = AddText("Label", helpButton.transform, "?", 28,
            FontStyle.Bold, TextAnchor.MiddleCenter, 0f);
        StretchToParent(label.rectTransform);
    }

    /// <summary>Small non-blocking panel that opens above the help button.</summary>
    private void BuildHelpPanel()
    {
        helpPanel = CreatePanel(
            "Help Panel", new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-screenMargin, screenMargin + helpButtonSize + 12f),
            helpPanelWidth, backgroundColor,
            new RectOffset(22, 22, 18, 18), 10f, TextAnchor.UpperLeft);

        AddText("Help Title", helpPanel.transform, "HOW TO PLAY",
            helpFontSize + 6, FontStyle.Bold, TextAnchor.MiddleLeft, 34f);

        Text body = AddText("Help Body", helpPanel.transform, InstructionsBody,
            helpFontSize - 2, FontStyle.Normal, TextAnchor.UpperLeft, 0f);
        body.verticalOverflow = VerticalWrapMode.Overflow;

        Text hint = AddText("Help Hint", helpPanel.transform,
            "Press H or ESC to close. The game keeps running.",
            helpFontSize - 4, FontStyle.Italic, TextAnchor.MiddleLeft, 24f);
        hint.color = new Color(1f, 1f, 1f, 0.55f);
    }

    /// <summary>Compact result card, centered and brought to the front, with a restart button.</summary>
    private void BuildGameOverPanel()
    {
        gameOverPanel = CreatePanel(
            "Game Over Panel", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            Vector2.zero,
            gameOverPanelWidth, backgroundColor,
            new RectOffset(24, 24, 20, 20), 10f, TextAnchor.UpperCenter);

        // Ensure the game over card renders in front of everything else.
        gameOverPanel.transform.SetAsLastSibling();

        CanvasGroup group = gameOverPanel.GetComponent<CanvasGroup>();
        if (group == null) group = gameOverPanel.AddComponent<CanvasGroup>();
        group.blocksRaycasts = true;
        group.interactable = true;
        group.alpha = 1f;

        AddText("Game Over Title", gameOverPanel.transform, "TOWER COMPLETE!",
            30, FontStyle.Bold, TextAnchor.MiddleCenter, 46f);

        gameOverScoreText = AddText("Final Score", gameOverPanel.transform, "SCORE  0",
            34, FontStyle.Bold, TextAnchor.MiddleCenter, 50f);

        gameOverBestText = AddText("Best Score", gameOverPanel.transform, "BEST  0",
            bodyFontSize, FontStyle.Normal, TextAnchor.MiddleCenter, 30f);
        gameOverBestText.color = secondaryColor;

        GameObject row = CreateRow("Button Row", gameOverPanel.transform, 56f);
        CreateButton("Play Again", row.transform, "BUILD AGAIN  (SPACE)", primaryColor, RestartTowerGame);
    }

    // ---------------------------------------------------------
    // COMPACT HUD REFRESH
    // ---------------------------------------------------------

    private void RefreshScore()
    {
        if (stackGame == null || compactScoreText == null) return;

        int score = stackGame.Score;
        compactScoreText.text = "SCORE " + score;

        if (score != lastScore)
        {
            lastScore = score;
            MarkActivity();
        }
    }

    private void RefreshCompactHud()
    {
        if (compactModeText == null) return;

        if (stackingDirector == null)
        {
            compactModeText.text = "MODE -";
            compactModeText.color = new Color(0.85f, 0.9f, 1f, 1f);
            return;
        }

        bool guiding = stackingDirector.BarberGuiding;
        TiltMatch match = stackingDirector.Match;

        string mode = guiding ? "GUIDING" : "HOLDING";
        string suffix = "";
        Color color = new Color(0.85f, 0.9f, 1f, 1f);

        switch (match)
        {
            case TiltMatch.Matched:
                suffix = " · MATCHED";
                color = matchColor;
                break;
            case TiltMatch.Mismatched:
                suffix = " · MISMATCH";
                color = warnColor;
                break;
        }

        compactModeText.text = mode + suffix;
        compactModeText.color = color;

        bool changed = guiding != lastGuiding || match != lastMatch;
        lastGuiding = guiding;
        lastMatch = match;

        if (changed)
            MarkActivity();
    }

    // ---------------------------------------------------------
    // COMPACT HUD FADE
    // ---------------------------------------------------------

    private void MarkActivity() => lastActivityTime = Time.time;

    private void UpdateCompactHudFade()
    {
        if (compactHudGroup == null || compactHudRoot == null || !compactHudRoot.activeSelf)
            return;

        if (!autoFadeCompactHud)
        {
            compactHudGroup.alpha = activeOpacity;
            currentOpacity = activeOpacity;
            return;
        }

        bool mismatch = lastMatch == TiltMatch.Mismatched;
        bool recentActivity = Time.time - lastActivityTime < idleDelaySeconds;
        float target = (mismatch || recentActivity) ? activeOpacity : idleOpacity;

        currentOpacity = Mathf.MoveTowards(currentOpacity, target, Time.unscaledDeltaTime * fadeSpeed);
        compactHudGroup.alpha = currentOpacity;
    }

    // ---------------------------------------------------------
    // UI HELPERS
    // ---------------------------------------------------------

    /// <summary>Auto-sizing vertical panel anchored to a screen point.</summary>
    private GameObject CreatePanel(
        string objectName, Vector2 anchor, Vector2 pivot, Vector2 anchoredPos,
        float width, Color color, RectOffset padding, float spacing, TextAnchor childAlignment)
    {
        GameObject panel = new GameObject(objectName);
        panel.transform.SetParent(canvas.transform, false);

        RectTransform rect = panel.AddComponent<RectTransform>();
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.anchoredPosition = anchoredPos;
        rect.sizeDelta = new Vector2(width, 0f);

        Image bg = panel.AddComponent<Image>();
        bg.color = color;

        VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.padding = padding;
        layout.spacing = spacing;
        layout.childAlignment = childAlignment;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        ContentSizeFitter fitter = panel.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        return panel;
    }

    private GameObject CreateRow(string objectName, Transform parent, float height)
    {
        GameObject row = new GameObject(objectName);
        row.transform.SetParent(parent, false);
        row.AddComponent<RectTransform>().sizeDelta = new Vector2(0f, height);

        HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 10f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;

        LayoutElement element = row.AddComponent<LayoutElement>();
        element.preferredHeight = height;
        element.minHeight = height;
        return row;
    }

    /// <summary>preferredHeight &lt;= 0 lets the text size itself from its content.</summary>
    private Text AddText(
        string objectName, Transform parent, string content, int fontSize,
        FontStyle fontStyle, TextAnchor alignment, float preferredHeight = 32f)
    {
        GameObject textObject = new GameObject(objectName);
        textObject.transform.SetParent(parent, false);

        RectTransform rect = textObject.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0f, Mathf.Max(0f, preferredHeight));

        if (preferredHeight > 0f)
        {
            LayoutElement layoutElement = textObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = preferredHeight;
            layoutElement.minHeight = preferredHeight;
        }

        Text text = textObject.AddComponent<Text>();
        text.text = content;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = fontSize;
        text.fontStyle = fontStyle;
        text.alignment = alignment;
        text.color = textColor;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;

        return text;
    }

    private Text AddInlineText(
        string objectName, Transform parent, string content, int fontSize,
        FontStyle fontStyle, Color color)
    {
        GameObject textObject = new GameObject(objectName);
        textObject.transform.SetParent(parent, false);

        RectTransform rect = textObject.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0f, compactHudHeight);

        LayoutElement layoutElement = textObject.AddComponent<LayoutElement>();
        layoutElement.preferredHeight = compactHudHeight;
        layoutElement.minHeight = compactHudHeight;
        layoutElement.flexibleWidth = 0f;

        Text text = textObject.AddComponent<Text>();
        text.text = content;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = fontSize;
        text.fontStyle = fontStyle;
        text.alignment = TextAnchor.MiddleLeft;
        text.color = color;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        text.supportRichText = false;

        return text;
    }

    private Button CreateButton(
        string objectName, Transform parent, string label, Color buttonColor, UnityAction action)
    {
        GameObject buttonObject = new GameObject(objectName);
        buttonObject.transform.SetParent(parent, false);

        buttonObject.AddComponent<RectTransform>().sizeDelta = new Vector2(0f, 52f);

        LayoutElement layoutElement = buttonObject.AddComponent<LayoutElement>();
        layoutElement.preferredHeight = 52f;
        layoutElement.minHeight = 52f;
        layoutElement.flexibleWidth = 1f;

        Image image = buttonObject.AddComponent<Image>();
        image.color = buttonColor;

        Button button = buttonObject.AddComponent<Button>();
        button.targetGraphic = image;
        ApplyButtonColors(button, buttonColor);

        Text buttonText = AddText("Label", buttonObject.transform, label, 20,
            FontStyle.Bold, TextAnchor.MiddleCenter, 0f);
        buttonText.color = new Color(0.04f, 0.06f, 0.12f, 1f);
        StretchToParent(buttonText.rectTransform);

        if (action != null)
            button.onClick.AddListener(action);

        return button;
    }

    private static void ApplyButtonColors(Button button, Color baseColor)
    {
        ColorBlock colors = button.colors;
        colors.normalColor = baseColor;
        colors.highlightedColor = Color.Lerp(baseColor, Color.white, 0.15f);
        colors.pressedColor = Color.Lerp(baseColor, Color.black, 0.15f);
        button.colors = colors;
    }

    private static void StretchToParent(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    // ---------------------------------------------------------
    // EVENTS
    // ---------------------------------------------------------

    private void SubscribeToGame()
    {
        if (subscribed || stackGame == null) return;
        stackGame.ScoreChanged += OnScoreChanged;
        stackGame.GameOver += OnGameOver;
        subscribed = true;
    }

    private void OnScoreChanged(int newScore)
    {
        if (phase != Phase.Playing) return;
        RefreshScore();
        MarkActivity();
    }
}