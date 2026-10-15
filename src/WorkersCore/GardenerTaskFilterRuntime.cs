using System;
using System.Collections.Generic;

namespace Valheim_Workers
{
    internal readonly struct GardenerForestryOption
    {
        internal readonly string Key;
        internal readonly string DisplayName;
        internal readonly int RequiredPlantTier;

        internal GardenerForestryOption(string key, string displayName, int requiredPlantTier)
        {
            Key = key ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName;
            RequiredPlantTier = Math.Max(0, requiredPlantTier);
        }
    }

    /// <summary>
    /// Per-worker detail filters for the modular Gardener/Forester profile.
    /// All explicitly planned cultivated crops (including explicitly planned seed plants) share
    /// the normal crop toggle. Automatic stock-driven seed-production no longer exists.
    /// </summary>
    internal static class GardenerTaskFilterRuntime
    {
        private const string NormalCropSetKey = "vw_gardener_normal_crop_set";
        private const string NormalCropAllowedKey = "vw_gardener_normal_crop_allowed";
        private const string SeedProductionSetKey = "vw_gardener_seed_mode_set";
        private const string SeedProductionAllowedKey = "vw_gardener_seed_mode_allowed";
        private const string DisabledForestrySpeciesKey = "vw_gardener_disabled_forestry_species";
        private const char Separator = '|';

        internal static bool AllowNormalCropPlanting(ZDO zdo) => ReadBoolDefaultTrue(zdo, NormalCropSetKey, NormalCropAllowedKey);
        internal static bool AllowSeedProduction(ZDO zdo) => ReadBoolDefaultTrue(zdo, SeedProductionSetKey, SeedProductionAllowedKey);

        internal static void SetNormalCropPlanting(ZDO zdo, bool allowed)
        {
            if (zdo == null) return;
            zdo.Set(NormalCropSetKey, true);
            zdo.Set(NormalCropAllowedKey, allowed);
            ResetGardenDecision(zdo);
        }

        internal static void SetSeedProduction(ZDO zdo, bool allowed)
        {
            if (zdo == null) return;
            zdo.Set(SeedProductionSetKey, true);
            zdo.Set(SeedProductionAllowedKey, allowed);
            ResetGardenDecision(zdo);
        }

        internal static bool IsGardenPlanAllowed(ZDO zdo, WorkerGardenPlan plan)
        {
            if (zdo == null) return true;

            // v0.5.288: WildGrowth is the Greydwarf Shaman's species-exclusive gardener
            // specialization. Berry-bush and Thistle plans remain ordinary persistent Garden
            // slots, but only this species may execute them. The Fuling Shaman keeps its separate
            // area planting/harvesting specialization and therefore cannot plant wild flora.
            // WildGrowth is its own specialization. It must not inherit the normal-crop toggle:
            // a Greydwarf Shaman may be configured as a pure WildGrowth gardener while normal field
            // crops are disabled. Conversely every other species rejects these slots completely.
            if (IsWildGrowthPlan(plan)) return CanUseWildGrowth(zdo);

            return AllowNormalCropPlanting(zdo);
        }

