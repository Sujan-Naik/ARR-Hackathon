// Role: asset listing every profile the game knows about, with lookups used by the profile mapping step.
using System.Collections.Generic;
using UnityEngine;

namespace AR.Vision
{
    [CreateAssetMenu(fileName = "TrackableRegistry", menuName = "AR/Vision/Trackable Registry")]
    public class TrackableRegistry : ScriptableObject
    {
        [SerializeField] private List<TrackableProfile> profiles = new List<TrackableProfile>();

        public IReadOnlyList<TrackableProfile> Profiles => profiles;

        public ItemProfile FindItemByDetectorLabel(string detectorLabel)
        {
            if (string.IsNullOrEmpty(detectorLabel)) return null;

            foreach (TrackableProfile p in profiles)
            {
                ItemProfile item = p as ItemProfile;
                if (item != null && item.DetectorLabel == detectorLabel) return item;
            }
            return null;
        }

        public CharacterProfile FindCharacterByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            foreach (TrackableProfile p in profiles)
            {
                CharacterProfile character = p as CharacterProfile;
                if (character != null && character.DisplayName == name) return character;
            }
            return null;
        }
    }
}
