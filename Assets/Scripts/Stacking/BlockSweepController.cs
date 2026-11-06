using UnityEngine;

namespace Stacking
{
    public class BlockSweepController : MonoBehaviour
    {
        [SerializeField] private StackGame stackGame;
        [SerializeField, Min(0.05f)] private float steerRate = 0.9f;
        [Header("Automatic sweep (when no parent is steering)")]
        [SerializeField] private bool autoSweep = true;
        [SerializeField, Min(0.05f)] private float autoSweepRate = 0.45f;
        public bool IsSweeping { get; private set; }
        public float SteerTarget { get; private set; }
        public CraneSignal.Hand SteerHand { get; private set; } = CraneSignal.Hand.None;
        private float pos = 0.5f;
        private int autoDir = 1;
        private void Awake() { if (stackGame == null) stackGame = FindObjectOfType<StackGame>(); }
        public void Steer(CraneSignal.Hand hand) { SteerHand = hand; }
        public void ReleaseSteer() { SteerHand = CraneSignal.Hand.None; }
        private void Update()
        {
            if (stackGame == null) return;
            IsSweeping = stackGame.IsMoving;
            if (!IsSweeping) { SteerHand = CraneSignal.Hand.None; return; }
            if (SteerHand == CraneSignal.Hand.Left) pos = Mathf.Clamp01(pos - Time.deltaTime * steerRate);
            else if (SteerHand == CraneSignal.Hand.Right) pos = Mathf.Clamp01(pos + Time.deltaTime * steerRate);
            else if (autoSweep) { pos += autoDir * Time.deltaTime * autoSweepRate; if (pos >= 1f) { pos = 1f; autoDir = -1; } else if (pos <= 0f) { pos = 0f; autoDir = 1; } }
            else return;
            SteerTarget = pos;
            stackGame.SteerMovingBlock(pos);
        }
    }
}
