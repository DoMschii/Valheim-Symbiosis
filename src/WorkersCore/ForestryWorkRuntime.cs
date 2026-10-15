using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Real-resource forestry loop for Greydwarf Shamans. Supported saplings are discovered from
    /// the live Cultivator PieceTable instead of relying on guessed prefab names. A valid forestry
    /// recipe is a Plant piece whose grown prefab becomes a TreeBase and which has exactly one
    /// positive item requirement (the seed/cone/acorn used by the worker).
    /// </summary>
    internal static class ForestryWorkRuntime
    {
        private sealed class ForestryRecipe
        {
            internal string SeedPrefab;
            internal string SaplingPrefab;
            internal GameObject SaplingObject;
            internal float Spacing;
        }

        internal const float CollectRadius = 3.2f;

        private static readonly FieldInfo PlantGrowTimeField = typeof(Plant).GetField(
            "m_growTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo PlantGrowTimeMaxField = typeof(Plant).GetField(
            "m_growTimeMax", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        internal static double EstimateGrowthSeconds(string saplingPrefab)
        {
            const double fallbackSeconds = 3600.0;
            try
            {
                GameObject prefab = ResolveSaplingPrefab(saplingPrefab);
                Plant plant = prefab != null ? prefab.GetComponent<Plant>() : null;
                if (plant == null) return fallbackSeconds;

                float min = ReadPositiveFloat(PlantGrowTimeField, plant);
                float max = ReadPositiveFloat(PlantGrowTimeMaxField, plant);
                float value = max > 0f ? max : min;
                if (value <= 0f) return fallbackSeconds;
                return Math.Max(300.0, Math.Min(14400.0, value));
            }
            catch
            {
                return fallbackSeconds;
            }
        }

        private static float ReadPositiveFloat(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return 0f;
            try
            {
                object value = field.GetValue(instance);
                if (value == null) return 0f;
                float result = Convert.ToSingle(value);
                return result > 0f ? result : 0f;
            }
            catch
            {
                return 0f;
            }
        }


        // These are only fallbacks for a very early startup where ObjectDB/PieceTable is not ready.
        // The live Cultivator table replaces/extends them as soon as it becomes available.
        private static readonly Dictionary<string, string> SeedToSapling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BeechSeeds"] = "Beech_Sapling",
            ["FirCone"] = "FirTree_Sapling",
            ["PineCone"] = "PineTree_Sapling",
            ["BirchSeeds"] = "Birch_Sapling",
            ["Acorn"] = "Oak_Sapling",
            ["OakSeeds"] = "Oak_Sapling",
            ["FirConeFrost"] = "FirTree_big_Sapling"
        };

        private static readonly Dictionary<string, ForestryRecipe> BySeed =
            new Dictionary<string, ForestryRecipe>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ForestryRecipe> BySapling =
            new Dictionary<string, ForestryRecipe>(StringComparer.OrdinalIgnoreCase);

        private static ObjectDB _database;
        private static ZNetScene _scene;
        private static Action<string> _debugLog;
        private static float _nextRegistryRetryAt;
        private static bool _reportedRegistry;

        internal static void Initialize(Action<string> debugLog)
        {
            _debugLog = debugLog;
            ResetRegistry();
        }

        internal static void Shutdown()
        {
            ResetRegistry();
            _debugLog = null;
        }

        private static void ResetRegistry()
        {
            BySeed.Clear();
            BySapling.Clear();
            _database = null;
            _scene = null;
            _nextRegistryRetryAt = 0f;
            _reportedRegistry = false;
        }

        private static bool EnsureRegistryReady()
        {
            ObjectDB db = ObjectDB.instance;
            ZNetScene scene = ZNetScene.instance;
            if (db == null || scene == null) return false;

            if (_database == db && _scene == scene && BySapling.Count > 0)
                return true;

            if (Time.unscaledTime < _nextRegistryRetryAt && BySapling.Count > 0)
                return true;
            _nextRegistryRetryAt = Time.unscaledTime + 2f;

            GameObject cultivator = db.GetItemPrefab("Cultivator");
            PieceTable table = cultivator != null
                ? cultivator.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces
                : null;
            if (table == null || table.m_pieces == null || table.m_pieces.Count == 0)
                return BySapling.Count > 0;

            BySeed.Clear();
            BySapling.Clear();

            int discovered = 0;
            for (int i = 0; i < table.m_pieces.Count; i++)
            {
                GameObject go = table.m_pieces[i];
                if (go == null) continue;
                Piece piece = go.GetComponent<Piece>();
                Plant plant = go.GetComponent<Plant>();
                if (piece == null || plant == null || !piece.m_enabled || plant.m_grownPrefabs == null) continue;

                bool growsIntoTree = false;
                for (int g = 0; g < plant.m_grownPrefabs.Length; g++)
                {
                    GameObject grown = plant.m_grownPrefabs[g];
                    if (grown == null) continue;
                    if (grown.GetComponent<TreeBase>() != null || grown.GetComponentInChildren<TreeBase>(true) != null)
                    {
                        growsIntoTree = true;
                        break;
                    }
                }
                if (!growsIntoTree) continue;

                Piece.Requirement requirement = null;
                int positiveRequirements = 0;
                if (piece.m_resources != null)
                {
                    for (int r = 0; r < piece.m_resources.Length; r++)
                    {
                        Piece.Requirement candidate = piece.m_resources[r];
                        if (candidate == null || candidate.GetAmount(0) <= 0 || candidate.m_resItem == null) continue;
                        positiveRequirements++;
                        if (requirement == null) requirement = candidate;
                    }
                }

                // Current worker cargo model carries one item kind per forestry task. Vanilla tree
                // saplings use one positive requirement. Skip exotic multi-resource recipes safely.
                if (requirement == null || positiveRequirements != 1) continue;

                string seedPrefab = ForestryTargetFinder.NormalizePrefabName(requirement.m_resItem.gameObject.name);
                string saplingPrefab = ForestryTargetFinder.NormalizePrefabName(go.name);
                if (string.IsNullOrEmpty(seedPrefab) || string.IsNullOrEmpty(saplingPrefab)) continue;

                ForestryRecipe recipe = new ForestryRecipe
                {
                    SeedPrefab = seedPrefab,
                    SaplingPrefab = saplingPrefab,
                    SaplingObject = go,
                    Spacing = Mathf.Max(ForestryTargetFinder.MinTreeSpacing, plant.m_growRadius * 2f * 1.05f)
                };

                BySeed[seedPrefab] = recipe;
                BySapling[saplingPrefab] = recipe;
                SeedToSapling[seedPrefab] = saplingPrefab;
                discovered++;
            }

            _database = db;
            _scene = scene;
            if (!_reportedRegistry || discovered > 0)
            {
                _reportedRegistry = true;
                _debugLog?.Invoke($"Forestry registry ready: liveSaplings={discovered}, seeds={BySeed.Count}, saplings={BySapling.Count}");
            }
            return BySapling.Count > 0;
        }

        internal static bool TryGetSaplingPrefab(string seedPrefab, out string saplingPrefab)
        {
            saplingPrefab = string.Empty;
            EnsureRegistryReady();
            if (BySeed.TryGetValue(seedPrefab ?? string.Empty, out ForestryRecipe recipe))
            {
                saplingPrefab = recipe.SaplingPrefab;
                return !string.IsNullOrEmpty(saplingPrefab);
            }
            return SeedToSapling.TryGetValue(seedPrefab ?? string.Empty, out saplingPrefab);
        }

        internal static bool TryGetPlanningData(Piece piece, out string seedPrefab, out string saplingPrefab, out float spacing)
        {
            seedPrefab = string.Empty;
            saplingPrefab = string.Empty;
            spacing = ForestryTargetFinder.MinTreeSpacing;
            if (piece == null || piece.gameObject == null) return false;

            string selected = ForestryTargetFinder.NormalizePrefabName(piece.gameObject.name);
            EnsureRegistryReady();
            if (BySapling.TryGetValue(selected, out ForestryRecipe recipe))
            {
                seedPrefab = recipe.SeedPrefab;
                saplingPrefab = recipe.SaplingPrefab;
                spacing = recipe.Spacing;
                return true;
            }

            // Fail-safe direct inspection of the selected Piece. This is deliberately structural:
            // if it grows into a TreeBase and has one real resource requirement, it is a sapling,
            // even if another mod registered it after our cached Cultivator scan.
            Plant plant = piece.GetComponent<Plant>();
            if (plant == null || plant.m_grownPrefabs == null) return false;
            bool growsIntoTree = false;
            for (int i = 0; i < plant.m_grownPrefabs.Length; i++)
            {
                GameObject grown = plant.m_grownPrefabs[i];
                if (grown != null && (grown.GetComponent<TreeBase>() != null || grown.GetComponentInChildren<TreeBase>(true) != null))
                {
                    growsIntoTree = true;
                    break;
                }
            }
            if (!growsIntoTree) return false;

            Piece.Requirement req = null;
            int positives = 0;
            if (piece.m_resources != null)
            {
                for (int i = 0; i < piece.m_resources.Length; i++)
                {
                    Piece.Requirement candidate = piece.m_resources[i];
                    if (candidate == null || candidate.GetAmount(0) <= 0 || candidate.m_resItem == null) continue;
                    positives++;
                    if (req == null) req = candidate;
                }
            }
            if (req == null || positives != 1) return false;

            seedPrefab = ForestryTargetFinder.NormalizePrefabName(req.m_resItem.gameObject.name);
            saplingPrefab = selected;
            spacing = Mathf.Max(ForestryTargetFinder.MinTreeSpacing, plant.m_growRadius * 2f * 1.05f);
            if (string.IsNullOrEmpty(seedPrefab) || string.IsNullOrEmpty(saplingPrefab)) return false;

            ForestryRecipe discovered = new ForestryRecipe
            {
                SeedPrefab = seedPrefab,
                SaplingPrefab = saplingPrefab,
                SaplingObject = piece.gameObject,
                Spacing = spacing
            };
            BySeed[seedPrefab] = discovered;
            BySapling[saplingPrefab] = discovered;
            SeedToSapling[seedPrefab] = saplingPrefab;
            _debugLog?.Invoke($"Forestry late-discovered planning piece: {saplingPrefab} <- {seedPrefab}, spacing={spacing:0.0}m");
            return true;
        }

        internal static bool IsSaplingPrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            EnsureRegistryReady();
            if (BySapling.ContainsKey(ForestryTargetFinder.NormalizePrefabName(prefabName))) return true;
            foreach (string sapling in SeedToSapling.Values)
                if (string.Equals(sapling, ForestryTargetFinder.NormalizePrefabName(prefabName), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static GameObject ResolveSaplingPrefab(string saplingPrefab)
        {
            if (string.IsNullOrEmpty(saplingPrefab)) return null;
            string normalized = ForestryTargetFinder.NormalizePrefabName(saplingPrefab);
            EnsureRegistryReady();
            if (BySapling.TryGetValue(normalized, out ForestryRecipe recipe) && recipe.SaplingObject != null)
                return recipe.SaplingObject;

            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(normalized) : null;
            if (prefab != null) return prefab;
            GameObject cultivator = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Cultivator") : null;
            PieceTable table = cultivator != null ? cultivator.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
            if (table?.m_pieces != null)
                for (int i = 0; i < table.m_pieces.Count; i++)
                    if (table.m_pieces[i] != null && string.Equals(ForestryTargetFinder.NormalizePrefabName(table.m_pieces[i].name), normalized, StringComparison.OrdinalIgnoreCase))
                        return table.m_pieces[i];
            return null;
        }

        internal static bool HasPlantableSeedCargo(ZDO zdo)
        {
            return WorkerZdo.TryReadCargo(zdo, out string seedPrefab, out int amount) &&
                   amount > 0 &&
                   TryGetSaplingPrefab(seedPrefab, out _);
        }

        // Forestry progression is capability-gated, not a generic speed bonus. Starter foresters
        // may reforest common softwood; harder broadleaf trees require a higher PlantSeeds tier.
        // Unknown/modded tree recipes default to tier 1 so we do not silently break integrations.
        internal static int GetRequiredPlantTier(string seedPrefab, string saplingPrefab)
        {
            string seed = ForestryTargetFinder.NormalizePrefabName(seedPrefab ?? string.Empty);
            string sapling = ForestryTargetFinder.NormalizePrefabName(saplingPrefab ?? string.Empty);

            if (seed.IndexOf("Birch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sapling.IndexOf("Birch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                seed.IndexOf("Acorn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                seed.IndexOf("Oak", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sapling.IndexOf("Oak", StringComparison.OrdinalIgnoreCase) >= 0)
                return 2;

            return 1;
        }

        internal static bool CanWorkerPlant(Character worker, string seedPrefab, string saplingPrefab, out int requiredTier)
        {
            requiredTier = GetRequiredPlantTier(seedPrefab, saplingPrefab);

            // The Greydwarf Shaman is our dedicated gardener/forester. It may plant Birch even
            // though Birch wood itself remains a higher-tier lumberjack resource. Planting a
            // managed Birch slot must therefore not be blocked by the generic PlantSeeds tier-2
            // gate, otherwise the Shaman never reaches RequestSupply() and BirchSeeds are never
            // staged into the Base/AirFreight chain. Oak stays tier 2.
            if (worker != null && IsGreydwarfShaman(worker) && IsBirchRecipe(seedPrefab, saplingPrefab))
            {
                requiredTier = 1;
                return true;
            }

            // The central species->job matrix is authoritative for reusable jobs. A Fuling Shaman
            // has Gardener tier 3 and must therefore be able to execute tier-2 Forestry such as Oak.
            // Keep the older PlantSeeds capability as a compatible lower-level gate, but use the
            // higher of both declarations so a valid job tier cannot be accidentally downgraded by
            // a stale/partial capability profile.
            int effectiveTier = WorkerTierCatalog.GetEffectiveRoleCapabilityTier(
                worker, WorkerRole.Gardener, WorkerCapability.PlantSeeds);
            return effectiveTier >= requiredTier;
        }

        private static bool IsGreydwarfShaman(Character worker)
        {
            if (worker == null) return false;
            string name = ForestryTargetFinder.NormalizePrefabName(worker.gameObject != null ? worker.gameObject.name : string.Empty);
            return string.Equals(name, "Greydwarf_Shaman", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBirchRecipe(string seedPrefab, string saplingPrefab)
        {
            string seed = ForestryTargetFinder.NormalizePrefabName(seedPrefab ?? string.Empty);
            string sapling = ForestryTargetFinder.NormalizePrefabName(saplingPrefab ?? string.Empty);
            return seed.IndexOf("Birch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   sapling.IndexOf("Birch", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool TryCollectSeed(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            if (WorkerZdo.HasCargo(zdo))
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            string desiredPrefab = zdo.GetString(WorkerZdo.TargetName, string.Empty);
            if (!TryGetSaplingPrefab(desiredPrefab, out _))
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            ItemDrop drop = FindNearbyMatchingDrop(worker.transform.position, desiredPrefab);
            if (drop == null || !ForestryTargetFinder.IsSupportedSeed(drop))
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

            string seedPrefab = GathererTargetFinder.GetCargoPrefabName(drop);
            if (string.IsNullOrEmpty(seedPrefab))
                seedPrefab = ForestryTargetFinder.NormalizePrefabName(drop.gameObject.name);
            if (!drop.RemoveOne()) return true;

            WorkerZdo.SetCargo(zdo, seedPrefab, 1);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            _debugLog?.Invoke($"Forestry collected seed: worker={worker.gameObject.name}, seed={seedPrefab}");
            return true;
        }

        internal static bool TryPlant(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadForestryPlan(zdo, out WorkerForestryPlan plan) ||
                !ForestryPlanRuntime.PersistentSlotExistsForWorker(zdo, plan.SlotId))
            {
                WorkerZdo.ClearForestryPlan(zdo);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            if (!CanWorkerPlant(worker, plan.SeedPrefab, plan.SaplingPrefab, out int requiredPlantTier))
            {
                _debugLog?.Invoke($"Forestry tier blocked at execution: worker={worker.gameObject.name}, sapling={plan.SaplingPrefab}, requiredTier={requiredPlantTier}, workerTier={WorkerTierCatalog.GetCapabilityTier(worker, WorkerCapability.PlantSeeds)}");
                WorkerZdo.ClearForestryPlan(zdo);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            if (!WorkerZdo.TryReadCargo(zdo, out string seedPrefab, out int amount) || amount <= 0 ||
                !string.Equals(seedPrefab, plan.SeedPrefab, StringComparison.OrdinalIgnoreCase))
            {
                if (WorkerZdo.HasCargo(zdo)) WorkerCargoRuntime.DropAllCargo(worker, zdo, "forestry execution cargo mismatch");
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.NeedsResource);
                return false;
            }

            Vector3 plantPoint = plan.Position;
            if (!ForestryTargetFinder.IsPlannedSlotPhysicallyFree(plantPoint))
            {
                // Do not plant randomly somewhere else. Planned forestry slots are authoritative.
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            GameObject prefab = ResolveSaplingPrefab(plan.SaplingPrefab);
            if (prefab == null || prefab.GetComponent<Plant>() == null || prefab.GetComponent<ZNetView>() == null)
            {
                _debugLog?.Invoke($"Forestry planned plant blocked: sapling prefab '{plan.SaplingPrefab}' unavailable/invalid. Seed cargo kept.");
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.NeedsResource);
                return true;
            }

            Quaternion rotation = Quaternion.Euler(0f, plan.RotationY, 0f);
            GameObject instance = UnityEngine.Object.Instantiate(prefab, plantPoint, rotation);
            ZNetView plantedNview = instance != null ? instance.GetComponent<ZNetView>() : null;
            if (instance == null || plantedNview == null || !plantedNview.IsValid() || plantedNview.GetZDO() == null)
            {
                if (instance != null) UnityEngine.Object.Destroy(instance);
                _debugLog?.Invoke($"Forestry planned plant failed for '{plan.SaplingPrefab}'; seed cargo kept for retry.");
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            ForestryPlanRuntime.CompleteForWorker(zdo, plan.SlotId);
            WorkerZdo.ClearForestryPlan(zdo);
            WorkerZdo.ClearCargo(zdo);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            _debugLog?.Invoke($"Forestry planned slot planted: worker={worker.gameObject.name}, slot={plan.SlotId}, seed={seedPrefab} -> sapling={plan.SaplingPrefab}, position={plantPoint}");
            return true;
        }

        private static ItemDrop FindNearbyMatchingDrop(Vector3 center, string prefabName)
        {
            ItemDrop best = null;
            float bestSqr = float.MaxValue;
            WorkerWorldIndex.ForEachItemDrop(center, CollectRadius, drop =>
            {
                if (drop == null || drop.gameObject == null || !drop.gameObject.activeInHierarchy) return;
                string cargoPrefab = GathererTargetFinder.GetCargoPrefabName(drop);
                if (string.IsNullOrEmpty(cargoPrefab))
                    cargoPrefab = ForestryTargetFinder.NormalizePrefabName(drop.gameObject.name);
                if (!string.Equals(cargoPrefab, prefabName, StringComparison.OrdinalIgnoreCase)) return;
                float sqr = (drop.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                best = drop;
            });
            return best;
        }
    }
}
