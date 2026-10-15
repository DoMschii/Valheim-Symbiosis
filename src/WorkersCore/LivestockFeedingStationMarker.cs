using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal enum LivestockFeedingStationKind
    {
        Trough = 1,
        Dispenser = 2
    }

    /// <summary>
    /// Strong identity for dedicated livestock feeding stations. The technical Container remains
    /// internal storage only; player interaction is owned by the livestock system rather than the
    /// generic chest/logistics UI.
    /// </summary>
    internal sealed class LivestockFeedingStationMarker : MonoBehaviour
    {
        [SerializeField] private int _kind = (int)LivestockFeedingStationKind.Trough;

        internal LivestockFeedingStationKind Kind => (LivestockFeedingStationKind)Mathf.Clamp(_kind, 1, 2);
        internal bool IsTrough => Kind == LivestockFeedingStationKind.Trough;
        internal bool IsDispenser => Kind == LivestockFeedingStationKind.Dispenser;

        internal void Configure(bool trough)
        {
            _kind = trough ? (int)LivestockFeedingStationKind.Trough : (int)LivestockFeedingStationKind.Dispenser;
        }

        internal Container GetContainer()
        {
            Container container = GetComponent<Container>();
            if (container == null) container = GetComponentInParent<Container>();
            if (container == null) container = GetComponentInChildren<Container>(true);
            return container;
        }

        private void OnEnable() => LivestockFeedingStationRegistry.Register(this);
        private void OnDisable() => LivestockFeedingStationRegistry.Unregister(this);
        private void OnDestroy() => LivestockFeedingStationRegistry.Unregister(this);
    }

    internal static class LivestockFeedingStationRegistry
    {
        private static readonly HashSet<LivestockFeedingStationMarker> Loaded = new HashSet<LivestockFeedingStationMarker>();

        internal static void Register(LivestockFeedingStationMarker marker)
        {
            if (marker != null) Loaded.Add(marker);
        }

        internal static void Unregister(LivestockFeedingStationMarker marker)
        {
            if (marker != null) Loaded.Remove(marker);
        }

        internal static void CopyLoaded(List<LivestockFeedingStationMarker> destination)
        {
            if (destination == null) return;
            destination.Clear();

            Loaded.RemoveWhere(x => x == null);
            foreach (LivestockFeedingStationMarker marker in Loaded)
            {
                if (marker == null || marker.gameObject == null || !marker.gameObject.activeInHierarchy) continue;
                destination.Add(marker);
            }
        }
    }
}
