using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Generic air-freight support for ordinary Valheim containers.
    /// Air freight is a capability layered on top of the FieldDepot role; it is not a special prefab.
    /// The visual is intentionally conservative: known lid/cover renderers are hidden while the
    /// capability is active. Inventory/collider/network components remain untouched.
    /// </summary>
    internal static class AirFreightContainerRuntime
    {
        private static readonly string[] LidTokens = { "lid", "cover", "deckel", "top" };

        internal static bool HasClearSky(Container container)
        {
            if (container == null) return false;
            Vector3 origin = container.transform.position + Vector3.up * 0.8f;
            RaycastHit[] hits;
            try { hits = Physics.RaycastAll(origin, Vector3.up, 80f, ~0, QueryTriggerInteraction.Ignore); }
            catch { return true; }
            if (hits == null || hits.Length == 0) return true;
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i].collider;
                if (col == null) continue;
                Transform t = col.transform;
                if (t == null) continue;
                if (t == container.transform || t.IsChildOf(container.transform) || container.transform.IsChildOf(t))
                    continue;
                return false;
            }
            return true;
        }

        internal static void Refresh(Container container)
        {
            if (container == null) return;
            bool air = WorkerContainerRoleRuntime.SupportsAirTransport(container);
            ApplyLidVisual(container, air);
            if (air)
            {
                AdvancedFieldDepotRuntime.RegisterLoadedDepot(container);
                if (WorkerContainerRoleRuntime.GetAirDepotMode(container) == AirDepotMode.BaseHub)
                    BackgroundSorterRuntime.CaptureLoadedNormalStorageNearBaseHub(container);
            }
            else
                AdvancedFieldDepotRuntime.UnregisterLoadedDepot(container);
        }

        internal static void ApplyLidVisual(Container container, bool airEnabled)
        {
            if (container == null) return;
            Renderer[] renderers;
            try { renderers = container.GetComponentsInChildren<Renderer>(true); }
            catch { return; }
            if (renderers == null) return;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || r.transform == null) continue;
                if (!LooksLikeLid(r.transform.name)) continue;
                r.enabled = !airEnabled;
            }
        }

        private static bool LooksLikeLid(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < LidTokens.Length; i++)
                if (lower.Contains(LidTokens[i])) return true;
            return false;
        }
    }
}
