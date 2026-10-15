using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers_DrakeForm
{
    /// <summary>
    /// Curated high-speed land-form necklace. The bound creature is Valheim's Fenring (the werewolf-like
    /// Mountain hunter); Mount owns movement, star-scaled claw damage and the Blood Hunt chain combo.
    /// </summary>
    internal sealed class WerewolfMedallionItemRuntime
    {
        internal const string PrefabName = "ValheimWorkers_WerewolfMedallion";
        internal const string WerewolfFormId = SymbiosisFormCatalog.Werewolf;

        private readonly Action<string> _debug;
        private readonly Action<string> _info;
        private bool _registered;
        private float _nextAttempt;

        internal WerewolfMedallionItemRuntime(Action<string> debug, Action<string> info)
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
                GameObject iconPrefab = getPrefab.Invoke(prefabManager, new object[] { "TrophyFenring" }) as GameObject
                    ?? getPrefab.Invoke(prefabManager, new object[] { "WolfFang" }) as GameObject
                    ?? getPrefab.Invoke(prefabManager, new object[] { "WolfPelt" }) as GameObject;
                if (iconPrefab == null) return;

                object config = Activator.CreateInstance(itemConfigType);
                SetMember(config, "Name", "Werwolfhalskette");
                SetMember(config, "Description", "Bindet einen geschwaechten Fenring und oeffnet die schnelle Werwolfform. 0 Sterne = x1,5 Klauenschaden, 1 Stern = x1,75, 2 Sterne = x2,5. Halte RMB und druecke LMB fuer Blutjagd: bis zu 5 nahe Gegner nacheinander anspringen und danach zur Startposition zurueckkehren.");
                SetMember(config, "CraftingStation", "forge");
                SetMember(config, "MinStationLevel", 2);
                SetMember(config, "Amount", 1);
                SetMember(config, "StackSize", 1);
                if (!AddRequirement(itemConfigType, config, "Silver", 8) ||
                    !AddRequirement(itemConfigType, config, "WolfPelt", 6) ||
                    !AddRequirement(itemConfigType, config, "WolfFang", 4)) return;

                ConstructorInfo ctor = customItemType.GetConstructor(new[] { typeof(string), typeof(string), itemConfigType });
                if (ctor == null) return;
                object customItem = ctor.Invoke(new object[] { PrefabName, "Wishbone", config });
                GameObject itemPrefab = customItemType.GetProperty("ItemPrefab", BindingFlags.Public | BindingFlags.Instance)?.GetValue(customItem) as GameObject;
                ItemDrop drop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
                if (itemPrefab == null || drop?.m_itemData?.m_shared == null) return;

                drop.m_itemData.m_shared.m_name = "Werwolfhalskette";
                drop.m_itemData.m_shared.m_description = "Schwaeche einen wilden Fenring auf hoechstens 30% Leben und binde ihn. Schnelle Landform: W/S vor/zurueck, A/D seitwaerts, Shift sprinten, LMB native Klaue, RMB halten + LMB Blutjagd (bis 5 Ziele), F6 Ego/Third. Rueckkehr: gebundene Halskette erneut benutzen. Sternschaden: 0*=x1,5, 1*=x1,75, 2*=x2,5.";
                drop.m_itemData.m_shared.m_maxStackSize = 1;
                StripCloneSourceGameplayEffects(drop.m_itemData.m_shared);

                ItemDrop iconDrop = iconPrefab.GetComponent<ItemDrop>();
                Sprite[] icons = iconDrop?.m_itemData?.m_shared?.m_icons;
                if (icons != null && icons.Length > 0) drop.m_itemData.m_shared.m_icons = icons;

                MethodInfo addItem = itemManagerType.GetMethod("AddItem", BindingFlags.Public | BindingFlags.Instance, null, new[] { customItemType }, null);
                if (addItem == null) return;
                addItem.Invoke(itemManager, new[] { customItem });
                _registered = true;
                _info?.Invoke("[Symbiosis] Werwolfhalskette registered (8 Silver, 6 WolfPelt, 4 WolfFang at Forge 2).");
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _debug?.Invoke("[Symbiosis] werewolf necklace registration pending: " + root.GetType().Name + ": " + root.Message);
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
                _debug?.Invoke("[Symbiosis] Could not strip inherited Wishbone effects from Werewolf necklace: " + ex.GetType().Name + ": " + ex.Message);
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
