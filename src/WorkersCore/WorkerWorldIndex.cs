using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Lightweight lifecycle index for objects Workers queries frequently.
    ///
    /// The previous implementation used broad Physics.OverlapSphere scans for containers,
    /// ItemDrops and Pickables. In a developed base those scans walk thousands of unrelated
    /// colliders and were a strong source of periodic frame spikes. These lists are populated
    /// once from the normal Valheim Awake lifecycle and then filtered by distance.
    /// </summary>
    internal static class WorkerWorldIndex
    {
        private static readonly List<Container> Containers = new List<Container>(256);
        private static readonly List<ItemDrop> ItemDrops = new List<ItemDrop>(512);
        private static readonly List<Pickable> Pickables = new List<Pickable>(512);
        private static readonly System.Reflection.FieldInfo NativeItemDropsField = AccessTools.Field(typeof(ItemDrop), "s_instances");
        private static float _nextCleanupAt;
        private static int _cleanupPhase;
        private static bool _itemDropsBootstrapped;
        private static bool _containersBootstrapped;
        private static bool _pickablesBootstrapped;
        private static readonly Collider[] PickableRepairBuffer = new Collider[384];
        private static readonly Collider[] ContainerRepairBuffer = new Collider[512];
        private static float _nextPickableLocalRepairAt;
        private static float _nextContainerLocalRepairAt;
        private const float PickableLocalRepairCooldownSeconds = 4f;
        private const float ContainerLocalRepairCooldownSeconds = 2f;

        internal static int ContainerCount => Containers.Count;
        internal static int ItemDropCount
        {
            get
            {
                EnsureItemDropsBootstrapped();
                return ItemDrops.Count;
            }
        }
        internal static int PickableCount
        {
            get
            {
                EnsurePickablesBootstrapped();
                return Pickables.Count;
            }
        }

        /// <summary>
        /// Explicit developer refresh used only when the user presses the Dev Reload key.
        /// Unlike normal runtime maintenance this is allowed to rebuild the loaded-object indexes
        /// in one shot because it is user-triggered, never periodic. Normal Worker AI still relies
        /// only on lifecycle registration and cheap indexed queries.
        /// </summary>
        internal static void RebuildLoadedState()
        {
            Containers.Clear();
            ItemDrops.Clear();
            Pickables.Clear();
            _nextCleanupAt = 0f;
            _cleanupPhase = 0;
            _itemDropsBootstrapped = false;
            _containersBootstrapped = false;
            _pickablesBootstrapped = false;
            _nextPickableLocalRepairAt = 0f;
            _nextContainerLocalRepairAt = 0f;

            try
            {
                Container[] containers = Resources.FindObjectsOfTypeAll<Container>();
                if (containers != null)
                {
                    for (int i = 0; i < containers.Length; i++)
                    {
                        Container value = containers[i];
                        if (value != null && value.gameObject != null && value.gameObject.activeInHierarchy)
                            Register(value);
                    }
                }
            }
            catch { }
            _containersBootstrapped = true;

            try
            {
                Pickable[] pickables = Resources.FindObjectsOfTypeAll<Pickable>();
                if (pickables != null)
                {
                    for (int i = 0; i < pickables.Length; i++)
                    {
                        Pickable value = pickables[i];
                        if (value != null && value.gameObject != null && value.gameObject.activeInHierarchy)
                            Register(value);
                    }
                }
            }
            catch { }
            _pickablesBootstrapped = true;

            // ItemDrop has a native loaded-instance registry; prefer that authoritative source.
            EnsureItemDropsBootstrapped();
        }

        internal static void Register(Container value)
        {
            if (value == null || Containers.Contains(value)) return;
            Containers.Add(value);

            // v0.5.457: SmartStorage feeding marker maintenance is event-driven.
            // No periodic loaded-container sweep is required anymore.
            SmartStorageFeedingPieceBridge.NotifyContainerLoaded(value);
        }

        internal static void Register(ItemDrop value)
        {
            if (value == null || ItemDrops.Contains(value)) return;
            ItemDrops.Add(value);
        }

        internal static void Unregister(ItemDrop value)
        {
            if (value == null) return;
            ItemDrops.Remove(value);
        }

        internal static void Register(Pickable value)
        {
            if (value == null || Pickables.Contains(value)) return;
            Pickables.Add(value);
        }

        private static void EnsureContainersBootstrapped()
        {
            if (_containersBootstrapped) return;
            _containersBootstrapped = true;

            // Harmony's Container.Awake hook only sees containers whose Awake runs after this mod
            // has patched the game. Existing base chests can therefore be missing from the lifecycle
            // index after load/hot-reload even though their ZDO role (Import/Export) is valid. Bootstrap
            // the currently loaded set exactly once, then remain fully incremental through Awake.
            // This is deliberately NOT a per-frame/world scan.
            try
            {
                Container[] loaded = Resources.FindObjectsOfTypeAll<Container>();
                if (loaded == null) return;
                for (int i = 0; i < loaded.Length; i++)
                {
                    Container value = loaded[i];
                    if (value != null && value.gameObject != null && value.gameObject.activeInHierarchy)
                        Register(value);
                }
            }
            catch
            {
                // Awake registration still keeps all subsequently loaded containers indexed.
            }
        }

        internal static int ForEachContainer(Vector3 center, float radius, Action<Container> visitor)
        {
            EnsureContainersBootstrapped();
            CleanupIfDue();
            float radiusSqr = Mathf.Max(1f, radius) * Mathf.Max(1f, radius);
            int visited = 0;
            for (int i = 0; i < Containers.Count; i++)
            {
                Container value = Containers[i];
                if (value == null || value.gameObject == null || !value.gameObject.activeInHierarchy) continue;
                if ((value.transform.position - center).sqrMagnitude > radiusSqr) continue;
                visited++;
                visitor?.Invoke(value);
            }
            return visited;
        }

        /// <summary>
        /// Iterates only the already-maintained loaded-container registry, without a world scan.
        /// Used by long-distance logistics endpoints (Import/Export), because a Hauler HomeAnchor
        /// belongs to its outpost and must not artificially limit recognition of a loaded Base chest.
        /// </summary>
        internal static int ForEachLoadedContainer(Action<Container> visitor)
        {
            EnsureContainersBootstrapped();
            CleanupIfDue();
            int visited = 0;
            for (int i = 0; i < Containers.Count; i++)
            {
                Container value = Containers[i];
                if (value == null || value.gameObject == null || !value.gameObject.activeInHierarchy) continue;
                visited++;
                visitor?.Invoke(value);
            }
            return visited;
        }

        internal static int ForEachItemDrop(Vector3 center, float radius, Action<ItemDrop> visitor)
        {
            EnsureItemDropsBootstrapped();
            CleanupIfDue();
            float radiusSqr = Mathf.Max(1f, radius) * Mathf.Max(1f, radius);
            int visited = 0;
            for (int i = 0; i < ItemDrops.Count; i++)
            {
                ItemDrop value = ItemDrops[i];
                if (value == null || value.gameObject == null || !value.gameObject.activeInHierarchy) continue;
                if ((value.transform.position - center).sqrMagnitude > radiusSqr) continue;
                visited++;
                visitor?.Invoke(value);
            }
            return visited;
        }

        internal static int ForEachPickable(Vector3 center, float radius, Action<Pickable> visitor)
        {
            EnsurePickablesBootstrapped();
            CleanupIfDue();
            float radiusSqr = Mathf.Max(1f, radius) * Mathf.Max(1f, radius);
            int visited = 0;
            for (int i = 0; i < Pickables.Count; i++)
            {
                Pickable value = Pickables[i];
                if (value == null || value.gameObject == null || !value.gameObject.activeInHierarchy) continue;
                if ((value.transform.position - center).sqrMagnitude > radiusSqr) continue;
                visited++;
                visitor?.Invoke(value);
            }
            return visited;
        }



        /// <summary>
        /// Repairs only the loaded Pickable index around an active work area. Valheim can stream an
        /// already-created zone back in without running the Pickable Awake path again, which leaves
        /// the lifecycle index stale until the old F12 developer rebuild is used. Keep the fast
        /// lifecycle index as the normal path and use this small, throttled local physics probe only
        /// when a Harvester found no usable target. Navigation and worker state are not touched.
        /// </summary>
        internal static int RepairLoadedPickablesNear(Vector3 center, float radius)
        {
            float now = Time.unscaledTime;
            if (now < _nextPickableLocalRepairAt) return 0;
            _nextPickableLocalRepairAt = now + PickableLocalRepairCooldownSeconds;

            float safeRadius = Mathf.Max(5f, radius);
            int hitCount;
            try
            {
                hitCount = Physics.OverlapSphereNonAlloc(center, safeRadius, PickableRepairBuffer, ~0, QueryTriggerInteraction.Collide);
            }
            catch
            {
                return 0;
            }

            int added = 0;
            float radiusSqr = safeRadius * safeRadius;
            for (int i = 0; i < hitCount && i < PickableRepairBuffer.Length; i++)
            {
                Collider collider = PickableRepairBuffer[i];
                PickableRepairBuffer[i] = null;
                if (collider == null) continue;

                Pickable pickable = ResolvePickable(collider);
                if (pickable == null || pickable.gameObject == null || !pickable.gameObject.activeInHierarchy) continue;
                if ((pickable.transform.position - center).sqrMagnitude > radiusSqr) continue;
                if (Pickables.Contains(pickable)) continue;
                Pickables.Add(pickable);
                added++;
            }

            return added;
        }

        /// <summary>
        /// Repairs only the loaded Container index inside the requested work area. This is a narrow,
        /// throttled fallback used when a physically active worker cannot see a streamed-in chest in
        /// the lifecycle index. It never searches outside the supplied Workzone radius.
        /// </summary>
        internal static int RepairLoadedContainersNear(Vector3 center, float radius)
        {
            float now = Time.unscaledTime;
            if (now < _nextContainerLocalRepairAt) return 0;
            _nextContainerLocalRepairAt = now + ContainerLocalRepairCooldownSeconds;

            float safeRadius = Mathf.Max(5f, radius);
            int hitCount;
            try
            {
                hitCount = Physics.OverlapSphereNonAlloc(center, safeRadius, ContainerRepairBuffer, ~0, QueryTriggerInteraction.Collide);
            }
            catch
            {
                return 0;
            }

            int added = 0;
            float radiusSqr = safeRadius * safeRadius;
            for (int i = 0; i < hitCount && i < ContainerRepairBuffer.Length; i++)
            {
                Collider collider = ContainerRepairBuffer[i];
                ContainerRepairBuffer[i] = null;
                if (collider == null) continue;

                Container container = collider.GetComponentInParent<Container>();
                if (container == null)
                {
                    ZNetView rootView = collider.GetComponentInParent<ZNetView>();
                    if (rootView != null) container = rootView.GetComponentInChildren<Container>(true);
                }
                if (container == null || container.gameObject == null || !container.gameObject.activeInHierarchy ||
                    (container.transform.position - center).sqrMagnitude > radiusSqr || Containers.Contains(container)) continue;

                Register(container);
                PersistentContainerRegistryRuntime.Remember(container);
                added++;
            }

            return added;
        }

        private static Pickable ResolvePickable(Collider collider)
        {
            if (collider == null) return null;
            Pickable direct = collider.GetComponentInParent<Pickable>();
            if (direct != null) return direct;
            ZNetView rootView = collider.GetComponentInParent<ZNetView>();
            if (rootView != null)
            {
                Pickable child = rootView.GetComponentInChildren<Pickable>(true);
                if (child != null) return child;
            }
            Transform root = collider.transform != null ? collider.transform.root : null;
            return root != null ? root.GetComponentInChildren<Pickable>(true) : null;
        }

        private static void EnsurePickablesBootstrapped()
        {
            if (_pickablesBootstrapped) return;
            _pickablesBootstrapped = true;

            // Pickable.Awake registration only observes objects whose Awake runs after Harmony is
            // installed. Farms/flora already loaded while the mod starts therefore used to be
            // completely absent from the Gatherer's harvest index until a manual Dev Reload.
            // Bootstrap the currently loaded Pickables exactly once, then remain incremental via
            // the normal Awake lifecycle hook. This is not a periodic/world-per-frame scan.
            try
            {
                Pickable[] loaded = Resources.FindObjectsOfTypeAll<Pickable>();
                if (loaded == null) return;
                for (int i = 0; i < loaded.Length; i++)
                {
                    Pickable value = loaded[i];
                    if (value != null && value.gameObject != null && value.gameObject.activeInHierarchy)
                        Register(value);
                }
            }
            catch
            {
                // Newly loaded Pickables are still registered by the Awake Harmony patch.
            }
        }

        private static void EnsureItemDropsBootstrapped()
        {
            if (_itemDropsBootstrapped) return;
            _itemDropsBootstrapped = true;

            // Valheim itself keeps every currently loaded world drop in ItemDrop.s_instances.
            // Awake hooks only see objects created after Harmony is installed, so a world can
            // already contain valid drops that our local index never observed. Bootstrap once
            // from the native registry, then stay incremental through Awake/OnDestroy. This is
            // a cheap loaded-object copy, not a FindObjectsOfType/Physics world scan.
            try
            {
                List<ItemDrop> native = NativeItemDropsField?.GetValue(null) as List<ItemDrop>;
                if (native == null) return;

                for (int i = 0; i < native.Count; i++)
                {
                    ItemDrop drop = native[i];
                    if (drop != null) Register(drop);
                }
            }
            catch
            {
                // The normal Awake lifecycle still keeps the index usable if a future Valheim
                // version changes the private native registry. Never break Worker AI for this.
            }
        }

        private static void CleanupIfDue()
        {
            if (Time.unscaledTime < _nextCleanupAt) return;
            // Spread maintenance over time instead of compacting all three registries in the
            // same frame. Large farms can contain hundreds of Pickables/ItemDrops and the old
            // combined cleanup produced a very visible periodic frame spike.
            _nextCleanupAt = Time.unscaledTime + 4f;
            switch (_cleanupPhase++ % 3)
            {
                case 0: Cleanup(Containers); break;
                case 1: Cleanup(ItemDrops); break;
                default: Cleanup(Pickables); break;
            }
        }

        private static void Cleanup<T>(List<T> list) where T : UnityEngine.Object
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] == null) list.RemoveAt(i);
            }
        }
    }

    [HarmonyPatch(typeof(Container), "Awake")]
    internal static class WorkerContainerIndexPatch
    {
        private static void Postfix(Container __instance)
        {
            WorkerWorldIndex.Register(__instance);
            try { SharedStorageStockIndexRuntime.OnContainerAwake(__instance); } catch { }
            try { FieldSupplyDemandCacheRuntime.OnContainerAwake(__instance); } catch { }
            PersistentContainerRegistryRuntime.Remember(__instance);
            AirFreightContainerRuntime.Refresh(__instance);
            WorkerBackgroundDeliveryApplier.Ensure(__instance);

            // Normal Base storage is not a logistics endpoint by itself, but remote Sorter
            // simulation needs a durable, real-stock snapshot. Once a normal chest is loaded near
            // a configured Base AirFreight hub, register it as a tracked sorter source.
            if (__instance != null && WorkerContainerRoleRuntime.GetRole(__instance) == WorkerContainerRole.Normal &&
                WorkerLogisticsRuntime.TryGetBaseAirHubOwnerNear(__instance.transform.position, WorkerStorageRuntime.LogisticsNetworkRadius, out long ownerId))
            {
                ZDO zdo = WorkerContainerRoleRuntime.GetZdo(__instance);
                if (zdo != null && zdo.GetBool(ContainerLedgerRuntime.DirtyKey, false))
                    ContainerLedgerRuntime.TryReconcileLoaded(__instance, out _);
                ContainerLedgerRuntime.MarkSorterStorage(__instance, ownerId);
            }

            // If the Base AirFreight chest happens to awaken after the ordinary chests, capture
            // those already-loaded sources once now. This removes load-order dependence without a
            // world/ZDO scan per chest.
            if (__instance != null && WorkerContainerRoleRuntime.GetRole(__instance) == WorkerContainerRole.FieldDepot &&
                WorkerContainerRoleRuntime.SupportsAirTransport(__instance) &&
                WorkerContainerRoleRuntime.GetAirDepotMode(__instance) == AirDepotMode.BaseHub)
            {
                ZDO hubZdo = WorkerContainerRoleRuntime.GetZdo(__instance);
                long hubOwnerId = hubZdo != null ? hubZdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L) : 0L;
                Vector3 hubPosition = __instance.transform.position;
                float maxSqr = WorkerStorageRuntime.LogisticsNetworkRadius * WorkerStorageRuntime.LogisticsNetworkRadius;
                WorkerStorageRuntime.ForEachLoadedContainer(candidate =>
                {
                    if (candidate == null || candidate == __instance || WorkerContainerRoleRuntime.GetRole(candidate) != WorkerContainerRole.Normal) return;
                    if ((candidate.transform.position - hubPosition).sqrMagnitude > maxSqr) return;
                    ZDO candidateZdo = WorkerContainerRoleRuntime.GetZdo(candidate);
                    if (candidateZdo != null && candidateZdo.GetBool(ContainerLedgerRuntime.DirtyKey, false))
                        ContainerLedgerRuntime.TryReconcileLoaded(candidate, out _);
                    ContainerLedgerRuntime.MarkSorterStorage(candidate, hubOwnerId);
                });
            }
        }
    }


    [HarmonyPatch(typeof(ItemDrop), "Awake")]
    internal static class WorkerItemDropIndexPatch
    {
        private static void Postfix(ItemDrop __instance) => WorkerWorldIndex.Register(__instance);
    }

    [HarmonyPatch(typeof(ItemDrop), "OnDestroy")]
    internal static class WorkerItemDropDestroyIndexPatch
    {
        private static void Prefix(ItemDrop __instance) => WorkerWorldIndex.Unregister(__instance);
    }

    [HarmonyPatch(typeof(Pickable), "Awake")]
    internal static class WorkerPickableIndexPatch
    {
        private static void Postfix(Pickable __instance) => WorkerWorldIndex.Register(__instance);
    }
}
