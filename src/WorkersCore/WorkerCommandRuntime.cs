using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Adds a client-local E interaction surface to recruited creatures. The command itself is
    /// persisted through WorkerState.Follow; actual locomotion stays inside vanilla MonsterAI via
    /// SetFollowTarget(GameObject).
    /// </summary>
    internal static class WorkerCommandRuntime
    {
        internal static void EnsureInteractable(Character worker)
        {
            if (worker == null || worker is Player) return;

            ZNetView nview = worker.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (!WorkerZdo.IsWorker(zdo)) return;

            if (worker.GetComponent<WorkerCommandInteractable>() == null)
                worker.gameObject.AddComponent<WorkerCommandInteractable>();
            WorkerStatusIconRuntime.Ensure(worker);
        }
    }

    // Character already supplies the normal creature hover text. We only need to become the
    // Interactable selected by Valheim's existing hover-object pipeline.
    internal sealed class WorkerCommandInteractable : MonoBehaviour, Interactable
    {
        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            Character worker = GetComponent<Character>() ?? GetComponentInParent<Character>();
            Plugin plugin = Plugin.Instance;
            if (plugin == null) return false;
            return alt
                ? plugin.TryOpenWorkerInventory(worker, user)
                : plugin.TryInteractWithWorker(worker, user);
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    }


    /// <summary>
    /// Vanilla Lox taming remains useful only as the underlying friendly/faction flag. As soon as a
    /// recruited Lox belongs to a real Workers work area, all player-facing Tameable interaction is
    /// replaced by the Workers command surface. This prevents Pet/Follow/Rename from competing with
    /// hauling and guarantees that Shift+E reaches the Worker menu.
    /// </summary>
    internal static class WorkerTameableInteractionRuntime
    {
        internal static bool IsWorkspaceManagedLox(Tameable tameable, out Character worker, out ZDO zdo)
        {
            worker = null;
            zdo = null;
            if (tameable == null) return false;

            worker = tameable.GetComponent<Character>() ?? tameable.GetComponentInParent<Character>();
            return IsWorkspaceManagedLox(worker, out zdo);
        }

        internal static bool IsWorkspaceManagedLox(Character worker, out ZDO zdo)
        {
            zdo = null;
            if (worker == null || worker is Player) return false;

            string prefab = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            if (!string.Equals(prefab, "Lox", System.StringComparison.OrdinalIgnoreCase)) return false;

            ZNetView nview = worker.GetComponent<ZNetView>();
            zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (!WorkerZdo.IsWorker(zdo)) return false;
            if ((WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None) != WorkerRole.Hauler) return false;

            return WorkerZdo.TryReadWorkArea(zdo, out _, out _, out _);
        }

        internal static string BuildWorkerOnlyHover(Character worker, ZDO zdo)
        {
            string zone = WorkerZdo.ReadWorkAreaName(zdo);
            string flagId = WorkerZdo.ReadWorkAreaId(zdo);
            zone = WorkFlagRegistry.TryGetDisplayName(flagId, zone);
            string tier = WorkerTierCatalog.DisplayTier(worker);
            string role = string.IsNullOrWhiteSpace(zone)
                ? $"<color=#8FD18F><b>Träger</b></color> <color=#A9C7FF>• {tier}</color>"
                : $"<color=#8FD18F><b>Träger</b></color> <color=#A9C7FF>• {tier}</color> <color=#D7C58A>• {zone}</color>";

            Plugin plugin = Plugin.Instance;
            string commands = plugin != null ? plugin.GetWorkerCommandHoverText(worker) : string.Empty;
            string title = "Lox";
            return string.IsNullOrEmpty(commands)
                ? title + "\n" + role
                : title + "\n" + role + "\n" + commands;
        }
    }

    // v0.5.396: a long-range assignment is resolved directly from the camera aim in Plugin.Update.
    // Suppress the normal short-range E interaction later in the same frame so one keypress cannot
    // also open/toggle an unrelated nearby interactable after the Worker was assigned.
    [HarmonyPatch(typeof(Player), "Interact", typeof(GameObject), typeof(bool), typeof(bool))]
    internal static class PendingWorkerAssignmentPlayerInteractSuppressPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Player __instance)
        {
            Plugin plugin = Plugin.Instance;
            return plugin == null || !plugin.ShouldSuppressNativeInteractThisFrame(__instance);
        }
    }

    // Route the actual player interaction before Valheim can pick Tameable's Pet/Follow/Rename
    // implementation. The target GameObject can be a child collider, so resolve Character upward.
    [HarmonyPatch(typeof(Player), "Interact", typeof(GameObject), typeof(bool), typeof(bool))]
    internal static class WorkerLoxPlayerInteractPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Player __instance, GameObject go, bool hold, bool alt)
        {
            try
            {
                if (__instance == null || __instance != Player.m_localPlayer || go == null || hold) return true;
                Character worker = go.GetComponent<Character>() ?? go.GetComponentInParent<Character>();
                if (!WorkerTameableInteractionRuntime.IsWorkspaceManagedLox(worker, out _)) return true;

                Plugin plugin = Plugin.Instance;
                if (plugin == null) return true;
                bool handled = alt
                    ? plugin.TryOpenWorkerInventory(worker, __instance)
                    : plugin.TryInteractWithWorker(worker, __instance);
                return !handled;
            }
            catch
            {
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(Tameable), "Interact")]
    internal static class WorkerLoxTameableInteractPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Tameable __instance, Humanoid user, bool hold, bool alt, ref bool __result)
        {
            if (!WorkerTameableInteractionRuntime.IsWorkspaceManagedLox(__instance, out Character worker, out _))
                return true;

            // Never allow vanilla Pet/Follow/Rename for a work-area Lox. If this method is reached
            // directly instead of Player.Interact, forward it to the exact same Workers command.
            Plugin plugin = Plugin.Instance;
            if (plugin == null || hold)
            {
                __result = true;
                return false;
            }

            __result = alt
                ? plugin.TryOpenWorkerInventory(worker, user)
                : plugin.TryInteractWithWorker(worker, user);
            return false;
        }
    }

    [HarmonyPatch(typeof(Tameable), "GetHoverText")]
    internal static class WorkerLoxTameableHoverPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Tameable __instance, ref string __result)
        {
            if (!WorkerTameableInteractionRuntime.IsWorkspaceManagedLox(__instance, out Character worker, out ZDO zdo))
                return true;

            __result = WorkerTameableInteractionRuntime.BuildWorkerOnlyHover(worker, zdo);
            return false;
        }
    }

    [HarmonyPatch(typeof(Character), "GetHoverText")]
    internal static class CharacterWorkerHoverPatch
    {
        private static void Postfix(Character __instance, ref string __result)
        {
            if (__instance == null || __instance is Player) return;

            ZNetView nview = __instance.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (!WorkerZdo.IsWorker(zdo)) return;

            WorkerCommandRuntime.EnsureInteractable(__instance);

            string roleLabel = GetRoleLabel(zdo);
            if (!string.IsNullOrEmpty(roleLabel))
            {
                string zone = WorkerZdo.ReadWorkAreaName(zdo);
                string flagId = WorkerZdo.ReadWorkAreaId(zdo);
                zone = WorkFlagRegistry.TryGetDisplayName(flagId, zone);
                string tierLabel = WorkerTierCatalog.DisplayTier(__instance);
                string roleLine = string.IsNullOrWhiteSpace(zone)
                    ? $"<color=#8FD18F><b>{roleLabel}</b></color> <color=#A9C7FF>• {tierLabel}</color>"
                    : $"<color=#8FD18F><b>{roleLabel}</b></color> <color=#A9C7FF>• {tierLabel}</color> <color=#D7C58A>• {zone}</color>";
                __result = string.IsNullOrEmpty(__result) ? roleLine : roleLine + "\n" + __result;
            }

            Plugin plugin = Plugin.Instance;
            string commandText = plugin != null ? plugin.GetWorkerCommandHoverText(__instance) : string.Empty;
            if (string.IsNullOrEmpty(commandText)) return;

            __result = string.IsNullOrEmpty(__result) ? commandText : __result + "\n" + commandText;
        }

        private static string GetRoleLabel(ZDO zdo)
        {
            if (zdo == null) return string.Empty;

            WorkerRole role = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            switch (role)
            {
                case WorkerRole.Lumberjack: return "Holzf\u00e4ller";
                case WorkerRole.Miner: return "Bergarbeiter";
                case WorkerRole.Fisher: return "Fischer";
                case WorkerRole.Gatherer:
                    if (WorkerZdo.TryReadWorkArea(zdo, out WorkOrder gatherOrder, out _, out _) && gatherOrder == WorkOrder.Harvest)
                        return "Ernter";
                    return "Sammler";
                case WorkerRole.Guard: return "Wache";
                case WorkerRole.Hauler: return "Tr\u00e4ger";
                case WorkerRole.Stonemason: return "Steinmetz";
                case WorkerRole.Firekeeper: return "Feuerh\u00fcter";
                case WorkerRole.Baker: return "B\u00e4cker / Produktion";
                case WorkerRole.Butcher: return "Metzger / Tierpfleger";
                case WorkerRole.Smith: return "Schmied / Metall";
                case WorkerRole.Hunter: return "J\u00e4ger";
                case WorkerRole.Gardener:
                    if (WorkerZdo.TryReadWorkArea(zdo, out WorkOrder order, out _, out _))
                    {
                        if (order == WorkOrder.Garden) return "G\u00e4rtner";
                        if (order == WorkOrder.Forestry) return "F\u00f6rster";
                    }
                    return "G\u00e4rtner / F\u00f6rster";
                default: return string.Empty;
            }
        }
    }
}
