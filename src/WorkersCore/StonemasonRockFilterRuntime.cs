using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal readonly struct StonemasonRockOption
    {
        internal readonly string Key;
        internal readonly string DisplayName;
        internal readonly int RequiredToolTier;

        internal StonemasonRockOption(string key, string displayName, int requiredToolTier)
        {
            Key = key ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName;
            RequiredToolTier = Math.Max(0, requiredToolTier);
        }
    }

    /// <summary>
    /// Per-worker quarry resource filter.
    ///
    /// The UI intentionally exposes resource categories (Stone, Copper, Iron, ...), not the
    /// dozens of internal world prefab variants that happen to produce those resources.
    /// Tier/capability checks remain authoritative for every concrete target.
    /// </summary>
    internal static class StonemasonRockFilterRuntime
    {
        // v0.4.67 deliberately uses a new key. The old v0.4.66 key stored individual prefab
        // names and must not leak those technical choices into the new resource-category UI.
        private const string DisabledResourceCategoriesKey = "vw_stonemason_disabled_resource_categories";
        private const char Separator = '|';

        private static readonly Dictionary<string, StonemasonRockOption> OptionMap =
            new Dictionary<string, StonemasonRockOption>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<StonemasonRockOption> CachedOptions =
            new List<StonemasonRockOption>(16);
        private static readonly HashSet<string> EmptyDisabled =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashSet<string>> ParsedDisabledCache =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private static ZNetScene _cachedScene;
        private static bool _optionCacheReady;

        internal static bool HasCustomFilter(ZDO workerZdo)
        {
            return workerZdo != null && !string.IsNullOrWhiteSpace(workerZdo.GetString(DisabledResourceCategoriesKey, string.Empty));
        }

        internal static bool IsTargetAllowed(ZDO workerZdo, StoneTarget target)
        {
            if (workerZdo == null || target == null || target.GameObject == null) return true;
            string category = ResolveTargetCategoryKey(target);
            return IsRockTypeAllowed(workerZdo, category);
        }

        internal static bool IsRockTypeAllowed(ZDO workerZdo, string categoryKey)
        {
            if (workerZdo == null || string.IsNullOrWhiteSpace(categoryKey)) return true;

            string raw = workerZdo.GetString(DisabledResourceCategoriesKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return true;
            return !GetParsedDisabled(raw).Contains(categoryKey.Trim().ToLowerInvariant());
        }

        internal static bool SetRockTypeAllowed(ZDO workerZdo, string categoryKey, bool allowed)
        {
            if (workerZdo == null || string.IsNullOrWhiteSpace(categoryKey)) return false;
            string normalized = categoryKey.Trim().ToLowerInvariant();
            HashSet<string> disabled = ReadDisabledCopy(workerZdo);
            bool changed = allowed ? disabled.Remove(normalized) : disabled.Add(normalized);
            if (!changed) return true;

            workerZdo.Set(DisabledResourceCategoriesKey, Serialize(disabled));
            WorkerZdo.ClearTarget(workerZdo);
            WorkerState state = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state != WorkerState.ReturnHomeForRest && state != WorkerState.Resting && state != WorkerState.Recover)
                workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            return true;
        }

        internal static void CollectVisibleOptions(int maxToolTier, List<StonemasonRockOption> output)
        {
            if (output == null) return;
            output.Clear();
            EnsureOptionCache();

            int tier = Math.Max(0, maxToolTier);
            for (int i = 0; i < CachedOptions.Count; i++)
            {
                StonemasonRockOption option = CachedOptions[i];
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

            // v0.5.454: this foldout used to perform three global Resources scans on
            // every IMGUI repaint. Discover categories once per ZNetScene/F12 lifetime.
            MineRock5[] rock5 = null;
            MineRock[] rocks = null;
            Destructible[] destructibles = null;
            try { rock5 = Resources.FindObjectsOfTypeAll<MineRock5>(); } catch { }
            try { rocks = Resources.FindObjectsOfTypeAll<MineRock>(); } catch { }
            try { destructibles = Resources.FindObjectsOfTypeAll<Destructible>(); } catch { }

            if (rock5 != null)
            {
                for (int i = 0; i < rock5.Length; i++)
                {
                    MineRock5 rock = rock5[i];
                    if (!StoneTargetFinder.IsConfigurableRockOrOre(rock)) continue;
                    AddOption(ResolveResourceCategory(
                        rock.gameObject != null ? rock.gameObject.name : string.Empty,
                        rock.m_dropItems), rock.m_minToolTier);
                }
            }

            if (rocks != null)
            {
                for (int i = 0; i < rocks.Length; i++)
                {
                    MineRock rock = rocks[i];
                    if (!StoneTargetFinder.IsConfigurableRockOrOre(rock)) continue;
                    AddOption(ResolveResourceCategory(
                        rock.gameObject != null ? rock.gameObject.name : string.Empty,
                        rock.m_dropItems), rock.m_minToolTier);
                }
            }

            if (destructibles != null)
            {
                for (int i = 0; i < destructibles.Length; i++)
                {
                    Destructible rock = destructibles[i];
                    if (!StoneTargetFinder.IsConfigurableRockOrOre(rock)) continue;
                    DropTable table = StoneTargetFinder.GetDestructibleDropTable(rock);
                    AddOption(ResolveResourceCategory(
                        rock.gameObject != null ? rock.gameObject.name : string.Empty,
                        table), StoneTargetFinder.GetDestructibleMinToolTier(rock));
                }
            }

            foreach (StonemasonRockOption option in OptionMap.Values)
                CachedOptions.Add(option);

            CachedOptions.Sort((a, b) =>
            {
                int ao = CategorySortOrder(a.Key);
                int bo = CategorySortOrder(b.Key);
                return ao != bo
                    ? ao.CompareTo(bo)
                    : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            });

            OptionMap.Clear();
        }

        private static string ResolveTargetCategoryKey(StoneTarget target)
        {
            if (target == null) return string.Empty;
            if (target.Rock5 != null)
                return ResolveResourceCategory(target.Rock5.gameObject != null ? target.Rock5.gameObject.name : string.Empty, target.Rock5.m_dropItems).Key;
            if (target.Rock != null)
                return ResolveResourceCategory(target.Rock.gameObject != null ? target.Rock.gameObject.name : string.Empty, target.Rock.m_dropItems).Key;
            if (target.Destructible != null)
                return ResolveResourceCategory(target.Destructible.gameObject != null ? target.Destructible.gameObject.name : string.Empty,
                    StoneTargetFinder.GetDestructibleDropTable(target.Destructible)).Key;
            return string.Empty;
        }

        private static StonemasonRockOption ResolveResourceCategory(string prefabName, DropTable table)
        {
            // Prefer the actual produced resource over the world object's technical prefab name.
            // Ore deposits often also yield Stone, therefore remember Stone only as a fallback.
            StonemasonRockOption stoneFallback = default;
            bool haveStoneFallback = false;

            if (table != null && table.m_drops != null)
            {
                for (int i = 0; i < table.m_drops.Count; i++)
                {
                    GameObject item = table.m_drops[i].m_item;
                    if (item == null) continue;
                    string itemPrefab = StoneTargetFinder.NormalizePrefabName(item.name);
                    ItemDrop drop = item.GetComponent<ItemDrop>();
                    string shared = drop != null && drop.m_itemData != null && drop.m_itemData.m_shared != null
                        ? drop.m_itemData.m_shared.m_name
                        : string.Empty;

                    StonemasonRockOption category = ClassifyResourceName(itemPrefab);
                    if (string.IsNullOrEmpty(category.Key)) category = ClassifyResourceName(shared);
                    if (string.IsNullOrEmpty(category.Key)) continue;

                    if (category.Key == "stone")
                    {
                        stoneFallback = category;
                        haveStoneFallback = true;
                        continue;
                    }
                    return category;
                }
            }

            StonemasonRockOption byPrefab = ClassifyResourceName(StoneTargetFinder.NormalizePrefabName(prefabName));
            if (!string.IsNullOrEmpty(byPrefab.Key) && byPrefab.Key != "stone") return byPrefab;
            if (haveStoneFallback) return stoneFallback;
            if (!string.IsNullOrEmpty(byPrefab.Key)) return byPrefab;

            // Every accepted but otherwise unclassified rock-like target belongs to the broad
            // Stone category. This keeps biome/prefab variants out of the player-facing UI.
            return new StonemasonRockOption("stone", "Stein", 0);
        }

        private static StonemasonRockOption ClassifyResourceName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return default;
            string n = value.Trim().ToLowerInvariant();

            if (n.Contains("copper") || n.Contains("kupfer")) return new StonemasonRockOption("copper", "Kupfer", 0);
            if (n.Contains("tin") || n.Contains("zinn")) return new StonemasonRockOption("tin", "Zinn", 0);
            if (n.Contains("silver") || n.Contains("silber")) return new StonemasonRockOption("silver", "Silber", 0);
            if (n.Contains("iron") || n.Contains("eisen") || n.Contains("scrapiron")) return new StonemasonRockOption("iron", "Eisen", 0);
            if (n.Contains("blackmetal") || n.Contains("black metal") || n.Contains("schwarzmetall")) return new StonemasonRockOption("blackmetal", "Schwarzmetall", 0);
            if (n.Contains("flametal")) return new StonemasonRockOption("flametal", "Flametal", 0);
            if (n.Contains("obsidian")) return new StonemasonRockOption("obsidian", "Obsidian", 0);
            if (n.Contains("chitin")) return new StonemasonRockOption("chitin", "Chitin", 0);

            if (n == "stone" || n == "$item_stone" || n.Contains("blackmarble") || n.Contains("black marble") ||
                n.Contains("rock") || n.Contains("stone") || n.Contains("boulder") || n.Contains("cliff") ||
                n.Contains("fels") || n.Contains("caverock"))
                return new StonemasonRockOption("stone", "Stein", 0);

            // Generic modded ore names stay one resource category instead of exposing every
            // deposit/prefab variant. Standard Valheim resources are handled explicitly above.
            if (n.Contains("ore") || n.Contains("erz") || n.Contains("meteor"))
                return new StonemasonRockOption("ore", "Erz", 0);

            return default;
        }

        private static void AddOption(StonemasonRockOption category, int requiredTier)
        {
            if (string.IsNullOrWhiteSpace(category.Key)) return;
            string key = category.Key.Trim().ToLowerInvariant();
            int required = Math.Max(0, requiredTier);

            if (OptionMap.TryGetValue(key, out StonemasonRockOption existing))
            {
                // A category is visible as soon as at least one concrete target is mineable by
                // this worker. Higher-tier variants remain blocked by the normal target tier gate.
                OptionMap[key] = new StonemasonRockOption(key, existing.DisplayName, Math.Min(existing.RequiredToolTier, required));
                return;
            }

            OptionMap[key] = new StonemasonRockOption(key, category.DisplayName, required);
        }

        private static int CategorySortOrder(string key)
        {
            switch ((key ?? string.Empty).ToLowerInvariant())
            {
                case "stone": return 0;
                case "copper": return 10;
                case "tin": return 20;
                case "iron": return 30;
                case "silver": return 40;
                case "blackmetal": return 50;
                case "flametal": return 60;
                case "obsidian": return 70;
                case "chitin": return 80;
                case "ore": return 90;
                default: return 100;
            }
        }

        private static HashSet<string> ReadDisabledCopy(ZDO workerZdo)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (workerZdo == null) return result;

            string raw = workerZdo.GetString(DisabledResourceCategoriesKey, string.Empty);
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
