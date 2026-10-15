using System;
using System.Collections.Generic;

namespace Valheim_Workers
{
    internal readonly struct WorkerCategoryOption
    {
        internal readonly string Key;
        internal readonly string DisplayName;

        internal WorkerCategoryOption(string key, string displayName)
        {
            Key = key ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName;
        }
    }

    /// <summary>
    /// Per-worker category filters for the modular Gather/Harvest profile.
    /// Only disabled categories are persisted, so existing workers remain fully enabled by default.
    /// Custom filters are intentionally physical-only for now; background simulation is paused for
    /// that task to keep the first implementation deterministic and avoid simulating a disallowed item.
    /// </summary>
    internal static class GatherHarvestCategoryFilterRuntime
    {
        private const string DisabledGatherKey = "vw_gather_disabled_categories";
        private const string DisabledHarvestKey = "vw_harvest_disabled_categories";
        private const char Separator = '|';

        internal static readonly WorkerCategoryOption[] GatherOptions =
        {
            new WorkerCategoryOption("stone", "Stein"),
            new WorkerCategoryOption("wood", "Holz"),
            new WorkerCategoryOption("workdrops", "Arbeitsdrops / Sonstiges")
        };

        internal static readonly WorkerCategoryOption[] HarvestOptions =
        {
            new WorkerCategoryOption("vegetables", "Gemuese"),
            new WorkerCategoryOption("seeds", "Samenpflanzen"),
            new WorkerCategoryOption("berries", "Beeren"),
            new WorkerCategoryOption("mushrooms", "Pilze"),
            new WorkerCategoryOption("other", "Sonstige Pickables")
        };

        internal static bool HasCustomGatherFilter(ZDO zdo) => HasAny(zdo, DisabledGatherKey);
        internal static bool HasCustomHarvestFilter(ZDO zdo) => HasAny(zdo, DisabledHarvestKey);

        internal static bool IsGatherCategoryAllowed(ZDO zdo, string key) => IsAllowed(zdo, DisabledGatherKey, key);
        internal static bool IsHarvestCategoryAllowed(ZDO zdo, string key) => IsAllowed(zdo, DisabledHarvestKey, key);

        internal static void SetGatherCategoryAllowed(ZDO zdo, string key, bool allowed) => SetAllowed(zdo, DisabledGatherKey, key, allowed);
        internal static void SetHarvestCategoryAllowed(ZDO zdo, string key, bool allowed) => SetAllowed(zdo, DisabledHarvestKey, key, allowed);

        internal static bool IsGatherDropAllowed(ZDO zdo, ItemDrop drop)
        {
            if (drop == null) return false;
            return IsGatherCategoryAllowed(zdo, ClassifyGatherDrop(drop));
        }

        internal static bool IsHarvestPickableAllowed(ZDO zdo, Pickable pickable)
        {
            if (pickable == null) return false;
            return IsHarvestCategoryAllowed(zdo, ClassifyHarvestPickable(pickable));
        }

        internal static string ClassifyGatherDrop(ItemDrop drop)
        {
            string prefab = GathererTargetFinder.GetCargoPrefabName(drop);
            if (string.IsNullOrWhiteSpace(prefab) && drop != null && drop.gameObject != null)
                prefab = GathererTargetFinder.NormalizePrefabName(drop.gameObject.name);
            string value = (prefab ?? string.Empty).Trim();

            if (EqualsAny(value, "Stone", "Flint")) return "stone";
            if (ContainsAny(value, "Wood", "RoundLog", "ElderBark", "YggdrasilWood", "Blackwood")) return "wood";
            return "workdrops";
        }

        internal static string ClassifyHarvestPickable(Pickable pickable)
        {
            string identity = GardenHarvestTargetFinder.GetIdentityName(pickable);
            string value = (identity ?? string.Empty).Trim();

            if (ContainsAny(value, "Raspberry", "Blueberr", "Cloudberr")) return "berries";
            if (value.IndexOf("Mushroom", StringComparison.OrdinalIgnoreCase) >= 0) return "mushrooms";
            if (value.IndexOf("Seed", StringComparison.OrdinalIgnoreCase) >= 0) return "seeds";

            // Anything discovered as a real Cultivator crop is a field crop. Seed-producing
            // variants were handled above; remaining mature crop identities are vegetables/crops.
            if (GardenWorkRuntime.IsHarvestableCropPrefab(value)) return "vegetables";
            return "other";
        }

        private static bool EqualsAny(string value, params string[] candidates)
        {
            if (string.IsNullOrEmpty(value) || candidates == null) return false;
            for (int i = 0; i < candidates.Length; i++)
                if (string.Equals(value, candidates[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool ContainsAny(string value, params string[] parts)
        {
            if (string.IsNullOrEmpty(value) || parts == null) return false;
            for (int i = 0; i < parts.Length; i++)
                if (!string.IsNullOrEmpty(parts[i]) && value.IndexOf(parts[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool HasAny(ZDO zdo, string storageKey)
        {
            return zdo != null && !string.IsNullOrWhiteSpace(zdo.GetString(storageKey, string.Empty));
        }

        private static bool IsAllowed(ZDO zdo, string storageKey, string key)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(key)) return true;
            return !ReadDisabled(zdo, storageKey).Contains(key.Trim().ToLowerInvariant());
        }

        private static void SetAllowed(ZDO zdo, string storageKey, string key, bool allowed)
        {
            if (zdo == null || string.IsNullOrWhiteSpace(key)) return;
            string normalized = key.Trim().ToLowerInvariant();
            HashSet<string> disabled = ReadDisabled(zdo, storageKey);
            bool changed = allowed ? disabled.Remove(normalized) : disabled.Add(normalized);
            if (!changed) return;

            zdo.Set(storageKey, Serialize(disabled));
            WorkerZdo.ClearTarget(zdo);
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.ReturnHomeForRest && state != WorkerState.Resting && state != WorkerState.Recover)
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
        }

        private static HashSet<string> ReadDisabled(ZDO zdo, string storageKey)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (zdo == null) return result;
            string raw = zdo.GetString(storageKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return result;
            string[] parts = raw.Split(Separator);
            for (int i = 0; i < parts.Length; i++)
            {
                string value = parts[i]?.Trim();
                if (!string.IsNullOrWhiteSpace(value)) result.Add(value.ToLowerInvariant());
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
