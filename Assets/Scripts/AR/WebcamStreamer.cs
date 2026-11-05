using UnityEngine;
using UnityEngine.UI;
using System.Collections;

[RequireComponent(typeof(RawImage))]
public class WebcamFullscreenStreamer : MonoBehaviour
{
    [SerializeField] private int requestedWidth = 1920;
    [SerializeField] private int requestedHeight = 1080;
    [SerializeField] private int requestedFps = 30;

    private WebCamTexture webcamTexture;
    private RawImage targetRawImage;
    private AspectRatioFitter aspectFitter;
    private Coroutine startRoutine;

    void Awake()
    {
        targetRawImage = GetComponent<RawImage>();

        // Stretch the RawImage to fill its parent (the canvas)
        RectTransform rt = (RectTransform)transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        // Fill the screen while keeping the camera's aspect ratio (crops edges if needed)
        aspectFitter = GetComponent<AspectRatioFitter>();
        if (aspectFitter == null)
            aspectFitter = gameObject.AddComponent<AspectRatioFitter>();
        aspectFitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
    }

    void OnEnable()
    {
        startRoutine = StartCoroutine(StartWebcam());
    }

    void OnDisable()
    {
        if (startRoutine != null)
        {
            StopCoroutine(startRoutine);
            startRoutine = null;
        }

        if (webcamTexture != null && webcamTexture.isPlaying)
            webcamTexture.Stop();
    }

    void OnDestroy()
    {
        if (webcamTexture != null)
        {
            webcamTexture.Stop();
            Destroy(webcamTexture);
            webcamTexture = null;
        }
    }

    IEnumerator StartWebcam()
    {
        // 1. Request permission if it hasn't been granted yet
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        }

        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            Debug.LogError("Webcam permission denied.");
            yield break;
        }

        if (WebCamTexture.devices.Length == 0)
        {
            Debug.LogError("No webcam devices found.");
            yield break;
        }

        // 2. Create the texture once and reuse it on later enables
        if (webcamTexture == null)
        {
            string deviceName = WebCamTexture.devices[0].name;
            webcamTexture = new WebCamTexture(deviceName, requestedWidth, requestedHeight, requestedFps);
        }

        // 3. Assign and start the stream
        targetRawImage.texture = webcamTexture;
        webcamTexture.Play();

        // 4. Wait until the camera reports its real dimensions
        float timeout = 5f;
        while (webcamTexture.width <= 16 && timeout > 0f)
        {
            timeout -= Time.deltaTime;
            yield return null;
        }

        ApplyOrientation();
        startRoutine = null;
    }

    void ApplyOrientation()
    {
        int angle = webcamTexture.videoRotationAngle;
        bool mirrored = webcamTexture.videoVerticallyMirrored;

        // Rotate the image so it appears upright
        targetRawImage.rectTransform.localEulerAngles = new Vector3(0f, 0f, -angle);

        // Flip vertically if the camera reports a mirrored feed
        targetRawImage.uvRect = mirrored
            ? new Rect(0f, 1f, 1f, -1f)
            : new Rect(0f, 0f, 1f, 1f);

        // Match the aspect ratio to the camera (swap for 90/270 degree rotations)
        float ratio = (angle == 90 || angle == 270)
            ? (float)webcamTexture.height / webcamTexture.width
            : (float)webcamTexture.width / webcamTexture.height;

        aspectFitter.aspectRatio = ratio;
    }
}