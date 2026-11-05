using System;
using System.Collections.Generic;
using UnityEngine;

namespace AR.Vision
{
    /// <summary>
    /// Assigns tracked faces to Child / Barber / Parent and associates each
    /// tracked hand with the nearest face.
    ///
    /// Role assignment has two paths:
    ///   • Automatic: when requiredParticipants faces are visible, they are
    ///     sorted left-to-right and mapped to Child, Barber, Parent.
    ///   • Manual: any caller can call AssignRole(role, faceTrackId). Once a
    ///     role is manual, auto-assignment will not overwrite it. Call
    ///     ClearManualRoles() to drop back to auto behaviour.
    ///
    /// This means the game still works with 1, 2 or 3 people: the demo
    /// operator binds the roles explicitly from RoleAssignmentPanel.
    /// </summary>
    public class ParticipantHandAssociation : MonoBehaviour
    {
        public enum ParticipantRole
        {
            Child,
            Barber,
            Parent
        }

        [Header("Vision")]
        [SerializeField] private VisionPipeline vision;

        [Header("Participant assignment")]
        [Tooltip("Number of faces required before AUTO assigning the three roles. Manual assignment works with any number.")]
        [SerializeField, Range(1, 3)] private int requiredParticipants = 3;

        [Tooltip("Reassign roles when a previously assigned face disappears.")]
        [SerializeField] private bool reassignWhenFaceLost = true;

        [Header("Hand association")]
        [Tooltip("Maximum normalized distance between a hand and face to allow association.")]
        [SerializeField, Range(0.05f, 1f)] private float maximumAssociationDistance = 0.55f;

        [Tooltip("A new face must be this much closer before a hand can switch owners.")]
        [SerializeField, Range(0f, 0.5f)] private float reassignmentMargin = 0.08f;

        [Tooltip("Number of consecutive hand observations required before changing owners.")]
        [SerializeField, Min(1)] private int reassignmentFrames = 5;

        [Header("Debug")]
        [SerializeField] private bool logAssignments;

        public bool RolesAssigned { get; private set; }

        public event Action RolesAssignedEvent;
        public event Action<int, ParticipantRole> HandAssociated;
        public event Action<int> HandUnassociated;

        private readonly Dictionary<int, Observation> faces =
            new Dictionary<int, Observation>();

        private readonly Dictionary<int, Observation> hands =
            new Dictionary<int, Observation>();

        private readonly Dictionary<int, ParticipantRole> faceRoles =
            new Dictionary<int, ParticipantRole>();

        private readonly Dictionary<int, int> handOwners =
            new Dictionary<int, int>();

        private readonly Dictionary<int, int> pendingOwners =
            new Dictionary<int, int>();

        private readonly Dictionary<int, int> pendingCounts =
            new Dictionary<int, int>();

        // Roles that were bound by AssignRole instead of by auto-mapping.
        private readonly HashSet<ParticipantRole> manualRoles =
            new HashSet<ParticipantRole>();

        private bool subscribed;

        // ---------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
            ClearHands();
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        private void Update()
        {
            Subscribe();
        }

        private void Subscribe()
        {
            if (subscribed || vision == null) return;

            vision.ObservationAppeared += OnObservationAppeared;
            vision.ObservationUpdated += OnObservationUpdated;
            vision.ObservationLost += OnObservationLost;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed || vision == null) return;

            vision.ObservationAppeared -= OnObservationAppeared;
            vision.ObservationUpdated -= OnObservationUpdated;
            vision.ObservationLost -= OnObservationLost;
            subscribed = false;
        }

        // ---------------------------------------------------------------
        // Observation intake
        // ---------------------------------------------------------------

        private void OnObservationAppeared(Observation observation)
        {
            StoreObservation(observation);
        }

        private void OnObservationUpdated(Observation observation)
        {
            StoreObservation(observation);

            if (observation.Kind == ObservationKind.Hand)
                UpdateHandAssociation(observation.TrackId);
        }

        private void StoreObservation(Observation observation)
        {
            if (observation.Kind == ObservationKind.Face)
            {
                faces[observation.TrackId] = observation;
                if (!RolesAssigned) TryAutoAssignRoles();
            }
            else if (observation.Kind == ObservationKind.Hand)
            {
                hands[observation.TrackId] = observation;
            }
        }

        private void OnObservationLost(ObservationKind kind, int trackId)
        {
            if (kind == ObservationKind.Hand)
            {
                hands.Remove(trackId);
                handOwners.Remove(trackId);
                pendingOwners.Remove(trackId);
                pendingCounts.Remove(trackId);
                HandUnassociated?.Invoke(trackId);
                return;
            }

            if (kind != ObservationKind.Face) return;

            faces.Remove(trackId);

            if (!faceRoles.TryGetValue(trackId, out ParticipantRole role)) return;

            faceRoles.Remove(trackId);
            bool wasManual = manualRoles.Contains(role);

            if (!reassignWhenFaceLost) return;

            if (wasManual)
            {
                // Manual bindings stay in the manual set: the operator can
                // re-bind without clearing first.
                if (logAssignments)
                    Debug.Log("Manual role " + role + " face lost. Waiting for rebind.", this);
                return;
            }

            RolesAssigned = faceRoles.Count > 0;
            if (RolesAssigned) return;

            faceRoles.Clear();
            ClearHands();

            if (logAssignments)
                Debug.Log("Participant face lost. Waiting to assign roles again.", this);
        }

