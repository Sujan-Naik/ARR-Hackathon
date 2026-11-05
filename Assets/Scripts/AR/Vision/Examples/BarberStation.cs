// Role: example workstation. Tracks tools on the tray, reports identity changes, and logs pinch pick-ups.
using System.Collections.Generic;
using UnityEngine;

namespace AR.Vision
{
    public class BarberStation : MonoBehaviour
    {
        [SerializeField] private VisionPipeline vision;
        [SerializeField] private float grabRadius = 0.08f;   // normalized frame units

        private readonly Dictionary<int, Observation> tools = new Dictionary<int, Observation>();
        private readonly Dictionary<int, bool> pinching = new Dictionary<int, bool>();

        void OnEnable()
        {
            vision.ObservationAppeared += OnAppeared;
            vision.ObservationUpdated += OnUpdated;
            vision.ObservationLost += OnLost;
            vision.IdentityChanged += OnIdentityChanged;
        }

        void OnDisable()
        {
            vision.ObservationAppeared -= OnAppeared;
            vision.ObservationUpdated -= OnUpdated;
            vision.ObservationLost -= OnLost;
            vision.IdentityChanged -= OnIdentityChanged;
        }

        private void OnAppeared(Observation o)
        {
            if (o.Kind == ObservationKind.Object) tools[o.TrackId] = o;
        }

        private void OnUpdated(Observation o)
        {
            if (o.Kind == ObservationKind.Object)
            {
                tools[o.TrackId] = o;
                return;
            }

            if (o.Kind != ObservationKind.Hand) return;

            bool pinchNow = HandGestures.IsPinching(o);
            pinching.TryGetValue(o.TrackId, out bool wasPinching);
            pinching[o.TrackId] = pinchNow;

            if (pinchNow && !wasPinching && TryFindToolNear(o.Center, out Observation tool))
            {
                string name = tool.Profile != null ? tool.Profile.DisplayName : tool.Label;
                Debug.Log($"Barber station: picked up {name}");
            }
        }

        private void OnLost(ObservationKind kind, int trackId)
        {
            tools.Remove(trackId);
            pinching.Remove(trackId);
        }

        private void OnIdentityChanged(Observation o, TrackableProfile profile)
        {
            if (o.Kind == ObservationKind.Object && profile is ItemProfile item)
                Debug.Log($"Barber station: {item.DisplayName} is on the tray");
        }

        private bool TryFindToolNear(Vector2 point, out Observation tool)
        {
            float best = grabRadius;
            bool found = false;
            tool = default;

            foreach (var pair in tools)
            {
                float d = Vector2.Distance(pair.Value.Center, point);
                if (d < best)
                {
                    best = d;
                    tool = pair.Value;
                    found = true;
                }
            }
            return found;
        }
    }
}
