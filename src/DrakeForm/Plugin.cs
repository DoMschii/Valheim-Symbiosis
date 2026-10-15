using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers_DrakeForm
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("germa.valheim.workers", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("germa.valheim.workers.mount", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.jotunn.jotunn", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "germa.valheim.workers.drakeform";
        public const string PluginName = "Valheim_Workers_DrakeForm";
        public const string PluginVersion = "0.7.0";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        private Harmony _harmony;
        private DrakeMedallionItemRuntime _itemRuntime;
        private SerpentMedallionItemRuntime _serpentItemRuntime;
        private TrollMedallionItemRuntime _trollItemRuntime;
        private WerewolfMedallionItemRuntime _werewolfItemRuntime;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            _itemRuntime = new DrakeMedallionItemRuntime(Logger.LogDebug, Logger.LogInfo);
            _serpentItemRuntime = new SerpentMedallionItemRuntime(Logger.LogDebug, Logger.LogInfo);
            _trollItemRuntime = new TrollMedallionItemRuntime(Logger.LogDebug, Logger.LogInfo);
            _werewolfItemRuntime = new WerewolfMedallionItemRuntime(Logger.LogDebug, Logger.LogInfo);
            _itemRuntime.Begin();
            _serpentItemRuntime.Begin();
            _trollItemRuntime.Begin();
            _werewolfItemRuntime.Begin();
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();
            Logger.LogInfo("Valheim_Workers_DrakeForm v0.7.0 loaded. Bound symbiosis necklaces activate by item-use from inventory/hotbar without equipping; different necklaces can switch forms safely after the current exit transition; Werewolf entry still requires darkness (night or instanced cave/dungeon).");
        }

        private void Update()
        {
            _itemRuntime?.Tick();
            _serpentItemRuntime?.Tick();
            _trollItemRuntime?.Tick();
            _werewolfItemRuntime?.Tick();
            Player player = Player.m_localPlayer;
            if (player == null) return;
            DrakeFormController controller = player.GetComponent<DrakeFormController>();
            if (controller == null) controller = player.gameObject.AddComponent<DrakeFormController>();
            controller.TickLocalInput();
        }

        private void OnDestroy()
        {
            try
            {
                Player player = Player.m_localPlayer;
                player?.GetComponent<DrakeFormController>()?.ForceExit("Add-on wird entladen.");
            }
            catch { }
            try { _harmony?.UnpatchSelf(); } catch { }
            Instance = null;
            Log = null;
        }
    }

    [HarmonyPatch(typeof(Player), "Awake")]
    internal static class DrakeFormPlayerAwakePatch
    {
        private static void Postfix(Player __instance)
        {
            if (__instance == null) return;
            if (__instance == Player.m_localPlayer && __instance.GetComponent<DrakeFormController>() == null)
                __instance.gameObject.AddComponent<DrakeFormController>();
        }
    }

    [HarmonyPatch(typeof(Player), "UseHotbarItem")]
    internal static class DrakeFormHotbarUsePatch
    {
        // Player.UseHotbarItem receives the visible hotbar number (1..8), while the
        // inventory grid itself is zero-based (x = 0..7). Converting here is important:
        // using the number directly reads the cell to the right and lets Valheim's native
        // item-use path run, which toggles an already-equipped necklace off instead of
        // activating the bound form.
        [HarmonyPriority(Priority.First + 1000)]
        private static bool Prefix(Player __instance, object[] __args)
        {
            if (__instance == null || __instance != Player.m_localPlayer) return true;

            int slot = -1;
            if (__args != null)
            {
                for (int i = 0; i < __args.Length; i++)
                {
                    if (__args[i] is int index) { slot = index; break; }
                }
            }
            if (slot < 1 || slot > 8) return true;

            Inventory inventory = __instance.GetInventory();
            int gridX = slot - 1;
            ItemDrop.ItemData item = inventory != null ? inventory.GetItemAt(gridX, 0) : null;
            if (!DrakeCaptureRuntime.IsBoundSymbiosisNecklace(item)) return true;

            DrakeFormController controller = __instance.GetComponent<DrakeFormController>();
            if (controller == null) controller = __instance.gameObject.AddComponent<DrakeFormController>();
            controller.ToggleFromHotbar(item);
            return false;
        }
    }

    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UseItem), new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(bool) })]
    internal static class SymbiosisNecklaceUsePatch
    {
        // Inventory right-click / "Use" must activate a bound necklace directly rather than
        // letting vanilla equip the Wishbone-derived utility item. Run before Mount's active-form
        // item lock so the same necklace can also be used to leave symbiosis.
        [HarmonyPriority(Priority.First + 1200)]
        private static bool Prefix(Humanoid __instance, object[] __args)
        {
            Player player = __instance as Player;
            if (player == null || player != Player.m_localPlayer) return true;

            ItemDrop.ItemData item = null;
            if (__args != null)
            {
                for (int i = 0; i < __args.Length; i++)
                {
                    if (__args[i] is ItemDrop.ItemData candidate)
                    {
                        item = candidate;
                        break;
                    }
                }
            }

            if (!DrakeCaptureRuntime.IsBoundSymbiosisNecklace(item)) return true;

            DrakeFormController controller = player.GetComponent<DrakeFormController>();
            if (controller == null) controller = player.gameObject.AddComponent<DrakeFormController>();
            controller.ToggleFromHotbar(item);
            return false;
        }
    }

}
