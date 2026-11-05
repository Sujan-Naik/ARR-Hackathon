using System.Diagnostics;
using UnityEngine;

/// One-button music track switcher.
/// Attach to an empty GameObject, drag your tracks into the "Tracks" list.
/// Press the key (default: M) or hook NextTrack() to a UI Button to cycle tracks.
[RequireComponent(typeof(AudioSource))]
public class MusicSwitcher : MonoBehaviour
{
    [Header("Tracks")]
    public bool playOnStart = true;
    public AudioClip trackA;
    public AudioClip trackB;
    bool playingA = true;

    [Header("Input")]
    public KeyCode switchKey = KeyCode.M;

    [Header("Audio")]
    [Range(0f, 1f)] public float volume = 0.6f;
    public float fadeTime = 0.4f;     // seconds to fade out/in when switching (0 = instant)

    [Header("UI")]
    public bool showTrackName = true;

    static MusicSwitcher instance;

    AudioSource source;
    int index = -1;
    float fadeTimer;
    int fadeState;                    // 0 = idle, 1 = fading out, 2 = fading in
    AudioClip pendingClip;
    float nameShownUntil;

    void Awake()
    {
        // Keep the music playing across scene reloads (e.g. game restart)
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);

        source = GetComponent<AudioSource>();
        source.loop = true;
        source.playOnAwake = false;
        source.volume = volume;
    }

    void Start()
    {
        if (playOnStart)
        {
            source.clip = trackA;
            source.Play();
            UnityEngine.Debug.Log("Playing track: " + trackA.name);
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(switchKey)) NextTrack();
        UpdateFade();
    }

    /// Call this from a UI Button's OnClick to use it as the one button.
    public void NextTrack()
    {
        playingA = !playingA;
        AudioClip next = playingA ? trackA : trackB;
        UnityEngine.Debug.Log("Switched to: " + next.name);

        if (fadeTime <= 0f || !source.isPlaying)
        {
            source.clip = next;
            source.volume = volume;
            source.Play();
            fadeState = 0;
        }
        else
        {
            pendingClip = next;
            fadeState = 1;
            fadeTimer = 0f;
        }
    }

    void UpdateFade()
    {
        if (fadeState == 0) return;

        fadeTimer += Time.unscaledDeltaTime;
        float t = Mathf.Clamp01(fadeTimer / (fadeTime * 0.5f));

        if (fadeState == 1)
        {
            source.volume = Mathf.Lerp(volume, 0f, t);
            if (t >= 1f)
            {
                source.clip = pendingClip;
                source.Play();
                fadeState = 2;
                fadeTimer = 0f;
            }
        }
        else
        {
            source.volume = Mathf.Lerp(0f, volume, t);
            if (t >= 1f) fadeState = 0;
        }
    }

    void OnGUI()
    {
        if (!showTrackName) return;
        if (Time.time > nameShownUntil) return;

        AudioClip current = playingA ? trackA : trackB;
        if (current == null) return;

        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 20,
            alignment = TextAnchor.UpperRight
        };
        GUI.Label(new Rect(Screen.width - 420, 20, 400, 40), "♪ " + current.name, style);
    }
}
