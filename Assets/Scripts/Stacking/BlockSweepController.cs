using UnityEngine;

namespace Stacking
{
    public class BlockSweepController : MonoBehaviour
    {
        [SerializeField] private StackGame stackGame;
        [SerializeField, Min(0.05f)] private float steerRate = 0.9f;

        public bool  IsSweeping  { get; private set; }
        public float SteerTarget { get; private set; }
        public CraneSignal.Hand SteerHand { get; private set; } = CraneSignal.Hand.None;

        private float pos = 0.5f;

        private void Awake()
        {
            if (stackGame == null) stackGame = FindObjectOfType<StackGame>();
        }

        public void Steer(CraneSignal.Hand hand) { SteerHand = hand; }

        private void Update()
        {
            if (stackGame == null) return;
            IsSweeping = stackGame.IsMoving;
            if (!IsSweeping) { SteerHand = CraneSignal.Hand.None; return; }

            if (SteerHand == CraneSignal.Hand.Left)
            {
                pos = Mathf.Clamp01(pos - Time.deltaTime * steerRate);
                SteerTarget = pos;
                stackGame.SteerMovingBlock(pos);
            }
            else if (SteerHand == CraneSignal.Hand.Right)
            {
                pos = Mathf.Clamp01(pos + Time.deltaTime * steerRate);
                SteerTarget = pos;
                stackGame.SteerMovingBlock(pos);
            }
        }
    }
}
