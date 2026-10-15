using UnityEngine;

namespace Valheim_Workers
{
    // Client-side interaction adapter attached to the already-loaded vanilla SpawnArea object.
    // It adds no network prefab and stores no state itself; persistent state remains on the nest ZDO.
    internal sealed class GreydwarfNestInteractable : MonoBehaviour, Hoverable, Interactable
    {
        private SpawnArea _spawner;

        internal void Initialize(SpawnArea spawner)
        {
            _spawner = spawner;
        }

        private void Awake()
        {
            if (_spawner == null)
                _spawner = GetComponent<SpawnArea>() ?? GetComponentInParent<SpawnArea>();
        }

        public string GetHoverText()
        {
            Plugin plugin = Plugin.Instance;
            if (plugin == null || _spawner == null) return string.Empty;
            return plugin.GetGreydwarfNestHoverText(_spawner);
        }

        public string GetHoverName()
        {
            Plugin plugin = Plugin.Instance;
            return plugin != null ? plugin.GetGreydwarfNestHoverName() : "Grauzwerg-Nest";
        }

        public float GetHoverOffset()
        {
            return 0f;
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || alt) return false;
            Plugin plugin = Plugin.Instance;
            return plugin != null && plugin.TryInteractWithGreydwarfNest(_spawner, user);
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            Plugin plugin = Plugin.Instance;
            return plugin != null && plugin.TryUseItemOnGreydwarfNest(_spawner, user, item);
        }
    }
}
