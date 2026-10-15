using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Fast per-item stock editor entry point for role-switched Import and FieldDepot containers.
    /// MMB or Alt+LMB over an item in the vanilla container grid opens the role-appropriate tiny editor.
    /// Valheim 1.0 no longer exposes the legacy hovered-element helper reliably, so we resolve
    /// the slot directly from the rendered InventoryElement RectTransforms only on the hotkey frame.
    /// No catalog/ObjectDB search and no per-frame element scan is performed.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), "Update")]
    internal static class ContainerStockQuickEditorInventoryPatch
    {
        private static readonly FieldInfo CurrentContainerField = AccessTools.Field(typeof(InventoryGui), "m_currentContainer");
        private static readonly FieldInfo ContainerGridField = AccessTools.Field(typeof(InventoryGui), "m_containerGrid");
        private static readonly FieldInfo GridRootField = AccessTools.Field(typeof(InventoryGrid), "m_gridRoot");
        private static int _handledFrame = -1;

        [HarmonyPrefix]
        private static bool Prefix(InventoryGui __instance)
        {
            Plugin plugin = Plugin.Instance;
            if (plugin == null || __instance == null) return true;

            Container container = CurrentContainerField?.GetValue(__instance) as Container;
            plugin.ValidateContainerStockQuickEditor(container);
            if (!IsSupported(container)) return true;

            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (!alt || !Input.GetMouseButtonDown(0) || _handledFrame == Time.frameCount) return true;

            if (!TryOpenHovered(__instance, container, plugin, "ALT+LMB")) return true;
            _handledFrame = Time.frameCount;

            // Consume this InventoryGui.Update frame so vanilla does not also move/use the clicked item.
            // The editor is already open; skipping one inventory update is harmless and prevents a double action.
            return false;
        }

        [HarmonyPostfix]
        private static void Postfix(InventoryGui __instance)
        {
            Plugin plugin = Plugin.Instance;
            if (plugin == null || __instance == null) return;

            Container container = CurrentContainerField?.GetValue(__instance) as Container;
            plugin.ValidateContainerStockQuickEditor(container);
            if (!IsSupported(container)) return;
            if (!Input.GetMouseButtonDown(2) || _handledFrame == Time.frameCount) return;

            if (TryOpenHovered(__instance, container, plugin, "MMB"))
                _handledFrame = Time.frameCount;
        }

        private static bool IsSupported(Container container)
        {
            if (container == null) return false;
            WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(container);
            return role == WorkerContainerRole.Import || role == WorkerContainerRole.FieldDepot;
        }

        private static bool TryOpenHovered(InventoryGui gui, Container container, Plugin plugin, string trigger)
        {
            InventoryGrid grid = ContainerGridField?.GetValue(gui) as InventoryGrid;
            if (grid == null)
            {
                Plugin.Log?.LogWarning($"[ContainerStockQuick] {trigger} erkannt, aber m_containerGrid ist null.");
                return false;
            }

            try
            {
                Inventory inventory = grid.GetInventory();
                if (inventory == null || inventory != container.GetInventory())
                {
                    Plugin.Log?.LogWarning($"[ContainerStockQuick] {trigger} erkannt, aber Container-Inventory stimmt nicht mit m_containerGrid ueberein.");
                    return false;
                }

                InventoryElement hovered = GetElementUnderMouse(grid, out int elementCount);
                if (hovered == null)
                {
                    // Alt+LMB may have been aimed at the player inventory, in which case vanilla should continue normally.
                    if (string.Equals(trigger, "MMB", StringComparison.Ordinal))
                        Plugin.Log?.LogInfo($"[ContainerStockQuick] MMB erkannt, aber kein Item-Slot unter Maus gefunden. elements={elementCount}, mouse=({Input.mousePosition.x:0},{Input.mousePosition.y:0}).");
                    return false;
                }

                Vector2i pos = hovered.Position;
                ItemDrop.ItemData item = inventory.GetItemAt(pos.x, pos.y);
                if (item == null || item.m_stack <= 0)
                {
                    if (string.Equals(trigger, "MMB", StringComparison.Ordinal))
                        Plugin.Log?.LogInfo($"[ContainerStockQuick] Slot ({pos.x},{pos.y}) getroffen, aber leer.");
                    return false;
                }

                Plugin.Log?.LogInfo($"[ContainerStockQuick] {trigger} -> Slot ({pos.x},{pos.y}), item={WorkerStorageRuntime.GetPrefabName(item)}, stack={item.m_stack}.");
                plugin.OpenContainerStockQuickEditor(container, item);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ContainerStockQuick] {trigger} item lookup failed: {ex}");
                return false;
            }
        }

        private static InventoryElement GetElementUnderMouse(InventoryGrid grid, out int elementCount)
        {
            elementCount = 0;
            if (grid == null) return null;

            Transform root = GridRootField?.GetValue(grid) as Transform;
            if (root == null)
            {
                GameObject rootGo = GridRootField?.GetValue(grid) as GameObject;
                if (rootGo != null) root = rootGo.transform;
            }
            if (root == null) root = grid.transform;

            InventoryElement[] elements = root.GetComponentsInChildren<InventoryElement>(true);
            elementCount = elements?.Length ?? 0;
            if (elements == null || elements.Length == 0) return null;

            Canvas canvas = root.GetComponentInParent<Canvas>();
            Camera uiCamera = null;
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                uiCamera = canvas.worldCamera;

            Vector2 mouse = Input.mousePosition;
            for (int i = 0; i < elements.Length; i++)
            {
                InventoryElement element = elements[i];
                if (element == null || !element.gameObject.activeInHierarchy) continue;

                RectTransform rect = element.transform as RectTransform;
                if (rect == null) rect = element.GetComponent<RectTransform>();
                if (rect == null) continue;

                if (RectTransformUtility.RectangleContainsScreenPoint(rect, mouse, uiCamera))
                    return element;
            }

            return null;
        }
    }
}
