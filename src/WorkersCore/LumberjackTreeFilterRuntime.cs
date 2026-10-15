using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal readonly struct LumberjackTreeOption
    {
        internal readonly string Key;
        internal readonly string DisplayName;
        internal readonly int RequiredToolTier;

        internal LumberjackTreeOption(string key, string displayName, int requiredToolTier)
        {
            Key = key ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName;
            RequiredToolTier = Math.Max(0, requiredToolTier);
        }
    }

    /// <summary>
    /// Per-worker standing-tree filter for Lumberjacks.
    ///
    /// The ZDO stores only disabled species. This keeps the migration/default deterministic:
    /// existing workers still accept every tree their capability tier can actually cut, while a
    /// newly discovered tree species is enabled automatically unless the player explicitly disables
    /// that species later.
    ///
    /// The filter applies only to standing TreeBase targets. Logs/stumps from an already felled tree
    /// remain cleanup work so a Lumberjack never abandons half of a physical tree cycle.
    /// </summary>
    internal static class LumberjackTreeFilterRuntime
    {
        private const string DisabledSpeciesKey = "vw_lumber_disabled_species";
        private const char Separator = '|';

        private static readonly Dictionary<string, LumberjackTreeOption> OptionMap =
            new Dictionary<string, LumberjackTreeOption>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<LumberjackTreeOption> CachedOptions =
            new List<LumberjackTreeOption>(16);
        private static readonly HashSet<string> EmptyDisabled =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashSet<string>> ParsedDisabledCache =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private static ZNetScene _cachedScene;
        private static bool _optionCacheReady;

        internal static bool HasCustomFilter(ZDO workerZdo)
        {
            return workerZdo != null && !string.IsNullOrWhiteSpace(workerZdo.GetString(DisabledSpeciesKey, string.Empty));
        }

        internal static bool IsStandingTreeAllowed(ZDO workerZdo, WoodTarget target)
        {
            if (target == null || target.Kind != WoodTargetKind.TreeBase || target.GameObject == null) return true;
            return IsSpeciesAllowed(workerZdo, TreeTargetFinder.NormalizePrefabName(target.GameObject.name));
        }

        internal static bool IsSpeciesAllowed(ZDO workerZdo, string prefabName)
        {
            if (workerZdo == null) return true;
            string key = ResolveSpeciesKey(prefabName);
            if (string.IsNullOrEmpty(key)) return true;

            string raw = workerZdo.GetString(DisabledSpeciesKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return true;
            return !GetParsedDisabled(raw).Contains(key);
        }

        internal static bool SetSpeciesAllowed(ZDO workerZdo, string speciesKey, bool allowed)
        {
            if (workerZdo == null || string.IsNullOrWhiteSpace(speciesKey)) return false;
            string normalized = speciesKey.Trim().ToLowerInvariant();
            HashSet<string> disabled = ReadDisabledCopy(workerZdo);
            bool changed = allowed ? disabled.Remove(normalized) : disabled.Add(normalized);
            if (!changed) return true;

            workerZdo.Set(DisabledSpeciesKey, Serialize(disabled));
            WorkerZdo.ClearTarget(workerZdo);
            WorkerState state = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.ReturnHomeForRest && state != WorkerState.Resting && state != WorkerState.Recover)
                workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            return true;
        }

        internal static void CollectVisibleOptions(int maxToolTier, List<LumberjackTreeOption> output)
        {
            if (output == null) return;
            output.Clear();
            EnsureOptionCache();

            int tier = Math.Max(0, maxToolTier);
            for (int i = 0; i < CachedOptions.Count; i++)
            {
                LumberjackTreeOption option = CachedOptions[i];
                if (option.RequiredToolTier <= tier)
                    output.Add(option);
            }
        }

        internal static void ClearCaches()
        {
            OptionMap.Clear();
            CachedOptions.Clear();
            ParsedDisabledCache.Clear();
            _cachedScene = null;
            _optionCacheReady = false;
        }

        private static void EnsureOptionCache()
        {
            ZNetScene scene = ZNetScene.instance;
            if (_optionCacheReady && object.ReferenceEquals(_cachedScene, scene)) return;

            _cachedScene = scene;
            _optionCacheReady = true;
            CachedOptions.Clear();
            OptionMap.Clear();

            // v0.5.454: this used to run a global TreeBase discovery on every
            // IMGUI repaint while the Lumberjack filter foldout was expanded.
            TreeBase[] trees = null;
            try { trees = Resources.FindObjectsOfTypeAll<TreeBase>(); } catch { }

            if (trees != null)
            {
                for (int i = 0; i < trees.Length; i++)
                {
                    TreeBase tree = trees[i];
                    if (tree == null || tree.gameObject == null) continue;

                    string prefab = TreeTargetFinder.NormalizePrefabName(tree.gameObject.name);
                    if (string.IsNullOrWhiteSpace(prefab) ||
                        prefab.IndexOf("sapling", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    string key = ResolveSpeciesKey(prefab);
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    int required = Math.Max(0, tree.m_minToolTier);
                    string display = ResolveDisplayName(key, prefab);

                    if (OptionMap.TryGetValue(key, out LumberjackTreeOption existing))
                    {
                        int mergedRequired = Math.Max(existing.RequiredToolTier, required);
                        OptionMap[key] = new LumberjackTreeOption(key, existing.DisplayName, mergedRequired);
                    }
                    else
                    {
                        OptionMap[key] = new LumberjackTreeOption(key, display, required);
                    }
                }
            }

            if (OptionMap.Count == 0)
            {
                AddFallback("beech", "Buche", 1);
                AddFallback("fir", "Tanne", 1);
                AddFallback("pine", "Kiefer", 1);
                AddFallback("birch", "Birke", 2);
                AddFallback("oak", "Eiche", 2);
                AddFallback("ancient", "Uralter Baum", 2);
                AddFallback("yggdrasil", "Yggdrasil", 3);
                AddFallback("ash", "Esche", 3);
            }

            foreach (LumberjackTreeOption option in OptionMap.Values)
                CachedOptions.Add(option);

            CachedOptions.Sort((a, b) =>
                string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            OptionMap.Clear();
        }

        internal static string ResolveSpeciesKey(string prefabName)
        {
            string value = TreeTargetFinder.NormalizePrefabName(prefabName);
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            if (value.IndexOf("beech", StringComparison.OrdinalIgnoreCase) >= 0) return "beech";
            if (value.IndexOf("birch", StringComparison.OrdinalIgnoreCase) >= 0) return "birch";
            if (value.IndexOf("oak", StringComparison.OrdinalIgnoreCase) >= 0) return "oak";
            if (value.IndexOf("pine", StringComparison.OrdinalIgnoreCase) >= 0) return "pine";
            if (value.IndexOf("fir", StringComparison.OrdinalIgnoreCase) >= 0) return "fir";
            if (value.IndexOf("ygg", StringComparison.OrdinalIgnoreCase) >= 0) return "yggdrasil";
            if (value.IndexOf("swamptree", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("ancienttree", StringComparison.OrdinalIgnoreCase) >= 0) return "ancient";
            if (value.IndexOf("ash", StringComparison.OrdinalIgnoreCase) >= 0) return "ash";

            // Modded/unknown mature trees still get a stable per-prefab category instead of being
            // silently impossible to configure.
            return value.Trim().ToLowerInvariant();
        }

        internal static string ResolveDisplayName(string speciesKey, string prefabName = null)
        {
            switch ((speciesKey ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "beech": return "Buche";
                case "birch": return "Birke";
                case "oak": return "Eiche";
                case "pine": return "Kiefer";
                case "fir": return "Tanne";
                case "yggdrasil": return "Yggdrasil";
                case "ancient": return "Uralter Baum";
                case "ash": return "Esche";
                default:
                    string fallback = TreeTargetFinder.NormalizePrefabName(prefabName);
                    return string.IsNullOrWhiteSpace(fallback) ? speciesKey : fallback;
            }
        }

        private static HashSet<string> ReadDisabledCopy(ZDO workerZdo)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (workerZdo == null) return result;

            string raw = workerZdo.GetString(DisabledSpeciesKey, string.Empty);
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

        private static void AddFallback(string key, string display, int requiredTier)
        {
            OptionMap[key] = new LumberjackTreeOption(key, display, requiredTier);
        }
    }
}
