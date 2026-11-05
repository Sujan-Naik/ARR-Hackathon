using UnityEngine;

namespace Stacking
{
    /// <summary>
    /// Owns the block's sweep state. First raise starts the block moving
    /// left to right; second raise commits it. Raise while stopped ends the row.
    /// </summary>
    public class BlockSweepController : MonoBehaviour
    {
        [SerializeField] private StackGame stackGame;
        [Tooltip("How long the row takes to traverse the playfield at full width.")]
        [SerializeField, Min(0.2f)] private float sweepSeconds = 2.5f;

        public bool IsSweeping { get; private set; }

        private float pos;

        private void Awake()
        {
            if (stackGame == null) stackGame = FindObjectOfType<StackGame>();
        }

        public void ToggleSweep()
        {
            if (stackGame == null) return;
            if (stackGame.IsGameOver) return;

            if (stackGame.IsMoving)
            {
                stackGame.StopBlock();
                IsSweeping = false;
            }
            else
            {
                stackGame.SpawnBlock();
                IsSweeping = true;
                pos = 0f;
            }
        }

        private void Update()
        {
            if (!IsSweeping || stackGame == null) return;
            if (!stackGame.IsMoving) { IsSweeping = false; return; }

            pos += Time.deltaTime / Mathf.Max(0.01f, sweepSeconds);
            if (pos > 1f) pos = 0f;

            stackGame.SteerMovingBlock(pos);
        }
    }
}
