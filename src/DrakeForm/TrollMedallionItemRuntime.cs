using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers_DrakeForm
{
    /// <summary>
    /// Land-form necklace for Troll symbiosis. The necklace owns only crafting/presentation;
    /// binding metadata is persisted by DrakeCaptureRuntime and possession is owned by Mount.
    /// </summary>
    internal sealed class TrollMedallionItemRuntime
    {
        internal const string PrefabName = "ValheimWorkers_TrollMedallion";
        internal const string TrollFormId = SymbiosisFormCatalog.Troll;

        private readonly Action<string> _debug;
        private readonly Action<string> _info;
        private bool _registered;
        private float _nextAttempt;

        internal TrollMedallionItemRuntime(Action<string> debug, Action<string> info)
        {
            _debug = debug;
            _info = info;
        }

        internal void Begin() => TryRegister();

        internal void Tick()
        {
            if (_registered || Time.unscaledTime < _nextAttempt) return;
            _nextAttempt = Time.unscaledTime + 1.5f;
            TryRegister();
        }

        private void TryRegister()
        {
            if (_registered) return;
            try
            {
                Assembly jotunn = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, "Jotunn", StringComparison.OrdinalIgnoreCase));
                if (jotunn == null) return;
                Type itemConfigType = jotunn.GetType("Jotunn.Configs.ItemConfig", false);
                Type customItemType = jotunn.GetType("Jotunn.Entities.CustomItem", false);
                Type itemManagerType = jotunn.GetType("Jotunn.Managers.ItemManager", false);
                Type prefabManagerType = jotunn.GetType("Jotunn.Managers.PrefabManager", false);
                if (itemConfigType == null || customItemType == null || itemManagerType == null) return;

                object itemManager = itemManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                object prefabManager = prefabManagerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                MethodInfo getPrefab = prefabManagerType?.GetMethod("GetPrefab", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (itemManager == null || prefabManager == null || getPrefab == null) return;

                if (getPrefab.Invoke(prefabManager, new object[] { PrefabName }) is GameObject)
                {
                    _registered = true;
                    return;
                }
                if (!(getPrefab.Invoke(prefabManager, new object[] { "Wishbone" }) is GameObject)) return;
                GameObject trollIconPrefab = getPrefab.Invoke(prefabManager, new object[] { "TrophyTroll" }) as GameObject
                    ?? getPrefab.Invoke(prefabManager, new object[] { "TrollHide" }) as GameObject;
                if (trollIconPrefab == null) return;

                object config = Activator.CreateInstance(itemConfigType);
                SetMember(config, "Name", "Trollhalskette");
                SetMember(config, "Description", "Eine schwere Bindungshalskette fuer die Troll-Symbiose. Rueste sie aus und schwaeche einen wilden Troll auf hoechstens 30% Leben. Der naechste Treffer bindet seine Gestalt. Als Troll erleidest du 35% weniger physischen Schaden und bist stark gegen Wanken und Rueckstoss; dein eigener Spielerzustand bleibt autoritativ.");
                SetMember(config, "CraftingStation", "forge");
                SetMember(config, "MinStationLevel", 1);
                SetMember(config, "Amount", 1);
                SetMember(config, "StackSize", 1);
                if (!AddRequirement(itemConfigType, config, "Bronze", 6) ||
                    !AddRequirement(itemConfigType, config, "TrollHide", 8) ||
                    !AddRequirement(itemConfigType, config, "BoneFragments", 4)) return;

                ConstructorInfo ctor = customItemType.GetConstructor(new[] { typeof(string), typeof(string), itemConfigType });
                if (ctor == null) return;
                object customItem = ctor.Invoke(new object[] { PrefabName, "Wishbone", config });
                GameObject itemPrefab = customItemType.GetProperty("ItemPrefab", BindingFlags.Public | BindingFlags.Instance)?.GetValue(customItem) as GameObject;
                ItemDrop drop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
                if (itemPrefab == null || drop?.m_itemData?.m_shared == null) return;

                drop.m_itemData.m_shared.m_name = "Trollhalskette";
                drop.m_itemData.m_shared.m_description = "Schwaeche einen wilden Troll auf hoechstens 30% Leben und binde ihn mit der ausgeruesteten leeren Trollhalskette. Tankform: 35% weniger physischer Schaden, starke Wank-/Rueckstossresistenz. W/S vor/zurueck, A/D seitwaerts, Shift sprinten (kostet Ausdauer), LMB/RMB/MMB native Trollangriffe, F6 Ego/Third. Rueckkehr: gebundene Halskette erneut benutzen.";
                drop.m_itemData.m_shared.m_maxStackSize = 1;
                StripCloneSourceGameplayEffects(drop.m_itemData.m_shared);

                ItemDrop iconDrop = trollIconPrefab.GetComponent<ItemDrop>();
                Sprite[] icons = iconDrop?.m_itemData?.m_shared?.m_icons;
                if (icons != null && icons.Length > 0) drop.m_itemData.m_shared.m_icons = icons;

                MethodInfo addItem = itemManagerType.GetMethod("AddItem", BindingFlags.Public | BindingFlags.Instance, null, new[] { customItemType }, null);
                if (addItem == null) return;
                addItem.Invoke(itemManager, new[] { customItem });
                _registered = true;
                _info?.Invoke("[Symbiosis] Trollhalskette registered (6 Bronze, 8 TrollHide, 4 BoneFragments at Forge 1).");
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _debug?.Invoke("[Symbiosis] troll necklace registration pending: " + root.GetType().Name + ": " + root.Message);
            }
        }

        private void StripCloneSourceGameplayEffects(object shared)
        {
            if (shared == null) return;
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type sharedType = shared.GetType();
                FieldInfo equipStatus = sharedType.GetField("m_equipStatusEffect", flags);
                if (equipStatus != null) equipStatus.SetValue(shared, null);
                FieldInfo setStatus = sharedType.GetField("m_setStatusEffect", flags);
                if (setStatus != null) setStatus.SetValue(shared, null);
            }
            catch (Exception ex)
            {
                _debug?.Invoke("[Symbiosis] Could not strip inherited Wishbone effects from Troll necklace: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool AddRequirement(Type t, object config, string prefab, int amount)
        {
            MethodInfo[] methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m => m.Name == "AddRequirement").ToArray();
            foreach (MethodInfo m in methods)
            {
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length < 2 || ps[0].ParameterType != typeof(string) || ps[1].ParameterType != typeof(int)) continue;
                object[] args = new object[ps.Length]; args[0] = prefab; args[1] = amount;
                bool ok = true;
                for (int i = 2; i < ps.Length; i++)
                {
                    if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                    else if (ps[i].ParameterType == typeof(int)) args[i] = 0;
                    else if (ps[i].ParameterType == typeof(bool)) args[i] = false;
                    else { ok = false; break; }
                }
                if (!ok) continue;
                m.Invoke(config, args);
                return true;
            }
            return false;
        }

        private static void SetMember(object target, string name, object value)
        {
            if (target == null) return;
            Type t = target.GetType();
            try
            {
                PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (p != null && p.CanWrite) { p.SetValue(target, ConvertIfNeeded(value, p.PropertyType)); return; }
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public);
                if (f != null) f.SetValue(target, ConvertIfNeeded(value, f.FieldType));
            }
            catch { }
        }

        private static object ConvertIfNeeded(object value, Type targetType)
        {
            if (value == null || targetType.IsInstanceOfType(value)) return value;
            return Convert.ChangeType(value, targetType);
        }
    }
}
