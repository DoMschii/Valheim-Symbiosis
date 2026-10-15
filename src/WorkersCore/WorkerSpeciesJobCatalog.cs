using System;
using System.Collections.Generic;

namespace Valheim_Workers
{
    internal readonly struct WorkerJobAccess
    {
        internal readonly WorkerRole Role;
        internal readonly WorkerTier Tier;

        internal WorkerJobAccess(WorkerRole role, WorkerTier tier)
        {
            Role = role;
            Tier = tier;
        }
    }

    /// <summary>
    /// Central species -> job-access matrix.
    ///
    /// Job runtimes remain role-driven and are intentionally NOT duplicated per creature.
    /// A species only declares which existing jobs it may use and at which job tier.
    /// DefaultRole is the species' stable internal compatibility role. Player-facing work is no
    /// longer selected by switching roles; additional specialties are exposed as tasks/priorities
    /// on that stable worker profile. The extra role entries remain useful capability metadata.
    /// </summary>
    internal sealed class WorkerSpeciesJobProfile
    {
        internal readonly string Species;
        internal readonly WorkerRole DefaultRole;
        private readonly Dictionary<WorkerRole, WorkerTier> _jobs;

        internal WorkerSpeciesJobProfile(string species, WorkerRole defaultRole, params WorkerJobAccess[] jobs)
        {
            Species = species ?? string.Empty;
            DefaultRole = defaultRole;
            _jobs = new Dictionary<WorkerRole, WorkerTier>();

            if (jobs != null)
            {
                for (int i = 0; i < jobs.Length; i++)
                {
                    WorkerJobAccess access = jobs[i];
                    if (access.Role == WorkerRole.None || access.Tier == WorkerTier.None) continue;
                    _jobs[access.Role] = access.Tier;
                }
            }

            // The default role must always be a valid permission. Keep this defensive fallback so
            // a future profile typo cannot make an existing creature unrecruitable.
            if (DefaultRole != WorkerRole.None && !_jobs.ContainsKey(DefaultRole))
                _jobs[DefaultRole] = WorkerTier.Tier1;
        }

        internal bool CanUse(WorkerRole role) => role != WorkerRole.None && _jobs.ContainsKey(role);

        internal WorkerTier GetTier(WorkerRole role)
        {
            return _jobs.TryGetValue(role, out WorkerTier tier) ? tier : WorkerTier.None;
        }

        internal IEnumerable<KeyValuePair<WorkerRole, WorkerTier>> GetJobs() => _jobs;
    }

    internal static class WorkerSpeciesJobCatalog
    {
        private static WorkerJobAccess Job(WorkerRole role, WorkerTier tier) => new WorkerJobAccess(role, tier);

