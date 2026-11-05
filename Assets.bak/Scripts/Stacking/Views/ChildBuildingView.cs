using UnityEngine;

namespace Stacking
{
    /// <summary>
    /// Renders the child's head as the tower's lean. The building is
    /// the block tower itself; this view rotates its parent pivot so
    /// "the child is holding up the building".
    /// </summary>
    public class ChildBuildingView : MonoBehaviour
    {
        [SerializeField] private Transform buildingPivot;

        [Header("Map")]
        [SerializeField, Range(0f, 60f)] private float maxLeanDegrees = 18f;
        [SerializeField] private bool invert = false;

        private float currentLean;

        public void Apply(HeadTiltSignal signal)
        {
            float target = 0f;

            if (signal.HasFace)
            {
                float normalized = Mathf.Clamp(signal.RollDegrees / 30f, -1f, 1f);
                target = normalized * maxLeanDegrees;
            }

            if (invert) target = -target;

            currentLean = Mathf.Lerp(currentLean, target, Time.deltaTime * 6f);

            if (buildingPivot != null)
                buildingPivot.localRotation = Quaternion.Euler(0f, 0f, currentLean);
        }
    }
}