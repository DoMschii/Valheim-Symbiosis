using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal readonly struct GardenHarvestTargetResult
    {
        internal readonly Pickable Target;
        internal readonly Vector3 Position;
        internal readonly int Candidates;
        internal readonly int PhysicsHits;
        internal readonly bool BufferFull;
        internal readonly int PickablesResolved;
        internal readonly int SmartMatches;

        internal GardenHarvestTargetResult(Pickable target, Vector3 position, int candidates, int physicsHits, bool bufferFull, int pickablesResolved, int smartMatches)
        {
            Target = target;
            Position = position;
            Candidates = candidates;
            PhysicsHits = physicsHits;
            BufferFull = bufferFull;
            PickablesResolved = pickablesResolved;
            SmartMatches = smartMatches;
        }
    }

    /// <summary>
    /// Mature harvest search backed by WorkerWorldIndex. All loaded Pickables register once during
    /// Awake, including wild berry bushes, so Gatherers no longer need a 1024-collider sphere scan
    /// or a ZNetScene-wide fallback just to find a harvestable plant.
    /// </summary>
    internal static class GardenHarvestTargetFinder
    {
        internal const string TargetKind = "garden_harvest";

        internal static int LastPhysicsHits { get; private set; }
        internal static int LastPickablesResolved { get; private set; }
        internal static int LastSmartMatches { get; private set; }
        internal static int LastUsableCandidates { get; private set; }
        internal static string LastPickableSample { get; private set; } = "none";
        internal static int LastSceneViews { get; private set; }
        internal static int LastSceneRegistered { get; private set; }
        internal static int LastScenePickables { get; private set; }
        internal static int LastSceneUsable { get; private set; }
        internal static string LastSceneStatus { get; private set; } = "world-index";

        internal static GardenHarvestTargetResult FindNearest(Vector3 center, float radius, Vector3 workerPosition, Func<Pickable, bool> acceptTarget = null)
        {
            GardenWorkRuntime.EnsureReady();

            float safeRadius = Mathf.Max(5f, radius);
            float bestSqr = float.MaxValue;
            Pickable best = null;
            int candidates = 0;
            int pickablesResolved = 0;
            int smartMatches = 0;
            var sampleNames = new List<string>(8);

            Action<Pickable> inspect = pickable =>
            {
                if (pickable == null) return;
                pickablesResolved++;
                AddSample(sampleNames, pickable);
                bool workersCrop = GardenWorkRuntime.IsHarvestableCropPrefab(GetIdentityName(pickable));
                bool wildGatherable = IsWildGatherable(pickable);
                bool smart = !workersCrop && !wildGatherable && SmartReplantBridge.IsSmartReplantHarvestable(pickable);
                if (smart) smartMatches++;
                if (!IsUsable(pickable, center, safeRadius, smart, workersCrop, wildGatherable)) return;
                if (acceptTarget != null && !acceptTarget(pickable)) return;

                candidates++;
                float sqr = (pickable.transform.position - workerPosition).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = pickable;
            };

            int scanned = WorkerWorldIndex.ForEachPickable(center, safeRadius, inspect);

            // F12 used to appear to "wake up" Harvesters because its developer rebuild repopulated
            // Pickables from the currently loaded zone. Do the same thing automatically, but only
            // locally and only after the normal indexed search found no usable harvest target.
            int repaired = 0;
            if (candidates == 0)
            {
                repaired = WorkerWorldIndex.RepairLoadedPickablesNear(center, safeRadius);
                if (repaired > 0)
                {
                    bestSqr = float.MaxValue;
                    best = null;
                    candidates = 0;
                    pickablesResolved = 0;
                    smartMatches = 0;
                    sampleNames.Clear();
                    scanned = WorkerWorldIndex.ForEachPickable(center, safeRadius, inspect);
                }
            }

            LastPhysicsHits = scanned;
            LastPickablesResolved = pickablesResolved;
            LastSmartMatches = smartMatches;
            LastUsableCandidates = candidates;
            LastPickableSample = sampleNames.Count == 0 ? "none" : string.Join(",", sampleNames.ToArray());
            LastSceneViews = 0;
            LastSceneRegistered = WorkerWorldIndex.PickableCount;
            LastScenePickables = scanned;
            LastSceneUsable = candidates;
            LastSceneStatus = repaired > 0
                ? $"world-index:auto-repair+{repaired}:{scanned}/{WorkerWorldIndex.PickableCount}"
                : $"world-index:{scanned}/{WorkerWorldIndex.PickableCount}";

            return new GardenHarvestTargetResult(best, best != null ? best.transform.position : default, candidates, scanned, false, pickablesResolved, smartMatches);
        }

        internal static Pickable FindLoadedHarvestableNear(Vector3 center, float radius, Vector3 workerPosition, Func<Pickable, bool> acceptTarget = null)
        {
            float safeRadius = Mathf.Max(2f, radius);
            float bestSqr = float.MaxValue;
            Pickable best = null;
            WorkerWorldIndex.ForEachPickable(center, safeRadius, pickable =>
            {
                if (!IsGatherable(pickable)) return;
                if (!IsUsable(pickable, center, safeRadius)) return;
                if (acceptTarget != null && !acceptTarget(pickable)) return;
                float sqr = (pickable.transform.position - workerPosition).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = pickable;
            });
            return best;
        }

        internal static Pickable ResolvePickable(Collider collider)
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

        internal static bool IsUsable(Pickable pickable, Vector3 center, float radius)
        {
            bool workersCrop = GardenWorkRuntime.IsHarvestableCropPrefab(GetIdentityName(pickable));
            bool wildGatherable = IsWildGatherable(pickable);
            bool smartReplant = !workersCrop && !wildGatherable && SmartReplantBridge.IsSmartReplantHarvestable(pickable);
            return IsUsable(pickable, center, radius, smartReplant, workersCrop, wildGatherable);
        }

        private static bool IsUsable(Pickable pickable, Vector3 center, float radius, bool smartReplant, bool workersCrop, bool wildGatherable)
        {
            if (pickable == null || pickable.gameObject == null || !pickable.gameObject.activeInHierarchy) return false;
            if (!workersCrop && !smartReplant && !wildGatherable) return false;

            try
            {
                if (pickable.GetPicked()) return false;
                if (!pickable.CanBePicked()) return false;
            }
            catch { }

            Vector3 position = pickable.transform.position;
            float safeRadius = Mathf.Max(5f, radius);
            if ((position - center).sqrMagnitude > safeRadius * safeRadius) return false;

            ZNetView nview = SmartReplantBridge.FindNetworkView(pickable);
            if (nview == null) nview = pickable.GetComponent<ZNetView>() ?? pickable.GetComponentInParent<ZNetView>();
            return nview != null && nview.IsValid() && nview.GetZDO() != null;
        }

        internal static bool IsGatherable(Pickable pickable)
        {
            if (pickable == null) return false;
            return GardenWorkRuntime.IsHarvestableCropPrefab(GetIdentityName(pickable)) ||
                   SmartReplantBridge.IsSmartReplantHarvestable(pickable) ||
                   IsWildGatherable(pickable);
        }

        internal static bool IsWildGatherable(Pickable pickable)
        {
            if (pickable == null || pickable.gameObject == null) return false;

            // Workers WildGrowth prefabs are persistent Pickables. Detect the Workers-owned
            // VW_Flora_* identity before falling back to vanilla berry-name heuristics. This is
            // important for Blueberries (plural): "VW_Flora_Blueberries" does not contain the
            // literal singular word "Blueberry", so the old check misclassified a harvested
            // Workers blueberry bush as a destructive crop and reactivated its planting slot.
            Transform current = pickable.transform;
            for (int depth = 0; current != null && depth < 6; depth++, current = current.parent)
            {
                string candidate = NormalizePrefabName(current.gameObject.name);
                if (WorkersWildFloraRuntime.IsWorkersWildFloraName(candidate) || LooksLikeWildBerryName(candidate)) return true;
            }

            ZNetView nview = SmartReplantBridge.FindNetworkView(pickable);
            if (nview != null)
            {
                string networkName = NormalizePrefabName(nview.gameObject.name);
                if (WorkersWildFloraRuntime.IsWorkersWildFloraName(networkName) || LooksLikeWildBerryName(networkName)) return true;
            }

            Transform root = pickable.transform.root;
            if (root == null) return false;
            string rootName = NormalizePrefabName(root.gameObject.name);
            return WorkersWildFloraRuntime.IsWorkersWildFloraName(rootName) || LooksLikeWildBerryName(rootName);
        }

        internal static string GetIdentityName(Pickable pickable)
        {
            if (pickable == null || pickable.gameObject == null) return string.Empty;

            // Prefer the canonical Workers WildGrowth root identity. Child Pickables on cloned
            // vanilla bushes can still carry names such as BlueberryBush; returning that child
            // name used to hide the fact that this is a persistent VW_Flora_* object.
            Transform probe = pickable.transform;
            for (int depth = 0; probe != null && depth < 6; depth++, probe = probe.parent)
            {
                string candidate = NormalizePrefabName(probe.gameObject.name);
                if (WorkersWildFloraRuntime.IsWorkersWildFloraName(candidate)) return candidate;
            }

            ZNetView workersView = SmartReplantBridge.FindNetworkView(pickable);
            if (workersView != null)
            {
                string networkName = NormalizePrefabName(workersView.gameObject.name);
                if (WorkersWildFloraRuntime.IsWorkersWildFloraName(networkName)) return networkName;
            }

            string own = NormalizePrefabName(pickable.gameObject.name);
            if (LooksLikeWildBerryName(own) || GardenWorkRuntime.IsHarvestableCropPrefab(own)) return own;

            Transform current = pickable.transform.parent;
            for (int depth = 0; current != null && depth < 6; depth++, current = current.parent)
            {
                string candidate = NormalizePrefabName(current.gameObject.name);
                if (LooksLikeWildBerryName(candidate) || GardenWorkRuntime.IsHarvestableCropPrefab(candidate)) return candidate;
            }

            if (workersView != null)
            {
                string networkName = NormalizePrefabName(workersView.gameObject.name);
                if (!string.IsNullOrEmpty(networkName)) return networkName;
            }
            return own;
        }

        private static bool LooksLikeWildBerryName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("Raspberry", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Blueberr", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Cloudberry", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AddSample(List<string> sample, Pickable pickable)
        {
            if (sample == null || pickable == null || sample.Count >= 8) return;
            string name = GetIdentityName(pickable);
            if (!string.IsNullOrEmpty(name) && !sample.Contains(name)) sample.Add(name);
        }

        private static string NormalizePrefabName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return string.Empty;
            int clone = objectName.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone >= 0) objectName = objectName.Substring(0, clone);
            return objectName.Trim();
        }
    }
}
