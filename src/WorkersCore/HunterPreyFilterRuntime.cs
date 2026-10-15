using System;
using System.Collections.Generic;

namespace Valheim_Workers
{
    internal readonly struct HunterPreyOption
    {
        internal readonly string Key;
        internal readonly string DisplayName;
        internal readonly int RequiredTier;

        internal HunterPreyOption(string key, string displayName, int requiredTier = 1)
        {
            Key = key ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName;
            RequiredTier = Math.Max(1, requiredTier);
        }
    }

    /// <summary>
    /// Per-worker prey filter for Hunters. Only disabled prey groups are persisted so existing
    /// Hunters keep their previous behaviour by default while newly supported prey is enabled
    /// automatically unless the player explicitly disables it.
    /// </summary>
    internal static class HunterPreyFilterRuntime
    {
        private const string DisabledPreyKey = "vw_hunter_disabled_prey";
        private const char Separator = '|';

        internal static readonly HunterPreyOption[] Options =
        {
            new HunterPreyOption("deer", "Reh", 1),
            new HunterPreyOption("boar", "Wildschwein", 1),
            new HunterPreyOption("neck", "Nixe", 1),
            new HunterPreyOption("leech", "Blutegel", 2),
            new HunterPreyOption("swamp_slime", "Schleime", 2)
        };

        private static readonly HashSet<string> EmptyDisabled =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashSet<string>> ParsedDisabledCache =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        internal static bool HasCustomFilter(ZDO workerZdo)
        {
            return workerZdo != null && !string.IsNullOrWhiteSpace(workerZdo.GetString(DisabledPreyKey, string.Empty));
        }

        internal static bool IsPreyAllowed(ZDO workerZdo, Character candidate)
        {
            if (candidate == null || candidate.gameObject == null) return false;
            return IsPreyAllowed(workerZdo, SpeciesRoleRegistry.NormalizePrefabName(candidate.gameObject.name));
        }

        internal static bool IsPreyAllowed(ZDO workerZdo, string prefabName)
        {
            string key = ResolvePreyKey(prefabName);
            if (string.IsNullOrEmpty(key)) return false;
            if (workerZdo == null) return true;
            if (!IsOptionAvailableForWorker(workerZdo, key)) return false;

            string raw = workerZdo.GetString(DisabledPreyKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return true;
            return !GetParsedDisabled(raw).Contains(key);
        }

        internal static bool SetPreyAllowed(ZDO workerZdo, string preyKey, bool allowed)
        {
            if (workerZdo == null || string.IsNullOrWhiteSpace(preyKey)) return false;
            string normalized = preyKey.Trim().ToLowerInvariant();
            HashSet<string> disabled = ReadDisabledCopy(workerZdo);
            bool changed = allowed ? disabled.Remove(normalized) : disabled.Add(normalized);
            if (!changed) return true;

            workerZdo.Set(DisabledPreyKey, Serialize(disabled));
            WorkerZdo.ClearTarget(workerZdo);
            WorkerState state = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.ReturnHomeForRest && state != WorkerState.Resting && state != WorkerState.Recover)
                workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            return true;
        }

        internal static void ClearCaches()
        {
            ParsedDisabledCache.Clear();
        }

        internal static string ResolvePreyKey(string prefabName)
        {
            string value = SpeciesRoleRegistry.NormalizePrefabName(prefabName);
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            if (string.Equals(value, "Deer", StringComparison.OrdinalIgnoreCase)) return "deer";
            if (string.Equals(value, "Boar", StringComparison.OrdinalIgnoreCase)) return "boar";
            if (string.Equals(value, "Neck", StringComparison.OrdinalIgnoreCase)) return "neck";
            if (string.Equals(value, "Leech", StringComparison.OrdinalIgnoreCase)) return "leech";
            if (string.Equals(value, "Blob", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "BlobElite", StringComparison.OrdinalIgnoreCase)) return "swamp_slime";
            return string.Empty;
        }


        internal static bool IsOptionAvailableForWorker(ZDO workerZdo, HunterPreyOption option)
        {
            return workerZdo != null && GetHunterTier(workerZdo) >= option.RequiredTier;
        }

        internal static bool IsOptionAvailableForWorker(ZDO workerZdo, string preyKey)
        {
            if (workerZdo == null || string.IsNullOrWhiteSpace(preyKey)) return false;
            for (int i = 0; i < Options.Length; i++)
            {
                HunterPreyOption option = Options[i];
                if (string.Equals(option.Key, preyKey, StringComparison.OrdinalIgnoreCase))
                    return IsOptionAvailableForWorker(workerZdo, option);
            }
            return false;
        }

        private static int GetHunterTier(ZDO workerZdo)
        {
            if (workerZdo == null) return 0;
            string species = SpeciesRoleRegistry.NormalizePrefabName(workerZdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
            return WorkerTierCatalog.GetCapabilityTier(species, WorkerCapability.HuntAnimals);
        }

        internal static string DescribeAllowed(ZDO workerZdo)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < Options.Length; i++)
            {
                HunterPreyOption option = Options[i];
                if (!IsOptionAvailableForWorker(workerZdo, option)) continue;
                if (IsPreyAllowed(workerZdo, option.Key)) names.Add(option.DisplayName);
            }
            return names.Count == 0 ? "keine" : string.Join(", ", names);
        }

        private static HashSet<string> ReadDisabledCopy(ZDO workerZdo)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (workerZdo == null) return result;

            string raw = workerZdo.GetString(DisabledPreyKey, string.Empty);
            HashSet<string> cached = GetParsedDisabled(raw);
            foreach (string value in cached) result.Add(value);
            return result;
        }

        private static HashSet<string> GetParsedDisabled(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return EmptyDisabled;
            if (ParsedDisabledCache.TryGetValue(raw, out HashSet<string> cached)) return cached;

            HashSet<string> parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] parts = raw.Split(Separator);
            for (int i = 0; i < parts.Length; i++)
            {
                string value = parts[i]?.Trim();
                if (!string.IsNullOrWhiteSpace(value)) parsed.Add(value.ToLowerInvariant());
            }

            if (ParsedDisabledCache.Count >= 128) ParsedDisabledCache.Clear();
            ParsedDisabledCache[raw] = parsed;
            return parsed;
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
