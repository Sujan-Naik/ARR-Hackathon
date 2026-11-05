using System;
using System.Collections.Generic;
using UnityEngine;
using AR.Vision;

namespace Stacking
{
    /// <summary>
    /// Adapter: barber's hands -> MonsterHandsSignal.
    /// Left/right are separated by centre.x. When only one hand is visible
    /// it fills the left slot and the right slot is unbound.
    /// </summary>
    public class BarberHandsInput : MonoBehaviour
    {
        [SerializeField] private ParticipantHandAssociation association;

        [Header("Tuning")]
        [SerializeField] private bool requireFace = true;

        public MonsterHandsSignal Current { get; private set; } = MonsterHandsSignal.None;
        public event Action<MonsterHandsSignal> Changed;

        private readonly List<Observation> scratch = new List<Observation>();
        private readonly MonsterHand left = default;
        private readonly MonsterHand right = default;

        private void Awake()
        {
            if (association == null) association = FindObjectOfType<ParticipantHandAssociation>();
        }

        private void Update()
        {
            MonsterHandsSignal next = Compute();
            if (!SignalsEqual(next, Current))
            {
                Current = next;
                Changed?.Invoke(next);
            }
        }

        private MonsterHandsSignal Compute()
        {
            if (association == null || !association.RolesAssigned)
                return MonsterHandsSignal.None;

            if (requireFace && !association.TryGetFace(
                    ParticipantHandAssociation.ParticipantRole.Barber, out _))
                return MonsterHandsSignal.None;

            association.GetHandsForRole(
                ParticipantHandAssociation.ParticipantRole.Barber, scratch);

            if (scratch.Count == 0) return MonsterHandsSignal.None;

            Observation a = scratch[0];
            Observation b = scratch.Count > 1 ? scratch[1] : default;

            Observation leftObs = a;
            Observation rightObs = b;
            bool hasRight = scratch.Count > 1;

            if (hasRight && leftObs.Center.x > rightObs.Center.x)
            {
                Observation t = leftObs;
                leftObs = rightObs;
                rightObs = t;
            }

            MonsterHand leftHand = ToHand(leftObs);
            MonsterHand rightHand = hasRight ? ToHand(rightObs) : default;

            return new MonsterHandsSignal(leftHand, rightHand, true);
        }

        private static MonsterHand ToHand(Observation o)
        {
            return new MonsterHand(
                o.TrackId,
                o.Center,
                HandGestures.IsOpen(o),
                HandGestures.IsClosed(o),
                HandGestures.IsPinching(o));
        }

        private static bool SignalsEqual(MonsterHandsSignal a, MonsterHandsSignal b)
        {
            if (a.HasAny != b.HasAny) return false;
            if (!a.HasAny) return true;
            return a.Left.TrackId == b.Left.TrackId
                && a.Right.TrackId == b.Right.TrackId
                && Vector2.Distance(a.Left.Center, b.Left.Center) < 0.001f
                && Vector2.Distance(a.Right.Center, b.Right.Center) < 0.001f
                && a.Left.IsClosed == b.Left.IsClosed
                && a.Right.IsClosed == b.Right.IsClosed;
        }
    }
}