using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers_DrakeForm
{
    internal sealed class DrakeMedallionItemRuntime
    {
        internal const string PrefabName = "ValheimWorkers_DrakeMedallion";
        internal const string BoundKey = "vw_drake_medallion_bound";
        internal const string BoundOwnerKey = "vw_drake_medallion_owner";
        internal const string BoundSourceKey = "vw_drake_medallion_source";
        internal const string BoundPrefabKey = "vw_drake_medallion_prefab";
        internal const string BoundLevelKey = "vw_drake_medallion_level";
        internal const string BoundSpeciesKey = "vw_drake_medallion_species";
        internal const string BindingVersionKey = "vw_drake_medallion_binding_version";
        internal const string CurrentBindingVersion = "2";
        internal const string FormKey = "vw_symbiosis_form";
        internal const string DrakeFormId = SymbiosisFormCatalog.Drake;

        private readonly Action<string> _debug;
        private readonly Action<string> _info;
        private bool _registered;
        private float _nextAttempt;

        internal DrakeMedallionItemRuntime(Action<string> debug, Action<string> info)
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
                if (itemManager == null) return;

                object prefabManager = prefabManagerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                MethodInfo getPrefab = prefabManagerType?.GetMethod("GetPrefab", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (prefabManager == null || getPrefab == null) return;
                if (getPrefab.Invoke(prefabManager, new object[] { PrefabName }) is GameObject)
                {
                    _registered = true;
                    return;
                }

                // ItemManager exists before Jotunn has necessarily populated vanilla prefabs.
                // Do not construct CustomItem until the clone source is actually available; otherwise
                // Jotunn creates an invalid item and logs misleading registration errors. Tick() retries.
                if (!(getPrefab.Invoke(prefabManager, new object[] { "Wishbone" }) is GameObject)) return;

                object config = Activator.CreateInstance(itemConfigType);
                SetMember(config, "Name", "Drachenhalskette");
                SetMember(config, "Description", "Eine leere Bindungshalskette fuer die Drachen-Symbiose. Binde damit einen selbst ausgebrueteten Drake. Danach benutzt du die gebundene Halskette direkt oder ueber die Schnellleiste, um die Drachenform anzunehmen; benutze sie erneut, um zurueckzukehren. Deine Lebenspunkte bleiben deine eigenen; die Symbiose nutzt die nativen Drake-Angriffe, die der gebundene Drake wirklich besitzt.");
                SetMember(config, "CraftingStation", "forge");
                SetMember(config, "MinStationLevel", 2);
                SetMember(config, "Amount", 1);
                SetMember(config, "StackSize", 1);
                if (!AddRequirement(itemConfigType, config, "Silver", 5) ||
                    !AddRequirement(itemConfigType, config, "Crystal", 3) ||
                    !AddRequirement(itemConfigType, config, "Chain", 1)) return;

                ConstructorInfo ctor = customItemType.GetConstructor(new[] { typeof(string), typeof(string), itemConfigType });
                if (ctor == null) return;
                object customItem = ctor.Invoke(new object[] { PrefabName, "Wishbone", config });
                GameObject itemPrefab = customItemType.GetProperty("ItemPrefab", BindingFlags.Public | BindingFlags.Instance)?.GetValue(customItem) as GameObject;
                ItemDrop drop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
                if (itemPrefab == null || drop?.m_itemData?.m_shared == null) return;
                if (drop.m_itemData.m_shared != null)
                {
                    drop.m_itemData.m_shared.m_name = "Drachenhalskette";
                    drop.m_itemData.m_shared.m_description = "Binde mit der Halskette einen selbst ausgebrueteten Drake. Danach benutzt du die gebundene Halskette direkt oder ueber die Schnellleiste, um dich selbst in den Drake zu verwandeln; benutze sie erneut, um dich zurueckzuverwandeln. LP, Essen und Spielerzustand bleiben deine eigenen. Die Symbiose nutzt die nativen Angriffe des gebundenen Drake; Prefab und Stern-/Levelstufe werden beim Verwandeln wiederhergestellt. Im Multiplayer sehen modded Mitspieler nur den Drake-Koerper statt eines ueberlagerten Menschen. Die Seeschlange nutzt eine eigene Seeschlangenhalskette.";
                    drop.m_itemData.m_shared.m_maxStackSize = 1;

                    // The necklace is cloned from Wishbone only to inherit a valid equippable
                    // utility-slot item setup. Never inherit Wishbone's SE_Finder gameplay effect:
                    // otherwise the dragon necklace still pulses/reveals buried treasure while worn.
                    StripCloneSourceGameplayEffects(drop.m_itemData.m_shared);

                    // Use the dedicated dragon-medallion artwork for inventory and hotbar UI.
                    // The PNG is embedded into this DLL so Setup.ps1 does not need to copy a
                    // loose asset next to the plugin.
                    Sprite icon = LoadEmbeddedIcon();
                    if (icon != null)
                    {
                        drop.m_itemData.m_shared.m_icons = new[] { icon };
                    }
                }
                MethodInfo addItem = itemManagerType.GetMethod("AddItem", BindingFlags.Public | BindingFlags.Instance, null, new[] { customItemType }, null);
                if (addItem == null) return;
                addItem.Invoke(itemManager, new[] { customItem });
                _registered = true;
                _info?.Invoke("[DrakeForm] Drachenhalskette registered (5 Silver, 3 Crystal, 1 Chain at Forge 2).");
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _debug?.Invoke("[DrakeForm] medallion registration pending: " + root.GetType().Name + ": " + root.Message);
            }
        }



        private void StripCloneSourceGameplayEffects(object shared)
        {
            if (shared == null) return;
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type sharedType = shared.GetType();

                // Vanilla Wishbone grants SE_Finder through SharedData.m_equipStatusEffect.
                // Use reflection so this remains source-compatible if Iron Gate changes the
                // concrete StatusEffect field type while keeping the SharedData member name.
                FieldInfo equipStatus = sharedType.GetField("m_equipStatusEffect", flags);
                if (equipStatus != null)
                {
                    object inherited = equipStatus.GetValue(shared);
                    if (inherited != null)
                    {
                        string inheritedName = inherited is UnityEngine.Object unityObject
                            ? unityObject.name
                            : inherited.GetType().Name;
                        equipStatus.SetValue(shared, null);
                        _debug?.Invoke("[DrakeForm] Removed inherited Wishbone equip status effect from Drachenhalskette: " + inheritedName);
                    }
                }

                // A cloned utility item must not accidentally inherit a set bonus either.
                // Wishbone normally has none, but clearing this makes the clone-source contract explicit.
                FieldInfo setStatus = sharedType.GetField("m_setStatusEffect", flags);
                if (setStatus != null && setStatus.GetValue(shared) != null)
                {
                    setStatus.SetValue(shared, null);
                }
            }
            catch (Exception ex)
            {
                _debug?.Invoke("[DrakeForm] Could not strip inherited clone-source effects: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Sprite LoadEmbeddedIcon()
        {
            const string ResourceName = "Valheim_Workers_DrakeForm.Assets.DrakeMedallionIcon.png";
            try
            {
                Assembly assembly = typeof(DrakeMedallionItemRuntime).Assembly;
                using (Stream stream = assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null) return null;

                    byte[] bytes = new byte[stream.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0) break;
                        offset += read;
                    }
                    if (offset != bytes.Length) return null;

                    Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    texture.name = "ValheimWorkers_DrakeMedallionIcon";
                    if (!ImageConversion.LoadImage(texture, bytes, false))
                    {
                        UnityEngine.Object.Destroy(texture);
                        return null;
                    }

                    texture.wrapMode = TextureWrapMode.Clamp;
                    texture.filterMode = FilterMode.Bilinear;
                    Sprite sprite = Sprite.Create(
                        texture,
                        new Rect(0f, 0f, texture.width, texture.height),
                        new Vector2(0.5f, 0.5f),
                        100f);
                    sprite.name = "ValheimWorkers_DrakeMedallionIcon";
                    return sprite;
                }
            }
            catch
            {
                return null;
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
                m.Invoke(config, args); return true;
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
