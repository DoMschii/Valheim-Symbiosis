namespace Valheim_Workers
{
    internal static class WorkerSkillCatalog
    {
        // Capability tiers are species-driven and centralized in WorkerTierCatalog. Job runtimes
        // remain role-driven and only ask for the capability level they need.
        internal static int GetWoodcutTier(Character worker) =>
            WorkerTierCatalog.GetCapabilityTier(worker, WorkerCapability.DamageWood);

        internal static int GetWoodcutTier(string species) =>
            WorkerTierCatalog.GetCapabilityTier(species, WorkerCapability.DamageWood);

        internal static int GetStoneworkTier(Character worker) =>
            WorkerTierCatalog.GetCapabilityTier(worker, WorkerCapability.DamageStone);

        internal static int GetStoneworkTier(string species) =>
            WorkerTierCatalog.GetCapabilityTier(species, WorkerCapability.DamageStone);

        internal static WorkerTier GetWorkerTier(Character worker) =>
            WorkerTierCatalog.GetWorkerTier(worker);

        internal static int GetGatherCarryCapacity(Character worker)
        {
            if (worker == null) return 1;
            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            switch (species)
            {
                // First small collector: deliberately modest capacity so the future Hauler remains
                // valuable for bulk logistics instead of the Gatherer replacing that role.
                case "Greyling": return 5;
                default: return 1;
            }
        }

        internal static float GetWoodcutDamage(Character worker)
        {
            string species = worker != null ? SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name) : string.Empty;
            switch (species)
            {
                case "Greydwarf_Elite": return 20f;
                // Tier-2 lumberjack; recruitment is available through the generic F7/debug path.
                case "Draugr": return 28f;
                case "Troll": return 42f;
                default: return 12f;
            }
        }

        internal static float GetStoneworkDamage(Character worker)
        {
            string species = worker != null ? SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name) : string.Empty;
            switch (species)
            {
                case "StoneGolem": return 55f;
                default: return 20f;
            }
        }
    }
}
