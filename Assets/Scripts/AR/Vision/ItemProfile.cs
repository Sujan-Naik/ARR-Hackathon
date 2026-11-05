// Role: an object the object module can detect. DetectorLabel must match the model's class name.
using UnityEngine;

namespace AR.Vision
{
    [CreateAssetMenu(fileName = "ItemProfile", menuName = "AR/Vision/Item Profile")]
    public class ItemProfile : TrackableProfile
    {
        [SerializeField] private string detectorLabel = "";

        public string DetectorLabel => detectorLabel;
        public override ObservationKind Kind => ObservationKind.Object;
    }
}
