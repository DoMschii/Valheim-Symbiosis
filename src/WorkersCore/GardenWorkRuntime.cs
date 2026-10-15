using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Real-resource gardening runtime. Crop pieces are discovered from the live Cultivator table
    /// and validated against ObjectDB/ZNetScene instead of guessing plant prefab names. Normal food
    /// crops and seed-production crops are both explicit player-planned field slots; stock levels do
    /// not silently convert one planned crop into another.
    /// </summary>
    internal static class GardenWorkRuntime
    {
        private sealed class GardenRecipe
        {
            internal string CropItemPrefab;
            internal string ResourcePrefab;
            internal string PlantPrefab;
            internal GameObject PlantPrefabObject;
            internal int ResourceAmount;
            internal float GrowRadius;
        }

        private sealed class SeedProductionRecipe
        {
            internal string CropItemPrefab;
            internal string SeedItemPrefab;
            internal string ResourcePrefab;
            internal string PlantPrefab;
            internal GameObject PlantPrefabObject;
            internal int ResourceAmount;
            internal float GrowRadius;
        }

        internal const float DefaultSpacingMultiplier = 1.05f;

        private static readonly string[] SupportedCropItems =
        {
            "Carrot",
            "Turnip",
            "Onion",
            "Barley",
            "Flax"
        };

        private static readonly Dictionary<string, GardenRecipe> ByResource =
            new Dictionary<string, GardenRecipe>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, GardenRecipe> ByHarvest =
            new Dictionary<string, GardenRecipe>(StringComparer.OrdinalIgnoreCase);
        // Keyed by the harvested vegetable item (Turnip/Carrot/Onion). The value is the
        // Cultivator piece that consumes that vegetable and grows the corresponding seed item.
        // This is discovered from the live PieceTable, so planned slots can temporarily switch
        // to seed production without hard-coding vanilla prefab names or relying on SmartReplant.
        private static readonly Dictionary<string, SeedProductionRecipe> SeedProductionByCrop =
            new Dictionary<string, SeedProductionRecipe>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> HarvestableCropPrefabs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> SmartReplantFloraPrefabs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Piece> DiscoveryPlants = new List<Piece>(32);
        private static readonly List<string> DebugResourceScratch = new List<string>(32);

        private static ObjectDB _database;
        private static ZNetScene _scene;
        private static Action<string> _debugLog;
        private static bool _reportedReady;
        private static float _nextSmartReplantRescanAt;

        internal static void Initialize(Action<string> debugLog)
        {
            _debugLog = debugLog;
            _database = null;
            _scene = null;
            ByResource.Clear();
            ByHarvest.Clear();
            SeedProductionByCrop.Clear();
            HarvestableCropPrefabs.Clear();
            SmartReplantFloraPrefabs.Clear();
            _reportedReady = false;
            _nextSmartReplantRescanAt = 0f;
        }

        internal static void Shutdown()
        {
            ByResource.Clear();
            ByHarvest.Clear();
            SeedProductionByCrop.Clear();
            HarvestableCropPrefabs.Clear();
            SmartReplantFloraPrefabs.Clear();
            _database = null;
            _scene = null;
            _debugLog = null;
            _reportedReady = false;
            _nextSmartReplantRescanAt = 0f;
        }


        internal static void InvalidateRecipeCache()
        {
            _database = null;
            _scene = null;
            _nextSmartReplantRescanAt = 0f;
        }

        internal static bool EnsureReady()
        {
            if (ObjectDB.instance == null || ZNetScene.instance == null) return false;
            if (_database == ObjectDB.instance && _scene == ZNetScene.instance && ByResource.Count > 0)
            {
                return true;
            }
            _nextSmartReplantRescanAt = Time.time + 5f;

            ByResource.Clear();
            ByHarvest.Clear();
            SeedProductionByCrop.Clear();
            HarvestableCropPrefabs.Clear();
            SmartReplantFloraPrefabs.Clear();

            GameObject cultivator = ObjectDB.instance.GetItemPrefab("Cultivator");
            PieceTable table = cultivator != null
                ? cultivator.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces
                : null;
            if (table == null || table.m_pieces == null || table.m_pieces.Count == 0) return false;

            // Use the live Cultivator table as the authoritative prefab source. Requiring the
            // PieceTable GameObject to be reference-equal to ZNetScene.GetPrefab(name) was too
            // strict and could reduce discovery to zero even though the crop is perfectly valid.
            DiscoveryPlants.Clear();
            for (int i = 0; i < table.m_pieces.Count; i++)
            {
                GameObject go = table.m_pieces[i];
                if (go == null || go.GetComponent<Plant>() == null || go.GetComponent<ZNetView>() == null) continue;
                Piece piece = go.GetComponent<Piece>();
                if (piece != null) DiscoveryPlants.Add(piece);
            }
            List<Piece> plants = DiscoveryPlants;

            // Discover normal cultivated food crops directly from the live Cultivator table.
            // Do not depend on hard-coded crop item names or reference equality between ObjectDB
            // and Pickable item prefabs. A normal crop is a Plant piece with exactly one real
            // resource cost and mature Pickables whose output is not a seed item. This mirrors
            // SmartReplant's data-driven approach and also supports compatible modded crops.
            for (int p = 0; p < plants.Count; p++)
            {
                Piece piece = plants[p];
                if (piece == null) continue;

                if (!TryGetSinglePositiveRequirement(piece, out Piece.Requirement cost))
                    continue;

                Plant plant = piece.GetComponent<Plant>();
                if (plant == null || plant.m_grownPrefabs == null || plant.m_grownPrefabs.Length == 0) continue;

                GameObject grownItemPrefab = null;
                bool allPickable = true;
                for (int g = 0; g < plant.m_grownPrefabs.Length; g++)
                {
                    GameObject grown = plant.m_grownPrefabs[g];
                    Pickable pickable = grown != null ? grown.GetComponent<Pickable>() : null;
                    if (pickable == null || pickable.m_itemPrefab == null)
                    {
                        allPickable = false;
                        break;
                    }

                    if (grownItemPrefab == null) grownItemPrefab = pickable.m_itemPrefab;
                }
                if (!allPickable || grownItemPrefab == null) continue;

                string grownItemName = NormalizePrefabName(grownItemPrefab.name);
                if (string.IsNullOrEmpty(grownItemName)) continue;

                int requiredAmount = cost.GetAmount(0);
                if (requiredAmount <= 0) continue;

                string resourcePrefab = NormalizePrefabName(cost.m_resItem.gameObject.name);
                string plantPrefab = NormalizePrefabName(piece.gameObject.name);
                if (string.IsNullOrEmpty(resourcePrefab) || string.IsNullOrEmpty(plantPrefab)) continue;

                float radius = Mathf.Max(0.35f, plant.m_growRadius);

                // v0.5.395: seed-production crops are normal, explicit planning choices now.
                // The player decides how many Carrot/Turnip/Onion seed plants should exist by
                // planning those Cultivator pieces directly. Workers must never convert an ordinary
                // vegetable slot into seed production based on stock thresholds. Keep the legacy
                // inverse lookup for old-save compatibility, but register the seed piece as a
                // first-class GardenRecipe so its own persistent ghost can be executed directly.
                if (IsSeedLikeName(grownItemName))
                {
                    SeedProductionRecipe seedRecipe = new SeedProductionRecipe
                    {
                        CropItemPrefab = resourcePrefab, SeedItemPrefab = grownItemName,
                        ResourcePrefab = resourcePrefab, PlantPrefab = plantPrefab,
                        PlantPrefabObject = piece.gameObject, ResourceAmount = requiredAmount, GrowRadius = radius
                    };
                    if (!SeedProductionByCrop.ContainsKey(resourcePrefab))
                        SeedProductionByCrop[resourcePrefab] = seedRecipe;

                    GardenRecipe plannedSeedRecipe = new GardenRecipe
                    {
                        CropItemPrefab = grownItemName, ResourcePrefab = resourcePrefab, PlantPrefab = plantPrefab,
                        PlantPrefabObject = piece.gameObject, ResourceAmount = requiredAmount, GrowRadius = radius
                    };
                    if (!ByResource.ContainsKey(resourcePrefab)) ByResource[resourcePrefab] = plannedSeedRecipe;
                    for (int g = 0; g < plant.m_grownPrefabs.Length; g++)
                    {
                        GameObject grown = plant.m_grownPrefabs[g];
                        if (grown == null || grown.GetComponent<Pickable>() == null) continue;
                        string mature = NormalizePrefabName(grown.name);
                        if (string.IsNullOrEmpty(mature)) continue;
                        HarvestableCropPrefabs.Add(mature);
                        if (!ByHarvest.ContainsKey(mature)) ByHarvest[mature] = plannedSeedRecipe;
                    }
                    continue;
                }

                GardenRecipe recipe = new GardenRecipe
                {
                    CropItemPrefab = grownItemName, ResourcePrefab = resourcePrefab, PlantPrefab = plantPrefab,
                    PlantPrefabObject = piece.gameObject, ResourceAmount = requiredAmount, GrowRadius = radius
                };
                if (!ByResource.ContainsKey(resourcePrefab)) ByResource[resourcePrefab] = recipe;

                for (int g = 0; g < plant.m_grownPrefabs.Length; g++)
                {
                    GameObject grown = plant.m_grownPrefabs[g];
                    if (grown == null || grown.GetComponent<Pickable>() == null) continue;
                    string mature = NormalizePrefabName(grown.name);
                    HarvestableCropPrefabs.Add(mature);
                    if (!ByHarvest.ContainsKey(mature)) ByHarvest[mature] = recipe;
                }
            }

            // Workers-native persistent flora. SmartReplant is deliberately excluded from
            // Workers planning/worker execution: our VW_Flora_* prefabs clone the vanilla
            // persistent Pickable directly and use the normal Workers field planner.
            for (int i = 0; i < table.m_pieces.Count; i++)
            {
                GameObject go = table.m_pieces[i];
                if (go == null || !WorkersWildFloraRuntime.TryGetDefinition(go, out WorkersWildFloraRuntime.Definition def)) continue;
                Piece piece = go.GetComponent<Piece>();
                if (piece == null) continue;

                GardenRecipe floraRecipe = new GardenRecipe
                {
                    CropItemPrefab = def.ResourcePrefab,
                    ResourcePrefab = def.ResourcePrefab,
                    PlantPrefab = def.PrefabName,
                    PlantPrefabObject = go,
                    ResourceAmount = def.Cost,
                    GrowRadius = Mathf.Max(0.25f, def.Spacing * 0.5f / DefaultSpacingMultiplier)
                };

                // Do not overwrite vanilla crop recipes that happen to consume the same item.
                // WildGrowth resolves primarily by the saved VW_Flora_* slot identity.
                if (!ByResource.ContainsKey(def.ResourcePrefab)) ByResource[def.ResourcePrefab] = floraRecipe;
                ByHarvest[def.PrefabName] = floraRecipe;
                HarvestableCropPrefabs.Add(def.PrefabName);
            }

            _database = ObjectDB.instance;
            _scene = ZNetScene.instance;

            if (!_reportedReady && ByResource.Count > 0)
            {
                _reportedReady = true;
                _debugLog?.Invoke($"Garden crop discovery ready: {ByResource.Count} planting recipes + {SeedProductionByCrop.Count} seed-production recipes + {HarvestableCropPrefabs.Count} mature crop prefabs from live Cultivator table. Workers-native Wildwuchs active.");
            }

            return ByResource.Count > 0;
        }


        /// <summary>
        /// Resolves the real Cultivator seed-production recipe for one already-planned normal
        /// vegetable slot. The permanent plan identity stays the normal crop; callers use this
        /// recipe only for the current cycle while local seed production is active.
        /// </summary>
        internal static bool TryGetSeedProductionPlan(string maturePrefab, out string seedResourcePrefab,
            out string cropResourcePrefab, out string plantPrefab, out int resourceAmount, out float spacing)
        {
            seedResourcePrefab = string.Empty;
            cropResourcePrefab = string.Empty;
            plantPrefab = string.Empty;
            resourceAmount = 0;
            spacing = 0f;
            if (!EnsureReady()) return false;

            string normalized = NormalizePrefabName(maturePrefab);
            GardenRecipe normal = null;
            if (!ByHarvest.TryGetValue(normalized, out normal))
            {
                normal = FindRecipeByCropItemPrefab(normalized);
            }
            if (normal == null || string.IsNullOrEmpty(normal.CropItemPrefab)) return false;
            if (!SeedProductionByCrop.TryGetValue(normal.CropItemPrefab, out SeedProductionRecipe seed) || seed == null)
                return false;

            seedResourcePrefab = seed.SeedItemPrefab;
            cropResourcePrefab = seed.ResourcePrefab;
            plantPrefab = seed.PlantPrefab;
            resourceAmount = seed.ResourceAmount;
            spacing = Mathf.Max(0.7f, seed.GrowRadius * 2f * DefaultSpacingMultiplier);
            return !string.IsNullOrEmpty(seedResourcePrefab) && !string.IsNullOrEmpty(cropResourcePrefab) &&
                   !string.IsNullOrEmpty(plantPrefab) && resourceAmount > 0;
        }

        internal static bool IsSupportedPlantingResource(string resourcePrefab)
        {
            return EnsureReady() && ByResource.ContainsKey(resourcePrefab ?? string.Empty);
        }

        internal static bool TryGetPlantingData(string resourcePrefab, out string plantPrefab, out int resourceAmount, out float spacing)
        {
            plantPrefab = string.Empty;
            resourceAmount = 0;
            spacing = 0f;
            if (!EnsureReady() || !ByResource.TryGetValue(resourcePrefab ?? string.Empty, out GardenRecipe recipe))
                return false;

            plantPrefab = recipe.PlantPrefab;
            resourceAmount = recipe.ResourceAmount;
            spacing = Mathf.Max(0.7f, recipe.GrowRadius * 2f * DefaultSpacingMultiplier);
            return true;
        }



        internal static bool TryGetPlanningData(Piece piece, out string harvestPrefab, out string plantPrefab, out string resourcePrefab, out int resourceAmount, out float spacing)
        {
            harvestPrefab = string.Empty;
            plantPrefab = string.Empty;
            resourcePrefab = string.Empty;
            resourceAmount = 0;
            spacing = 0f;
            if (piece == null || !EnsureReady()) return false;

            string selectedPlant = NormalizePrefabName(piece.gameObject != null ? piece.gameObject.name : string.Empty);
            if (string.IsNullOrEmpty(selectedPlant)) return false;

            // SmartReplant owns its own manual planting UI/ghosts. Workers no longer consumes or
            // translates SmartReplant_Flora_* pieces; select the Workers Wildwuchs entry instead.
            if (selectedPlant.StartsWith("SmartReplant_Flora_", StringComparison.OrdinalIgnoreCase)) return false;

            if (WorkersWildFloraRuntime.TryGetPlanningData(piece, out harvestPrefab, out plantPrefab,
                    out resourcePrefab, out resourceAmount, out spacing))
                return true;

            GardenRecipe recipe = FindRecipeByPlantPrefab(selectedPlant);
            if (recipe == null)
            {
                // v0.5.112: Barley/Flax are self-seeding crops: the planted resource and the
                // harvested item are the same prefab and there is no separate seed-production
                // half. Some live Cultivator tables expose those pieces in a way that can miss
                // the initial recipe cache. Validate the selected Plant directly instead of
                // rejecting a perfectly valid planning click.
                return TryRegisterDirectPlanningPiece(piece, out harvestPrefab, out plantPrefab,
                    out resourcePrefab, out resourceAmount, out spacing);
            }

            plantPrefab = recipe.PlantPrefab;
            resourcePrefab = recipe.ResourcePrefab;
            resourceAmount = recipe.ResourceAmount;
            spacing = Mathf.Max(0.7f, recipe.GrowRadius * 2f * DefaultSpacingMultiplier);

            Plant plant = piece.GetComponent<Plant>();
            if (plant != null && plant.m_grownPrefabs != null)
            {
                for (int i = 0; i < plant.m_grownPrefabs.Length; i++)
                {
                    GameObject grown = plant.m_grownPrefabs[i];
                    if (grown == null) continue;
                    string mature = NormalizePrefabName(grown.name);
                    if (ByHarvest.ContainsKey(mature))
                    {
                        harvestPrefab = mature;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(harvestPrefab))
            {
                // Same defensive direct path for a recipe that was cached but whose mature
                // identity was not. This is especially relevant to Barley/Flax because their
                // resource and harvest item intentionally collapse to one identity.
                return TryRegisterDirectPlanningPiece(piece, out harvestPrefab, out plantPrefab,
                    out resourcePrefab, out resourceAmount, out spacing);
            }

            return !string.IsNullOrEmpty(harvestPrefab) && !string.IsNullOrEmpty(resourcePrefab) && resourceAmount > 0;
        }

        private static bool TryRegisterDirectPlanningPiece(Piece piece, out string harvestPrefab, out string plantPrefab,
            out string resourcePrefab, out int resourceAmount, out float spacing)
        {
            if (piece != null && piece.gameObject != null)
            {
                string directName = NormalizePrefabName(piece.gameObject.name);
                if (directName.StartsWith("SmartReplant_Flora_", StringComparison.OrdinalIgnoreCase))
                {
                    harvestPrefab = string.Empty; plantPrefab = string.Empty; resourcePrefab = string.Empty; resourceAmount = 0; spacing = 0f;
                    return false;
                }
                if (WorkersWildFloraRuntime.TryGetPlanningData(piece, out harvestPrefab, out plantPrefab, out resourcePrefab, out resourceAmount, out spacing))
                    return true;
            }
            harvestPrefab = string.Empty;
            plantPrefab = string.Empty;
            resourcePrefab = string.Empty;
            resourceAmount = 0;
            spacing = 0f;
            if (piece == null || piece.gameObject == null) return false;

            // SmartReplant persistent flora menu pieces (notably Thistle/Distel) are not
            // vanilla Plant components. They are already the final persistent flora prefab, so
            // requiring Plant.m_grownPrefabs here made the Workers planner silently reject them.
            // Register these pieces directly before entering the vanilla crop discovery path.
            if (SmartReplantBridge.IsSmartReplantPlantingPiece(piece.gameObject))
                return false;

            Plant plant = piece.GetComponent<Plant>();
            if (plant == null || plant.m_grownPrefabs == null || plant.m_grownPrefabs.Length == 0) return false;

            if (!TryGetSinglePositiveRequirement(piece, out Piece.Requirement cost))
                return false;

            string harvestedItem = string.Empty;
            List<string> maturePrefabs = new List<string>();
            for (int i = 0; i < plant.m_grownPrefabs.Length; i++)
            {
                GameObject grown = plant.m_grownPrefabs[i];
                Pickable pickable = grown != null ? grown.GetComponent<Pickable>() : null;
                if (pickable == null || pickable.m_itemPrefab == null) return false;

                string currentItem = NormalizePrefabName(pickable.m_itemPrefab.name);
                if (string.IsNullOrEmpty(currentItem)) return false;
                if (string.IsNullOrEmpty(harvestedItem)) harvestedItem = currentItem;
                else if (!string.Equals(harvestedItem, currentItem, StringComparison.OrdinalIgnoreCase)) return false;

                string mature = NormalizePrefabName(grown.name);
                if (!string.IsNullOrEmpty(mature)) maturePrefabs.Add(mature);
            }

            // v0.5.395: seed-production pieces (Carrot -> CarrotSeeds etc.) are valid permanent
            // field identities when the PLAYER planned them. Their mature prefab is persisted just
            // like an ordinary crop, so the exact same number of seed plants is replanted after
            // harvest. Barley/Flax continue to use the same generic direct path.
            if (string.IsNullOrEmpty(harvestedItem) || maturePrefabs.Count == 0)
                return false;

            resourcePrefab = NormalizePrefabName(cost.m_resItem.gameObject.name);
            plantPrefab = NormalizePrefabName(piece.gameObject.name);
            resourceAmount = cost.GetAmount(0);
            if (string.IsNullOrEmpty(resourcePrefab) || string.IsNullOrEmpty(plantPrefab) || resourceAmount <= 0)
                return false;

            float radius = Mathf.Max(0.35f, plant.m_growRadius);
            spacing = Mathf.Max(0.7f, radius * 2f * DefaultSpacingMultiplier);
            harvestPrefab = maturePrefabs[0];

            // Persist the direct discovery into the normal registries so the worker that later
            // executes this planned slot sees exactly the same recipe even when the original
            // startup discovery missed it.
            GardenRecipe direct = new GardenRecipe
            {
                CropItemPrefab = harvestedItem,
                ResourcePrefab = resourcePrefab,
                PlantPrefab = plantPrefab,
                PlantPrefabObject = piece.gameObject,
                ResourceAmount = resourceAmount,
                GrowRadius = radius
            };
            ByResource[resourcePrefab] = direct;
            for (int i = 0; i < maturePrefabs.Count; i++)
            {
                string mature = maturePrefabs[i];
                HarvestableCropPrefabs.Add(mature);
                ByHarvest[mature] = direct;
            }

            _debugLog?.Invoke($"Garden direct planning recipe registered: plant={plantPrefab}, resource={resourcePrefab} x{resourceAmount}, harvestItem={harvestedItem}, mature={string.Join(",", maturePrefabs)}.");
            return true;
        }


        private static bool TryRegisterSmartReplantFloraPlanningPiece(Piece piece, out string harvestPrefab, out string plantPrefab,
            out string resourcePrefab, out int resourceAmount, out float spacing)
        {
            harvestPrefab = string.Empty;
            plantPrefab = string.Empty;
            resourcePrefab = string.Empty;
            resourceAmount = 0;
            spacing = 0f;
            if (piece == null || piece.gameObject == null || !SmartReplantBridge.IsSmartReplantPlantingPiece(piece.gameObject))
                return false;

            plantPrefab = NormalizePrefabName(piece.gameObject.name);
            if (string.IsNullOrEmpty(plantPrefab)) return false;

            // Prefer SmartReplant's actual build requirement when exposed. Planning itself still
            // consumes nothing; this resource is only what the worker later fetches to plant it.
            if (TryGetSinglePositiveRequirement(piece, out Piece.Requirement floraCost))
            {
                resourcePrefab = NormalizePrefabName(floraCost.m_resItem.gameObject.name);
                resourceAmount = Mathf.Max(1, floraCost.GetAmount(0));
            }

            // SmartReplant persistent flora can deliberately expose no vanilla Piece requirement.
            // Resolve the actual target Pickable and use its harvested ItemDrop as the canonical
            // planting resource. This makes the planner generic for mushrooms, Thistle, Dandelion
            // and future SmartReplant_Flora_* entries instead of maintaining a hard-coded allowlist.
            float resolvedSpacing = 0f;
            GameObject targetPrefab = null;
            SmartReplantBridge.TryResolveFloraDefinition(piece, out targetPrefab, out resolvedSpacing);
            if (string.IsNullOrEmpty(resourcePrefab) && targetPrefab != null)
            {
                Pickable targetPickable = targetPrefab.GetComponent<Pickable>() ?? targetPrefab.GetComponentInChildren<Pickable>(true);
                if (targetPickable != null && targetPickable.m_itemPrefab != null)
                {
                    resourcePrefab = NormalizePrefabName(targetPickable.m_itemPrefab.name);
                    // Current SmartReplant non-berry persistent flora recipes use 3 harvested
                    // items when the custom Piece does not expose a vanilla requirement. Berries
                    // use 5. This value is only consumed by the worker later, never by planning.
                    string targetName = NormalizePrefabName(targetPrefab.name);
                    bool berry = targetName.IndexOf("Raspberry", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 targetName.IndexOf("Blueberr", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 targetName.IndexOf("Cloudberr", StringComparison.OrdinalIgnoreCase) >= 0;
                    resourceAmount = berry ? 5 : 3;
                }
            }

            if (string.IsNullOrEmpty(resourcePrefab) || resourceAmount <= 0) return false;

            // SmartReplant persistent flora has TWO identities: the custom network Piece
            // (SmartReplant_Flora_*) and the vanilla target Pickable used for the visible/harvested
            // plant. Persist the target as the slot identity so WildGrowth/harvest logic sees the
            // real mature prefab, while PlantPrefab remains the custom SmartReplant Piece that the
            // worker must instantiate later.
            if (targetPrefab != null)
                harvestPrefab = NormalizePrefabName(targetPrefab.name);
            if (string.IsNullOrEmpty(harvestPrefab)) harvestPrefab = plantPrefab;
            spacing = resolvedSpacing > 0.05f ? Mathf.Max(0.55f, resolvedSpacing) : 1.4f;
            float growRadius = Mathf.Max(0.35f, spacing * 0.5f);

            GardenRecipe direct = new GardenRecipe
            {
                CropItemPrefab = resourcePrefab,
                ResourcePrefab = resourcePrefab,
                PlantPrefab = plantPrefab,
                PlantPrefabObject = piece.gameObject,
                ResourceAmount = resourceAmount,
                GrowRadius = growRadius
            };
            ByResource[resourcePrefab] = direct;
            HarvestableCropPrefabs.Add(harvestPrefab);
            ByHarvest[harvestPrefab] = direct;

            _debugLog?.Invoke($"Garden SmartReplant flora planning recipe registered: plant={plantPrefab}, resource={resourcePrefab} x{resourceAmount}, identity={harvestPrefab}.");
            return true;
        }

        internal static bool TryGetFallbackReplantPlan(string maturePrefab, out string resourcePrefab, out string plantPrefab, out int resourceAmount, out float spacing)
        {
            resourcePrefab = string.Empty; plantPrefab = string.Empty; resourceAmount = 0; spacing = 0f;
            if (!EnsureReady() || !ByHarvest.TryGetValue(NormalizePrefabName(maturePrefab), out GardenRecipe recipe)) return false;
            resourcePrefab = recipe.ResourcePrefab; plantPrefab = recipe.PlantPrefab; resourceAmount = recipe.ResourceAmount;
            spacing = Mathf.Max(0.7f, recipe.GrowRadius * 2f * DefaultSpacingMultiplier);
            return !string.IsNullOrEmpty(resourcePrefab) && !string.IsNullOrEmpty(plantPrefab) && resourceAmount > 0;
        }

        /// <summary>
        /// Returns the direct replant recipe for crops where the harvested item is also the
        /// planting resource (vanilla Barley/Flax and compatible data-driven crops). These crops
        /// have no separate seed-production half, so one unit from the current harvest must be
        /// reserved before the remaining yield is released as normal ItemDrops.
        /// </summary>
        internal static bool TryGetSelfSeedingReplantPlan(string maturePrefab, out string resourcePrefab, out string plantPrefab, out int resourceAmount, out float spacing)
        {
            resourcePrefab = string.Empty; plantPrefab = string.Empty; resourceAmount = 0; spacing = 0f;
            if (!EnsureReady() || !ByHarvest.TryGetValue(NormalizePrefabName(maturePrefab), out GardenRecipe recipe) || recipe == null)
                return false;

            if (string.IsNullOrEmpty(recipe.ResourcePrefab) || string.IsNullOrEmpty(recipe.CropItemPrefab) ||
                !string.Equals(recipe.ResourcePrefab, recipe.CropItemPrefab, StringComparison.OrdinalIgnoreCase))
                return false;

            resourcePrefab = recipe.ResourcePrefab;
            plantPrefab = recipe.PlantPrefab;
            resourceAmount = recipe.ResourceAmount;
            spacing = Mathf.Max(0.7f, recipe.GrowRadius * 2f * DefaultSpacingMultiplier);
            return !string.IsNullOrEmpty(plantPrefab) && resourceAmount > 0;
        }

        internal static GameObject ResolvePlantPrefab(string plantPrefab)
        {
            if (string.IsNullOrEmpty(plantPrefab)) return null;
            if (EnsureReady())
            {
                foreach (GardenRecipe recipe in ByResource.Values)
                    if (recipe != null && string.Equals(recipe.PlantPrefab, plantPrefab, StringComparison.OrdinalIgnoreCase) && recipe.PlantPrefabObject != null)
                        return recipe.PlantPrefabObject;
            }
            if (ZNetScene.instance != null)
            {
                GameObject scenePrefab = ZNetScene.instance.GetPrefab(plantPrefab);
                if (scenePrefab != null) return scenePrefab;
            }
            GameObject cultivator = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Cultivator") : null;
            PieceTable table = cultivator != null ? cultivator.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
            if (table?.m_pieces != null)
                for (int i = 0; i < table.m_pieces.Count; i++)
                    if (table.m_pieces[i] != null && string.Equals(NormalizePrefabName(table.m_pieces[i].name), plantPrefab, StringComparison.OrdinalIgnoreCase))
                        return table.m_pieces[i];
            return null;
        }

        internal static bool IsHarvestableCropPrefab(string prefabName)
        {
            string normalized = NormalizePrefabName(prefabName);
            return EnsureReady() && HarvestableCropPrefabs.Contains(normalized);
        }

        /// <summary>
        /// Returns true only for loose non-Material ItemDrops that are legitimate harvest outputs.
        /// Gatherers already accept every normal Material item; this method deliberately stays
        /// narrow so dropped weapons, armor and unrelated consumables are not vacuumed up.
        /// </summary>
        /// <summary>
        /// Resolves either a real harvested item prefab or a SmartReplant flora/wrapper prefab
        /// to the canonical ItemDrop prefab that belongs in inventories and Worker cargo.
        /// This also lets workers recover cargo persisted by older Workers versions that stored
        /// the SmartReplant world-wrapper name instead of the contained item prefab.
        /// </summary>
        internal static bool TryResolveGatherableItemPrefab(string prefabName, out string canonicalPrefab)
        {
            canonicalPrefab = string.Empty;
            string normalized = NormalizePrefabName(prefabName);
            if (string.IsNullOrEmpty(normalized)) return false;

            // If ObjectDB already knows this as an ItemDrop, it is canonical.
            GameObject direct = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(normalized) : null;
            if (direct != null && direct.GetComponent<ItemDrop>() != null)
            {
                canonicalPrefab = NormalizePrefabName(direct.name);
                return !string.IsNullOrEmpty(canonicalPrefab);
            }

            if (!EnsureReady()) return false;

            foreach (GardenRecipe recipe in ByResource.Values)
            {
                if (recipe == null || string.IsNullOrEmpty(recipe.CropItemPrefab)) continue;

                if (string.Equals(recipe.CropItemPrefab, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(recipe.PlantPrefab, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    canonicalPrefab = recipe.CropItemPrefab;
                    return !string.IsNullOrEmpty(canonicalPrefab);
                }
            }

            return false;
        }

        internal static bool IsGatherableLooseItemPrefab(string prefabName)
        {
            string normalized = NormalizePrefabName(prefabName);
            if (string.IsNullOrEmpty(normalized)) return false;

            // Vanilla wild harvests are not necessarily represented by a Cultivator recipe.
            // Keep these explicit outputs aligned with GardenHarvestTargetFinder wild support.
            if (string.Equals(normalized, "Raspberry", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Blueberries", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Cloudberry", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Mushroom", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!EnsureReady()) return false;

            // Normal crops and SmartReplant flora are discovered from the live Cultivator table,
            // so this also covers compatible modded crops without hard-coding their ItemType.
            foreach (GardenRecipe recipe in ByResource.Values)
            {
                if (recipe != null &&
                    !string.IsNullOrEmpty(recipe.CropItemPrefab) &&
                    string.Equals(recipe.CropItemPrefab, normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        internal static bool HasPlantableCargo(ZDO zdo)
        {
            if (zdo == null) return false;
            List<WorkerZdo.CargoStack> cargo = WorkerZdo.ReadCargoStacks(zdo);
            for (int i = 0; i < cargo.Count; i++)
            {
                WorkerZdo.CargoStack stack = cargo[i];
                if (stack.Amount <= 0 || string.IsNullOrEmpty(stack.Prefab)) continue;
                if (TryGetPlantingData(stack.Prefab, out _, out int required, out _) && stack.Amount >= required)
                    return true;
            }
            return false;
        }

        internal static bool TryPlant(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;

            bool hasPlan = WorkerZdo.TryReadGardenPlan(zdo, out WorkerGardenPlan plan);
            if (hasPlan && plan.SlotId > 0 && !GardenReplantPlanRuntime.PersistentSlotExistsForWorker(zdo, plan.SlotId))
            {
                WorkerZdo.ClearGardenPlan(zdo);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                _debugLog?.Invoke($"Garden cached plan cancelled because persistent slot was deleted: worker={worker.gameObject.name}, slot={plan.SlotId}. Cargo kept.");
                return true;
            }
            string resourcePrefab = string.Empty;
            int amount = 0;
            string plantPrefab;
            int requiredAmount;
            float spacing;
            Vector3 plantPoint;
            if (hasPlan)
            {
                resourcePrefab = plan.ResourcePrefab;
                amount = WorkerZdo.GetCargoAmount(zdo, resourcePrefab);
                if (amount < plan.ResourceAmount)
                {
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.NeedsResource);
                    return false;
                }
                plantPrefab = plan.PlantPrefab; requiredAmount = plan.ResourceAmount; spacing = plan.Spacing; plantPoint = plan.Position;
            }
            else
            {
                List<WorkerZdo.CargoStack> cargo = WorkerZdo.ReadCargoStacks(zdo);
                bool foundPlantable = false;
                plantPrefab = string.Empty; requiredAmount = 0; spacing = 0f;
                for (int i = 0; i < cargo.Count; i++)
                {
                    WorkerZdo.CargoStack stack = cargo[i];
                    if (stack.Amount <= 0 || string.IsNullOrEmpty(stack.Prefab)) continue;
                    if (!TryGetPlantingData(stack.Prefab, out string candidatePlant, out int candidateRequired, out float candidateSpacing) || stack.Amount < candidateRequired) continue;
                    resourcePrefab = stack.Prefab; amount = stack.Amount;
                    plantPrefab = candidatePlant; requiredAmount = candidateRequired; spacing = candidateSpacing;
                    foundPlantable = true;
                    break;
                }
                if (!foundPlantable)
                { WorkerZdo.ClearTarget(zdo); zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob); return false; }
                if (!WorkerZdo.TryReadTarget(zdo, out _, out string targetKind, out plantPoint) ||
                    !string.Equals(targetKind, GardenTargetFinder.PlantTargetKind, StringComparison.Ordinal))
                { zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob); return false; }
            }

            if (!GardenTargetFinder.IsPlantingPointStillClear(plantPoint, spacing))
            {
                // A persistent slot can already be fulfilled by the exact expected crop (for example
                // after an old planning-version test, another player, or another compatible system).
                // Do not let the Shaman loop forever in front of that occupied slot. Mark only the
                // matching planned crop complete; unrelated obstructions remain blocked/retriable.
                bool wildGrowthBlockedPlan = hasPlan && GardenerTaskFilterRuntime.IsWildGrowthPlan(plan);
                if (hasPlan)
                {
                    bool exactFulfilled = wildGrowthBlockedPlan
                        ? GardenTargetFinder.HasConfirmedWildGrowthPlantAt(plantPoint, spacing, plantPrefab, plan.SlotId)
                        : GardenTargetFinder.HasExpectedPlantAt(plantPoint, spacing, plantPrefab);

                    // v0.5.329: old WildGrowth builds can leave a correct networked bush without
                    // a slot marker. Never let FindJob auto-complete that plan on reload. Adoption
                    // is allowed only here, when the worker actually reaches the blocked slot.
                    if (!exactFulfilled && wildGrowthBlockedPlan)
                        exactFulfilled = GardenTargetFinder.TryAdoptLegacyWildGrowthPlantAt(plantPoint, spacing, plantPrefab,
                            plan.SlotId, WorkerZdo.ReadWorkAreaId(zdo));

                    if (exactFulfilled)
                    {
                        bool completed = GardenReplantPlanRuntime.CompleteForWorker(zdo, plan.SlotId);
                        WorkerZdo.ClearGardenPlan(zdo);
                        WorkerZdo.ClearTarget(zdo);
                        zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                        _debugLog?.Invoke($"Garden planned slot already fulfilled/adopted: worker={worker.gameObject.name}, slot={plan.SlotId}, plant={plantPrefab}, completed={completed}. Cargo kept for next slot/return.");
                        return true;
                    }
                }

                // v0.5.330: persistent WildGrowth is an explicit player-authored planting point.
                // Do not bounce MoveToTarget -> FindJob just because the generic clearance sphere
                // sees some collider near that point. The slot ghost itself is renderer-only, and
                // legacy flora/nearby scenery can still overlap the broad spacing radius. Releasing
                // the claim here caused the same slot to be reacquired from cargo every AI tick.
                // If there is no exact/adoptable bush for THIS slot, keep the plan and plant exactly
                // at the saved position. Normal crops retain the conservative clearance behaviour.
                if (wildGrowthBlockedPlan)
                {
                    _debugLog?.Invoke($"WildGrowth planned slot clearance override: worker={worker.gameObject.name}, slot={plan.SlotId}, plant={plantPrefab}; generic overlap ignored, exact slot remains authoritative.");
                }
                else
                {
                    // The resource is already present; this is a temporarily blocked planting point,
                    // not a missing-resource condition. Release only the worker-side claim so the
                    // persistent slot stays pending and the gardener can try another planned slot.
                    if (hasPlan) WorkerZdo.ClearGardenPlan(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    _debugLog?.Invoke($"Garden planned slot temporarily blocked: worker={worker.gameObject.name}, slot={(hasPlan ? plan.SlotId.ToString() : "none")}, plant={plantPrefab}. Releasing worker claim and trying another slot; cargo kept.");
                    return true;
                }
            }

            GameObject prefab = ResolvePlantPrefab(plantPrefab);
            bool isPlant = prefab != null && prefab.GetComponent<Plant>() != null;
            // SmartReplant persistent flora may have no Pickable on the planting prefab itself
            // (notably Thistle). The SmartReplant_Flora_* identity plus a valid ZNetView is the
            // authoritative signal; SmartReplant owns the runtime pick/respawn behaviour.
            bool isWorkersWildFlora = prefab != null && WorkersWildFloraRuntime.IsWorkersWildFloraName(prefab.name);
            bool isNativeWildFlora = prefab != null && prefab.GetComponent<Pickable>() != null &&
                                     WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(prefab.name, out _);
            if (prefab == null || (!isPlant && !isWorkersWildFlora && !isNativeWildFlora) || prefab.GetComponent<ZNetView>() == null)
            {
                _debugLog?.Invoke($"Garden plant blocked: validated crop prefab '{plantPrefab}' is no longer available. Cargo kept.");
                WorkerZdo.ClearTarget(zdo); zdo.Set(WorkerZdo.State, (int)WorkerState.NeedsResource); return true;
            }

            bool wildGrowthPlan = hasPlan && GardenerTaskFilterRuntime.IsWildGrowthPlan(plan);
            if (wildGrowthPlan)
            {
                string workAreaId = WorkerZdo.ReadWorkAreaId(zdo);
                // v0.5.386: a picked WildGrowth plant can have no active collider while it waits for
                // vanilla respawn. Physics therefore made the point look empty and an old/raced active
                // ghost could spend another 3 resources and create a second plant with the same slot.
                // Query the existing Pickable index by durable Workzone+slot identity before ANY spawn.
                if (GardenTargetFinder.HasManagedWildGrowthSlotLoaded(plantPoint, plan.Spacing, plan.SlotId, workAreaId, out _))
                {
                    GardenReplantPlanRuntime.ConfirmPersistentWildGrowthSlot(workAreaId, plan.SlotId);
                    WorkerZdo.ClearGardenPlan(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    _debugLog?.Invoke($"WildGrowth spawn suppressed: slot #{plan.SlotId} already owns a loaded persistent plant; cargo kept, no material consumed.");
                    return true;
                }
            }

            Vector3 face = plantPoint - worker.transform.position; face.y = 0f;
            Quaternion rotation = hasPlan
                ? Quaternion.Euler(0f, plan.RotationY, 0f)
                : (face.sqrMagnitude > 0.01f ? Quaternion.LookRotation(face.normalized, Vector3.up) : Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));

            GameObject instance = UnityEngine.Object.Instantiate(prefab, plantPoint, rotation);
            if (wildGrowthPlan && instance != null)
                WorkersWildFloraRuntime.PrepareSpawnedInstance(instance);
            ZNetView plantedNview = instance != null ? instance.GetComponent<ZNetView>() : null;
            if (instance == null || plantedNview == null || !plantedNview.IsValid() || plantedNview.GetZDO() == null)
            {
                if (instance != null) UnityEngine.Object.Destroy(instance);
                _debugLog?.Invoke($"Garden plant failed for '{plantPrefab}'; cargo kept for retry.");
                WorkerZdo.ClearTarget(zdo); zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob); return true;
            }

            if (wildGrowthPlan)
                GardenTargetFinder.MarkWildGrowthPlantForSlot(plantedNview, plan.SlotId, WorkerZdo.ReadWorkAreaId(zdo));

            int batchPlanted = 0;
            if (hasPlan)
            {
                // v0.5.383: MarkWildGrowthPlantForSlot closes a persistent WildGrowth blueprint
                // immediately after the authoritative network ZDO receives its slot marker. The
                // physical Pickable itself now owns all later picked/respawn cycles, so this plan
                // must never be reopened merely because the yield was harvested.
                if (!wildGrowthPlan)
                    GardenReplantPlanRuntime.CompleteForWorker(zdo, plan.SlotId);
                WorkerZdo.RemoveCargoAmount(zdo, resourcePrefab, requiredAmount);
                if (!wildGrowthPlan)
                    batchPlanted = FulingShamanGardenRuntime.TryPlantAdditional(worker, zdo, plan, prefab);
                WorkerZdo.ClearGardenPlan(zdo);
            }
            else
            {
                WorkerZdo.RemoveCargoAmount(zdo, resourcePrefab, requiredAmount);
            }
            LocalWorkFallbackRuntime.Clear(zdo);
            WorkerJobProfileRuntime.NoteCompletedAction(zdo, WorkerRole.Gardener, WorkOrder.Garden);
            WorkerZdo.ClearTarget(zdo); zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            string batchInfo = batchPlanted > 0 ? $", FulingShamanBatch=+{batchPlanted} ({batchPlanted + 1} total)" : string.Empty;
            string confirmInfo = wildGrowthPlan ? ", WildGrowth=persistent-respawn-owned" : string.Empty;
            _debugLog?.Invoke($"Garden planted: worker={worker.gameObject.name}, resource={resourcePrefab} x{requiredAmount} -> plant={plantPrefab}, position={plantPoint}, persistentSlot={(hasPlan ? plan.SlotId.ToString() : "none")}{batchInfo}{confirmInfo}");
            return true;
        }

        private static bool YieldsOnly(Piece piece, GameObject item)
        {
            if (piece == null || item == null) return false;
            Plant plant = piece.GetComponent<Plant>();
            if (plant == null || plant.m_grownPrefabs == null || plant.m_grownPrefabs.Length == 0) return false;

            for (int i = 0; i < plant.m_grownPrefabs.Length; i++)
            {
                GameObject grown = plant.m_grownPrefabs[i];
                Pickable pickable = grown != null ? grown.GetComponent<Pickable>() : null;
                if (pickable == null || pickable.m_itemPrefab != item) return false;
            }
            return true;
        }

        internal static string GetDebugSummary()
        {
            if (!EnsureReady()) return "garden recipes=0 (Cultivator discovery not ready)";

            DebugResourceScratch.Clear();
            foreach (string key in ByResource.Keys)
                DebugResourceScratch.Add(key);
            DebugResourceScratch.Sort(StringComparer.OrdinalIgnoreCase);

            int shown = Math.Min(8, DebugResourceScratch.Count);
            string resources = string.Empty;
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) resources += ", ";
                resources += DebugResourceScratch[i];
            }
            if (ByResource.Count > 8) resources += ", ...";

            return $"garden recipes={ByResource.Count}; resources=[{resources}]; mature={HarvestableCropPrefabs.Count}; nativeWildFlora=VW_Flora_*";
        }

        private static GardenRecipe FindRecipeByCropItemPrefab(string cropItemPrefab)
        {
            if (string.IsNullOrWhiteSpace(cropItemPrefab)) return null;

            foreach (GardenRecipe recipe in ByResource.Values)
            {
                if (recipe != null &&
                    string.Equals(recipe.CropItemPrefab, cropItemPrefab, StringComparison.OrdinalIgnoreCase))
                    return recipe;
            }

            return null;
        }

        private static GardenRecipe FindRecipeByPlantPrefab(string plantPrefab)
        {
            if (string.IsNullOrWhiteSpace(plantPrefab)) return null;

            foreach (GardenRecipe recipe in ByResource.Values)
            {
                if (recipe != null &&
                    string.Equals(recipe.PlantPrefab, plantPrefab, StringComparison.OrdinalIgnoreCase))
                    return recipe;
            }

            return null;
        }

        private static bool TryGetSinglePositiveRequirement(Piece piece, out Piece.Requirement requirement)
        {
            requirement = null;
            if (piece == null || piece.m_resources == null) return false;

            for (int i = 0; i < piece.m_resources.Length; i++)
            {
                Piece.Requirement candidate = piece.m_resources[i];
                if (candidate == null || candidate.m_resItem == null || candidate.GetAmount(0) <= 0) continue;
                if (requirement != null) return false;
                requirement = candidate;
            }

            return requirement != null;
        }

        private static float EstimateSmartFloraRadius(GameObject prefab)
        {
            if (prefab == null) return 0.8f;
            try
            {
                Collider[] colliders = prefab.GetComponentsInChildren<Collider>(true);
                float radius = 0.8f;
                for (int i = 0; i < colliders.Length; i++)
                {
                    Collider c = colliders[i];
                    if (c == null) continue;
                    Bounds b = c.bounds;
                    float horizontal = Mathf.Max(b.extents.x, b.extents.z);
                    if (horizontal > radius && horizontal < 6f) radius = horizontal;
                }
                return Mathf.Clamp(radius, 0.5f, 2.5f);
            }
            catch
            {
                return 0.8f;
            }
        }

        private static bool IsSeedLikeName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            return prefabName.IndexOf("seed", StringComparison.OrdinalIgnoreCase) >= 0;
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
