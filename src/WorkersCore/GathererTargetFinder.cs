using System;
using UnityEngine;

namespace Valheim_Workers
{
    internal readonly struct GathererTargetResult
    {
        internal readonly ItemDrop Target;
        internal readonly string PrefabName;
        internal readonly Vector3 Position;
        internal readonly int Candidates;
        internal readonly int PhysicsHits;
        internal readonly bool BufferFull;

        internal GathererTargetResult(ItemDrop target, string prefabName, Vector3 position, int candidates, int physicsHits, bool bufferFull)
        {
            Target = target;
            PrefabName = prefabName ?? string.Empty;
            Position = position;
            Candidates = candidates;
            PhysicsHits = physicsHits;
            BufferFull = bufferFull;
        }
    }

    /// <summary>
    /// Loose resource search backed by WorkerWorldIndex. No broad OverlapSphere is needed, so
    /// dense bases no longer make a Gatherer query every floor, wall, roof and decoration collider.
    /// </summary>
    internal static class GathererTargetFinder
    {
        internal const string TargetKind = "ItemDrop";
        internal const float DepotRadius = 4f;

        internal static GathererTargetResult FindNearest(Vector3 center, float radius, Vector3 workerPosition)
        {
            float safeRadius = Mathf.Max(5f, radius);
            float bestSqr = float.MaxValue;
            ItemDrop best = null;
            string bestPrefab = string.Empty;
            int candidates = 0;

            int scanned = WorkerWorldIndex.ForEachItemDrop(center, safeRadius, drop =>
            {
                if (!IsEligible(drop)) return;
                Vector3 position = drop.transform.position;
                candidates++;
                float sqr = (position - workerPosition).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = drop;
                bestPrefab = GetCargoPrefabName(drop);
            });

            return new GathererTargetResult(best, bestPrefab, best != null ? best.transform.position : default, candidates, scanned, false);
        }


        /// <summary>
        /// Finds the nearest loose drop that can actually still be added to the Worker's
        /// persistent multi-stack cargo. This prevents a full-slot cargo from selecting an
        /// unrelated prefab that cannot fit while a compatible partial stack is still nearby.
        /// </summary>
        internal static GathererTargetResult FindNearestAddable(Character worker, ZDO zdo, Vector3 center, float radius, Vector3 workerPosition, Func<ItemDrop, bool> acceptTarget = null)
        {
            if (worker == null || zdo == null)
                return new GathererTargetResult(null, string.Empty, default, 0, 0, false);

            float safeRadius = Mathf.Max(5f, radius);
            float bestSqr = float.MaxValue;
            ItemDrop best = null;
            string bestPrefab = string.Empty;
            int candidates = 0;

            int scanned = WorkerWorldIndex.ForEachItemDrop(center, safeRadius, drop =>
            {
                if (!IsEligible(drop) || drop.m_itemData == null) return;
                if (acceptTarget != null && !acceptTarget(drop)) return;
                string prefab = GetCargoPrefabName(drop);
                int available = Mathf.Max(0, drop.m_itemData.m_stack);
                if (available <= 0 || WorkerCargoRuntime.GetMaxAddAmount(worker, zdo, prefab, available) <= 0) return;

                Vector3 position = drop.transform.position;
                candidates++;
                float sqr = (position - workerPosition).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = drop;
                bestPrefab = prefab;
            });

            return new GathererTargetResult(best, bestPrefab, best != null ? best.transform.position : default, candidates, scanned, false);
        }

        internal static GathererTargetResult FindNearestMatching(Vector3 center, float radius, Vector3 workerPosition, string wantedPrefab)
        {
            if (string.IsNullOrEmpty(wantedPrefab)) return new GathererTargetResult(null, string.Empty, default, 0, 0, false);
            float safeRadius = Mathf.Max(5f, radius);
            float bestSqr = float.MaxValue;
            ItemDrop best = null;
            int candidates = 0;

            int scanned = WorkerWorldIndex.ForEachItemDrop(center, safeRadius, drop =>
            {
                if (!IsEligible(drop)) return;
                string prefab = GetCargoPrefabName(drop);
                if (!string.Equals(prefab, wantedPrefab, StringComparison.OrdinalIgnoreCase)) return;
                Vector3 position = drop.transform.position;
                candidates++;
                float sqr = (position - workerPosition).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = drop;
            });

            return new GathererTargetResult(best, wantedPrefab, best != null ? best.transform.position : default, candidates, scanned, false);
        }