        private static readonly Dictionary<string, WorkerSpeciesJobProfile> Profiles =
            new Dictionary<string, WorkerSpeciesJobProfile>(StringComparer.OrdinalIgnoreCase)
            {
                // Existing v0.5.05 defaults are preserved exactly.
                ["Greyling"] = new WorkerSpeciesJobProfile(
                    "Greyling", WorkerRole.Gatherer,
                    Job(WorkerRole.Gatherer, WorkerTier.Tier1)),

                ["Greydwarf"] = new WorkerSpeciesJobProfile(
                    "Greydwarf", WorkerRole.Hauler,
                    Job(WorkerRole.Hauler, WorkerTier.Tier1)),

                ["Greydwarf_Elite"] = new WorkerSpeciesJobProfile(
                    "Greydwarf_Elite", WorkerRole.Lumberjack,
                    Job(WorkerRole.Lumberjack, WorkerTier.Tier1)),

                ["Greydwarf_Shaman"] = new WorkerSpeciesJobProfile(
                    "Greydwarf_Shaman", WorkerRole.Gardener,
                    Job(WorkerRole.Gardener, WorkerTier.Tier1)),

                ["Draugr"] = new WorkerSpeciesJobProfile(
                    "Draugr", WorkerRole.Lumberjack,
                    Job(WorkerRole.Lumberjack, WorkerTier.Tier2)),

                ["Draugr_Elite"] = new WorkerSpeciesJobProfile(
                    "Draugr_Elite", WorkerRole.Butcher,
                    Job(WorkerRole.Butcher, WorkerTier.Tier2)),

                // Hunter remains the default. Guard access is declared centrally for the existing
                // design where the same ranged Draugr may later switch between Hunter and Guard.
                ["Draugr_Ranged"] = new WorkerSpeciesJobProfile(
                    "Draugr_Ranged", WorkerRole.Hunter,
                    Job(WorkerRole.Hunter, WorkerTier.Tier2),
                    Job(WorkerRole.Guard, WorkerTier.Tier2)),

                // Legacy compatibility only: Lox are normal tameables/mounts again. Keeping the
                // profile lets old saves containing a Worker-Lox continue to simulate safely.
                ["Lox"] = new WorkerSpeciesJobProfile(
                    "Lox", WorkerRole.Hauler,
                    Job(WorkerRole.Hauler, WorkerTier.Tier3)),

                ["Drake"] = new WorkerSpeciesJobProfile(
                    "Drake", WorkerRole.Hauler,
                    Job(WorkerRole.Hauler, WorkerTier.Tier2)),

                ["Troll"] = new WorkerSpeciesJobProfile(
                    "Troll", WorkerRole.Lumberjack,
                    Job(WorkerRole.Lumberjack, WorkerTier.Tier3)),

                ["StoneGolem"] = new WorkerSpeciesJobProfile(
                    "StoneGolem", WorkerRole.Stonemason,
                    Job(WorkerRole.Stonemason, WorkerTier.Tier3)),

                ["Neck"] = new WorkerSpeciesJobProfile(
                    "Neck", WorkerRole.Fisher,
                    Job(WorkerRole.Fisher, WorkerTier.Tier1)),

                // Legacy compatibility only: ordinary wolves are livestock/breeding animals and
                // are no longer recruitable/spawnable as Workers. Keeping this mapping prevents
                // old saves containing a Worker-Wolf from losing its persisted Guard role.
                ["Wolf"] = new WorkerSpeciesJobProfile(
                    "Wolf", WorkerRole.Guard,
                    Job(WorkerRole.Guard, WorkerTier.Tier2)),

                ["Surtling"] = new WorkerSpeciesJobProfile(
                    "Surtling", WorkerRole.Firekeeper,
                    Job(WorkerRole.Firekeeper, WorkerTier.Tier2)),

                ["Fuling"] = new WorkerSpeciesJobProfile(
                    "Fuling", WorkerRole.Smith,
                    Job(WorkerRole.Smith, WorkerTier.Tier2)),

                ["Fuling_Shaman"] = new WorkerSpeciesJobProfile(
                    "Fuling_Shaman", WorkerRole.Gardener,
                    Job(WorkerRole.Gardener, WorkerTier.Tier3),
                    Job(WorkerRole.Baker, WorkerTier.Tier3)),

                ["Fuling_Berserker"] = new WorkerSpeciesJobProfile(
                    "Fuling_Berserker", WorkerRole.Stonemason,
                    Job(WorkerRole.Stonemason, WorkerTier.Tier3)),

                // The vanilla Dvergr rogue carries a crossbow and therefore belongs in the
                // ranged combat branch rather than the production branch. Hunter is the default;
                // Guard can be selected explicitly for the same Tier-3 specialist.
                ["Dverger"] = new WorkerSpeciesJobProfile(
                    "Dverger", WorkerRole.Hunter,
                    Job(WorkerRole.Hunter, WorkerTier.Tier3),
                    Job(WorkerRole.Guard, WorkerTier.Tier3))
            };

        internal static bool TryGetProfile(string species, out WorkerSpeciesJobProfile profile)
        {
            return Profiles.TryGetValue(SpeciesRoleRegistry.NormalizePrefabName(species), out profile);
        }

        internal static bool TryGetDefaultRole(string species, out WorkerRole role)
        {
            if (TryGetProfile(species, out WorkerSpeciesJobProfile profile))
            {
                role = profile.DefaultRole;
                return role != WorkerRole.None;
            }

            role = WorkerRole.None;
            return false;
        }

        internal static bool CanUseRole(string species, WorkerRole role)
        {
            return TryGetProfile(species, out WorkerSpeciesJobProfile profile) && profile.CanUse(role);
        }

        internal static WorkerTier GetJobTier(string species, WorkerRole role)
        {
            return TryGetProfile(species, out WorkerSpeciesJobProfile profile)
                ? profile.GetTier(role)
                : WorkerTier.None;
        }

        internal static IEnumerable<WorkerSpeciesJobProfile> GetAll() => Profiles.Values;
    }
}
