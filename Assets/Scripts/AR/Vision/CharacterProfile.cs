// Role: a person the face module can recognize. DisplayName must match the gallery name used at enrollment.
using UnityEngine;

namespace AR.Vision
{
    [CreateAssetMenu(fileName = "CharacterProfile", menuName = "AR/Vision/Character Profile")]
    public class CharacterProfile : TrackableProfile
    {
        [SerializeField, TextArea] private string greeting = "";

        public string Greeting => greeting;
        public override ObservationKind Kind => ObservationKind.Face;
    }
}
