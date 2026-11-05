using System;
using UnityEngine;
using AR.Vision;

namespace Stacking
{
    /// <summary>
    /// Adapter: child's face -> HeadTiltSignal. Publishes the child's head
    /// angles AND the child's head centre in normalized screen space, because
    /// the barber-guiding mode needs proximity between barber hands and head.
    /// </summary>
    public class ChildHeadInput : MonoBehaviour
    {
        [SerializeField] private ParticipantHandAssociation association;
        [SerializeField] private VisionPipeline vision;

        [Header("Tuning")]
        [SerializeField, Range(0f, 20f)] private float deadZoneDegrees = 2f;
        [SerializeField, Min(0f)] private float smoothing = 12f;

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
                smoothedRoll = Mathf.Lerp(smoothedRoll, 0f, Time.deltaTime * smoothing);
                smoothedYaw = Mathf.Lerp(smoothedYaw, 0f, Time.deltaTime * smoothing);
                if (Mathf.Abs(smoothedRoll) < 0.05f) smoothedRoll = 0f;
                if (Mathf.Abs(smoothedYaw) < 0.05f) smoothedYaw = 0f;
                next = new HeadTiltSignal(smoothedRoll, smoothedYaw, Vector2.zero, false, -1);
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

                next = new HeadTiltSignal(smoothedRoll, smoothedYaw, next.Center, true, next.TrackId);
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

            return new HeadTiltSignal(head.Roll, head.Yaw, face.Center, true, face.TrackId);
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
                && Mathf.Approximately(a.YawDegrees, b.YawDegrees)
                && Vector2.Distance(a.Center, b.Center) < 0.001f;
        }
    }
}
