using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Valheim_Workers
{
    /// <summary>
    /// Adds a compact worker-stock shortcut directly below Valheim's normal Craft/Herstellen button.
    /// Shift+E on the station opens the full production-target overview; this inline control stays
    /// bound only to the currently selected recipe. It never enumerates recipes or storage, so
    /// opening the crafting screen does not reintroduce the expensive scans fixed in v0.5.207.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), "Update")]
    internal static class ProductionCraftingInlineRuntime
    {
        private static readonly FieldInfo CraftButtonField = AccessTools.Field(typeof(InventoryGui), "m_craftButton");
        private static readonly FieldInfo[] SelectedRecipeFields = BuildRecipeFieldList(typeof(InventoryGui));
        private static readonly PropertyInfo[] SelectedRecipeProperties = BuildRecipePropertyList(typeof(InventoryGui));
        private static readonly FieldInfo[] GuiStationFields = BuildStationFieldList(typeof(InventoryGui));
        private static readonly FieldInfo[] PlayerStationFields = BuildStationFieldList(typeof(Player));
        private static readonly Collider[] NearbyBuffer = new Collider[48];
        private static readonly Vector3[] CraftCorners = new Vector3[4];
        private static readonly string[] OutputMemberNames = { "m_item", "item", "m_output", "output", "m_itemData", "itemData", "m_dropPrefab", "dropPrefab", "m_prefab", "prefab" };
        private static readonly string[] RecipeWrapperMemberNames = { "m_recipe", "recipe", "Recipe", "m_recipeData", "recipeData", "m_data", "data", "m_value", "value", "Value", "m_selected", "selected" };

        private static InventoryGui _owner;
        private static GameObject _root;
        private static Button _toggleButton;
        private static Button _minus10Button;
        private static Button _minus1Button;
        private static Button _targetButton;
        private static Button _plus1Button;
        private static Button _plus10Button;
        private static RectTransform _craftRect;
        private static RectTransform _rootRect;
        private static RectTransform _uiParentRect;
        private static ZDO _stationZdo;
        private static ProductionStationKind _stationKind;
        private static string _output = string.Empty;
        private static bool _writable;
        private static float _nextNearbyResolveAt;
        private static ProductionStationRef _cachedNearbyStation;
        private static bool _loggedMissingCraftButton;

        // Harmony AccessTools logs a warning every time a requested member does not exist.
        // InventoryGui.Update runs every frame and RecipeDataPair legitimately differs between
        // Valheim versions, so probing several compatibility names through AccessTools produced
        // hundreds of thousands of log lines. Cache normal reflection lookups, including misses,
        // and keep the hot UI path completely silent.
        private static readonly Dictionary<Type, Dictionary<string, MemberInfo>> MemberLookupCache =
            new Dictionary<Type, Dictionary<string, MemberInfo>>();

        private static MemberInfo GetCachedMember(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            if (!MemberLookupCache.TryGetValue(type, out Dictionary<string, MemberInfo> byName))
            {
                byName = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
                MemberLookupCache[type] = byName;
            }
            if (byName.TryGetValue(name, out MemberInfo cached)) return cached;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MemberInfo resolved = null;
            try
            {
                resolved = (MemberInfo)type.GetField(name, flags) ?? type.GetProperty(name, flags);
            }
            catch { }
            byName[name] = resolved; // null is intentional: cache compatibility misses too.
            return resolved;
        }

        [HarmonyPostfix]
        private static void Postfix(InventoryGui __instance)
        {
            try
            {
                Tick(__instance);
            }
            catch (Exception ex)
            {
                Hide();
                Plugin.Log?.LogWarning($"[ProductionInline] UI update failed: {ex.Message}");
            }
        }

        private static void Tick(InventoryGui gui)
        {
            if (gui == null || !gui.gameObject.activeInHierarchy || Player.m_localPlayer == null)
            {
                Hide();
                return;
            }

            if (_owner != gui || _root == null)
            {
                DestroyUi();
                _owner = gui;
                if (!TryCreateUi(gui))
                {
                    Hide();
                    return;
                }
            }

            if (_root == null || _craftRect == null || !_craftRect.gameObject.activeInHierarchy)
            {
                Hide();
                return;
            }

            string output = ResolveSelectedOutput(gui);
            if (string.IsNullOrEmpty(output))
            {
                Hide();
                return;
            }

            if (!TryResolveCurrentStation(gui, out ProductionStationRef station) || station == null || station.Zdo == null)
            {
                Hide();
                return;
            }

            // Inline control is for cook/preparation recipes. Windmill/oven keep their existing
            // station popup because those are machine pipelines rather than vanilla recipe lists.
            if (station.Kind != ProductionStationKind.Preparation && station.Kind != ProductionStationKind.Cauldron)
            {
                Hide();
                return;
            }

            if (!ProductionStationRuntime.SupportsOutput(station.Kind, output))
            {
                Hide();
                return;
            }

            _stationZdo = station.Zdo;
            _stationKind = station.Kind;
            _output = output;
            _writable = station.View != null && station.View.IsOwner();

            PositionBelowCraftButton();
            RefreshLabels();
            _root.SetActive(true);
        }

        private static bool TryCreateUi(InventoryGui gui)
        {
            Button craftButton = ResolveCraftButton(gui);
            if (craftButton == null)
            {
                if (!_loggedMissingCraftButton)
                {
                    _loggedMissingCraftButton = true;
                    Plugin.Log?.LogWarning("[ProductionInline] Vanilla craft button was not found; inline stock controls stay disabled.");
                }
                return false;
            }

            _craftRect = craftButton.transform as RectTransform;
            if (_craftRect == null) _craftRect = craftButton.GetComponent<RectTransform>();
            if (_craftRect == null || _craftRect.parent == null) return false;

            // The vanilla crafting button sits inside a clipped crafting panel. v0.5.208
            // parented the worker controls to that same panel, so the controls existed but were
            // clipped as soon as they were positioned below the Herstellen button. Put the
            // overlay on the top UI canvas instead and track the craft button in canvas space.
            Canvas canvas = craftButton.GetComponentInParent<Canvas>();
            _uiParentRect = canvas != null ? canvas.transform as RectTransform : _craftRect.parent as RectTransform;
            if (_uiParentRect == null) return false;

            _root = new GameObject("ValheimWorkers_ProductionInline", typeof(RectTransform));
            _rootRect = _root.GetComponent<RectTransform>();
            _rootRect.SetParent(_uiParentRect, false);
            _rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            _rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            _rootRect.pivot = new Vector2(0.5f, 1f);
            _rootRect.localScale = Vector3.one;
            _root.transform.SetAsLastSibling();

            _toggleButton = CreateButton(craftButton, _rootRect, "StockToggle", "Worker-Bestand: AUS");
            _minus10Button = CreateButton(craftButton, _rootRect, "StockMinus10", "-10");
            _minus1Button = CreateButton(craftButton, _rootRect, "StockMinus1", "-1");
            _targetButton = CreateButton(craftButton, _rootRect, "StockTarget", "10");
            _plus1Button = CreateButton(craftButton, _rootRect, "StockPlus1", "+1");
            _plus10Button = CreateButton(craftButton, _rootRect, "StockPlus10", "+10");

            if (_toggleButton == null || _minus10Button == null || _minus1Button == null || _targetButton == null || _plus1Button == null || _plus10Button == null)
            {
                DestroyUi();
                return false;
            }

            _toggleButton.onClick.AddListener(ToggleMaintainStock);
            _minus10Button.onClick.AddListener(() => AdjustTarget(-10));
            _minus1Button.onClick.AddListener(() => AdjustTarget(-1));
            _plus1Button.onClick.AddListener(() => AdjustTarget(1));
            _plus10Button.onClick.AddListener(() => AdjustTarget(10));
            _targetButton.interactable = false;

            _root.SetActive(false);
            return true;
        }

        private static Button ResolveCraftButton(InventoryGui gui)
        {
            try
            {
                object raw = CraftButtonField?.GetValue(gui);
                if (raw is Button direct) return direct;
                if (raw is GameObject go) return go.GetComponent<Button>();
                if (raw is Component component) return component.GetComponent<Button>();
            }
            catch { }

            // Field names can change between Valheim builds. Do this fallback only while creating
            // the UI, never per frame.
            try
            {
                Button[] buttons = gui.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < buttons.Length; i++)
                {
                    Button button = buttons[i];
                    if (button == null) continue;
                    string n = button.gameObject.name ?? string.Empty;
                    if (n.IndexOf("craft", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("make", StringComparison.OrdinalIgnoreCase) >= 0)
                        return button;
                }
            }
            catch { }
            return null;
        }

        private static Button CreateButton(Button template, RectTransform parent, string name, string initialText)
        {
            if (template == null || parent == null) return null;

            GameObject go = new GameObject("VW_" + name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;

            Image image = go.GetComponent<Image>();
            Image sourceImage = template.GetComponent<Image>();
            if (sourceImage != null)
            {
                image.sprite = sourceImage.sprite;
                image.overrideSprite = sourceImage.overrideSprite;
                image.type = sourceImage.type;
                image.fillCenter = sourceImage.fillCenter;
                image.color = sourceImage.color;
                image.material = sourceImage.material;
                image.pixelsPerUnitMultiplier = sourceImage.pixelsPerUnitMultiplier;
            }

            Button button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.transition = template.transition;
            button.colors = template.colors;
            button.spriteState = template.spriteState;
            button.navigation = new Navigation { mode = Navigation.Mode.None };

            GameObject label = CloneTextVisual(template.gameObject, go.transform);
            if (label != null)
                SetAnyText(label, initialText);

            return button;
        }

        private static GameObject CloneTextVisual(GameObject sourceRoot, Transform parent)
        {
            Component[] components = sourceRoot.GetComponentsInChildren<Component>(true);
            Component sourceText = null;
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || component.gameObject == sourceRoot) continue;
                Type t = component.GetType();
                if (component is Text || t.Name.IndexOf("TMP_Text", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.Name.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sourceText = component;
                    break;
                }
            }
            if (sourceText == null) return null;

            GameObject clone = UnityEngine.Object.Instantiate(sourceText.gameObject, parent, false);
            clone.name = "Label";
            RectTransform rt = clone.transform as RectTransform;
            if (rt != null)
            {
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = new Vector2(3f, 1f);
                rt.offsetMax = new Vector2(-3f, -1f);
                rt.localScale = Vector3.one;
            }
            Graphic[] graphics = clone.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
                if (graphics[i] != null) graphics[i].raycastTarget = false;
            return clone;
        }

        private static void PositionBelowCraftButton()
        {
            if (_rootRect == null || _craftRect == null || _uiParentRect == null) return;

            _craftRect.GetWorldCorners(CraftCorners);
            Vector3 bl = _uiParentRect.InverseTransformPoint(CraftCorners[0]);
            Vector3 tl = _uiParentRect.InverseTransformPoint(CraftCorners[1]);
            Vector3 br = _uiParentRect.InverseTransformPoint(CraftCorners[3]);

            float width = Mathf.Abs(br.x - bl.x);
            float craftHeight = Mathf.Abs(tl.y - bl.y);
            if (width < 180f) width = 300f;
            if (craftHeight < 22f) craftHeight = 42f;

            float scale = Mathf.Clamp(craftHeight / 42f, 0.75f, 1.5f);
            float topHeight = 29f * scale;
            float rowHeight = 26f * scale;
            float gap = 4f * scale;
            float totalHeight = topHeight + gap + rowHeight;

            _rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            _rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            _rootRect.pivot = new Vector2(0.5f, 1f);
            _rootRect.sizeDelta = new Vector2(width, totalHeight);
            _rootRect.localPosition = new Vector3((bl.x + br.x) * 0.5f, bl.y - 6f * scale, 0f);
            _rootRect.SetAsLastSibling();

            SetButtonRect(_toggleButton, 0f, 0f, width, topHeight, width, totalHeight);

            float small = Mathf.Max(42f * scale, width * 0.155f);
            float target = Mathf.Max(54f * scale, width - small * 4f - gap * 4f);
            float left = -width * 0.5f;
            float y = -(topHeight + gap);
            SetButtonRectAbsolute(_minus10Button, left, y, small, rowHeight, width, totalHeight);
            left += small + gap;
            SetButtonRectAbsolute(_minus1Button, left, y, small, rowHeight, width, totalHeight);
            left += small + gap;
            SetButtonRectAbsolute(_targetButton, left, y, target, rowHeight, width, totalHeight);
            left += target + gap;
            SetButtonRectAbsolute(_plus1Button, left, y, small, rowHeight, width, totalHeight);
            left += small + gap;
            SetButtonRectAbsolute(_plus10Button, left, y, small, rowHeight, width, totalHeight);
        }

        private static void SetButtonRect(Button button, float x, float y, float width, float height, float parentWidth, float parentHeight)
        {
            SetButtonRectAbsolute(button, -width * 0.5f + x, y, width, height, parentWidth, parentHeight);
        }

        private static void SetButtonRectAbsolute(Button button, float left, float top, float width, float height, float parentWidth, float parentHeight)
        {
            if (button == null) return;
            RectTransform rt = button.transform as RectTransform;
            if (rt == null) return;
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(left, top);
        }

        private static void RefreshLabels()
        {
            if (_stationZdo == null || string.IsNullOrEmpty(_output)) return;

            bool hasOrder = ProductionOrderRuntime.TryGetOrder(_stationZdo, _output, out ProductionOrderState order);
            bool maintain = hasOrder && order.Mode == ProductionOrderMode.MaintainStock;
            int target = hasOrder ? Math.Max(1, order.Target) : ProductionStationRuntime.DefaultTarget(_output);
            string product = ProductionOrderRuntime.DisplayProduct(_output);

            SetButtonText(_toggleButton, maintain
                ? $"Worker-Bestand: AN  |  {product}"
                : hasOrder
                    ? $"Worker-Bestand: AUS  |  anderer Auftrag aktiv"
                    : $"Worker-Bestand: AUS  |  {product}");
            SetButtonText(_targetButton, target.ToString());

            _toggleButton.interactable = _writable;
            _minus10Button.interactable = _writable;
            _minus1Button.interactable = _writable;
            _plus1Button.interactable = _writable;
            _plus10Button.interactable = _writable;
            _targetButton.interactable = false;
        }

        private static void ToggleMaintainStock()
        {
            if (!_writable || _stationZdo == null || string.IsNullOrEmpty(_output)) return;
            if (ProductionOrderRuntime.TryGetOrder(_stationZdo, _output, out ProductionOrderState order) && order.Mode == ProductionOrderMode.MaintainStock)
            {
                ProductionOrderRuntime.RemoveOrder(_stationZdo, _output);
                Plugin.Instance?.SetExternalStatus($"{ProductionOrderRuntime.DisplayProduct(_output)}: Worker-Bestand ausgeschaltet.");
            }
            else
            {
                int target = order != null ? Math.Max(1, order.Target) : ProductionStationRuntime.DefaultTarget(_output);
                ProductionOrderRuntime.SetMaintainStock(_stationZdo, _output, target);
                Plugin.Instance?.SetExternalStatus($"{ProductionOrderRuntime.DisplayProduct(_output)}: Bestand {target} halten.");
            }
            RefreshLabels();
        }

        private static void AdjustTarget(int delta)
        {
            if (!_writable || _stationZdo == null || string.IsNullOrEmpty(_output) || delta == 0) return;
            int current = ProductionStationRuntime.DefaultTarget(_output);
            if (ProductionOrderRuntime.TryGetOrder(_stationZdo, _output, out ProductionOrderState order) && order != null)
                current = Math.Max(1, order.Target);
            int target = Math.Max(1, Math.Min(9999, current + delta));
            ProductionOrderRuntime.SetMaintainStock(_stationZdo, _output, target);
            Plugin.Instance?.SetExternalStatus($"{ProductionOrderRuntime.DisplayProduct(_output)}: Bestand {target} halten.");
            RefreshLabels();
        }

        private static string ResolveSelectedOutput(InventoryGui gui)
        {
            if (gui == null) return string.Empty;

            // Valheim 1.0.x has changed the selected-recipe holder between builds. It may be a
            // Recipe directly or a small UI/list wrapper containing m_recipe/recipe. Unwrap the
            // known candidates instead of assuming m_selectedRecipe itself owns m_item.
            for (int i = 0; i < SelectedRecipeFields.Length; i++)
            {
                object candidate = null;
                try { candidate = SelectedRecipeFields[i].GetValue(gui); } catch { }
                string prefab = ExtractOutputPrefab(candidate, 0);
                if (!string.IsNullOrEmpty(prefab)) return prefab;
            }

            // Property fallback for game builds that moved selection state away from fields.
            for (int i = 0; i < SelectedRecipeProperties.Length; i++)
            {
                object candidate = null;
                try { candidate = SelectedRecipeProperties[i].GetValue(gui, null); } catch { }
                string prefab = ExtractOutputPrefab(candidate, 0);
                if (!string.IsNullOrEmpty(prefab)) return prefab;
            }

            return string.Empty;
        }

        private static string ExtractOutputPrefab(object value, int depth)
        {
            if (value == null || depth > 4) return string.Empty;

            if (value is ItemDrop drop && drop.gameObject != null)
                return SpeciesRoleRegistry.NormalizePrefabName(drop.gameObject.name);
            if (value is GameObject go)
                return SpeciesRoleRegistry.NormalizePrefabName(go.name);
            if (value is Component component && component.gameObject != null)
            {
                // ItemDrop is handled above. Other components may be recipe/station UI wrappers;
                // do not return their GameObject name because that is not an item prefab.
                if (component is ItemDrop componentDrop)
                    return SpeciesRoleRegistry.NormalizePrefabName(componentDrop.gameObject.name);
            }
            if (value is string prefabString)
            {
                string normalized = SpeciesRoleRegistry.NormalizePrefabName(prefabString);
                if (!string.IsNullOrEmpty(normalized) &&
                    (CookingRecipeRuntime.TryGetRecipe(ProductionStationKind.Preparation, normalized, out _) ||
                     CookingRecipeRuntime.TryGetRecipe(ProductionStationKind.Cauldron, normalized, out _)))
                    return normalized;
            }

            // First unwrap members that directly describe the recipe output.
            for (int i = 0; i < OutputMemberNames.Length; i++)
            {
                object child = GetSingleMemberValue(value, OutputMemberNames[i]);
                if (child == null || ReferenceEquals(child, value)) continue;
                string prefab = ExtractOutputPrefab(child, depth + 1);
                if (!string.IsNullOrEmpty(prefab)) return prefab;
            }

            // Then unwrap common selected-list wrappers used by InventoryGui.
            for (int i = 0; i < RecipeWrapperMemberNames.Length; i++)
            {
                object child = GetSingleMemberValue(value, RecipeWrapperMemberNames[i]);
                if (child == null || ReferenceEquals(child, value)) continue;
                string prefab = ExtractOutputPrefab(child, depth + 1);
                if (!string.IsNullOrEmpty(prefab)) return prefab;
            }

            return string.Empty;
        }

        private static object GetSingleMemberValue(object source, string name)
        {
            if (source == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                MemberInfo member = GetCachedMember(source.GetType(), name);
                if (member is FieldInfo field) return field.GetValue(source);
                if (member is PropertyInfo property && property.CanRead && property.GetIndexParameters().Length == 0)
                    return property.GetValue(source, null);
            }
            catch { }
            return null;
        }

        private static bool TryResolveCurrentStation(InventoryGui gui, out ProductionStationRef station)
        {
            station = null;

            for (int i = 0; i < GuiStationFields.Length; i++)
            {
                object value = null;
                try { value = GuiStationFields[i].GetValue(gui); } catch { }
                if (TryResolveStationObject(value, out station)) return true;
            }

            Player player = Player.m_localPlayer;
            if (player != null)
            {
                for (int i = 0; i < PlayerStationFields.Length; i++)
                {
                    object value = null;
                    try { value = PlayerStationFields[i].GetValue(player); } catch { }
                    if (TryResolveStationObject(value, out station)) return true;
                }
            }

            // Dense bases can contain thousands of colliders. This fallback is deliberately tiny,
            // capped and cached. In the normal path one of the direct current-station fields above
            // resolves without any physics query.
            float now = Time.unscaledTime;
            if (_cachedNearbyStation != null && now < _nextNearbyResolveAt)
            {
                station = _cachedNearbyStation;
                return station.Zdo != null;
            }
            _nextNearbyResolveAt = now + 0.75f;
            _cachedNearbyStation = null;
            if (player == null) return false;

            int hits = 0;
            try { hits = Physics.OverlapSphereNonAlloc(player.transform.position, 5.5f, NearbyBuffer, ~0, QueryTriggerInteraction.Collide); }
            catch { hits = 0; }
            float best = float.MaxValue;
            var seen = new HashSet<ZDO>();
            for (int i = 0; i < hits && i < NearbyBuffer.Length; i++)
            {
                Collider collider = NearbyBuffer[i];
                NearbyBuffer[i] = null;
                if (collider == null || !ProductionStationRuntime.TryResolve(collider.gameObject, out ProductionStationRef candidate) || candidate?.Zdo == null || !seen.Add(candidate.Zdo))
                    continue;
                if (candidate.Kind != ProductionStationKind.Preparation && candidate.Kind != ProductionStationKind.Cauldron) continue;
                float d = (candidate.Position - player.transform.position).sqrMagnitude;
                if (d >= best) continue;
                best = d;
                _cachedNearbyStation = candidate;
            }
            station = _cachedNearbyStation;
            return station != null;
        }

        private static bool TryResolveStationObject(object value, out ProductionStationRef station)
        {
            station = null;
            if (value == null) return false;
            if (value is GameObject go) return ProductionStationRuntime.TryResolve(go, out station);
            if (value is Component component) return ProductionStationRuntime.TryResolve(component.gameObject, out station);
            return false;
        }

        private static FieldInfo[] BuildRecipeFieldList(Type type)
        {
            var result = new List<FieldInfo>();
            FieldInfo exact = AccessTools.Field(type, "m_selectedRecipe");
            if (exact != null) result.Add(exact);
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field == exact) continue;
                string name = field.Name ?? string.Empty;
                string typeName = field.FieldType?.Name ?? string.Empty;
                bool mentionsRecipe = name.IndexOf("recipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      typeName.IndexOf("recipe", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!mentionsRecipe) continue;
                bool looksSelected = name.IndexOf("selected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("current", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("active", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!looksSelected && typeName.IndexOf("recipe", StringComparison.OrdinalIgnoreCase) < 0) continue;
                result.Add(field);
            }
            return result.ToArray();
        }


        private static PropertyInfo[] BuildRecipePropertyList(Type type)
        {
            var result = new List<PropertyInfo>();
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                string name = property.Name ?? string.Empty;
                string typeName = property.PropertyType?.Name ?? string.Empty;
                bool mentionsRecipe = name.IndexOf("recipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      typeName.IndexOf("recipe", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!mentionsRecipe) continue;
                bool looksSelected = name.IndexOf("selected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("current", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("active", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!looksSelected && typeName.IndexOf("recipe", StringComparison.OrdinalIgnoreCase) < 0) continue;
                result.Add(property);
            }
            return result.ToArray();
        }

        private static FieldInfo[] BuildStationFieldList(Type type)
        {
            var result = new List<FieldInfo>();
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                string name = field.Name ?? string.Empty;
                if (name.IndexOf("station", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Type ft = field.FieldType;
                if (typeof(Component).IsAssignableFrom(ft) || typeof(GameObject).IsAssignableFrom(ft) || ft.Name.IndexOf("CraftingStation", StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Add(field);
            }
            return result.ToArray();
        }

        private static object GetMemberValue(object source, params string[] names)
        {
            if (source == null) return null;
            Type type = source.GetType();
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                try
                {
                    MemberInfo member = GetCachedMember(type, name);
                    if (member is FieldInfo field) return field.GetValue(source);
                    if (member is PropertyInfo property && property.CanRead && property.GetIndexParameters().Length == 0)
                        return property.GetValue(source, null);
                }
                catch { }
            }
            return null;
        }

        private static void SetButtonText(Button button, string text)
        {
            if (button == null) return;
            SetAnyText(button.gameObject, text ?? string.Empty);
        }

        private static void SetAnyText(GameObject root, string text)
        {
            if (root == null) return;
            Component[] components = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null) continue;
                if (component is Text uiText)
                {
                    uiText.text = text;
                    continue;
                }
                Type t = component.GetType();
                if (t.Name.IndexOf("TMP_Text", StringComparison.OrdinalIgnoreCase) < 0 && t.Name.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                try
                {
                    PropertyInfo property = t.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                    if (property != null && property.CanWrite) property.SetValue(component, text, null);
                }
                catch { }
            }
        }

        private static void Hide()
        {
            if (_root != null && _root.activeSelf) _root.SetActive(false);
            _stationZdo = null;
            _stationKind = ProductionStationKind.None;
            _output = string.Empty;
            _writable = false;
        }

        private static void DestroyUi()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _rootRect = null;
            _craftRect = null;
            _uiParentRect = null;
            _toggleButton = null;
            _minus10Button = null;
            _minus1Button = null;
            _targetButton = null;
            _plus1Button = null;
            _plus10Button = null;
            _stationZdo = null;
            _output = string.Empty;
        }
    }
}
