# AR Vision

A webcam-based vision layer for Unity. It detects and tracks faces, hands, and objects, then exposes them to gameplay through events, a read-only `Observation` struct, and static helpers for head pose and hand gestures. Game code never handles tensors.


Face Detector: https://huggingface.co/globalnebula/insightface-buffalo-l-onnx/blob/main/scrfd_10g.onnx

Face Embedder: https://huggingface.co/globalnebula/insightface-buffalo-l-onnx/blob/main/arcface_w600k_r50.onnx

Palm Detection: https://huggingface.co/opencv/palm_detection_mediapipe/tree/main

Hand Landmarker: https://huggingface.co/opencv/handpose_estimation_mediapipe/blob/main/handpose_estimation_mediapipe_2023feb.onnx

Object Detector: https://huggingface.co/webml/yolov8n/tree/main/onnx


## Contents

- [Quick start](#quick-start)
- [Class reference](#class-reference)
  - [VisionPipeline](#visionpipeline)
  - [Observation](#observation)
  - [HeadPose and HeadAngles](#headpose-and-headangles)
  - [HandGestures](#handgestures)
  - [ObservationKind](#observationkind)
  - [TrackableProfile](#trackableprofile)
  - [CharacterProfile](#characterprofile)
  - [ItemProfile](#itemprofile)
  - [TrackableRegistry](#trackableregistry)
  - [ModelSettings](#modelsettings)
- [Output reference](#output-reference)
- [Coordinates](#coordinates)
- [Examples](#examples)
- [Limitations](#limitations)
- [Notes](#notes)

---

## Quick start

**Prerequisites**

1. The `Hidden/OrientCorrect` shader is present in the project.
2. The `.onnx` models are imported into `Assets/Models/Vision/`.

**Setup**

3. A `RawImage` is placed on a Screen Space - Overlay canvas, and `VisionPipeline` is added to it.
4. A `TrackableRegistry` asset is created (right-click → AR → Vision → Trackable Registry).
5. Profiles are created for each recognized thing (AR → Vision → Character Profile or Item Profile), and each one is added to the registry.
6. The registry is assigned to `VisionPipeline → Profiles → Registry`.
7. Model assets are assigned under `VisionPipeline → Models`, and the modules to use are enabled.
8. `Requested Width` and `Requested Height` are set to 1280 and 720.

**Gameplay**

9. A script is written with a `VisionPipeline` field, and its events are subscribed in `OnEnable` and unsubscribed in `OnDisable`:

```csharp
[SerializeField] private VisionPipeline vision;

void OnEnable()  => vision.IdentityChanged += OnIdentityChanged;
void OnDisable() => vision.IdentityChanged -= OnIdentityChanged;

void OnIdentityChanged(Observation o, TrackableProfile profile)
{
    // respond to the recognized profile here
}
```

10. The script is added to a GameObject, and `vision` is assigned in the Inspector.

**Run**

11. Play mode is entered, and the console is checked for missing-asset errors.

---

## Class reference

### VisionPipeline

File: [`Assets/Scripts/AR/Vision/VisionPipeline.cs`](Assets/Scripts/AR/Vision/VisionPipeline.cs)

A `MonoBehaviour` that requires a `RawImage` on the same GameObject. It owns the webcam, the orientation shader, the frame readback, the module slots, and the public events. This is the only class most gameplay code needs to reference.

**Inspector fields**

| Group | Field | Type | Default | Purpose |
|---|---|---|---|---|
| Webcam | `requestedWidth` | `int` | 1920 | Requested capture width. Use 1280 if the image looks grainy. |
| Webcam | `requestedHeight` | `int` | 1080 | Requested capture height. Use 720 if the image looks grainy. |
| Webcam | `requestedFps` | `int` | 30 | Requested frame rate. |
| Models | `models` | `ModelSettings` | new instance | Holds all module assets and thresholds. See [ModelSettings](#modelsettings). |
| Modules | `enableFaces` | `bool` | true | Creates the face module and its tracker. |
| Modules | `enableHands` | `bool` | false | Creates the hand module and its tracker. |
| Modules | `enableObjects` | `bool` | false | Creates the object module and its tracker. |
| Tracking | `trackIouThreshold` | `float` | 0.3 | Minimum IoU for a detection to match an existing track. |
| Tracking | `maxMissedFrames` | `int` | 5 | Frames a track can go unmatched before `ObservationLost` fires. |
| Profiles | `registry` | `TrackableRegistry` | none | Maps recognized labels to profile assets. Required for `Profile` to be set. |
| Debug | `drawOverlay` | `bool` | true | Draws boxes and labels with `OnGUI`. Turn off for builds. |

**Public API**

| Member | Kind | Returns or fires | Purpose |
|---|---|---|---|
| `ObservationAppeared` | event | `Action<Observation>` | A new track was created this frame. |
| `ObservationUpdated` | event | `Action<Observation>` | Fires every frame for each active track. |
| `ObservationLost` | event | `Action<ObservationKind, int>` | A track was removed. Passes its `TrackId`. |
| `IdentityChanged` | event | `Action<Observation, TrackableProfile>` | A track's label, identity, or profile changed. |
| `Observations` | property | `IReadOnlyList<Observation>` | All active observations from the latest completed frame. |
| `FrameAspect` | property | `float` | Width divided by height of the upright frame. Pass it to `HeadPose` and `HandGestures`. |
| `IsReady()` | method | `bool` | True when the webcam is playing and the frame buffers exist. |
| `EnrollLargestFace(string)` | method | `void` | Stores the largest visible face in the gallery under the given name. |

---

### Observation

File: [`Assets/Scripts/AR/Vision/Observation.cs`](Assets/Scripts/AR/Vision/Observation.cs)

A `readonly struct` received in events and returned by `Observations`. It is a snapshot, so copy it if you need it after the frame ends.

| Property | Type | Purpose |
|---|---|---|
| `TrackId` | `int` | Stable ID while the track stays active. |
| `Kind` | `ObservationKind` | `Face`, `Hand`, or `Object`. |
| `Label` | `string` | Recognized name, class name, or the kind name when unidentified. |
| `Profile` | `TrackableProfile` | Resolved profile asset, or null. |
| `Confidence` | `float` | Detector score. |
| `MatchScore` | `float` | Gallery similarity for faces. |
| `NormalizedRect` | `Rect` | Bounding box in normalized frame coordinates. |
| `Keypoints` | `Vector2[]` | Landmarks in normalized frame coordinates. Face: 5 points. Hand: 21 points. Object: empty. |
| `FramesTracked` | `int` | Frames the track has been active. Use it to measure how long something has been visible. |
| `IsIdentified` | `bool` | True when the label has been accepted as an identity. |
| `Center` | `Vector2` | Center of `NormalizedRect`. |

---

### HeadPose and HeadAngles

File: [`Assets/Scripts/AR/Vision/HeadPose.cs`](Assets/Scripts/AR/Vision/HeadPose.cs)

`HeadPose` is a static class that estimates head angles from a face `Observation`. It uses geometry only and needs no model. Its accuracy is about ±15°.

**`HeadPose.TryEstimate(Observation o, float frameAspect, out HeadAngles angles)`** returns `bool`. It returns false for a non-face observation or one with missing keypoints. Pass `VisionPipeline.FrameAspect` as `frameAspect`.

**`HeadAngles`** is a readonly struct:

| Field | Type | Unit | Sign convention |
|---|---|---|---|
| `Yaw` | `float` | degrees | Positive when the nose moves toward the image's right. |
| `Pitch` | `float` | degrees | Positive when the head tilts down. |
| `Roll` | `float` | degrees | Positive when the right eye sits lower than the left in the upright image. |

Public constants that can be tuned: `NeutralNoseRatio` (0.45), `YawGain` (2.0), and `PitchDegreesPerUnit` (90). Each is marked as an assumption in the source.

---

### HandGestures

File: [`Assets/Scripts/AR/Vision/HandGestures.cs`](Assets/Scripts/AR/Vision/HandGestures.cs)

A static class of gesture helpers for hand `Observation`s. Non-hand observations always return false or 0.

**Methods**

| Method | Returns | Purpose |
|---|---|---|
| `IsPinching(Observation)` | `bool` | True when the thumb tip and index tip are closer than `PinchRatio` times the hand scale. |
| `Openness(Observation, float frameAspect = 1f)` | `float` | 0 for a fist to 1 for a fully open hand. Measures the four non-thumb fingers. |
| `IsOpen(Observation, float frameAspect = 1f)` | `bool` | True when `Openness` is at least `OpenThreshold` (0.75). |
| `IsClosed(Observation, float frameAspect = 1f)` | `bool` | True when `Openness` is at most `ClosedThreshold` (0.25). |

**Constants**

| Constant | Value | Purpose |
|---|---|---|
| `Wrist` | 0 | Landmark index. |
| `ThumbTip` | 4 | Landmark index. |
| `IndexTip` | 8 | Landmark index. |
| `MiddleMcp` | 9 | Landmark index. |
| `JointCount` | 21 | Expected landmarks per hand. |
| `PinchRatio` | 0.25 | Pinch distance as a fraction of hand scale. |
| `ExtendedRatio` | 1.1 | A finger is extended when its tip is this many times farther from the wrist than its PIP joint. |
| `OpenThreshold` | 0.75 | Openness needed for `IsOpen`. |
| `ClosedThreshold` | 0.25 | Openness allowed for `IsClosed`. |

---

### ObservationKind

File: [`Assets/Scripts/AR/Vision/Internal/ObservationKind.cs`](Assets/Scripts/AR/Vision/Internal/ObservationKind.cs)

An enum with `Face`, `Hand`, and `Object`. It lives in the Internal assembly but uses the `AR.Vision` namespace, so you can reference it directly.

---

### TrackableProfile

File: [`Assets/Scripts/AR/Vision/TrackableProfile.cs`](Assets/Scripts/AR/Vision/TrackableProfile.cs)

An abstract `ScriptableObject` base for all profiles.

| Member | Type | Purpose |
|---|---|---|
| `displayName` | `string` (serialized) | Human-readable name for gameplay. |
| `DisplayName` | `string` | Read-only accessor. |
| `Kind` | `ObservationKind` (abstract) | Implemented by each subclass. |

---

### CharacterProfile

File: [`Assets/Scripts/AR/Vision/CharacterProfile.cs`](Assets/Scripts/AR/Vision/CharacterProfile.cs)

Create with `AR → Vision → Character Profile`. Its `DisplayName` must match the name used at enrollment.

| Member | Type | Purpose |
|---|---|---|
| `greeting` | `string` (serialized, text area) | Optional greeting text. |
| `Greeting` | `string` | Read-only accessor. |
| `Kind` | `ObservationKind` | Always `Face`. |

---

### ItemProfile

File: [`Assets/Scripts/AR/Vision/ItemProfile.cs`](Assets/Scripts/AR/Vision/ItemProfile.cs)

Create with `AR → Vision → Item Profile`. Its `detectorLabel` must match a class name in `ObjectSettings.classNames`.

| Member | Type | Purpose |
|---|---|---|
| `detectorLabel` | `string` (serialized) | Class name the object detector outputs. |
| `DetectorLabel` | `string` | Read-only accessor. |
| `Kind` | `ObservationKind` | Always `Object`. |

---

### TrackableRegistry

File: [`Assets/Scripts/AR/Vision/TrackableRegistry.cs`](Assets/Scripts/AR/Vision/TrackableRegistry.cs)

Create with `AR → Vision → Trackable Registry`. It lists every profile the pipeline can resolve.

| Member | Type | Purpose |
|---|---|---|
| `profiles` | `List<TrackableProfile>` (serialized) | All character and item profiles. |
| `Profiles` | `IReadOnlyList<TrackableProfile>` | Read-only accessor. |
| `FindItemByDetectorLabel(string)` | method, returns `ItemProfile` | The item whose `detectorLabel` matches, or null. |
| `FindCharacterByName(string)` | method, returns `CharacterProfile` | The character whose `DisplayName` matches, or null. |

---

### ModelSettings

File: [`Assets/Scripts/AR/Vision/Internal/Shared/ModelSettings.cs`](Assets/Scripts/AR/Vision/Internal/Shared/ModelSettings.cs)

A `[Serializable]` class that appears under `Models` in the `VisionPipeline` Inspector. It is configured in the Inspector and not used from code.

**Face (`FaceSettings`)**

| Field | Type | Default | Purpose |
|---|---|---|---|
| `detector` | `ModelAsset` | none | SCRFD detector, for example `scrfd_10g.onnx`. |
| `embedder` | `ModelAsset` | none | ArcFace embedder, for example `arcface_w600k_r50.onnx`. |
| `detectionThreshold` | `float` | 0.5 | Minimum detection score. |
| `nmsIouThreshold` | `float` | 0.4 | IoU above which overlapping faces are suppressed. |
| `matchThreshold` | `float` | 0.45 | Minimum cosine similarity to accept a gallery match. |
| `recognizeEveryNFrames` | `int` | 10 | Runs embedding every N frames. |

**Hand (`HandSettings`)**

| Field | Type | Default | Purpose |
|---|---|---|---|
| `palmDetector` | `ModelAsset` | none | Palm detector, 192 × 192 input. |
| `handLandmarker` | `ModelAsset` | none | 21-joint landmark model, 224 × 224 input. |
| `palmScoreThreshold` | `float` | 0.5 | Minimum palm score. |
| `palmNmsIouThreshold` | `float` | 0.3 | IoU for palm NMS. |
| `handPresenceThreshold` | `float` | 0.5 | Minimum landmark presence. |

**Object (`ObjectSettings`)**

| Field | Type | Default | Purpose |
|---|---|---|---|
| `detector` | `ModelAsset` | none | YOLO-style detector, 640 × 640 input. |
| `transposedOutput` | `bool` | false | True for `[1, anchors, 4 + classes]` output. |
| `classNames` | `string[]` | empty | Class names in model index order. Must match exactly. |
| `scoreThreshold` | `float` | 0.4 | Minimum class score. |
| `nmsIouThreshold` | `float` | 0.45 | IoU for per-class NMS. |

---

## Output reference

This table lists every value a gameplay script can read, with its source.

| Output | Source | Available for | Units or range |
|---|---|---|---|
| Track events | `VisionPipeline` events | All kinds | n/a |
| Current observations | `VisionPipeline.Observations` | All kinds | n/a |
| Bounding box | `Observation.NormalizedRect` | All kinds | 0..1, top-left origin |
| Center | `Observation.Center` | All kinds | 0..1, top-left origin |
| Recognized name | `Observation.Label`, `IsIdentified` | Face, Object | string, bool |
| Match score | `Observation.MatchScore` | Face | cosine similarity |
| Detector score | `Observation.Confidence` | All kinds | 0..1 |
| Profile asset | `Observation.Profile`, `IdentityChanged` | Face, Object | `TrackableProfile` |
| Track age | `Observation.FramesTracked` | All kinds | frames |
| Landmarks | `Observation.Keypoints` | Face (5), Hand (21) | 0..1, top-left origin |
| Pinch | `HandGestures.IsPinching` | Hand | bool |
| Hand openness | `HandGestures.Openness` | Hand | 0..1 |
| Hand open or closed | `HandGestures.IsOpen`, `IsClosed` | Hand | bool |
| Head yaw, pitch, roll | `HeadPose.TryEstimate` → `HeadAngles` | Face | degrees, approximate |
| Frame aspect | `VisionPipeline.FrameAspect` | All kinds | width ÷ height |

Hand depth (z) is computed inside the model and is not exposed in `Observation`.

---

## Coordinates

- All `Observation` rectangles and keypoints are normalized to 0..1 with the origin at the top left.
- Some geometry depends on aspect ratio. Pass `VisionPipeline.FrameAspect` to `HeadPose.TryEstimate`, `HandGestures.Openness`, `IsOpen`, and `IsClosed`.
- The overlay in `OnGUI` maps these coordinates onto the `RawImage`.

---

## Examples

Both examples live in [`Assets/Scripts/AR/Vision/Examples/`](Assets/Scripts/AR/Vision/Examples/).

**[`BarberStation.cs`](Assets/Scripts/AR/Vision/Examples/BarberStation.cs)** tracks the client's head and exposes a `CutState`. The state is `SafeToCut` only after the head has been within limits and still for `settleSeconds`. Cutting tools can be picked up only in that state.

**[`PlayerGreeter.cs`](Assets/Scripts/AR/Vision/Examples/PlayerGreeter.cs)** greets each recognized face once per cooldown window.

---

## Limitations

- Head angles are geometric estimates from five landmarks, with about ±15° error. They become unreliable beyond roughly 60° of yaw. A dedicated head-pose model would be more accurate.
- Hand openness ignores the thumb, so a fist with the thumb extended reads as closed.
- The palm and hand decoders are still tied to the model's output layout. Check them in Netron if hands fail to appear.
- Gallery faces are held in memory only and are cleared when the scene stops.

---

## Notes

- Serialized fields are private and exposed through `[SerializeField]`. Read them through the public properties, events, and methods listed above.
- `ModelSettings` is read when the pipeline starts, so restart Play mode after changing model assets.
- `ObservationKind`, `Detection`, `VisionFrame`, `TrackedObservation`, and the `Internal/` classes are implementation details. Gameplay code should depend only on the classes listed in this README.