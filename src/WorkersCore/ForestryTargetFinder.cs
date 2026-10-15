using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal readonly struct ForestrySeedTargetResult
    {
        internal readonly ItemDrop Target;
        internal readonly string PrefabName;
        internal readonly Vector3 Position;
        internal readonly int Candidates;

        internal ForestrySeedTargetResult(ItemDrop target, string prefabName, Vector3 position, int candidates)
        {
            Target = target;
            PrefabName = prefabName ?? string.Empty;
            Position = position;
            Candidates = candidates;
        }
    }

    /// <summary>
    /// Forestry intentionally uses only real tree seed/cone ItemDrops staged at the work-flag depot.
    /// The Shaman does not create stock and does not scan the whole world for supplies.
    /// Planting points are searched locally inside the flag radius, snapped to actual terrain and
    /// rejected when trees/plants/pieces are too close.
    /// </summary>
    internal static class ForestryTargetFinder
    {
        internal const string SeedTargetKind = "ForestrySeed";
        internal const string PlantTargetKind = "ForestryPlant";
        internal const float SupplyRadius = 6.0f;
        internal const float MinTreeSpacing = 5.0f;
        private const float TreeDebrisClearance = 0.75f;
        private const float PiecePointClearance = 0.45f;
        private const float PlannedLivingClearance = 0.55f;
        private const float PlannedProbeRadius = 0.95f;

        internal static string LastPhysicalBlocker { get; private set; } = "none";

        private static readonly Collider[] SeedBuffer = new Collider[128];
        private static readonly Collider[] ClearanceBuffer = new Collider[128];
        private static readonly RaycastHit[] GroundHits = new RaycastHit[32];
        private static readonly HashSet<int> Seen = new HashSet<int>();
        private static readonly int TerrainLayer = LayerMask.NameToLayer("terrain");
        private static readonly int GhostLayer = LayerMask.NameToLayer("ghost");

        internal static ForestrySeedTargetResult FindNearestSeed(Vector3 workCenter, Vector3 workerPosition)
        {
            ItemDrop best = null;
            string bestPrefab = string.Empty;
            float bestSqr = float.MaxValue;
            int candidates = 0;

            WorkerWorldIndex.ForEachItemDrop(workCenter, SupplyRadius, drop =>
            {
                if (!IsSupportedSeed(drop)) return;
                candidates++;
                float sqr = (drop.transform.position - workerPosition).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = drop;
                bestPrefab = NormalizePrefabName(drop.gameObject.name);
            });

            return new ForestrySeedTargetResult(
                best,
                bestPrefab,
                best != null ? best.transform.position : default,
                candidates);
        }

        internal static bool IsUsableSeed(ItemDrop drop, Vector3 workCenter)
        {
            if (!IsSupportedSeed(drop)) return false;
            return (drop.transform.position - workCenter).sqrMagnitude <= SupplyRadius * SupplyRadius;
        }

        internal static bool IsSupportedSeed(ItemDrop drop)
        {
            if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) return false;
            if (drop.m_itemData.m_stack <= 0 || drop.m_itemData.m_shared.m_questItem) return false;
            return ForestryWorkRuntime.TryGetSaplingPrefab(NormalizePrefabName(drop.gameObject.name), out _);
        }

        internal static bool TryFindPlantingPoint(Vector3 workCenter, float workRadius, Vector3 workerPosition, out Vector3 point)
        {
            point = default;
            float outer = Mathf.Max(7f, workRadius - 2f);
            float inner = Mathf.Min(6f, Mathf.Max(3f, outer * 0.25f));

            // A few local attempts per 2s Worker tick are cheap and avoid global object scans.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                Vector2 dir = UnityEngine.Random.insideUnitCircle;
                if (dir.sqrMagnitude < 0.05f) continue;
                dir.Normalize();
                float distance = UnityEngine.Random.Range(inner, outer);
                Vector3 candidate = workCenter + new Vector3(dir.x * distance, 0f, dir.y * distance);

                if (!TrySnapToTerrain(candidate, out Vector3 ground, out Vector3 normal)) continue;
                if (normal.y < 0.82f) continue; // avoid steep slopes/cliffs
                if ((ground - workCenter).sqrMagnitude > workRadius * workRadius) continue;
                if (!IsPlantingPointStillClear(ground)) continue;

                point = ground;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Direct occupancy check for an explicit player-planned forestry slot. The player plan is
        /// authoritative, so this deliberately does NOT enforce the broad 5 m forestry spacing
        /// against every surrounding tree. It only rejects objects that physically occupy the
        /// planting point. Rendering/placement ghosts are ignored completely.
        /// </summary>
        internal static bool HasLivingPlannedOccupant(Vector3 point)
        {
            // Persistent forestry plans represent permanent planting positions. This probe is
            // intentionally local to the exact slot and only answers whether a living Plant/TreeBase
            // still fulfils that position. Logs, stumps and nearby building pieces do not count as
            // fulfilment; they may temporarily block replanting, but the slot itself must already be
            // active again once the living tree is gone.
            int hits = Physics.OverlapSphereNonAlloc(
                point + Vector3.up * 0.45f,
                1.35f,
                ClearanceBuffer,
                ~0,
                QueryTriggerInteraction.Collide);

            Seen.Clear();
            for (int i = 0; i < hits; i++)
            {
                Collider collider = ClearanceBuffer[i];
                ClearanceBuffer[i] = null;
                if (collider == null || !collider.enabled) continue;
                if (GhostLayer >= 0 && collider.gameObject.layer == GhostLayer) continue;

                TreeBase tree = collider.GetComponentInParent<TreeBase>();
                Plant plant = collider.GetComponentInParent<Plant>();
                if (tree == null && plant == null) continue;

                int rootId = collider.transform.root != null
                    ? collider.transform.root.gameObject.GetInstanceID()
                    : collider.gameObject.GetInstanceID();
                if (!Seen.Add(rootId)) continue;

                if (HorizontalDistanceToCollider(collider, point) <= 0.95f)
                {
                    Seen.Clear();
                    return true;
                }
            }

            Seen.Clear();
            return false;
        }

        internal static bool IsPlannedSlotPhysicallyFree(Vector3 point)
        {
            // A manual forestry plan is authoritative. This is only a last-moment footprint check,
            // not a second spacing system. Most importantly, ignore trigger volumes: workbenches,
            // wards, stations and other Valheim pieces can expose large trigger colliders whose
            // ClosestPoint is the slot itself while the actual visible piece is many metres away.
            // v0.2.08 treated those trigger volumes as a physical obstruction, so every planned
            // slot inside a base could appear blocked.
            LastPhysicalBlocker = "none";
            int hits = Physics.OverlapSphereNonAlloc(
                point + Vector3.up * 0.35f,
                PlannedProbeRadius,
                ClearanceBuffer,
                ~0,
                QueryTriggerInteraction.Ignore);

            Seen.Clear();
            for (int i = 0; i < hits; i++)
            {
                Collider collider = ClearanceBuffer[i];
                ClearanceBuffer[i] = null;
                if (collider == null || !collider.enabled || collider.isTrigger) continue;
                if (GhostLayer >= 0 && collider.gameObject.layer == GhostLayer) continue;
                if (collider.GetComponentInParent<Character>() != null) continue;
                if (collider.GetComponentInParent<ItemDrop>() != null) continue;
                if (collider.GetComponentInParent<Pickable>() != null) continue;
                if (collider.GetComponentInParent<Heightmap>() != null) continue;

                int rootId = collider.transform.root != null
                    ? collider.transform.root.gameObject.GetInstanceID()
                    : collider.gameObject.GetInstanceID();
                if (!Seen.Add(rootId)) continue;

                float horizontalDistance = HorizontalDistanceToCollider(collider, point);
                TreeBase tree = collider.GetComponentInParent<TreeBase>();
                Plant plant = collider.GetComponentInParent<Plant>();
                if ((tree != null || plant != null) && horizontalDistance <= PlannedLivingClearance)
                {
                    LastPhysicalBlocker = $"living:{NormalizePrefabName(collider.transform.root != null ? collider.transform.root.name : collider.gameObject.name)}@{horizontalDistance:0.00}m";
                    Seen.Clear();
                    return false;
                }

                TreeLog log = collider.GetComponentInParent<TreeLog>();
                Destructible destructible = collider.GetComponentInParent<Destructible>();
                if ((log != null || TreeTargetFinder.IsTreeStump(destructible)) && horizontalDistance <= TreeDebrisClearance)
                {
                    LastPhysicalBlocker = $"debris:{NormalizePrefabName(collider.transform.root != null ? collider.transform.root.name : collider.gameObject.name)}@{horizontalDistance:0.00}m";
                    Seen.Clear();
                    return false;
                }

                if (collider.GetComponentInParent<Piece>() != null && horizontalDistance <= PiecePointClearance)
                {
                    LastPhysicalBlocker = $"piece:{NormalizePrefabName(collider.transform.root != null ? collider.transform.root.name : collider.gameObject.name)}@{horizontalDistance:0.00}m";
                    Seen.Clear();
                    return false;
                }
            }

            Seen.Clear();
            return true;
        }

        internal static bool IsPlantingPointStillClear(Vector3 point)
        {
            // Tree spacing and physical occupancy are different concepts. v0.2.06 used the full
            // 5 m tree-spacing radius for *every* Piece collider, which meant a nearby floor, wall,
            // WorkFlag or other build piece could invalidate an otherwise perfectly valid forestry
            // slot. Keep the wide radius only for living trees/plants. Logs/stumps and generic
            // building pieces only block when they are physically close to the actual planting point.
            int hits = Physics.OverlapSphereNonAlloc(point, MinTreeSpacing, ClearanceBuffer, ~0, QueryTriggerInteraction.Collide);
            Seen.Clear();
            for (int i = 0; i < hits; i++)
            {
                Collider collider = ClearanceBuffer[i];
                ClearanceBuffer[i] = null;
                if (collider == null) continue;

                TreeBase tree = collider.GetComponentInParent<TreeBase>();
                Plant plant = collider.GetComponentInParent<Plant>();
                if (tree != null || plant != null)
                {
                    Seen.Clear();
                    return false;
                }

                float horizontalDistance = HorizontalDistanceToCollider(collider, point);
                TreeLog log = collider.GetComponentInParent<TreeLog>();
                Destructible destructible = collider.GetComponentInParent<Destructible>();
                if ((log != null || TreeTargetFinder.IsTreeStump(destructible)) && horizontalDistance <= TreeDebrisClearance)
                {
                    Seen.Clear();
                    return false;
                }

                // Buildings should only invalidate the slot when the actual collider occupies the
                // planting point. A wall several metres away must not block the entire forestry area.
                if (collider.GetComponentInParent<Piece>() != null && horizontalDistance <= PiecePointClearance)
                {
                    Seen.Clear();
                    return false;
                }
            }
            Seen.Clear();
            return true;
        }

        private static float HorizontalDistanceToCollider(Collider collider, Vector3 point)
        {
            if (collider == null) return float.MaxValue;
            Vector3 closest;
            try { closest = ColliderGeometryRuntime.ClosestPointSafe(collider, point); }
            catch { closest = collider.transform.position; }
            Vector3 delta = closest - point;
            delta.y = 0f;
            return delta.magnitude;
        }

        internal static Vector3 GetPlannedSlotApproachPoint(Vector3 workerPosition, Vector3 slotPosition)
        {
            // Workers only need to get close enough to interact with the planned slot; they must not
            // pathfind into the exact planting pixel. Some valid tree slots sit on terrain points that
            // are awkward navmesh destinations even though standing 2-3 m beside them is perfectly
            // reachable. Keep this Forestry-local so the stable shared navigator remains untouched.
            Vector3 away = workerPosition - slotPosition;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) return slotPosition;

            Vector3 candidate = slotPosition + away.normalized * 2.35f;
            if (TrySnapToTerrain(candidate, out Vector3 ground, out _))
                return ground;
            return candidate;
        }

        private static bool TrySnapToTerrain(Vector3 xzPoint, out Vector3 ground, out Vector3 normal)
        {
            ground = default;
            normal = Vector3.up;
            Vector3 origin = xzPoint + Vector3.up * 40f;
            int hits = Physics.RaycastNonAlloc(origin, Vector3.down, GroundHits, 90f, ~0, QueryTriggerInteraction.Ignore);
            bool found = false;
            float bestY = float.MinValue;

            for (int i = 0; i < hits; i++)
            {
                RaycastHit hit = GroundHits[i];
                GroundHits[i] = default;
                if (hit.collider == null) continue;
                if (!LooksLikeTerrain(hit.collider)) continue;
                if (hit.point.y <= bestY) continue;

                bestY = hit.point.y;
                ground = hit.point + Vector3.up * 0.03f;
                normal = hit.normal;
                found = true;
            }

            return found;
        }

        private static bool LooksLikeTerrain(Collider collider)
        {
            if (collider == null) return false;
            if (TerrainLayer >= 0 && collider.gameObject.layer == TerrainLayer) return true;
            if (collider.GetComponentInParent<Heightmap>() != null) return true;
            string name = collider.gameObject.name ?? string.Empty;
            return name.IndexOf("terrain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("heightmap", StringComparison.OrdinalIgnoreCase) >= 0;
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
