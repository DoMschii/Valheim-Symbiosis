using System;
using System.Collections.Generic;

namespace Valheim_Workers
{
    /// <summary>
    /// Compatibility adapter for role-driven runtimes. The central source of truth for which jobs
    /// a species may use is WorkerSpeciesJobCatalog. The default role is now a stable internal
    /// anchor; player-facing job choice lives in the per-worker task/priority profile.
    /// </summary>
    internal static class SpeciesRoleRegistry
    {
        internal static bool TryGetRole(string prefabName, out WorkerRole role) =>
            WorkerSpeciesJobCatalog.TryGetDefaultRole(NormalizePrefabName(prefabName), out role);

        internal static bool CanUseRole(string prefabName, WorkerRole role) =>
            WorkerSpeciesJobCatalog.CanUseRole(NormalizePrefabName(prefabName), role);

        internal static WorkerTier GetJobTier(string prefabName, WorkerRole role) =>
            WorkerSpeciesJobCatalog.GetJobTier(NormalizePrefabName(prefabName), role);

        internal static IEnumerable<KeyValuePair<string, WorkerRole>> GetMappings()
        {
            foreach (WorkerSpeciesJobProfile profile in WorkerSpeciesJobCatalog.GetAll())
                yield return new KeyValuePair<string, WorkerRole>(profile.Species, profile.DefaultRole);
        }

        internal static IEnumerable<KeyValuePair<WorkerRole, WorkerTier>> GetAllowedJobs(string prefabName)
        {
            if (WorkerSpeciesJobCatalog.TryGetProfile(NormalizePrefabName(prefabName), out WorkerSpeciesJobProfile profile))
                return profile.GetJobs();
            return Array.Empty<KeyValuePair<WorkerRole, WorkerTier>>();
        }

        internal static string NormalizePrefabName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return string.Empty;

            int cloneIndex = objectName.IndexOf("(Clone)", StringComparison.Ordinal);
            if (cloneIndex >= 0) objectName = objectName.Substring(0, cloneIndex);

            objectName = objectName.Trim();

            // Keep player-facing canonical species names separate from Valheim's internal prefab names.
            // This is especially important for the Plains Fuling family, whose vanilla prefabs still
            // use the historic Goblin names internally.
            if (string.Equals(objectName, "Hatchling", StringComparison.OrdinalIgnoreCase))
                return "Drake";
            if (string.Equals(objectName, "Goblin", StringComparison.OrdinalIgnoreCase))
                return "Fuling";
            if (string.Equals(objectName, "GoblinShaman", StringComparison.OrdinalIgnoreCase))
                return "Fuling_Shaman";
            if (string.Equals(objectName, "GoblinBrute", StringComparison.OrdinalIgnoreCase))
                return "Fuling_Berserker";

            return objectName;
        }
    }
}
