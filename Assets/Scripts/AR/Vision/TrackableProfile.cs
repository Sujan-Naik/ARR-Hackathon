// Role: abstract base for anything the game can attach meaning to (a character, a tool).
using UnityEngine;

namespace AR.Vision
{
    public abstract class TrackableProfile : ScriptableObject
    {
        [SerializeField] private string displayName = "";

        public string DisplayName => displayName;
        public abstract ObservationKind Kind { get; }
    }
}
