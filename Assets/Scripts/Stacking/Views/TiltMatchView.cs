using UnityEngine;
using UnityEngine.UI;

namespace Stacking
{
    /// <summary>
    /// Drives the child-match feedback: a red flashing vignette while the child
    /// fails to mirror the tower's lean. Four edge Images; transparent when matched.
    /// </summary>
    public class TiltMatchView : MonoBehaviour
    {
        [SerializeField] private Image[] edges = new Image[4];

        [Header("Look")]
        [SerializeField] private Color calmColor = new Color(1f, 0f, 0f, 0f);
        [SerializeField] private Color flashColor = new Color(1f, 0.1f, 0.1f, 0.75f);
        [SerializeField, Min(0.5f)] private float flashHz = 6f;
        [SerializeField, Range(0f, 1f)] private float maxOpacity = 0.7f;

        private float intensity;

        public void Apply(TiltMatch match, float severity01)
        {
            bool mismatched = match == TiltMatch.Mismatched;
            float target = mismatched ? Mathf.Clamp01(severity01) : 0f;
            intensity = Mathf.MoveTowards(intensity, target, Time.deltaTime * 6f);

            float pulse = mismatched
                ? 0.6f + 0.4f * Mathf.Sin(Time.time * Mathf.PI * 2f * flashHz)
                : 1f;

            Color c = Color.Lerp(calmColor, flashColor, intensity * pulse * maxOpacity);
            ApplyColor(c);
        }

        private void ApplyColor(Color color)
        {
            if (edges == null) return;
            for (int i = 0; i < edges.Length; i++)
            {
                if (edges[i] == null) continue;
                edges[i].color = color;
                edges[i].raycastTarget = false;
            }
        }
    }
}