        internal static bool IsUsable(ItemDrop drop, Vector3 center, float radius)
        {
            if (drop == null || drop.gameObject == null || !drop.gameObject.activeInHierarchy) return false;
            if (!IsEligible(drop)) return false;
            float safeRadius = Mathf.Max(5f, radius);
            Vector3 position = drop.transform.position;
            if ((position - center).sqrMagnitude > safeRadius * safeRadius) return false;
            return true;
        }

        internal static bool IsEligible(ItemDrop drop)
        {
            if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) return false;
            if (drop.m_itemData.m_stack <= 0) return false;
            if (drop.m_itemData.m_shared.m_questItem) return false;
            if (LivestockFeedingRuntime.IsProtectedFeedDrop(drop)) return false;
            if (IsInWater(drop)) return false;

            string prefab = GetCargoPrefabName(drop);
            string worldPrefab = NormalizePrefabName(drop.gameObject.name);
            if (string.Equals(prefab, "AncientSeed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(worldPrefab, "AncientSeed", StringComparison.OrdinalIgnoreCase)) return false;

            // Most loose building/crafting resources are Material, but harvested food crops and
            // berries (Onion, Carrot, Turnip, Raspberry, etc.) use other ItemTypes in Valheim.
            // The old Material-only gate made Gatherers harvest a crop successfully and then
            // ignore the real ItemDrop lying on the ground. Keep the broad Material behavior,
            // but allow non-Material drops only when the live garden/harvest registry identifies
            // the prefab as a real crop/flora output. This avoids turning Gatherers into generic
            // loot vacuum cleaners for weapons, armor or arbitrary dropped food.
            if (drop.m_itemData.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Material &&
                !GardenWorkRuntime.IsGatherableLooseItemPrefab(prefab) &&
                !GardenWorkRuntime.IsGatherableLooseItemPrefab(worldPrefab))
                return false;

            return true;
        }



        internal static bool IsInWater(ItemDrop drop)
        {
            if (drop == null || drop.gameObject == null || ZoneSystem.instance == null) return false;

            Vector3 position = drop.transform.position;
            float waterLevel = ZoneSystem.instance.m_waterLevel;
            float groundHeight = ZoneSystem.instance.GetGroundHeight(position);

            // Only reject a drop when there is actual water depth at its X/Z position and the
            // item itself is at/near the water surface. This avoids rejecting items lying on a
            // low beach or on a dock above the water.
            bool waterBelowItem = groundHeight < waterLevel - 0.20f;
            bool itemTouchesWater = position.y <= waterLevel + 0.55f;
            return waterBelowItem && itemTouchesWater;
        }

        /// <summary>
        /// Returns the canonical inventory/item prefab identity for a loose world drop.
        /// SmartReplant can spawn wrapper/world GameObjects whose GameObject.name is not the
        /// actual item prefab stored in ItemData. Cargo must use the real drop prefab identity,
        /// otherwise ObjectDB/ZNetScene cannot reconstruct the item when depositing to a chest.
        /// </summary>
        internal static string GetCargoPrefabName(ItemDrop drop)
        {
            if (drop?.m_itemData != null)
            {
                if (drop.m_itemData.m_dropPrefab != null)
                {
                    string direct = NormalizePrefabName(drop.m_itemData.m_dropPrefab.name);
                    if (!string.IsNullOrEmpty(direct)) return direct;
                }

                string resolved = WorkerStorageRuntime.GetPrefabName(drop.m_itemData);
                if (!string.IsNullOrEmpty(resolved)) return NormalizePrefabName(resolved);
            }

            return drop != null && drop.gameObject != null
                ? NormalizePrefabName(drop.gameObject.name)
                : string.Empty;
        }

        internal static string NormalizePrefabName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return string.Empty;
            int clone = objectName.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone >= 0) objectName = objectName.Substring(0, clone);
            return objectName.Trim();
        }
    }
}
