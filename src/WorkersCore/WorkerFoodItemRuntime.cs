using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Registers the shared Greydwarf feast "Gebratene Pilze" as a real networked item through
    /// Jotunn. It is intentionally a tiny cauldron recipe (2 normal mushrooms -> 1 meal) so the
    /// existing cook/production pipeline can discover and produce it like every other recipe.
    /// No runtime polling is involved.
    /// </summary>
    internal sealed class WorkerFoodItemRuntime
    {
        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private bool _registered;
        private string _status = "waiting for Jotunn ItemManager";

        internal WorkerFoodItemRuntime(Action<string> debugLog, Action<string> infoLog)
        {
            _debugLog = debugLog;
            _infoLog = infoLog;
        }

        internal string Status => _status;

        internal void Begin()
        {
            if (JotunnPrefabLifecycleRuntime.Subscribe(TryRegister, _debugLog))
            {
                if (!_registered) _status = "waiting for Jotunn vanilla prefabs";
                return;
            }

            // Legacy fallback for unusual Jotunn builds where the lifecycle event is unavailable.
            TryRegister();
        }

        private void TryRegister()
        {
            if (_registered) return;
            try
            {
                Assembly jotunn = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Jotunn", StringComparison.OrdinalIgnoreCase));
                if (jotunn == null) { _status = "Jotunn not loaded"; return; }

                Type itemConfigType = jotunn.GetType("Jotunn.Configs.ItemConfig", false);
                Type customItemType = jotunn.GetType("Jotunn.Entities.CustomItem", false);
                Type itemManagerType = jotunn.GetType("Jotunn.Managers.ItemManager", false);
                Type prefabManagerType = jotunn.GetType("Jotunn.Managers.PrefabManager", false);
                if (itemConfigType == null || customItemType == null || itemManagerType == null)
                {
                    _status = "Jotunn custom-item API types missing";
                    return;
                }

                object itemManager = itemManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                if (itemManager == null) { _status = "Jotunn ItemManager.Instance not ready"; return; }

                object prefabManager = prefabManagerType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                MethodInfo getPrefab = prefabManagerType?.GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (prefabManager != null && getPrefab != null && getPrefab.Invoke(prefabManager, new object[] { WorkerSustenanceRuntime.RoastedMushroomPrefab }) is GameObject)
                {
                    _registered = true;
                    _status = "already registered";
                    return;
                }

                object config = Activator.CreateInstance(itemConfigType);
                SetMember(config, "Name", "Gebratene Pilze");
                SetMember(config, "Description", "Warme Pilze - fuer Greydwarfs ein echtes Festmahl.");
                SetMember(config, "CraftingStation", "piece_cauldron");
                SetMember(config, "Amount", 1);
                SetMember(config, "StackSize", 20);
                SetMember(config, "MinStationLevel", 1);
                if (!AddRequirement(itemConfigType, config, "Mushroom", 2))
                {
                    _status = "Jotunn ItemConfig.AddRequirement API unavailable";
                    return;
                }

                ConstructorInfo ctor = customItemType.GetConstructor(new[] { typeof(string), typeof(string), itemConfigType });
                if (ctor == null)
                {
                    _status = "Jotunn CustomItem(name,basePrefab,ItemConfig) constructor missing";
                    return;
                }

                object customItem = ctor.Invoke(new object[] { WorkerSustenanceRuntime.RoastedMushroomPrefab, "Mushroom", config });
                if (customItem == null) { _status = "Jotunn created no CustomItem"; return; }

                PropertyInfo itemPrefabProperty = customItemType.GetProperty("ItemPrefab", BindingFlags.Instance | BindingFlags.Public);
                GameObject itemPrefab = itemPrefabProperty?.GetValue(customItem) as GameObject;
                ItemDrop drop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
                if (drop?.m_itemData?.m_shared != null)
                {
                    drop.m_itemData.m_shared.m_name = "Gebratene Pilze";
                    drop.m_itemData.m_shared.m_description = "Warme Pilze - fuer Greydwarfs ein echtes Festmahl.";
                }

                MethodInfo addItem = itemManagerType.GetMethod("AddItem", BindingFlags.Instance | BindingFlags.Public, null, new[] { customItemType }, null);
                if (addItem == null) { _status = "Jotunn ItemManager.AddItem(CustomItem) missing"; return; }
                addItem.Invoke(itemManager, new[] { customItem });

                _registered = true;
                _status = "registered: 2 Pilze -> Gebratene Pilze (Kessel)";
                _infoLog?.Invoke("Workers food: Gebratene Pilze registered (2x Mushroom -> 1x feast at cauldron).");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                _status = $"registration pending: {inner.GetType().Name}: {inner.Message}";
                _debugLog?.Invoke("Workers food registration pending: " + _status);
            }
            catch (Exception ex)
            {
                _status = $"registration failed: {ex.GetType().Name}: {ex.Message}";
                _debugLog?.Invoke("Workers food registration failed: " + _status);
            }
        }

        private static bool AddRequirement(Type itemConfigType, object config, string prefab, int amount)
        {
            MethodInfo[] methods = itemConfigType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => string.Equals(m.Name, "AddRequirement", StringComparison.Ordinal))
                .ToArray();
            for (int i = 0; i < methods.Length; i++)
            {
                ParameterInfo[] ps = methods[i].GetParameters();
                if (ps.Length < 2 || ps[0].ParameterType != typeof(string) || ps[1].ParameterType != typeof(int)) continue;
                object[] args = new object[ps.Length];
                args[0] = prefab;
                args[1] = amount;
                bool compatible = true;
                for (int p = 2; p < ps.Length; p++)
                {
                    if (ps[p].HasDefaultValue) args[p] = ps[p].DefaultValue;
                    else if (ps[p].ParameterType == typeof(int)) args[p] = 0;
                    else if (ps[p].ParameterType == typeof(bool)) args[p] = false;
                    else { compatible = false; break; }
                }
                if (!compatible) continue;
                methods[i].Invoke(config, args);
                return true;
            }
            return false;
        }

        private static void SetMember(object target, string name, object value)
        {
            if (target == null || string.IsNullOrWhiteSpace(name)) return;
            Type type = target.GetType();
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (property != null && property.CanWrite)
                {
                    object converted = value;
                    if (value != null && !property.PropertyType.IsInstanceOfType(value))
                        converted = Convert.ChangeType(value, property.PropertyType);
                    property.SetValue(target, converted);
                    return;
                }

                // ItemConfig.StackSize is a public field in current Jotunn builds, while most
                // other config values are properties. Supporting both keeps this reflection-only
                // integration compatible without adding a hard Jotunn compile dependency.
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
                if (field == null) return;
                object fieldValue = value;
                if (value != null && !field.FieldType.IsInstanceOfType(value))
                    fieldValue = Convert.ChangeType(value, field.FieldType);
                field.SetValue(target, fieldValue);
            }
            catch { }
        }
    }
}
