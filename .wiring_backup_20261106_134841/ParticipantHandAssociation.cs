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
    ///
    /// Public read-only snapshot APIs (GetBinding / GetAllBindings / NextFaceId)
    /// exist so HUD / gameplay code can display "who is who" without poking
    /// at private dictionaries. The RoleChanged event fires on every change
    /// of a single role (auto or manual) so UIs can react immediately.
    /// </summary>
    public class ParticipantHandAssociation : MonoBehaviour
    {
        public enum ParticipantRole
        {
            Child,
            Barber,
            Parent
        }

        /// <summary>
        /// Immutable snapshot of one role binding, safe to hand to UI code.
        /// FaceTrackId is -1 when unbound.
        /// </summary>
        public readonly struct RoleBinding
        {
            public readonly ParticipantRole Role;
            public readonly int FaceTrackId;
            public readonly bool IsManual;
            public readonly string Label;

            public RoleBinding(ParticipantRole role, int faceTrackId, bool isManual, string label)
            {
                Role = role;
                FaceTrackId = faceTrackId;
                IsManual = isManual;
                Label = label ?? string.Empty;
            }

            public bool IsBound => FaceTrackId >= 0;
        }

        [Header("Vision")]
        [SerializeField] private VisionPipeline vision;

        [Header("Participant assignment")]
        [Tooltip("Number of faces required before AUTO assigning the three roles. Manual assignment works with any number.")]
        [SerializeField, Range(1, 3)] private int requiredParticipants = 2;

        [Tooltip("Reassign roles when a previously assigned face disappears.")]
        [SerializeField] private bool reassignWhenFaceLost = true;

        [Tooltip("When a face is lost and other faces are still tracked, re-run auto assignment immediately to refill the empty slot (only affects roles that are not manually bound).")]
        [SerializeField] private bool autoRefillOnFaceLost = true;

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

        /// <summary>Fires for a single role whenever its binding changes. Carries the new snapshot.</summary>
        public event Action<RoleBinding> RoleChanged;

        /// <summary>Fires when RolesAssigned flips from false to true or true to false.</summary>
        public event Action<bool> RolesAssignedChanged;

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

        private static readonly ParticipantRole[] AllRoles =
        {
            ParticipantRole.Child,
            ParticipantRole.Barber,
            ParticipantRole.Parent,
        };

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

            NotifyRoleChanged(role);

            if (!reassignWhenFaceLost)
            {
                RefreshRolesAssignedFlag();
                return;
            }

            if (wasManual)
            {
                // Manual bindings stay in the manual set: the operator can
                // re-bind without clearing first.
                if (logAssignments)
                    Debug.Log("Manual role " + role + " face lost. Waiting for rebind.", this);
                RefreshRolesAssignedFlag();
                return;
            }

            // Auto path: if a slot is now empty and other faces are still
            // tracked, try to refill it immediately instead of waiting for a
            // brand new face to appear.
            if (autoRefillOnFaceLost && !RolesAssigned)
                TryAutoAssignRoles();
            else if (autoRefillOnFaceLost)
                TryRefillEmptyAutoRoles();

            RefreshRolesAssignedFlag();

            if (!RolesAssigned && faceRoles.Count == 0)
            {
                faceRoles.Clear();
                ClearHands();
                if (logAssignments)
                    Debug.Log("Participant face lost. Waiting to assign roles again.", this);
            }
        }

        // ---------------------------------------------------------------
        // Auto assignment
        // ---------------------------------------------------------------

        private void TryAutoAssignRoles()
        {
            if (RolesAssigned) return;
            if (faces.Count < requiredParticipants) return;
            if (manualRoles.Count >= 3) return;   // all roles manual: never auto

            AssignAutoRoles();
        }

        /// <summary>
        /// Refills any role that is currently unbound AND not manually bound,
        /// using unbound tracked faces in left-to-right order. Used when a
        /// face is lost but other faces remain.
        /// </summary>
        private void TryRefillEmptyAutoRoles()
        {
            if (manualRoles.Count >= 3) return;

            // Which roles are currently unbound?
            bool anyEmpty = false;
            for (int i = 0; i < AllRoles.Length; i++)
            {
                if (manualRoles.Contains(AllRoles[i])) continue;
                if (GetFaceIdForRoleInternal(AllRoles[i]) < 0) { anyEmpty = true; break; }
            }
            if (!anyEmpty) return;
            if (faces.Count == 0) return;

            AssignAutoRoles();
        }

        private void AssignAutoRoles()
        {
            List<Observation> sorted = new List<Observation>(faces.Values);
            sorted.Sort((a, b) => a.Center.x.CompareTo(b.Center.x));
            int faceIndex = 0;
            foreach (ParticipantRole role in AllRoles)
            {
                if (manualRoles.Contains(role)) continue;
                if (GetFaceIdForRoleInternal(role) >= 0) continue;
                while (faceIndex < sorted.Count && faceRoles.ContainsKey(sorted[faceIndex].TrackId)) faceIndex++;
                if (faceIndex >= sorted.Count) break;
                int faceId = sorted[faceIndex].TrackId;
                faceRoles[faceId] = role;
                faceIndex++;
                NotifyRoleChanged(role);
            }
            RefreshRolesAssignedFlag();
            if (logAssignments) Debug.Log("Participant roles auto-assigned (manual: " + manualRoles.Count + ").", this);
        }

        /// <summary>
        /// Manual trigger for auto-assignment. Useful from a UI button or a
        /// debug key. Respects manual bindings.
        /// </summary>
        public void AutoAssignNow()
        {
            if (manualRoles.Count >= 3) return;
            if (faces.Count == 0) return;
            AssignAutoRoles();
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
            int previousId = GetFaceIdForRoleInternal(role);

            if (faceTrackId < 0)
            {
                if (previousId >= 0) faceRoles.Remove(previousId);
                manualRoles.Remove(role);
            }
            else
            {
                if (previousId >= 0 && previousId != faceTrackId)
                    faceRoles.Remove(previousId);

                faceRoles[faceTrackId] = role;
                manualRoles.Add(role);
            }

            NotifyRoleChanged(role);
            RefreshRolesAssignedFlag();

            if (logAssignments)
                Debug.Log("Manual role " + role + " -> face " + faceTrackId
                    + " (assigned: " + RolesAssigned + ")", this);
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

            // Re-run auto so the player sees an immediate effect.
            if (faces.Count >= requiredParticipants)
                AssignAutoRoles();
        }

        /// <summary>Returns true if the given role was bound by a human.</summary>
        public bool IsRoleManual(ParticipantRole role) => manualRoles.Contains(role);

        /// <summary>Snapshot of the current face id for a role, or -1.</summary>
        public int GetFaceIdForRole(ParticipantRole role)
            => GetFaceIdForRoleInternal(role);

        private int GetFaceIdForRoleInternal(ParticipantRole role)
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

        /// <summary>Immutable snapshot of a role binding, safe to hand to UI.</summary>
        public RoleBinding GetBinding(ParticipantRole role)
        {
            int faceId = GetFaceIdForRoleInternal(role);
            string label = string.Empty;
            if (faceId >= 0 && faces.TryGetValue(faceId, out Observation face))
                label = face.Label;

            return new RoleBinding(role, faceId, manualRoles.Contains(role), label);
        }

        /// <summary>All three bindings in role order Child, Barber, Parent.</summary>
        public void GetAllBindings(List<RoleBinding> results)
        {
            if (results == null) return;
            results.Clear();
            for (int i = 0; i < AllRoles.Length; i++)
                results.Add(GetBinding(AllRoles[i]));
        }

        /// <summary>Returns the face that is NOT currently bound to any role, or -1.</summary>
        public int NextUnboundFaceId()
        {
            foreach (KeyValuePair<int, Observation> pair in faces)
                if (!faceRoles.ContainsKey(pair.Key)) return pair.Key;
            return -1;
        }

        // ---------------------------------------------------------------
        // Internal change notification
        // ---------------------------------------------------------------

        private void NotifyRoleChanged(ParticipantRole role)
        {
            RoleChanged?.Invoke(GetBinding(role));
            RolesAssignedEvent?.Invoke();
        }

        private void RefreshRolesAssignedFlag()
        {
            bool next = faceRoles.Count > 0;
            if (next == RolesAssigned) return;

            RolesAssigned = next;
            RolesAssignedChanged?.Invoke(next);
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