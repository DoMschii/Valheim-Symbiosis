using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal static class GardenTargetFinder
    {
        internal const string PlantTargetKind = "garden_plant";

        private static readonly RaycastHit[] GroundHits = new RaycastHit[32];
        private static readonly Collider[] ClearanceBuffer = new Collider[96];
        private static readonly HashSet<int> Seen = new HashSet<int>();
        private static readonly int TerrainLayer = LayerMask.NameToLayer("terrain");
        private const string WildGrowthSlotIdKey = "vw_garden_slot_id";
        private const string WildGrowthFlagIdKey = "vw_garden_flag_id";

        internal static bool TryFindPlantingPoint(Vector3 workCenter, float workRadius, Vector3 workerPosition, float spacing, out Vector3 point)
        {
            point = default;
            float outer = Mathf.Max(4f, workRadius - 1.5f);
            float inner = Mathf.Min(3f, Mathf.Max(1f, outer * 0.12f));

            for (int attempt = 0; attempt < 6; attempt++)
            {
                Vector2 dir = UnityEngine.Random.insideUnitCircle;
                if (dir.sqrMagnitude < 0.02f) continue;
                dir.Normalize();
                float distance = UnityEngine.Random.Range(inner, outer);
                Vector3 candidate = workCenter + new Vector3(dir.x * distance, 0f, dir.y * distance);

                if (!TrySnapToTerrain(candidate, out Vector3 ground, out Vector3 normal)) continue;
                if (normal.y < 0.88f) continue;
                if ((ground - workCenter).sqrMagnitude > workRadius * workRadius) continue;
                if (!IsPlantingPointStillClear(ground, spacing)) continue;

                point = ground;
                return true;
            }

            return false;
        }

        internal static bool IsPlantingPointStillClear(Vector3 point, float spacing)
        {
            float radius = Mathf.Max(0.45f, spacing * 0.5f);
            int hits = Physics.OverlapSphereNonAlloc(point, radius, ClearanceBuffer, ~0, QueryTriggerInteraction.Collide);
            Seen.Clear();
            for (int i = 0; i < hits; i++)
            {
                Collider collider = ClearanceBuffer[i];
                ClearanceBuffer[i] = null;
                if (collider == null) continue;

                GameObject root = collider.transform.root != null ? collider.transform.root.gameObject : collider.gameObject;
                if (!Seen.Add(root.GetInstanceID())) continue;

                if (collider.GetComponentInParent<Plant>() != null ||
                    collider.GetComponentInParent<TreeBase>() != null ||
                    IsPersistentWildPickable(collider.GetComponentInParent<Pickable>()))
                {
                    Seen.Clear();
                    return false;
                }
            }
            Seen.Clear();
            return true;
        }

        /// <summary>
        /// Returns true only when the already occupied planned point contains the exact plant/flora
        /// prefab that this Worker was supposed to place. This is a bounded interaction-time check,
        /// not an AI scan, and lets persistent plans self-heal after legacy/manual placement.
        /// </summary>
        internal static bool HasExpectedPlantAt(Vector3 point, float spacing, string expectedPrefab)
        {
            string expected = NormalizePrefabName(expectedPrefab);
            if (string.IsNullOrEmpty(expected)) return false;

            float radius = Mathf.Clamp(Mathf.Max(0.32f, spacing * 0.35f), 0.32f, 0.75f);
            int hits = Physics.OverlapSphereNonAlloc(point, radius, ClearanceBuffer, ~0, QueryTriggerInteraction.Collide);
            Seen.Clear();
            for (int i = 0; i < hits; i++)
            {
                Collider collider = ClearanceBuffer[i];
                ClearanceBuffer[i] = null;
                if (collider == null) continue;

                Transform rootTransform = collider.transform.root != null ? collider.transform.root : collider.transform;
                GameObject root = rootTransform.gameObject;
                if (!Seen.Add(root.GetInstanceID())) continue;

                Plant plant = collider.GetComponentInParent<Plant>();
                Pickable pickable = collider.GetComponentInParent<Pickable>();
                if (plant == null && pickable == null) continue;

                ZNetView nview = collider.GetComponentInParent<ZNetView>();
                if (nview == null && plant != null) nview = plant.GetComponentInParent<ZNetView>();
                if (nview == null && pickable != null) nview = pickable.GetComponentInParent<ZNetView>();

                // v0.5.326: a Cultivator/SmartReplant placement preview can be a full clone of
                // VW_Flora_* and therefore carries Pickable + collider + the expected prefab name.
                // It is NOT a planted world object, though: placement previews have no authoritative
                // ZDO. Treat only a live network object as proof that a persistent slot is fulfilled.
                // Otherwise the gardener sees the player's preview sitting on the freshly planned
                // point, completes the slot immediately, and the plan ghost vanishes as soon as the
                // player moves the preview away.
                if (!HasAuthoritativeNetworkState(nview)) continue;

                if (MatchesExpected(root != null ? root.name : string.Empty, expected) ||
                    MatchesExpected(nview != null && nview.gameObject != null ? nview.gameObject.name : string.Empty, expected) ||
                    MatchesExpected(plant != null && plant.gameObject != null ? plant.gameObject.name : string.Empty, expected) ||
                    MatchesExpected(pickable != null && pickable.gameObject != null ? pickable.gameObject.name : string.Empty, expected))
                {
                    Seen.Clear();
                    return true;
                }
            }
            Seen.Clear();
            return false;
        }

        /// <summary>
        /// v0.5.329: WildGrowth completion is tied to the exact persistent plan slot.
        /// A merely nearby/matching Pickable is no longer enough, because old broken builds can
        /// leave a valid but invisible/unconfirmed VW_Flora_* ZDO at the same point. Those legacy
        /// objects used to make the scheduler complete the ghost about one second after world load.
        /// </summary>
        internal static bool HasConfirmedWildGrowthPlantAt(Vector3 point, float spacing, string expectedPrefab, int slotId)
        {
            if (slotId <= 0) return false;
            string expected = NormalizePrefabName(expectedPrefab);
            if (string.IsNullOrEmpty(expected)) return false;

            float radius = Mathf.Clamp(Mathf.Max(0.32f, spacing * 0.35f), 0.32f, 0.75f);
            int hits = Physics.OverlapSphereNonAlloc(point, radius, ClearanceBuffer, ~0, QueryTriggerInteraction.Collide);
            Seen.Clear();
            for (int i = 0; i < hits; i++)
            {
                Collider collider = ClearanceBuffer[i];
                ClearanceBuffer[i] = null;
                if (collider == null) continue;
                Transform rootTransform = collider.transform.root != null ? collider.transform.root : collider.transform;
                GameObject root = rootTransform.gameObject;
                if (!Seen.Add(root.GetInstanceID())) continue;

                Plant plant = collider.GetComponentInParent<Plant>();
                Pickable pickable = collider.GetComponentInParent<Pickable>();
                if (plant == null && pickable == null) continue;

                ZNetView nview = collider.GetComponentInParent<ZNetView>();
                if (nview == null && plant != null) nview = plant.GetComponentInParent<ZNetView>();
                if (nview == null && pickable != null) nview = pickable.GetComponentInParent<ZNetView>();
                if (!HasAuthoritativeNetworkState(nview)) continue;

                if (!MatchesExpected(root != null ? root.name : string.Empty, expected) &&
                    !MatchesExpected(nview != null && nview.gameObject != null ? nview.gameObject.name : string.Empty, expected) &&
                    !MatchesExpected(plant != null && plant.gameObject != null ? plant.gameObject.name : string.Empty, expected) &&
                    !MatchesExpected(pickable != null && pickable.gameObject != null ? pickable.gameObject.name : string.Empty, expected))
                    continue;

                ZDO zdo = null;
                try { zdo = nview.GetZDO(); } catch { }
                if (zdo != null && zdo.GetInt(WildGrowthSlotIdKey, 0) == slotId)
                {
                    Seen.Clear();
                    return true;
                }
            }
            Seen.Clear();
            return false;
        }

        /// <summary>
        /// Legacy bridge used only when the worker physically reaches a blocked WildGrowth slot.
        /// If an older build already left the correct networked bush there, adopt it into the new
        /// slot-owned lifecycle instead of deleting/replanting it. This is intentionally NOT used
        /// by FindJob, so reload cannot make plan ghosts disappear merely because an old object exists.
        /// </summary>
        internal static bool TryAdoptLegacyWildGrowthPlantAt(Vector3 point, float spacing, string expectedPrefab, int slotId, string workAreaId)
        {
            if (slotId <= 0) return false;
            string expected = NormalizePrefabName(expectedPrefab);
            if (string.IsNullOrEmpty(expected)) return false;

            float radius = Mathf.Clamp(Mathf.Max(0.32f, spacing * 0.35f), 0.32f, 0.75f);
            int hits = Physics.OverlapSphereNonAlloc(point, radius, ClearanceBuffer, ~0, QueryTriggerInteraction.Collide);
            Seen.Clear();
            for (int i = 0; i < hits; i++)
            {
                Collider collider = ClearanceBuffer[i];
                ClearanceBuffer[i] = null;
                if (collider == null) continue;
                Transform rootTransform = collider.transform.root != null ? collider.transform.root : collider.transform;
                GameObject root = rootTransform.gameObject;
                if (!Seen.Add(root.GetInstanceID())) continue;

                Pickable pickable = collider.GetComponentInParent<Pickable>();
                Plant plant = collider.GetComponentInParent<Plant>();
                if (pickable == null && plant == null) continue;
                ZNetView nview = collider.GetComponentInParent<ZNetView>();
                if (nview == null && pickable != null) nview = pickable.GetComponentInParent<ZNetView>();
                if (nview == null && plant != null) nview = plant.GetComponentInParent<ZNetView>();
                if (!HasAuthoritativeNetworkState(nview)) continue;

                if (!MatchesExpected(root != null ? root.name : string.Empty, expected) &&
                    !MatchesExpected(nview != null && nview.gameObject != null ? nview.gameObject.name : string.Empty, expected) &&
                    !MatchesExpected(pickable != null && pickable.gameObject != null ? pickable.gameObject.name : string.Empty, expected) &&
                    !MatchesExpected(plant != null && plant.gameObject != null ? plant.gameObject.name : string.Empty, expected))
                    continue;

                ZDO zdo = null;
                try { zdo = nview.GetZDO(); } catch { }
                if (zdo == null) continue;
                int existingSlot = zdo.GetInt(WildGrowthSlotIdKey, 0);
                if (existingSlot > 0 && existingSlot != slotId) continue;

                try
                {
                    zdo.Set(WildGrowthSlotIdKey, slotId);
                    zdo.Set(WildGrowthFlagIdKey, workAreaId ?? string.Empty);
                    zdo.Set("vw_wildgrowth_managed", true);
                    if (pickable != null) WorkersWildFloraRuntime.EnsurePersistentBushVisual(pickable);
                    if (root != null) WorkersWildFloraRuntime.PrepareSpawnedInstance(root);
                }
                catch { }
                Seen.Clear();
                return true;
            }
            Seen.Clear();
            return false;
        }

        /// <summary>
        /// v0.5.386: slot-identity lookup through the existing loaded Pickable index. Unlike the
        /// collider/Physics test, this still sees a picked WildGrowth object while its renderers or
        /// collider are disabled during vanilla respawn. It runs only when a Gardener is about to
        /// spend planting material, so it cannot become a background/per-frame scan.
        /// </summary>
        internal static bool HasManagedWildGrowthSlotLoaded(Vector3 point, float spacing, int slotId, string workAreaId, out Pickable found)
        {
            found = null;
            if (slotId <= 0 || string.IsNullOrEmpty(workAreaId)) return false;
            float radius = Mathf.Clamp(Mathf.Max(1.25f, spacing * 1.75f), 1.25f, 3f);
            Pickable match = null;
            WorkerWorldIndex.ForEachPickable(point, radius, pickable =>
            {
                if (match != null || pickable == null) return;
                ZNetView nview = SmartReplantBridge.FindNetworkView(pickable);
                if (nview == null) nview = pickable.GetComponent<ZNetView>() ?? pickable.GetComponentInParent<ZNetView>();
                if (!TryReadWildGrowthMarker(nview, out int existingSlot, out string existingArea)) return;
                if (existingSlot != slotId || !string.Equals(existingArea, workAreaId, StringComparison.Ordinal)) return;
                match = pickable;
            });
            found = match;
            return found != null;
        }

        internal static bool TryReadWildGrowthMarker(ZNetView nview, out int slotId, out string workAreaId)
        {
            slotId = 0;
            workAreaId = string.Empty;
            if (nview == null || !HasAuthoritativeNetworkState(nview)) return false;
            try
            {
                ZDO zdo = nview.GetZDO();
                if (zdo == null || !zdo.GetBool("vw_wildgrowth_managed", false)) return false;
                slotId = zdo.GetInt(WildGrowthSlotIdKey, 0);
                workAreaId = zdo.GetString(WildGrowthFlagIdKey, string.Empty);
                return slotId > 0 && !string.IsNullOrEmpty(workAreaId);
            }
            catch
            {
                slotId = 0;
                workAreaId = string.Empty;
                return false;
            }
        }

        internal static void MarkWildGrowthPlantForSlot(ZNetView nview, int slotId, string workAreaId)
        {
            if (nview == null || slotId <= 0 || !HasAuthoritativeNetworkState(nview)) return;
            try
            {
                ZDO zdo = nview.GetZDO();
                if (zdo == null) return;
                string areaId = workAreaId ?? string.Empty;
                zdo.Set(WildGrowthSlotIdKey, slotId);
                zdo.Set(WildGrowthFlagIdKey, areaId);
                zdo.Set("vw_wildgrowth_managed", true);

                // v0.5.383: the networked VW_Flora_* object itself is now the authoritative
                // completion proof. Keeping the plan active for one more scheduler pass allowed an
                // equal-priority Harvest job to pick a freshly placed mushroom before Garden ran
                // again; the still-active slot then reappeared as a ghost and charged 3 mushrooms
                // a second time. Complete the permanent slot immediately after its managed ZDO
                // marker exists. The physical Pickable remains in-world and uses vanilla respawn.
                GardenReplantPlanRuntime.ConfirmPersistentWildGrowthSlot(areaId, slotId);
            }
            catch { }
        }

        private static bool MatchesExpected(string objectName, string expected)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            string actual = NormalizePrefabName(objectName);
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return true;

            // Existing worlds can contain either a Workers VW_Flora_* clone or a native vanilla
            // Pickable created by v0.5.324. Treat both names as the same WildGrowth species so
            // upgrades neither duplicate bushes nor reactivate an already fulfilled ghost.
            if (WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(expected, out WorkersWildFloraRuntime.Definition expectedWild) &&
                WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(actual, out WorkersWildFloraRuntime.Definition actualWild))
                return string.Equals(expectedWild.PrefabName, actualWild.PrefabName, StringComparison.OrdinalIgnoreCase);

            return false;
        }

        private static bool IsPersistentWildPickable(Pickable pickable)
        {
            if (pickable == null) return false;

            // v0.5.326: ignore Cultivator/SmartReplant preview clones. They can contain the exact
            // same Pickable/collider hierarchy as a real VW_Flora_* bush but are not backed by a
            // live ZDO. A preview must neither satisfy a plan nor block the worker's planting point.
            ZNetView nview = pickable.GetComponent<ZNetView>() ?? pickable.GetComponentInParent<ZNetView>();
            if (!HasAuthoritativeNetworkState(nview)) return false;

            string identity = GardenHarvestTargetFinder.GetIdentityName(pickable);
            return WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(identity, out _);
        }

        private static bool HasAuthoritativeNetworkState(ZNetView nview)
        {
            if (nview == null || !nview.IsValid()) return false;
            try { return nview.GetZDO() != null; }
            catch { return false; }
        }

        private static string NormalizePrefabName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return string.Empty;
            int clone = objectName.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone >= 0) objectName = objectName.Substring(0, clone);
            return objectName.Trim();
        }

        internal static bool TrySnapToTerrain(Vector3 xzPoint, out Vector3 ground, out Vector3 normal)
        {
            ground = default;
            normal = Vector3.up;
            Vector3 origin = xzPoint + Vector3.up * 30f;
            int hits = Physics.RaycastNonAlloc(origin, Vector3.down, GroundHits, 70f, ~0, QueryTriggerInteraction.Ignore);
            bool found = false;
            float bestY = float.MinValue;

            for (int i = 0; i < hits; i++)
            {
                RaycastHit hit = GroundHits[i];
                GroundHits[i] = default;
                if (hit.collider == null || !LooksLikeTerrain(hit.collider) || hit.point.y <= bestY) continue;
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
    }
}
