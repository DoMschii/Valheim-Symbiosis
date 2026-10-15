using System;

namespace Valheim_Workers
{
    /// <summary>
    /// Capability gate for the player-facing Cook job. The historic internal Baker role/order
    /// names are intentionally kept for save compatibility; gameplay progression is tiered here.
    /// T1 cooks handle the cauldron, T2 additionally the preparation table, and T3 unlocks the
    /// complete production chain including windmill and stone oven.
    /// </summary>
    internal static class CookingWorkerTierRuntime
    {
        internal static WorkerTier GetCookingTier(Character worker, ZDO zdo)
        {
            string species = string.Empty;
            if (zdo != null)
                species = SpeciesRoleRegistry.NormalizePrefabName(zdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
            if (string.IsNullOrEmpty(species) && worker != null && worker.gameObject != null)
                species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            return string.IsNullOrEmpty(species)
                ? WorkerTier.None
                : WorkerSpeciesJobCatalog.GetJobTier(species, WorkerRole.Baker);
        }

        internal static WorkerTier GetCookingTier(ZDO zdo) => GetCookingTier(null, zdo);

        internal static bool CanUseStation(Character worker, ZDO zdo, ProductionStationKind kind)
        {
            WorkerTier tier = GetCookingTier(worker, zdo);
            if (tier < WorkerTier.Tier1) return false;
            if (kind == ProductionStationKind.Cauldron) return true;
            if (tier >= WorkerTier.Tier2 && kind == ProductionStationKind.Preparation) return true;
            if (tier >= WorkerTier.Tier3 && (kind == ProductionStationKind.Windmill || kind == ProductionStationKind.Oven)) return true;
            return false;
        }

        internal static bool CanUseRecipe(Character worker, ZDO zdo, CookingRecipeSpec recipe) =>
            recipe != null && CanUseStation(worker, zdo, recipe.Kind);

        internal static bool CanUseAdvancedProduction(Character worker, ZDO zdo) =>
            GetCookingTier(worker, zdo) >= WorkerTier.Tier3;

        internal static bool CanUseAdvancedProduction(ZDO zdo) => CanUseAdvancedProduction(null, zdo);

        internal static string DescribeTier(Character worker, ZDO zdo)
        {
            switch (GetCookingTier(worker, zdo))
            {
                case WorkerTier.Tier1: return "T1 Koch: Kessel";
                case WorkerTier.Tier2: return "T2 Koch: Kessel + Zubereitungstisch";
                case WorkerTier.Tier3: return "T3 Koch: alle Koch-/Produktionsstationen";
                default: return "kein Kochzugang";
            }
        }
    }
}
