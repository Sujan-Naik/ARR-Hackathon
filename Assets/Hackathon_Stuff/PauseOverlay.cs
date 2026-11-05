using UnityEngine;

/// Toggles a full-screen overlay with text and pauses the game while it is shown.
/// Attach to an empty GameObject. Press the toggle key (default: P) to show/hide it.
public class PauseOverlay : MonoBehaviour
{
    /// Other scripts can check this to ignore input while paused.
    public static bool IsPaused { get; private set; }

    [Header("Input")]
    public KeyCode toggleKey = KeyCode.P;

    [Header("Overlay")]
    [TextArea] public string overlayText = "PAUSED\nPress P to resume";
    public Color overlayColor = new Color(0f, 0f, 0f, 0.7f);
    public Color textColor = Color.white;
    public int fontSize = 48;

    [Header("Options")]
    public bool pauseAudio = false;   // also pause music/sounds while the overlay is open

    Texture2D pixel;

    void Awake()
    {
        IsPaused = false;
        pixel = new Texture2D(1, 1);
        pixel.SetPixel(0, 0, Color.white);
        pixel.Apply();
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey)) Toggle();
    }

    /// Shows/hides the overlay. Can also be hooked to a UI Button's OnClick.
    public void Toggle()
    {
        SetPaused(!IsPaused);
    }

    void SetPaused(bool paused)
    {
        IsPaused = paused;
        Time.timeScale = paused ? 0f : 1f;
        if (pauseAudio) AudioListener.pause = paused;
        Debug.Log("Game " + (paused ? "paused" : "resumed"));
    }

    void OnGUI()
    {
        if (!IsPaused) return;

        GUI.depth = -1000; // draw on top of other OnGUI elements

        var previous = GUI.color;
        GUI.color = overlayColor;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), pixel);
        GUI.color = previous;

        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        style.normal.textColor = textColor;

        GUI.Label(new Rect(0, 0, Screen.width, Screen.height), overlayText, style);
    }

    void OnDestroy()
    {
        // Never leave the game frozen after a scene reload or object removal
        if (IsPaused)
        {
            IsPaused = false;
            Time.timeScale = 1f;
            AudioListener.pause = false;
        }
    }
}
