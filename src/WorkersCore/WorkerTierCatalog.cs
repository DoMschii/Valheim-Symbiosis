using System;
using System.Collections.Generic;

namespace Valheim_Workers
{
    internal enum WorkerTier
    {
        None = 0,
        Tier1 = 1,
        Tier2 = 2,
        Tier3 = 3
    }

    internal sealed class WorkerTierProfile
    {
        internal readonly string Species;
        internal readonly WorkerTier Tier;
        internal readonly Dictionary<WorkerCapability, int> CapabilityTiers;

        internal WorkerTierProfile(string species, WorkerTier tier, params KeyValuePair<WorkerCapability, int>[] capabilityTiers)
        {
            Species = species;
            Tier = tier;
            CapabilityTiers = new Dictionary<WorkerCapability, int>();
            if (capabilityTiers == null) return;

            for (int i = 0; i < capabilityTiers.Length; i++)
                CapabilityTiers[capabilityTiers[i].Key] = Math.Max(0, capabilityTiers[i].Value);
        }

        internal int GetCapabilityTier(WorkerCapability capability)
        {
            if (CapabilityTiers.TryGetValue(capability, out int value))
                return value;
            return (int)Tier;
        }
    }

    /// <summary>
    /// Central progression layer for worker species. Tiers unlock harder resources / more complex
    /// work; they are not a generic speed multiplier. Role AI should ask this catalog whether a
    /// worker has the required capability tier before selecting a target.
    ///
    /// Important: profiles may be defined before a creature has a finished recruitment path. That
    /// lets future roles share the same progression rules without coupling recruitment to job AI.
    /// </summary>
    internal static class WorkerTierCatalog
    {
        private static KeyValuePair<WorkerCapability, int> Cap(WorkerCapability capability, int tier) =>
            new KeyValuePair<WorkerCapability, int>(capability, tier);

        private static readonly Dictionary<string, WorkerTierProfile> Profiles =
            new Dictionary<string, WorkerTierProfile>(StringComparer.OrdinalIgnoreCase)
            {
                // Black Forest / starter workforce.
                ["Greyling"] = new WorkerTierProfile("Greyling", WorkerTier.Tier1),
                ["Greydwarf"] = new WorkerTierProfile("Greydwarf", WorkerTier.Tier1),
                ["Greydwarf_Elite"] = new WorkerTierProfile(
                    "Greydwarf_Elite",
                    WorkerTier.Tier1,
                    Cap(WorkerCapability.DamageWood, 1)),
                ["Greydwarf_Shaman"] = new WorkerTierProfile("Greydwarf_Shaman", WorkerTier.Tier1),
                ["Neck"] = new WorkerTierProfile("Neck", WorkerTier.Tier1),

                // Swamp / mountain / plains specialists. Some are progression declarations only;
                // recruitment/role implementations can be added later without changing tier rules.
                ["Draugr"] = new WorkerTierProfile(
                    "Draugr",
                    WorkerTier.Tier2,
                    Cap(WorkerCapability.DamageWood, 2)),
                ["Draugr_Elite"] = new WorkerTierProfile(
                    "Draugr_Elite",
                    WorkerTier.Tier2,
                    Cap(WorkerCapability.ManageLivestock, 2),
                    Cap(WorkerCapability.UseDoors, 2)),
                ["Draugr_Ranged"] = new WorkerTierProfile(
                    "Draugr_Ranged",
                    WorkerTier.Tier2,
                    Cap(WorkerCapability.HuntAnimals, 2),
                    Cap(WorkerCapability.UseHunterStand, 2),
                    Cap(WorkerCapability.Combat, 2)),
                ["Surtling"] = new WorkerTierProfile("Surtling", WorkerTier.Tier2),
                ["Wolf"] = new WorkerTierProfile("Wolf", WorkerTier.Tier2),
                ["Drake"] = new WorkerTierProfile(
                    "Drake",
                    WorkerTier.Tier2,
                    Cap(WorkerCapability.TransportBetweenContainers, 2),
                    Cap(WorkerCapability.AirTransport, 2)),
                ["Fuling"] = new WorkerTierProfile("Fuling", WorkerTier.Tier2),
                ["Fuling_Shaman"] = new WorkerTierProfile(
                    "Fuling_Shaman",
                    WorkerTier.Tier3,
                    Cap(WorkerCapability.PlantSeeds, 3)),
                ["Fuling_Berserker"] = new WorkerTierProfile(
                    "Fuling_Berserker",
                    WorkerTier.Tier3,
                    Cap(WorkerCapability.DamageStone, 3),
                    Cap(WorkerCapability.DamageOre, 3)),

                // Heavy specialists.
                ["Troll"] = new WorkerTierProfile(
                    "Troll",
                    WorkerTier.Tier3,
                    Cap(WorkerCapability.DamageWood, 3)),
                ["StoneGolem"] = new WorkerTierProfile(
                    "StoneGolem",
                    WorkerTier.Tier3,
                    Cap(WorkerCapability.DamageStone, 3)),
                ["Lox"] = new WorkerTierProfile("Lox", WorkerTier.Tier3),
                ["Dverger"] = new WorkerTierProfile("Dverger", WorkerTier.Tier3,
                    Cap(WorkerCapability.HuntAnimals, 3),
                    Cap(WorkerCapability.UseHunterStand, 3),
                    Cap(WorkerCapability.Combat, 3),
                    Cap(WorkerCapability.UseDoors, 3))
            };