        // ---------------------------------------------------------------
        // Auto assignment
        // ---------------------------------------------------------------

        private void TryAutoAssignRoles()
        {
            if (RolesAssigned) return;
            if (faces.Count < requiredParticipants) return;
            if (manualRoles.Count >= 3) return;   // all roles manual: never auto

            List<Observation> sorted = new List<Observation>(faces.Values);
            sorted.Sort((a, b) => a.Center.x.CompareTo(b.Center.x));

            // Fill any role that isn't already manually bound, in order.
            ParticipantRole[] order =
            {
                ParticipantRole.Child,
                ParticipantRole.Barber,
                ParticipantRole.Parent,
            };

            int faceIndex = 0;
            foreach (ParticipantRole role in order)
            {
                if (manualRoles.Contains(role)) continue;
                if (faceIndex >= sorted.Count) break;

                // Skip faces already bound to another role.
                while (faceIndex < sorted.Count &&
                       faceRoles.ContainsKey(sorted[faceIndex].TrackId))
                {
                    faceIndex++;
                }
                if (faceIndex >= sorted.Count) break;

                faceRoles[sorted[faceIndex].TrackId] = role;
                faceIndex++;
            }

            if (faceRoles.Count == 0) return;

            RolesAssigned = true;

            if (logAssignments)
                Debug.Log("Participant roles auto-assigned (manual: " + manualRoles.Count + ").", this);

            RolesAssignedEvent?.Invoke();
        }

        // ---------------------------------------------------------------
        // Manual assignment (public API)
        // ---------------------------------------------------------------

        /// <summary>
        /// Forces a face track into a role. Binds the role as manual; auto
        /// assignment will not overwrite it. Pass -1 to clear the role.
        /// </summary>
        public void AssignRole(ParticipantRole role, int faceTrackId)
        {
            // Clear this face from any other role it held.
            List<int> toClear = null;
            foreach (KeyValuePair<int, ParticipantRole> pair in faceRoles)
            {
                if (pair.Value == role) continue;
                if (pair.Key == faceTrackId)
                {
                    toClear ??= new List<int>();
                    toClear.Add(pair.Key);
                }
            }
            if (toClear != null)
                for (int i = 0; i < toClear.Count; i++) faceRoles.Remove(toClear[i]);

            // Clear anyone currently in this role.
            int previousId = -1;
            foreach (KeyValuePair<int, ParticipantRole> pair in faceRoles)
            {
                if (pair.Value == role) { previousId = pair.Key; break; }
            }
            if (previousId >= 0 && previousId != faceTrackId)
                faceRoles.Remove(previousId);

            if (faceTrackId < 0)
            {
                if (previousId >= 0) faceRoles.Remove(previousId);
                manualRoles.Remove(role);
            }
            else
            {
                faceRoles[faceTrackId] = role;
                manualRoles.Add(role);
            }

            RolesAssigned = faceRoles.Count > 0;

            if (logAssignments)
                Debug.Log("Manual role " + role + " -> face " + faceTrackId
                    + " (assigned: " + RolesAssigned + ")", this);

            RolesAssignedEvent?.Invoke();
        }

        /// <summary>
        /// Binds the nearest not-yet-bound face to the given role. Great for a
        /// keyboard panel: press "1" to bind Child to whichever face is nearest
        /// to the screen centre. Returns the face id bound, or -1.
        /// </summary>
        public int AssignNearestUnassigned(ParticipantRole role)
        {
            int bestId = -1;
            float bestDistance = float.MaxValue;
            Vector2 reference = new Vector2(0.5f, 0.5f);

            foreach (KeyValuePair<int, Observation> pair in faces)
            {
                if (faceRoles.ContainsKey(pair.Key)) continue;
                float d = Vector2.Distance(pair.Value.Center, reference);
                if (d < bestDistance) { bestDistance = d; bestId = pair.Key; }
            }

            if (bestId < 0) return -1;

            AssignRole(role, bestId);
            return bestId;
        }

        /// <summary>Removes every manual binding, allowing auto-assignment again.</summary>
        public void ClearManualRoles()
        {
            manualRoles.Clear();

            if (logAssignments)
                Debug.Log("Manual roles cleared; auto-assignment re-enabled.", this);
        }

        /// <summary>Returns true if the given role was bound by a human.</summary>
        public bool IsRoleManual(ParticipantRole role) => manualRoles.Contains(role);

