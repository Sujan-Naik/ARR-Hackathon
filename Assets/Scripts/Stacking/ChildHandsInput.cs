using System;
using System.Collections.Generic;
using UnityEngine;
using AR.Vision;

namespace Stacking
{
    public class ChildHandsInput : MonoBehaviour
    {
        [SerializeField] private ParticipantHandAssociation association;
        [SerializeField, Range(0.01f, 0.3f)] private float raiseDistance = 0.09f;
        [SerializeField] private bool requiresOpenHand = true;
        [SerializeField, Min(0.1f)] private float raiseCooldown = 0.6f;

        public bool RaisedThisFrame { get; private set; }
        public bool HasHand { get; private set; }
        public event Action Placed;

        private readonly List<Observation> scratch = new List<Observation>();
        private readonly Dictionary<int, Vector2> previous = new Dictionary<int, Vector2>();
        private readonly HashSet<int> active = new HashSet<int>();
        private float lastRaiseTime = -100f;

        private void Awake()
        {
            if (association == null) association = FindObjectOfType<ParticipantHandAssociation>();
        }

        private void Update()
        {
            RaisedThisFrame = false;
            if (association == null || !association.RolesAssigned)
            { HasHand = false; previous.Clear(); return; }

            association.GetHandsForRole(ParticipantHandAssociation.ParticipantRole.Child, scratch);
            HasHand = scratch.Count > 0;
            if (!HasHand) { previous.Clear(); return; }

            active.Clear();
            bool canFire = Time.time - lastRaiseTime >= raiseCooldown;

            for (int i = 0; i < scratch.Count; i++)
            {
                Observation hand = scratch[i];
                int id = hand.TrackId;
                active.Add(id);

                Vector2 pos = hand.Center;
                if (previous.TryGetValue(id, out Vector2 prev))
                {
                    float upward = prev.y - pos.y;
                    bool open = !requiresOpenHand || HandGestures.IsOpen(hand);
                    if (canFire && upward >= raiseDistance && open)
                    {
                        lastRaiseTime = Time.time;
                        RaisedThisFrame = true;
                        previous[id] = pos;
                        Placed?.Invoke();
                        return;
                    }
                }
                previous[id] = pos;
            }

            List<int> stale = null;
            foreach (int id in previous.Keys)
                if (!active.Contains(id)) (stale ??= new List<int>()).Add(id);
            if (stale != null)
                for (int i = 0; i < stale.Count; i++) previous.Remove(stale[i]);
        }
    }
}
