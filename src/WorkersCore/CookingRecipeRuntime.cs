using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class CookingIngredientSpec
    {
        internal string Prefab = string.Empty;
        internal int Amount;
        internal string DisplayName = string.Empty;
    }

    internal sealed class CookingRecipeSpec
    {
        internal ProductionStationKind Kind;
        internal string OutputPrefab = string.Empty;
        internal int OutputAmount = 1;
        internal string DisplayName = string.Empty;
        internal readonly List<CookingIngredientSpec> Ingredients = new List<CookingIngredientSpec>(4);
        internal float WorkSeconds = 6f;
    }

    /// <summary>
    /// Discovers the real ObjectDB recipes assigned to the Food Preparation Table / Cauldron.
    /// This deliberately reads the live recipe database instead of maintaining a hard-coded food
    /// list, so Deep-North recipes and compatible mod recipes become available automatically.
    /// </summary>
    internal static class CookingRecipeRuntime
    {
        private static readonly Dictionary<string, CookingRecipeSpec> ByKey = new Dictionary<string, CookingRecipeSpec>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> DisplayByOutput = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> PrepOutputs = new List<string>(48);
        private static readonly List<string> CauldronOutputs = new List<string>(48);
        private static string[] PrepOutputCache = Array.Empty<string>();
        private static string[] CauldronOutputCache = Array.Empty<string>();
        private static int _lastRecipeCount = -1;

        internal static string[] GetOutputs(ProductionStationKind kind)
        {
            EnsureReady();
            return kind == ProductionStationKind.Preparation ? PrepOutputCache :
                   kind == ProductionStationKind.Cauldron ? CauldronOutputCache : Array.Empty<string>();
        }

        internal static bool TryGetRecipe(ProductionStationKind kind, string output, out CookingRecipeSpec recipe)
        {
            recipe = null;
            if (kind != ProductionStationKind.Preparation && kind != ProductionStationKind.Cauldron) return false;
            if (string.IsNullOrWhiteSpace(output)) return false;
            EnsureReady();
            return ByKey.TryGetValue(Key(kind, output), out recipe) && recipe != null;
        }

        internal static bool TryGetAnyRecipe(string output, out CookingRecipeSpec recipe)
        {
            recipe = null;
            if (string.IsNullOrWhiteSpace(output)) return false;
            EnsureReady();
            return ByKey.TryGetValue(Key(ProductionStationKind.Preparation, output), out recipe) ||
                   ByKey.TryGetValue(Key(ProductionStationKind.Cauldron, output), out recipe);
        }

        internal static bool TryGetDisplayName(string output, out string display)
        {
            display = string.Empty;
            if (string.IsNullOrWhiteSpace(output)) return false;
            EnsureReady();
            return DisplayByOutput.TryGetValue(output, out display) && !string.IsNullOrWhiteSpace(display);
        }

        internal static string DisplayIngredient(string prefab)
        {
            if (TryGetItemDisplay(prefab, out string display)) return display;
            return prefab ?? string.Empty;
        }

        internal static string DescribeIngredients(CookingRecipeSpec recipe)
        {
            if (recipe == null || recipe.Ingredients.Count == 0) return "keine Zutaten";
            var parts = new List<string>(recipe.Ingredients.Count);
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                CookingIngredientSpec ing = recipe.Ingredients[i];
                if (ing == null || ing.Amount <= 0) continue;
                parts.Add($"{(string.IsNullOrEmpty(ing.DisplayName) ? ing.Prefab : ing.DisplayName)} x{ing.Amount}");
            }
            return parts.Count == 0 ? "keine Zutaten" : string.Join(", ", parts);
        }

        private static string Key(ProductionStationKind kind, string output) => ((int)kind) + ":" + (output ?? string.Empty);

        private static void EnsureReady()
        {
            ObjectDB db = ObjectDB.instance;
            if (db == null) return;

            IList recipes = null;
            try
            {
                FieldInfo f = AccessTools.Field(typeof(ObjectDB), "m_recipes");
                recipes = f?.GetValue(db) as IList;
            }
            catch { recipes = null; }
            int count = recipes?.Count ?? 0;
            if (count <= 0) return;

            // Recipe discovery is immutable for the normal Valheim session after ObjectDB is
            // populated. The old code rebuilt the entire recipe catalog periodically even when the
            // recipe count had not changed, causing visible hitches on heavily modded databases.
            // Rebuild only when the actual recipe count changes.
            if (ByKey.Count > 0 && count == _lastRecipeCount) return;
            _lastRecipeCount = count;
            Rebuild(recipes);
        }

        private static void Rebuild(IList recipes)
        {
            ByKey.Clear();
            DisplayByOutput.Clear();
            PrepOutputs.Clear();
            CauldronOutputs.Clear();

            for (int i = 0; i < recipes.Count; i++)
            {
                object raw = recipes[i];
                if (raw == null) continue;
                Type t = raw.GetType();

                bool enabled = ReadBool(raw, t, "m_enabled", true);
                if (!enabled) continue;

                object stationObj = ReadField(raw, t, "m_craftingStation");
                ProductionStationKind kind = ClassifyRecipeStation(stationObj);
                if (kind != ProductionStationKind.Preparation && kind != ProductionStationKind.Cauldron) continue;

                ItemDrop outputDrop = ReadField(raw, t, "m_item") as ItemDrop;
                if (outputDrop == null || outputDrop.gameObject == null || outputDrop.m_itemData == null) continue;
                string outputPrefab = SpeciesRoleRegistry.NormalizePrefabName(outputDrop.gameObject.name);
                if (string.IsNullOrWhiteSpace(outputPrefab)) continue;
                int outputAmount = Mathf.Max(1, ReadInt(raw, t, "m_amount", 1));

                var spec = new CookingRecipeSpec
                {
                    Kind = kind,
                    OutputPrefab = outputPrefab,
                    OutputAmount = outputAmount,
                    DisplayName = LocalizedItemName(outputDrop, outputPrefab),
                    WorkSeconds = kind == ProductionStationKind.Preparation ? 5f : 6f
                };

                object resourcesObj = ReadField(raw, t, "m_resources");
                if (resourcesObj is IEnumerable resources)
                {
                    foreach (object requirement in resources)
                    {
                        if (requirement == null) continue;
                        Type rt = requirement.GetType();
                        ItemDrop resourceDrop = ReadField(requirement, rt, "m_resItem") as ItemDrop;
                        int amount = Mathf.Max(0, ReadInt(requirement, rt, "m_amount", 0));
                        if (resourceDrop == null || resourceDrop.gameObject == null || amount <= 0) continue;
                        string prefab = SpeciesRoleRegistry.NormalizePrefabName(resourceDrop.gameObject.name);
                        if (string.IsNullOrWhiteSpace(prefab)) continue;

                        CookingIngredientSpec existing = null;
                        for (int ri = 0; ri < spec.Ingredients.Count; ri++)
                            if (string.Equals(spec.Ingredients[ri].Prefab, prefab, StringComparison.OrdinalIgnoreCase)) { existing = spec.Ingredients[ri]; break; }
                        if (existing != null)
                        {
                            existing.Amount += amount;
                        }
                        else
                        {
                            spec.Ingredients.Add(new CookingIngredientSpec
                            {
                                Prefab = prefab,
                                Amount = amount,
                                DisplayName = LocalizedItemName(resourceDrop, prefab)
                            });
                        }
                    }
                }

                if (spec.Ingredients.Count == 0) continue;
                string key = Key(kind, outputPrefab);
                if (ByKey.ContainsKey(key)) continue; // Stable first recipe wins for duplicate outputs.
                ByKey[key] = spec;
                DisplayByOutput[outputPrefab] = spec.DisplayName;
                (kind == ProductionStationKind.Preparation ? PrepOutputs : CauldronOutputs).Add(outputPrefab);
            }

            PrepOutputs.Sort(CompareByDisplay);
            CauldronOutputs.Sort(CompareByDisplay);
            PrepOutputCache = PrepOutputs.Count == 0 ? Array.Empty<string>() : PrepOutputs.ToArray();
            CauldronOutputCache = CauldronOutputs.Count == 0 ? Array.Empty<string>() : CauldronOutputs.ToArray();
        }

        private static int CompareByDisplay(string a, string b)
        {
            string da = DisplayByOutput.TryGetValue(a ?? string.Empty, out string av) ? av : a;
            string db = DisplayByOutput.TryGetValue(b ?? string.Empty, out string bv) ? bv : b;
            return string.Compare(da, db, StringComparison.CurrentCultureIgnoreCase);
        }

        private static ProductionStationKind ClassifyRecipeStation(object stationObj)
        {
            if (stationObj == null) return ProductionStationKind.None;
            string name = string.Empty;
            string display = string.Empty;
            try
            {
                if (stationObj is Component component && component.gameObject != null) name = component.gameObject.name ?? string.Empty;
                else if (stationObj is GameObject go) name = go.name ?? string.Empty;
                Type t = stationObj.GetType();
                object n = ReadField(stationObj, t, "m_name");
                display = n as string ?? string.Empty;
            }
            catch { }

            string probe = (SpeciesRoleRegistry.NormalizePrefabName(name) + " " + display).ToLowerInvariant();
            if (probe.Contains("preptable") || probe.Contains("prep_table") || probe.Contains("foodprep") || probe.Contains("food_prep") || probe.Contains("preparation"))
                return ProductionStationKind.Preparation;
            if (probe.Contains("cauldron")) return ProductionStationKind.Cauldron;
            return ProductionStationKind.None;
        }

        private static string LocalizedItemName(ItemDrop drop, string fallback)
        {
            try
            {
                string key = drop?.m_itemData?.m_shared?.m_name;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    // Valheim 1.0.x no longer exposes Localization as a compile-time type in every
                    // managed reference set. Resolve it dynamically so recipe discovery remains
                    // compatible across game builds instead of failing the entire mod build.
                    Type localizationType = AccessTools.TypeByName("Localization");
                    if (localizationType != null)
                    {
                        object instance = null;
                        PropertyInfo instanceProperty = localizationType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (instanceProperty != null) instance = instanceProperty.GetValue(null, null);
                        if (instance == null)
                        {
                            FieldInfo instanceField = localizationType.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            if (instanceField != null) instance = instanceField.GetValue(null);
                        }

                        MethodInfo localize = AccessTools.Method(localizationType, "Localize", new[] { typeof(string) });
                        if (instance != null && localize != null)
                        {
                            string localized = localize.Invoke(instance, new object[] { key }) as string;
                            if (!string.IsNullOrWhiteSpace(localized) && !string.Equals(localized, key, StringComparison.Ordinal))
                                return localized;
                        }
                    }

                    if (!key.StartsWith("$", StringComparison.Ordinal)) return key;
                }
            }
            catch { }
            return fallback ?? string.Empty;
        }

        private static bool TryGetItemDisplay(string prefab, out string display)
        {
            display = prefab ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prefab) || ObjectDB.instance == null) return false;
            try
            {
                GameObject go = ObjectDB.instance.GetItemPrefab(prefab);
                ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
                if (drop == null) return false;
                display = LocalizedItemName(drop, prefab);
                return !string.IsNullOrWhiteSpace(display);
            }
            catch { return false; }
        }

        private static object ReadField(object instance, Type type, string name)
        {
            try { return AccessTools.Field(type, name)?.GetValue(instance); }
            catch { return null; }
        }

        private static int ReadInt(object instance, Type type, string name, int fallback)
        {
            object value = ReadField(instance, type, name);
            try { return value == null ? fallback : Convert.ToInt32(value); }
            catch { return fallback; }
        }

        private static bool ReadBool(object instance, Type type, string name, bool fallback)
        {
            object value = ReadField(instance, type, name);
            try { return value == null ? fallback : Convert.ToBoolean(value); }
            catch { return fallback; }
        }
    }
}