        /// <summary>Snapshot of the current face id for a role, or -1.</summary>
        public int GetFaceIdForRole(ParticipantRole role)
        {
            foreach (KeyValuePair<int, ParticipantRole> pair in faceRoles)
                if (pair.Value == role) return pair.Key;
            return -1;
        }

        /// <summary>Snapshot of every face the association is currently tracking.</summary>
        public IReadOnlyList<Observation> GetTrackedFaces()
        {
            List<Observation> list = new List<Observation>(faces.Values);
            return list;
        }

        // ---------------------------------------------------------------
        // Hand association (unchanged behaviour)
        // ---------------------------------------------------------------

        private void UpdateHandAssociation(int handTrackId)
        {
            if (!RolesAssigned || !hands.ContainsKey(handTrackId)) return;

            Observation hand = hands[handTrackId];

            int bestFaceId = -1;
            float bestDistance = float.MaxValue;

            foreach (KeyValuePair<int, ParticipantRole> rolePair in faceRoles)
            {
                if (!faces.TryGetValue(rolePair.Key, out Observation face)) continue;

                float distance = Vector2.Distance(hand.Center, face.Center);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestFaceId = face.TrackId;
                }
            }

            if (bestFaceId < 0 || bestDistance > maximumAssociationDistance)
            {
                if (handOwners.Remove(handTrackId))
                    HandUnassociated?.Invoke(handTrackId);

                pendingOwners.Remove(handTrackId);
                pendingCounts.Remove(handTrackId);
                return;
            }

            if (!handOwners.ContainsKey(handTrackId))
            {
                SetHandOwner(handTrackId, bestFaceId);
                return;
            }

            int currentOwner = handOwners[handTrackId];

            if (bestFaceId == currentOwner)
            {
                pendingOwners.Remove(handTrackId);
                pendingCounts.Remove(handTrackId);
                return;
            }

            if (!faces.TryGetValue(currentOwner, out Observation currentFace))
            {
                SetHandOwner(handTrackId, bestFaceId);
                return;
            }

            float currentDistance = Vector2.Distance(hand.Center, currentFace.Center);

            if (bestDistance + reassignmentMargin >= currentDistance)
            {
                pendingOwners.Remove(handTrackId);
                pendingCounts.Remove(handTrackId);
                return;
            }

            if (pendingOwners.TryGetValue(handTrackId, out int pendingOwner) &&
                pendingOwner == bestFaceId)
            {
                pendingCounts[handTrackId]++;
            }
            else
            {
                pendingOwners[handTrackId] = bestFaceId;
                pendingCounts[handTrackId] = 1;
            }

            if (pendingCounts[handTrackId] >= reassignmentFrames)
            {
                SetHandOwner(handTrackId, bestFaceId);
                pendingOwners.Remove(handTrackId);
                pendingCounts.Remove(handTrackId);
            }
        }

        private void SetHandOwner(int handTrackId, int faceTrackId)
        {
            handOwners[handTrackId] = faceTrackId;

            if (!faceRoles.TryGetValue(faceTrackId, out ParticipantRole role)) return;

            if (logAssignments)
                Debug.Log("Hand " + handTrackId + " assigned to " + role, this);

            HandAssociated?.Invoke(handTrackId, role);
        }

        private void ClearHands()
        {
            hands.Clear();
            handOwners.Clear();
            pendingOwners.Clear();
            pendingCounts.Clear();
        }

        // ---------------------------------------------------------------
        // Read APIs (unchanged signatures)
        // ---------------------------------------------------------------

        public bool TryGetRoleForFace(int faceTrackId, out ParticipantRole role)
            => faceRoles.TryGetValue(faceTrackId, out role);

        public bool TryGetFaceForHand(int handTrackId, out int faceTrackId)
            => handOwners.TryGetValue(handTrackId, out faceTrackId);

        public bool TryGetRoleForHand(int handTrackId, out ParticipantRole role)
        {
            role = ParticipantRole.Child;
            if (!handOwners.TryGetValue(handTrackId, out int faceId)) return false;
            return faceRoles.TryGetValue(faceId, out role);
        }

        public bool TryGetHand(int handTrackId, out Observation hand)
            => hands.TryGetValue(handTrackId, out hand);

        public bool TryGetFace(ParticipantRole role, out Observation face)
        {
            foreach (KeyValuePair<int, ParticipantRole> pair in faceRoles)
            {
                if (pair.Value == role && faces.TryGetValue(pair.Key, out face))
                    return true;
            }
            face = default;
            return false;
        }

        public void GetHandsForRole(ParticipantRole role, List<Observation> results)
        {
            if (results == null) return;
            results.Clear();

            foreach (KeyValuePair<int, int> pair in handOwners)
            {
                if (!faceRoles.TryGetValue(pair.Value, out ParticipantRole ownerRole)) continue;
                if (ownerRole != role) continue;
                if (hands.TryGetValue(pair.Key, out Observation hand)) results.Add(hand);
            }
        }
    }
}