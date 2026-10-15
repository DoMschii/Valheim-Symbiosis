using System;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Physical Neck/Fisher runtime. It never creates fish from a timer: only already loaded real
    /// Valheim Fish objects are valid targets. Catching consumes that real ItemDrop and transfers
    /// one item into the Worker's persistent two-slot cargo inventory.
    /// </summary>
    internal static class FisherWorkRuntime
    {
        internal const string TargetKind = "FisherFish";
        internal const float CatchRange = 3.1f;
        private const float MaxLocalSearchRadius = 32f;

        private static readonly Type FishType = AccessTools.TypeByName("Fish");
        private static Action<string> _debugLog;

        internal static void Initialize(Action<string> debugLog)
        {
            _debugLog = debugLog;
            if (FishType == null)
                _debugLog?.Invoke("Fisher note: Valheim Fish component type not found; physical fishing remains idle instead of creating synthetic fish.");
        }

        internal static void Shutdown() => _debugLog = null;

        internal static bool TryFindFish(Character worker, ZDO zdo, Vector3 workCenter, float workRadius, out string fishPrefab, out Vector3 fishPosition)
        {
            fishPrefab = string.Empty;
            fishPosition = default;
            if (worker == null || zdo == null || FishType == null) return false;
            if (WorkerCargoRuntime.IsFull(worker, zdo)) return false;

            ItemDrop best = null;
            string bestPrefab = string.Empty;
            float bestSqr = float.MaxValue;
            float localRadius = Mathf.Clamp(workRadius, 8f, MaxLocalSearchRadius);

            WorkerWorldIndex.ForEachItemDrop(worker.transform.position, localRadius, candidate =>
            {
                if (!IsLiveFish(candidate)) return;
                Vector3 position = candidate.transform.position;
                if (!WorkerWorkAreaRules.IsPointInsideArea(position, workCenter, workRadius)) return;

                string prefab = GathererTargetFinder.GetCargoPrefabName(candidate);
                if (string.IsNullOrEmpty(prefab)) return;
                if (!ProductionDemandRuntime.AllowsPrefab(zdo, prefab)) return;
                if (WorkerCargoRuntime.GetMaxAddAmount(worker, zdo, prefab, 1) <= 0) return;

                float sqr = (position - worker.transform.position).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = candidate;
                bestPrefab = prefab;
            });

            if (best == null) return false;
            fishPrefab = bestPrefab;
            fishPosition = best.transform.position;
            return true;
        }

        internal static bool TryCatch(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            string desiredPrefab = zdo.GetString(WorkerZdo.TargetName, string.Empty);
            if (string.IsNullOrEmpty(desiredPrefab))
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            ItemDrop fish = FindNearbyMatchingFish(worker.transform.position, desiredPrefab, CatchRange + 0.9f);
            if (fish == null)
            {
                // Fish move. Do not keep walking toward a stale water coordinate forever; the next
                // AI decision will search the loaded water around the Neck again.
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            string prefab = GathererTargetFinder.GetCargoPrefabName(fish);
            if (string.IsNullOrEmpty(prefab))
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            if (!ProductionDemandRuntime.AllowsPrefab(zdo, prefab))
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            if (WorkerCargoRuntime.GetMaxAddAmount(worker, zdo, prefab, 1) <= 0)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                return true;
            }

            ZNetView view = fish.GetComponent<ZNetView>() ?? fish.GetComponentInParent<ZNetView>();
            if (view == null || !view.IsValid() || view.GetZDO() == null)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            if (!view.IsOwner())
            {
                fish.RequestOwn();
                return true;
            }

            // Remove the authoritative real fish first. Only after Valheim confirms that removal do
            // we persist the catch as Worker cargo. This is deliberately dup-safe rather than loss-safe:
            // a failed removal can never create a duplicate fish.
            if (!fish.RemoveOne()) return true;

            WorkerZdo.AddCargoAmount(zdo, prefab, 1);
            BackgroundFisherRuntime.RememberObservedFish(zdo, prefab);
            ProductionMetricsRuntime.RecordProduced(zdo, prefab, 1);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, WorkerCargoRuntime.IsFull(worker, zdo)
                ? (int)WorkerState.Deliver
                : (int)WorkerState.FindJob);
            _debugLog?.Invoke($"Fisher caught real fish: worker={worker.gameObject.name}, item={prefab}, cargo={WorkerCargoRuntime.GetSummary(worker, zdo)}");
            return true;
        }

        private static ItemDrop FindNearbyMatchingFish(Vector3 center, string prefabName, float radius)
        {
            ItemDrop best = null;
            float bestSqr = float.MaxValue;
            WorkerWorldIndex.ForEachItemDrop(center, radius, candidate =>
            {
                if (!IsLiveFish(candidate)) return;
                string candidatePrefab = GathererTargetFinder.GetCargoPrefabName(candidate);
                if (!string.Equals(candidatePrefab, prefabName, StringComparison.OrdinalIgnoreCase)) return;
                float sqr = (candidate.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = candidate;
            });
            return best;
        }

        internal static bool IsLiveFish(ItemDrop drop)
        {
            if (drop == null || drop.gameObject == null || !drop.gameObject.activeInHierarchy || FishType == null)
                return false;

            Transform cursor = drop.transform;
            for (int depth = 0; depth < 4 && cursor != null; depth++, cursor = cursor.parent)
                if (cursor.GetComponent(FishType) != null) return true;

            try
            {
                return drop.gameObject.GetComponentInChildren(FishType, true) != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
