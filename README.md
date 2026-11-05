# AR Vision

A webcam-based vision layer for Unity. It detects and tracks faces, hands, and objects, then exposes them to gameplay through events and a read-only [`Observation`](#observation) struct. Game code never handles tensors.

## Usage
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

## Contents

- [VisionPipeline](#visionpipeline)
- [ModelSettings](#modelsettings)
- [Observation](#observation)
- [ObservationKind](#observationkind)
- [TrackableProfile](#trackableprofile)
- [CharacterProfile](#characterprofile)
- [ItemProfile](#itemprofile)
- [TrackableRegistry](#trackableregistry)
- [HandGestures](#handgestures)
- [Examples](#examples)

---

## VisionPipeline

File: [`Assets/Scripts/AR/Vision/VisionPipeline.cs`](Assets/Scripts/AR/Vision/VisionPipeline.cs)

A `MonoBehaviour` that requires a `RawImage` on the same GameObject. It owns the webcam, the orientation shader, the frame readback, the module slots, and the public events.

### Inspector fields

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

### Public API

| Member | Kind | Purpose |
|---|---|---|
| `ObservationAppeared` | event `Action<Observation>` | Fires when a new track is created. |
| `ObservationUpdated` | event `Action<Observation>` | Fires every frame for each active track. |
| `ObservationLost` | event `Action<ObservationKind, int>` | Fires when a track is removed. Passes the track ID. |
| `IdentityChanged` | event `Action<Observation, TrackableProfile>` | Fires when a track's label, identity, or profile changes. |
| `Observations` | property `IReadOnlyList<Observation>` | Observations from the most recent completed frame. |
| `IsReady()` | method `bool` | Returns true when the webcam is playing and the frame buffers exist. |
| `EnrollLargestFace(string)` | method | Stores the largest visible face in the gallery under the given name. |

---

## ModelSettings

File: [`Assets/Scripts/AR/Vision/Internal/Shared/ModelSettings.cs`](Assets/Scripts/AR/Vision/Internal/Shared/ModelSettings.cs)

A `[Serializable]` class that appears under `Models` in the `VisionPipeline` Inspector. It contains three nested groups.

### Face (`FaceSettings`)

| Field | Type | Default | Purpose |
|---|---|---|---|
| `detector` | `ModelAsset` | none | SCRFD detector, for example `scrfd_10g.onnx`. |
| `embedder` | `ModelAsset` | none | ArcFace embedder, for example `arcface_w600k_r50.onnx`. |
| `detectionThreshold` | `float` | 0.5 | Minimum score for a detection. |
| `nmsIouThreshold` | `float` | 0.4 | IoU above which overlapping detections are suppressed. |
| `matchThreshold` | `float` | 0.45 | Minimum cosine similarity to accept a gallery match. Raise it to reduce false matches. |
| `recognizeEveryNFrames` | `int` | 10 | Runs embedding every N frames. Lower it for faster recognition. |

### Hand (`HandSettings`)

| Field | Type | Default | Purpose |
|---|---|---|---|
| `palmDetector` | `ModelAsset` | none | Palm detector, 256 × 256 input. |
| `handLandmarker` | `ModelAsset` | none | 21-joint landmark model, 224 × 224 input. |
| `palmScoreThreshold` | `float` | 0.5 | Minimum palm score. |
| `palmNmsIouThreshold` | `float` | 0.3 | IoU for palm NMS. |
| `handPresenceThreshold` | `float` | 0.5 | Minimum landmark presence score. |

### Object (`ObjectSettings`)

| Field | Type | Default | Purpose |
|---|---|---|---|
| `detector` | `ModelAsset` | none | YOLO-style detector, 640 × 640 input. |
| `transposedOutput` | `bool` | false | Set to true for `[1, anchors, 4 + classes]` output. |
| `classNames` | `string[]` | empty | Class names in the model's training order. Must match exactly. |
| `scoreThreshold` | `float` | 0.4 | Minimum class score. |
| `nmsIouThreshold` | `float` | 0.45 | IoU for per-class NMS. |

---

## Observation

File: [`Assets/Scripts/AR/Vision/Observation.cs`](Assets/Scripts/AR/Vision/Observation.cs)

A `readonly struct` that game code receives in events. All positions are normalized 0..1 with the origin at the top left.

| Property | Type | Purpose |
|---|---|---|
| `TrackId` | `int` | Stable ID for the track while it remains active. |
| `Kind` | `ObservationKind` | Face, Hand, or Object. |
| `Label` | `string` | Recognized name, class name, or the kind name when unidentified. |
| `Profile` | `TrackableProfile` | Resolved profile asset, or null. |
| `Confidence` | `float` | Detector score. |
| `MatchScore` | `float` | Gallery similarity for faces. |
| `NormalizedRect` | `Rect` | Bounding box in normalized frame coordinates. |
| `Keypoints` | `Vector2[]` | Landmarks in normalized frame coordinates. |
| `FramesTracked` | `int` | Number of frames the track has been active. |
| `IsIdentified` | `bool` | True when the label has been accepted as an identity. |
| `Center` | `Vector2` | Center of `NormalizedRect`. |

---

## ObservationKind

File: [`Assets/Scripts/AR/Vision/Internal/ObservationKind.cs`](Assets/Scripts/AR/Vision/Internal/ObservationKind.cs)

An enum with three values: `Face`, `Hand`, and `Object`. It lives in the Internal assembly but keeps the `AR.Vision` namespace, so game code uses it directly.

---

## TrackableProfile

File: [`Assets/Scripts/AR/Vision/TrackableProfile.cs`](Assets/Scripts/AR/Vision/TrackableProfile.cs)

An abstract `ScriptableObject` base for all profiles.

| Member | Type | Purpose |
|---|---|---|
| `displayName` | `string` (serialized) | Human-readable name shown in gameplay. |
| `DisplayName` | `string` | Read-only accessor for `displayName`. |
| `Kind` | `ObservationKind` (abstract) | Implemented by each subclass. |

---

## CharacterProfile

File: [`Assets/Scripts/AR/Vision/CharacterProfile.cs`](Assets/Scripts/AR/Vision/CharacterProfile.cs)

Create with `AR → Vision → Character Profile`. Its `DisplayName` must match the name used at enrollment.

| Member | Type | Purpose |
|---|---|---|
| `greeting` | `string` (serialized, text area) | Optional greeting text. |
| `Greeting` | `string` | Read-only accessor. |
| `Kind` | `ObservationKind` | Always `Face`. |

---

## ItemProfile

File: [`Assets/Scripts/AR/Vision/ItemProfile.cs`](Assets/Scripts/AR/Vision/ItemProfile.cs)

Create with `AR → Vision → Item Profile`. Its `detectorLabel` must match the class name in `ObjectSettings.classNames`.

| Member | Type | Purpose |
|---|---|---|
| `detectorLabel` | `string` (serialized) | Class name the object detector outputs. |
| `DetectorLabel` | `string` | Read-only accessor. |
| `Kind` | `ObservationKind` | Always `Object`. |

---

## TrackableRegistry

File: [`Assets/Scripts/AR/Vision/TrackableRegistry.cs`](Assets/Scripts/AR/Vision/TrackableRegistry.cs)

Create with `AR → Vision → Trackable Registry`. It lists every profile the pipeline can resolve.

| Member | Type | Purpose |
|---|---|---|
| `profiles` | `List<TrackableProfile>` (serialized) | All character and item profiles in the game. |
| `Profiles` | `IReadOnlyList<TrackableProfile>` | Read-only accessor. |
| `FindItemByDetectorLabel(string)` | method, returns `ItemProfile` | Returns the item whose `detectorLabel` matches, or null. |
| `FindCharacterByName(string)` | method, returns `CharacterProfile` | Returns the character whose `DisplayName` matches, or null. |

---

## HandGestures

File: [`Assets/Scripts/AR/Vision/HandGestures.cs`](Assets/Scripts/AR/Vision/HandGestures.cs)

A static class with no Inspector fields.

| Member | Type | Value | Purpose |
|---|---|---|---|
| `Wrist` | `const int` | 0 | Landmark index. |
| `ThumbTip` | `const int` | 4 | Landmark index. |
| `IndexTip` | `const int` | 8 | Landmark index. |
| `MiddleMcp` | `const int` | 9 | Landmark index. |
| `JointCount` | `const int` | 21 | Expected landmarks per hand. |
| `PinchRatio` | `const float` | 0.25 | Thumb-to-index distance, as a fraction of hand scale, that counts as a pinch. |
| `IsPinching(Observation)` | method, returns `bool` | n/a | Returns true for a hand whose thumb and index tips are close together. |

---

## Examples

Both examples are in [`Assets/Scripts/AR/Vision/Examples/`](Assets/Scripts/AR/Vision/Examples/).

### BarberStation

File: [`BarberStation.cs`](Assets/Scripts/AR/Vision/Examples/BarberStation.cs)

| Field | Type | Default | Purpose |
|---|---|---|---|
| `vision` | `VisionPipeline` (serialized) | none | Pipeline to subscribe to. Must be assigned. |
| `grabRadius` | `float` (serialized) | 0.08 | Maximum normalized distance between a pinch and a tool to count as a pick-up. |

### PlayerGreeter

File: [`PlayerGreeter.cs`](Assets/Scripts/AR/Vision/Examples/PlayerGreeter.cs)

| Field | Type | Default | Purpose |
|---|---|---|---|
| `vision` | `VisionPipeline` (serialized) | none | Pipeline to subscribe to. Must be assigned. |
| `cooldownSeconds` | `float` (serialized) | 30 | Minimum time before the same person is greeted again. |

---

## Notes

- Serialized fields are private and exposed through `[SerializeField]`. Code reads them through their public properties, or the events and methods listed above.
- `ModelSettings` is a plain class, so changes take effect only when the pipeline starts. Restart Play mode after changing model assets.
- `ObservationKind` and `Detection` are internal implementation details. Game code should not depend on them beyond the enum values.