// Role: face-only example. Greets recognized people once per cooldown window.
// Enable only the face module on the VisionPipeline for this scene.
using System.Collections.Generic;
using UnityEngine;

namespace AR.Vision
{
    public class PlayerGreeter : MonoBehaviour
    {
        [SerializeField] private VisionPipeline vision;
        [SerializeField] private float cooldownSeconds = 30f;

        private readonly Dictionary<string, float> lastGreeted = new Dictionary<string, float>();

        void OnEnable()
        {
            vision.IdentityChanged += OnIdentityChanged;
        }

        void OnDisable()
        {
            vision.IdentityChanged -= OnIdentityChanged;
        }

        private void OnIdentityChanged(Observation o, TrackableProfile profile)
        {
            if (o.Kind != ObservationKind.Face || !o.IsIdentified) return;

            CharacterProfile character = profile as CharacterProfile;
            string name = character != null ? character.DisplayName : o.Label;

            if (lastGreeted.TryGetValue(name, out float last) && Time.time - last < cooldownSeconds) return;
            lastGreeted[name] = Time.time;

            string text = character != null && !string.IsNullOrEmpty(character.Greeting)
                ? character.Greeting
                : $"Welcome back, {name}";

            Debug.Log(text);
        }
    }
}
