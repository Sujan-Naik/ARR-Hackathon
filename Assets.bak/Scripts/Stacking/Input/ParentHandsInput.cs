using System;
using System.Collections.Generic;
using UnityEngine;
using AR.Vision;

namespace Stacking
{
    /// <summary>
    /// Adapter: parent's hands -> CraneSignal.
    /// A raise is a gesture edge (upward movement past a threshold while open).
    /// SteeredX is the averaged centre.x of visible parent hands, 0..1.
    /// </summary>
    public class ParentHandsInput : MonoBehaviour
    {
        [SerializeField] private ParticipantHandAssociation association;

        [Header("Raise gesture")]
        [SerializeField, Range(0.01f, 0.3f)] private float raiseDistance = 0.09f;
        [SerializeField] private bool requiresOpenHand = true;
        [SerializeField, Min(0.1f)] private float raiseCooldown = 0.8f;

        public CraneSignal Current { get; private set; } = CraneSignal.None;
        public event Action<CraneSignal> Changed;

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
            CraneSignal next = Compute();
            Current = next;
            Changed?.Invoke(next);
        }

        private CraneSignal Compute()
        {
            if (association == null || !association.RolesAssigned)
            {
                previous.Clear();
                return CraneSignal.None;
            }

            association.GetHandsForRole(
                ParticipantHandAssociation.ParticipantRole.Parent, scratch);

            if (scratch.Count == 0)
            {
                previous.Clear();
                return CraneSignal.None;
            }

            active.Clear();
            float sumX = 0f;
            bool raise = false;

            for (int i = 0; i < scratch.Count; i++)
            {
                Observation hand = scratch[i];
                int id = hand.TrackId;
                active.Add(id);

                Vector2 pos = hand.Center;
                sumX += pos.x;

                if (previous.TryGetValue(id, out Vector2 prev))
                {
                    float upward = prev.y - pos.y;
                    bool open = HandGestures.IsOpen(hand);
                    if (upward >= raiseDistance
                        && (!requiresOpenHand || open)
                        && Time.time - lastRaiseTime >= raiseCooldown)
                    {
                        raise = true;
                    }
                }

                previous[id] = pos;
            }

            // Forget hands we can no longer see.
            List<int> stale = null;
            foreach (int id in previous.Keys)
            {
                if (active.Contains(id)) continue;
                stale ??= new List<int>();
                stale.Add(id);
            }
            if (stale != null)
                for (int i = 0; i < stale.Count; i++) previous.Remove(stale[i]);

            if (raise) lastRaiseTime = Time.time;

            float steeredX = Mathf.Clamp01(sumX / scratch.Count);
            return new CraneSignal(steeredX, raise, true);
        }
    }
}