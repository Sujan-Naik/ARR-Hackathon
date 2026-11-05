using System;
using UnityEngine;
using AR.Vision;

namespace Stacking
{
    /// <summary>
    /// Adapter: child's face -> HeadTiltSignal.
    /// Reads the association for the Child role, runs HeadPose on the face,
    /// and publishes smoothed angles. Does no gameplay work.
    /// </summary>
    public class ChildHeadInput : MonoBehaviour
    {
        [SerializeField] private ParticipantHandAssociation association;
        [SerializeField] private VisionPipeline vision;

        [Header("Tuning")]
        [SerializeField, Range(0f, 20f)] private float deadZoneDegrees = 2f;
        [SerializeField, Min(0f)] private float smoothing = 12f;
        [Tooltip("Set false to also require the child to face the camera (|yaw| < maxYaw).")]
        [SerializeField] private bool acceptTurnedHead = false;
        [SerializeField, Range(5f, 60f)] private float maxYaw = 35f;

        public HeadTiltSignal Current { get; private set; } = HeadTiltSignal.None;
        public event Action<HeadTiltSignal> Changed;

        private float smoothedRoll;
        private float smoothedYaw;
        private bool initialized;

        private void Awake()
        {
            if (association == null) association = FindObjectOfType<ParticipantHandAssociation>();
            if (vision == null) vision = FindObjectOfType<VisionPipeline>();
        }

        private void Update()
        {
            HeadTiltSignal next = Compute();

            if (!next.HasFace)
            {
                // Settle upright when the child is gone.
                smoothedRoll = Mathf.Lerp(smoothedRoll, 0f, Time.deltaTime * smoothing);
                smoothedYaw = Mathf.Lerp(smoothedYaw, 0f, Time.deltaTime * smoothing);
                if (Mathf.Abs(smoothedRoll) < 0.05f) smoothedRoll = 0f;
                if (Mathf.Abs(smoothedYaw) < 0.05f) smoothedYaw = 0f;
                next = new HeadTiltSignal(smoothedRoll, smoothedYaw, false, -1);
            }
            else
            {
                float roll = ApplyDeadZone(next.RollDegrees, deadZoneDegrees);
                float yaw = ApplyDeadZone(next.YawDegrees, deadZoneDegrees);

                if (!initialized)
                {
                    smoothedRoll = roll;
                    smoothedYaw = yaw;
                    initialized = true;
                }
                else
                {
                    smoothedRoll = Mathf.Lerp(smoothedRoll, roll, Time.deltaTime * smoothing);
                    smoothedYaw = Mathf.Lerp(smoothedYaw, yaw, Time.deltaTime * smoothing);
                }

                next = new HeadTiltSignal(smoothedRoll, smoothedYaw, true, next.TrackId);
            }

            if (!SignalsEqual(next, Current))
            {
                Current = next;
                Changed?.Invoke(next);
            }
        }

        private HeadTiltSignal Compute()
        {
            if (association == null || vision == null || !vision.IsReady())
                return HeadTiltSignal.None;

            if (!association.RolesAssigned) return HeadTiltSignal.None;
            if (!association.TryGetFace(
                    ParticipantHandAssociation.ParticipantRole.Child,
                    out Observation face))
                return HeadTiltSignal.None;

            if (!HeadPose.TryEstimate(face, vision.FrameAspect, out HeadAngles head))
                return HeadTiltSignal.None;

            if (!acceptTurnedHead && Mathf.Abs(head.Yaw) > maxYaw)
                return HeadTiltSignal.None;

            return new HeadTiltSignal(head.Roll, head.Yaw, true, face.TrackId);
        }

        private static float ApplyDeadZone(float value, float deadZone)
        {
            if (Mathf.Abs(value) <= deadZone) return 0f;
            return Mathf.Sign(value) * (Mathf.Abs(value) - deadZone);
        }

        private static bool SignalsEqual(HeadTiltSignal a, HeadTiltSignal b)
        {
            return a.HasFace == b.HasFace
                && a.TrackId == b.TrackId
                && Mathf.Approximately(a.RollDegrees, b.RollDegrees)
                && Mathf.Approximately(a.YawDegrees, b.YawDegrees);
        }
    }
}