        /// <summary>
        /// Job-specific access tier from the central species/job matrix. This is intentionally
        /// separate from capability-tier overrides so existing target filters keep their proven
        /// behaviour while new workers can be granted access to an existing job in one place.
        /// </summary>
        internal static WorkerTier GetJobTier(Character worker, WorkerRole role)
        {
            if (worker == null) return WorkerTier.None;
            return SpeciesRoleRegistry.GetJobTier(worker.gameObject.name, role);
        }

        internal static WorkerTier GetJobTier(string species, WorkerRole role) =>
            SpeciesRoleRegistry.GetJobTier(species, role);

        internal static bool CanUseRole(Character worker, WorkerRole role)
        {
            if (worker == null) return false;
            return SpeciesRoleRegistry.CanUseRole(worker.gameObject.name, role);
        }

        internal static WorkerTier GetWorkerTier(Character worker)
        {
            if (worker == null) return WorkerTier.None;
            return GetWorkerTier(SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name));
        }

        internal static WorkerTier GetWorkerTier(string species)
        {
            return TryGetProfile(species, out WorkerTierProfile profile) ? profile.Tier : WorkerTier.None;
        }

        internal static int GetCapabilityTier(Character worker, WorkerCapability capability)
        {
            if (worker == null) return 0;
            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            return GetCapabilityTier(species, capability);
        }

        internal static int GetCapabilityTier(string species, WorkerCapability capability)
        {
            return TryGetProfile(species, out WorkerTierProfile profile)
                ? profile.GetCapabilityTier(capability)
                : 0;
        }

        /// <summary>
        /// Jobs are cumulative: a higher-tier Gardener/Forester must retain every lower-tier
        /// planting capability. Keep this in one place so UI filters and runtime target selection
        /// cannot drift apart when a species has both a job tier and an explicit capability tier.
        /// </summary>
        internal static int GetEffectiveRoleCapabilityTier(Character worker, WorkerRole role, WorkerCapability capability)
        {
            if (worker == null) return 0;
            return Math.Max(GetCapabilityTier(worker, capability), (int)GetJobTier(worker, role));
        }

        internal static int GetEffectiveRoleCapabilityTier(string species, WorkerRole role, WorkerCapability capability)
        {
            return Math.Max(GetCapabilityTier(species, capability), (int)GetJobTier(species, role));
        }

        internal static bool MeetsCapabilityTier(Character worker, WorkerCapability capability, int requiredTier)
        {
            if (requiredTier <= 0) return true;
            return GetCapabilityTier(worker, capability) >= requiredTier;
        }

        internal static string DisplayTier(Character worker) => DisplayTier(GetWorkerTier(worker));

        internal static string DisplayTier(WorkerTier tier)
        {
            switch (tier)
            {
                case WorkerTier.Tier1: return "Stufe 1";
                case WorkerTier.Tier2: return "Stufe 2";
                case WorkerTier.Tier3: return "Stufe 3";
                default: return "Stufe ?";
            }
        }

        internal static bool TryGetProfile(string species, out WorkerTierProfile profile) =>
            Profiles.TryGetValue(SpeciesRoleRegistry.NormalizePrefabName(species), out profile);

        internal static IEnumerable<WorkerTierProfile> GetAll() => Profiles.Values;
    }
}
