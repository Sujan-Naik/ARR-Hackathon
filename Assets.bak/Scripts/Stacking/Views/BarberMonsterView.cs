using UnityEngine;

namespace Stacking
{
    /// <summary>
    /// Renders the monster's hands as 3D "grabby" spheres above the tower.
    /// Uses the two hand centres and their openness to drive a pair of
    /// glowing hands. Abstract on purpose; no imported model required.
    /// </summary>
    public class BarberMonsterView : MonoBehaviour
    {
        [SerializeField] private Camera renderCamera;
        [SerializeField] private Transform leftHand;
        [SerializeField] private Transform rightHand;
        [SerializeField] private Renderer leftRenderer;
        [SerializeField] private Renderer rightRenderer;

        [Header("Placement")]
        [SerializeField, Min(0.1f)] private float depth = 4f;
        [SerializeField, Min(0.01f)] private float handSize = 0.25f;

        [Header("Look")]
        [SerializeField] private Color openColor = new Color(0.9f, 0.4f, 0.7f);
        [SerializeField] private Color closedColor = new Color(1f, 0.15f, 0.35f);

        private MaterialPropertyBlock block;

        private void Awake()
        {
            if (renderCamera == null) renderCamera = Camera.main;
            block = new MaterialPropertyBlock();
        }

        public void Apply(MonsterHandsSignal signal)
        {
            if (renderCamera == null) return;

            if (!signal.HasAny)
            {
                if (leftHand != null) leftHand.gameObject.SetActive(false);
                if (rightHand != null) rightHand.gameObject.SetActive(false);
                return;
            }

            bool hasRight = signal.Right.TrackId >= 0;

            Place(signal.Left, leftHand, leftRenderer);
            if (rightHand != null) rightHand.gameObject.SetActive(hasRight);
            if (hasRight) Place(signal.Right, rightHand, rightRenderer);
        }

        private void Place(MonsterHand hand, Transform target, Renderer targetRenderer)
        {
            if (target == null) return;

            // Normalized top-left -> viewport bottom-left.
            Vector3 viewport = new Vector3(
                Mathf.Clamp01(hand.Center.x),
                1f - Mathf.Clamp01(hand.Center.y),
                depth);

            target.position = renderCamera.ViewportToWorldPoint(viewport);

            float scale = hand.IsClosed ? handSize * 0.7f
                        : hand.IsPinching ? handSize * 0.85f
                        : handSize;
            target.localScale = Vector3.one * scale;

            if (targetRenderer != null && block != null)
            {
                targetRenderer.GetPropertyBlock(block);
                block.SetColor("_Color", hand.IsClosed ? closedColor : openColor);
                targetRenderer.SetPropertyBlock(block);
            }

            if (!target.gameObject.activeSelf)
                target.gameObject.SetActive(true);
        }
    }
}