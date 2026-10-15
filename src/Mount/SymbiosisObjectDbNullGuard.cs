using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Conservative guard for malformed ObjectDB/PieceTable entries created by any mod.
    /// Valheim 1.0.17 calls GameObject.GetComponent while rebuilding known recipes/build pieces;
    /// a destroyed/null prefab in those lists therefore crashes every inventory change.
    /// We only remove entries that are already unusable (Unity-null) and never swallow the
    /// vanilla exception path for any other error.
    /// </summary>
    internal static class SymbiosisObjectDbNullGuard
    {
        private static bool _running;
        private static readonly FieldInfo ObjectDbItemsField = AccessTools.Field(typeof(ObjectDB), "m_items");
        private static readonly FieldInfo ObjectDbRecipesField = AccessTools.Field(typeof(ObjectDB), "m_recipes");
        private static readonly FieldInfo PieceTablePiecesField = AccessTools.Field(typeof(PieceTable), "m_pieces");
        private static readonly Dictionary<Type, FieldInfo> RecipeItemFields = new Dictionary<Type, FieldInfo>();

        internal static void Sanitize(string reason)
        {
            if (_running) return;
            _running = true;
            try
            {
                int removedItems = 0;
                int removedRecipes = 0;
                int removedPieces = 0;
                List<string> details = new List<string>();

                ObjectDB db = ObjectDB.instance;
                if (db != null)
                {
                    removedItems += RemoveUnityNullEntries(ObjectDbItemsField?.GetValue(db) as IList, "ObjectDB.m_items", details);
                    removedRecipes += RemoveInvalidRecipes(ObjectDbRecipesField?.GetValue(db) as IList, details);
                }

                PieceTable[] tables = Resources.FindObjectsOfTypeAll<PieceTable>();
                if (tables != null)
                {
                    for (int i = 0; i < tables.Length; i++)
                    {
                        PieceTable table = tables[i];
                        if (table == null) continue;
                        IList pieces = PieceTablePiecesField?.GetValue(table) as IList;
                        string tableName = string.IsNullOrEmpty(table.name) ? "<unnamed PieceTable>" : table.name;
                        removedPieces += RemoveUnityNullEntries(pieces, "PieceTable " + tableName, details);
                    }
                }

                int total = removedItems + removedRecipes + removedPieces;
                if (total > 0)
                {
                    string suffix = details.Count > 0 ? " | " + string.Join("; ", details.ToArray()) : string.Empty;
                    MountPlugin.LogGuardWarning($"ObjectDB null-guard repaired {total} invalid entries before {reason}: items={removedItems}, recipes={removedRecipes}, pieces={removedPieces}{suffix}");
                }
            }
            catch (Exception ex)
            {
                MountPlugin.LogGuardWarning("ObjectDB null-guard diagnostic failed before " + reason + ": " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _running = false;
            }
        }

        private static int RemoveUnityNullEntries(IList list, string owner, List<string> details)
        {
            if (list == null) return 0;
            int removed = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                object entry;
                try { entry = list[i]; }
                catch { continue; }

                if (!IsUnityNull(entry)) continue;
                try
                {
                    list.RemoveAt(i);
                    removed++;
                    AddDetail(details, owner + "[" + i + "]=null");
                }
                catch { }
            }
            return removed;
        }

        private static int RemoveInvalidRecipes(IList recipes, List<string> details)
        {
            if (recipes == null) return 0;
            int removed = 0;
            for (int i = recipes.Count - 1; i >= 0; i--)
            {
                object recipe;
                try { recipe = recipes[i]; }
                catch { continue; }

                if (IsUnityNull(recipe))
                {
                    try
                    {
                        recipes.RemoveAt(i);
                        removed++;
                        AddDetail(details, "ObjectDB.m_recipes[" + i + "]=null");
                    }
                    catch { }
                    continue;
                }

                Type type = recipe.GetType();
                if (!RecipeItemFields.TryGetValue(type, out FieldInfo itemField))
                {
                    itemField = AccessTools.Field(type, "m_item");
                    RecipeItemFields[type] = itemField;
                }
                if (itemField == null) continue;

                object output = null;
                try { output = itemField.GetValue(recipe); }
                catch { continue; }
                if (!IsUnityNull(output)) continue;

                string recipeName = recipe is UnityEngine.Object unityObject && unityObject != null && !string.IsNullOrEmpty(unityObject.name)
                    ? unityObject.name
                    : type.Name;
                try
                {
                    recipes.RemoveAt(i);
                    removed++;
                    AddDetail(details, "recipe " + recipeName + " has null output");
                }
                catch { }
            }
            return removed;
        }

        private static bool IsUnityNull(object value)
        {
            if (value == null) return true;
            if (value is UnityEngine.Object unityObject) return unityObject == null;
            return false;
        }

        private static void AddDetail(List<string> details, string detail)
        {
            if (details == null || details.Count >= 8 || string.IsNullOrEmpty(detail)) return;
            details.Add(detail);
        }
    }

    [HarmonyPatch(typeof(Player), "UpdateKnownRecipesList")]
    internal static class SymbiosisKnownRecipesNullGuardPatch
    {
        [HarmonyPriority(Priority.First + 1500)]
        private static void Prefix()
        {
            SymbiosisObjectDbNullGuard.Sanitize("Player.UpdateKnownRecipesList");
        }
    }

    [HarmonyPatch(typeof(ObjectDB), "GetAllBuildPieces")]
    internal static class SymbiosisBuildPiecesNullGuardPatch
    {
        [HarmonyPriority(Priority.First + 1500)]
        private static void Prefix()
        {
            SymbiosisObjectDbNullGuard.Sanitize("ObjectDB.GetAllBuildPieces");
        }
    }
}
