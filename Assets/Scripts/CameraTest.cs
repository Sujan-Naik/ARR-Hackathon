using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RawImage))]
public class CameraTest : MonoBehaviour
{
    void Start()
    {
        Debug.Log($"Webcam devices: {WebCamTexture.devices.Length}");
        if (WebCamTexture.devices.Length == 0) return;

        var cam = new WebCamTexture(WebCamTexture.devices[0].name, 1280, 720, 30);
        GetComponent<RawImage>().texture = cam;
        cam.Play();
        Debug.Log($"Playing: {cam.isPlaying}");
    }
}