        internal static bool CanUseWildGrowth(ZDO zdo)
        {
            if (zdo == null) return false;
            string species = SpeciesRoleRegistry.NormalizePrefabName(zdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
            return string.Equals(species, "Greydwarf_Shaman", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsWildGrowthPlan(WorkerGardenPlan plan) =>
            IsWildGrowthIdentity(plan.HarvestPrefab, plan.PlantPrefab, plan.ResourcePrefab);

        internal static bool IsWildGrowthIdentity(string harvestPrefab, string plantPrefab, string resourcePrefab = null)
        {
            return IsWildFloraIdentity(harvestPrefab) || IsWildFloraIdentity(plantPrefab) || IsWildFloraIdentity(resourcePrefab);
        }

        private static bool IsWildFloraIdentity(string value)
        {
            string normalized = GardenWorkRuntime.NormalizePrefabName(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalized)) return false;
            return normalized.StartsWith(WorkersWildFloraRuntime.Prefix, StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith("SmartReplant_Flora_", StringComparison.OrdinalIgnoreCase) ||
                   normalized.IndexOf("Raspberry", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("Blueberr", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("Cloudberr", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("Thistle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("Dandelion", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("Mushroom", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool HasCustomGardenFilter(ZDO zdo)
        {
            if (zdo == null) return false;
            return zdo.GetBool(NormalCropSetKey, false);
        }

        internal static bool IsForestrySpeciesAllowed(ZDO zdo, string saplingPrefab)
        {
            if (zdo == null) return true;
            string key = ResolveSpeciesKey(saplingPrefab);
            if (string.IsNullOrWhiteSpace(key)) return true;
            return !ReadDisabledSpecies(zdo).Contains(key);
        }

        internal static void SetForestrySpeciesAllowed(ZDO zdo, string speciesKey, bool allowed)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(speciesKey)) return;
            string key = speciesKey.Trim().ToLowerInvariant();
            HashSet<string> disabled = ReadDisabledSpecies(zdo);
            bool changed = allowed ? disabled.Remove(key) : disabled.Add(key);
            if (!changed) return;

            zdo.Set(DisabledForestrySpeciesKey, Serialize(disabled));
            WorkerZdo.ClearForestryPlan(zdo);
            WorkerZdo.ClearTarget(zdo);
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.ReturnHomeForRest && state != WorkerState.Resting && state != WorkerState.Recover)
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
        }

        internal static bool HasCustomForestryFilter(ZDO zdo) =>
            zdo != null && !string.IsNullOrWhiteSpace(zdo.GetString(DisabledForestrySpeciesKey, string.Empty));

        internal static void CollectForestryOptions(string workerSpecies, List<GardenerForestryOption> output)
        {
            if (output == null) return;
            output.Clear();

            AddIfPlantable(workerSpecies, output, "beech", "Buche", "BeechSeeds", "Beech1");
            AddIfPlantable(workerSpecies, output, "fir", "Tanne", "FirCone", "FirTree");
            AddIfPlantable(workerSpecies, output, "pine", "Kiefer", "PineCone", "PineTree");
            AddIfPlantable(workerSpecies, output, "birch", "Birke", "BirchSeeds", "Birch1");
            AddIfPlantable(workerSpecies, output, "oak", "Eiche", "Acorn", "Oak1");
        }

        internal static string ResolveSpeciesKey(string saplingPrefab)
        {
            string value = ForestryTargetFinder.NormalizePrefabName(saplingPrefab ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            if (value.IndexOf("beech", StringComparison.OrdinalIgnoreCase) >= 0) return "beech";
            if (value.IndexOf("birch", StringComparison.OrdinalIgnoreCase) >= 0) return "birch";
            if (value.IndexOf("oak", StringComparison.OrdinalIgnoreCase) >= 0) return "oak";
            if (value.IndexOf("pine", StringComparison.OrdinalIgnoreCase) >= 0) return "pine";
            if (value.IndexOf("fir", StringComparison.OrdinalIgnoreCase) >= 0) return "fir";
            return value.Trim().ToLowerInvariant();
        }

        private static void AddIfPlantable(string workerSpecies, List<GardenerForestryOption> output,
            string key, string displayName, string seedPrefab, string saplingPrefab)
        {
            int required = ForestryWorkRuntime.GetRequiredPlantTier(seedPrefab, saplingPrefab);
            bool shamanBirch = string.Equals(SpeciesRoleRegistry.NormalizePrefabName(workerSpecies), "Greydwarf_Shaman", StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(key, "birch", StringComparison.OrdinalIgnoreCase);
            int effectiveTier = WorkerTierCatalog.GetEffectiveRoleCapabilityTier(
                workerSpecies, WorkerRole.Gardener, WorkerCapability.PlantSeeds);
            if (!shamanBirch && effectiveTier < required) return;
            output.Add(new GardenerForestryOption(key, displayName, shamanBirch ? 1 : required));
        }

        private static bool ReadBoolDefaultTrue(ZDO zdo, string setKey, string valueKey)
        {
            if (zdo == null) return true;
            return !zdo.GetBool(setKey, false) || zdo.GetBool(valueKey, true);
        }

        private static void ResetGardenDecision(ZDO zdo)
        {
            WorkerZdo.ClearGardenPlan(zdo);
            WorkerZdo.ClearTarget(zdo);
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.ReturnHomeForRest && state != WorkerState.Resting && state != WorkerState.Recover)
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
        }

        private static HashSet<string> ReadDisabledSpecies(ZDO zdo)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (zdo == null) return result;
            string raw = zdo.GetString(DisabledForestrySpeciesKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return result;
            string[] parts = raw.Split(Separator);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i]?.Trim();
                if (!string.IsNullOrWhiteSpace(part)) result.Add(part.ToLowerInvariant());
            }
            return result;
        }

        private static string Serialize(HashSet<string> values)
        {
            if (values == null || values.Count == 0) return string.Empty;
            List<string> ordered = new List<string>(values);
            ordered.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(Separator.ToString(), ordered);
        }
    }
}
