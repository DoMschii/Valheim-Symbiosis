using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Gatherer collection runtime: collect real loose ItemDrops and persist the cargo on the
    /// Worker ZDO. Normal delivery is handled through WorkerStorageRuntime into a FieldDepot.
    /// TryDeposit remains only as a legacy helper and is no longer used as an implicit navigation fallback.
    /// </summary>
    internal static class GathererWorkRuntime
    {
        internal const float CollectRadius = 3.2f;
        internal const float DepositRadius = 3.0f;

        private static readonly Collider[] Nearby = new Collider[96];
        private static Action<string> _debugLog;

        internal static void Initialize(Action<string> debugLog) => _debugLog = debugLog;
        internal static void Shutdown() => _debugLog = null;

        internal static bool TryCollect(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            string desiredPrefab = zdo.GetString(WorkerZdo.TargetName, string.Empty);
            if (GardenWorkRuntime.TryResolveGatherableItemPrefab(desiredPrefab, out string canonicalDesired) &&
                !string.IsNullOrEmpty(canonicalDesired))
                desiredPrefab = canonicalDesired;
            if (string.IsNullOrEmpty(desiredPrefab))
            {
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            Vector3 collectCenter = worker.transform.position;
            string targetKind = zdo.GetString(WorkerZdo.TargetKind, string.Empty);
            if (LocalWorkFallbackRuntime.IsActive(zdo, desiredPrefab, targetKind) &&
                WorkerZdo.TryReadTarget(zdo, out _, out _, out Vector3 fallbackTargetPosition))
            {
                collectCenter = fallbackTargetPosition;
            }

            ItemDrop drop = FindNearbyMatchingDrop(collectCenter, desiredPrefab);
            if (drop == null || !GathererTargetFinder.IsEligible(drop) || !GatherHarvestCategoryFilterRuntime.IsGatherDropAllowed(zdo, drop))
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            ZNetView nview = drop.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            if (!nview.IsOwner())
            {
                drop.RequestOwn();
                return true;
            }

            int available = Mathf.Max(0, drop.m_itemData.m_stack);
            int wanted = WorkerCargoRuntime.GetMaxAddAmount(worker, zdo, desiredPrefab, available);
            if (wanted <= 0)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                return true;
            }

            string prefabName = GathererTargetFinder.GetCargoPrefabName(drop);
            if (!ProductionDemandRuntime.AllowsPrefab(zdo, prefabName))
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            int removed = 0;
            for (int i = 0; i < wanted; i++)
            {
                if (drop == null) break;
                if (!drop.RemoveOne()) break;
                removed++;
            }

            if (removed <= 0) return true;

            WorkerZdo.AddCargoAmount(zdo, prefabName, removed);
            LocalWorkFallbackRuntime.Clear(zdo);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, WorkerCargoRuntime.IsFull(worker, zdo) ? (int)WorkerState.Deliver : (int)WorkerState.FindJob);
            string worldName = GathererTargetFinder.NormalizePrefabName(drop != null && drop.gameObject != null ? drop.gameObject.name : string.Empty);
            _debugLog?.Invoke($"Gatherer collected: worker={worker.gameObject.name}, item={prefabName}, world={worldName}, added={removed}, cargo={WorkerCargoRuntime.GetSummary(worker, zdo)}");
            return true;
        }

        internal static bool TryDeposit(Character worker, ZDO zdo, Vector3 depotCenter)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadCargo(zdo, out string prefabName, out int amount))
            {
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            GameObject prefab = null;
            if (ObjectDB.instance != null)
                prefab = ObjectDB.instance.GetItemPrefab(prefabName);
            if (prefab == null && ZNetScene.instance != null)
                prefab = ZNetScene.instance.GetPrefab(prefabName);

            ItemDrop template = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (template == null || template.m_itemData == null)
            {
                _debugLog?.Invoke($"Gatherer deposit blocked: item prefab '{prefabName}' is unavailable; cargo remains on worker ZDO.");
                return true;
            }

            int maxStack = template.m_itemData.m_shared != null ? Mathf.Max(1, template.m_itemData.m_shared.m_maxStackSize) : amount;
            int remaining = amount;
            Vector3 forward = worker.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();

            int stackIndex = 0;
            while (remaining > 0)
            {
                int stack = Mathf.Min(remaining, maxStack);
                ItemDrop.ItemData data = template.m_itemData.Clone();
                data.m_dropPrefab = prefab;
                Vector3 side = Vector3.Cross(Vector3.up, forward) * ((stackIndex % 3) - 1) * 0.35f;
                Vector3 position = depotCenter + Vector3.up * 0.45f + forward * 0.6f + side;
                ItemDrop.DropItem(data, stack, position, Quaternion.identity);
                remaining -= stack;
                stackIndex++;
            }

            WorkerZdo.ClearCargo(zdo);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            _debugLog?.Invoke($"Gatherer deposited: worker={worker.gameObject.name}, item={prefabName}, amount={amount}");
            return true;
        }

        private static ItemDrop FindNearbyMatchingDrop(Vector3 center, string prefabName)
        {
            ItemDrop best = null;
            float bestSqr = float.MaxValue;
            WorkerWorldIndex.ForEachItemDrop(center, CollectRadius, drop =>
            {
                if (drop == null || drop.gameObject == null || !drop.gameObject.activeInHierarchy) return;
                if (!string.Equals(GathererTargetFinder.GetCargoPrefabName(drop), prefabName, StringComparison.OrdinalIgnoreCase)) return;
                float sqr = (drop.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = drop;
            });
            return best;
        }
    }
}
