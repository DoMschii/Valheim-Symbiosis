using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class WorkFlagInteractable : MonoBehaviour, Hoverable, Interactable
    {
        private bool _identityReady;

        private void OnEnable()
        {
            WorkFlagRegistry.Register(this);
            _identityReady = false;
            CancelInvoke(nameof(TryInitializeIdentity));
            Invoke(nameof(TryInitializeIdentity), 0.05f);
        }

        private void TryInitializeIdentity()
        {
            if (_identityReady || !isActiveAndEnabled) return;

            Plugin plugin = Plugin.Instance;
            _identityReady = plugin != null && plugin.TryInitializeWorkFlagIdentity(this);
            if (!_identityReady)
                Invoke(nameof(TryInitializeIdentity), 1f);
        }

        private void OnDisable()
        {
            CancelInvoke(nameof(TryInitializeIdentity));
            WorkFlagRegistry.Unregister(this);
        }

        private void OnDestroy()
        {
            CancelInvoke(nameof(TryInitializeIdentity));
            WorkFlagRegistry.Unregister(this);
        }

        public string GetHoverName() => "Workers-Arbeitsfahne";
        public float GetHoverOffset() => 0f;

        public string GetHoverText()
        {
            Plugin plugin = Plugin.Instance;
            return plugin != null ? plugin.GetWorkFlagHoverText(this) : string.Empty;
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || alt) return false;
            Plugin plugin = Plugin.Instance;
            return plugin != null && plugin.TryOpenWorkFlagMenu(this, user);
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

        internal ZNetView GetNetworkView()
        {
            ZNetView nview = GetComponent<ZNetView>();
            if (nview == null) nview = GetComponentInParent<ZNetView>();
            return nview;
        }
    }
}
