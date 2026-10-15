using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    internal enum WorkerContainerRole
    {
        Normal = 0,
        FieldDepot = 1,
        Import = 2,
        Export = 3
    }

    internal enum AirDepotMode
    {
        Outpost = 0,
        BaseHub = 1
    }

    internal static class WorkerContainerRoleRuntime
    {
        internal const string RoleKey = "vw_container_role";
        internal const string OwnerKey = "vw_container_owner";
        internal const string AirTransportKey = "vw_container_air_transport";
        internal const string AirDepotModeKey = "vw_air_depot_mode";
        internal const string KeepOneOverrideKey = "vw_keep_one_override";

        internal static WorkerContainerRole GetRole(Container container)
        {
            ZDO zdo = GetZdo(container);
            return zdo == null ? WorkerContainerRole.Normal : (WorkerContainerRole)Mathf.Clamp(zdo.GetInt(RoleKey, 0), 0, 3);
        }

        internal static long GetOwner(Container container)
        {
            ZDO zdo = GetZdo(container);
            return zdo?.GetLong(OwnerKey, 0L) ?? 0L;
        }

        internal static bool SetRole(Container container, WorkerContainerRole role, long ownerPlayerId)
        {
            ZNetView view = GetNetworkView(container);
            if (view == null || !view.IsValid() || !view.IsOwner()) return false;
            ZDO zdo = view.GetZDO();
            if (zdo == null) return false;
            zdo.Set(RoleKey, (int)role);
            if (role == WorkerContainerRole.Normal) zdo.Set(OwnerKey, 0L);
            else if (ownerPlayerId != 0L) zdo.Set(OwnerKey, ownerPlayerId);
            if (role != WorkerContainerRole.FieldDepot)
            {
                zdo.Set(AirTransportKey, false);
                zdo.Set(FieldDepotWorkzoneRuntime.WorkAreaBindingKey, string.Empty);
                zdo.Set(FieldDepotWorkzoneRuntime.ExplicitBindingKey, false);
                zdo.Set(FieldDepotWorkzoneRuntime.AutoAssignmentHandledKey, false);
                zdo.Set(AirFreightGroupRuntime.GroupIdKey, string.Empty);
            }
            if (role != WorkerContainerRole.Normal)
            {
                ContainerLedgerRuntime.SyncFromPhysical(container, "role-change");
                PersistentContainerRegistryRuntime.Remember(zdo);
            }
            else
            {
                PersistentContainerRegistryRuntime.Remember(zdo);
            }
            AirFreightContainerRuntime.Refresh(container);
            FieldSupplyDemandCacheRuntime.OnRoleOrPolicyChanged(container);
            WorkerProvisioningLogisticsRuntime.RequestPersistentProvisionAudit();
            return true;
        }

        internal static bool SupportsAirTransport(Container container)
        {
            ZDO zdo = GetZdo(container);
            return zdo != null && zdo.GetBool(AirTransportKey, false);
        }

        internal static bool SetAirTransport(Container container, bool enabled, long ownerPlayerId)
        {
            ZNetView view = GetNetworkView(container);
            if (view == null || !view.IsValid() || !view.IsOwner()) return false;
            ZDO zdo = view.GetZDO();
            if (zdo == null) return false;

            if (enabled)
            {
                // Air freight is always a FieldDepot capability. This keeps local-buffer semantics
                // (reserve/desired stock) while merely changing the transport method.
                zdo.Set(RoleKey, (int)WorkerContainerRole.FieldDepot);
                if (ownerPlayerId != 0L) zdo.Set(OwnerKey, ownerPlayerId);
                zdo.Set(AirTransportKey, true);
            }
            else
            {
                zdo.Set(AirTransportKey, false);
            }

            PersistentContainerRegistryRuntime.Remember(zdo);
            if (enabled) AirFreightGroupRuntime.TryResolveGroup(container, out _, out _);
            ContainerLedgerRuntime.SyncFromPhysical(container, enabled ? "air-freight-enabled" : "air-freight-disabled");
            AirFreightContainerRuntime.Refresh(container);
            FieldSupplyDemandCacheRuntime.OnRoleOrPolicyChanged(container);
            WorkerProvisioningLogisticsRuntime.RequestPersistentProvisionAudit();
            return true;
        }

        internal static bool HasClearSky(Container container)
        {
            return AirFreightContainerRuntime.HasClearSky(container);
        }


        internal static AirDepotMode GetAirDepotMode(Container container)
        {
            ZDO zdo = GetZdo(container);
            return zdo == null ? AirDepotMode.Outpost : (AirDepotMode)Mathf.Clamp(zdo.GetInt(AirDepotModeKey, 0), 0, 1);
        }

        internal static bool SetAirDepotMode(Container container, AirDepotMode mode)
        {
            ZNetView view = GetNetworkView(container);
            if (view == null || !view.IsValid() || !view.IsOwner()) return false;
            ZDO zdo = view.GetZDO();
            if (zdo == null) return false;
            zdo.Set(AirDepotModeKey, (int)mode);
            AirFreightGroupRuntime.TryResolveGroup(container, out _, out _);
            PersistentContainerRegistryRuntime.Remember(zdo);
            WorkerProvisioningLogisticsRuntime.RequestPersistentProvisionAudit();
            return true;
        }

        internal static string AirDepotModeDisplayName(AirDepotMode mode)
        {
            return mode == AirDepotMode.BaseHub ? "Base-Luftfracht" : "Aussenposten";
        }

        internal static bool MatchesOwner(Container container, long ownerPlayerId)
        {
            long owner = GetOwner(container);
            return owner == 0L || ownerPlayerId == 0L || owner == ownerPlayerId;
        }

        internal static bool ProtectLastItem(WorkerContainerRole role)
        {
            return role == WorkerContainerRole.Normal || role == WorkerContainerRole.FieldDepot;
        }

        internal static bool ProtectLastItem(Container container)
        {
            if (container == null) return false;
            ZDO zdo = GetZdo(container);
            WorkerContainerRole role = GetRole(container);
            if (zdo == null) return ProtectLastItem(role);
            int overrideValue = zdo.GetInt(KeepOneOverrideKey, -1);
            if (overrideValue == 0) return false;
            if (overrideValue == 1) return true;
            return ProtectLastItem(role);
        }

        internal static bool ProtectLastItem(ZDO zdo)
        {
            if (zdo == null) return false;
            WorkerContainerRole role = (WorkerContainerRole)Mathf.Clamp(zdo.GetInt(RoleKey, 0), 0, 3);
            int overrideValue = zdo.GetInt(KeepOneOverrideKey, -1);
            if (overrideValue == 0) return false;
            if (overrideValue == 1) return true;
            return ProtectLastItem(role);
        }

        internal static bool SetKeepOne(Container container, bool enabled)
        {
            ZNetView view = GetNetworkView(container);
            if (view == null || !view.IsValid() || !view.IsOwner()) return false;
            ZDO zdo = view.GetZDO();
            if (zdo == null) return false;
            zdo.Set(KeepOneOverrideKey, enabled ? 1 : 0);
            ContainerLedgerRuntime.SyncFromPhysical(container, "keep-one-change");
            return true;
        }

        internal static string DisplayName(WorkerContainerRole role)
        {
            switch (role)
            {
                case WorkerContainerRole.FieldDepot: return "Felddepot";
                case WorkerContainerRole.Import: return "Importkiste";
                case WorkerContainerRole.Export: return "Exportkiste";
                default: return "Normale Kiste";
            }
        }

        internal static string ShortDescription(WorkerContainerRole role)
        {
            switch (role)
            {
                case WorkerContainerRole.FieldDepot: return "Lokaler Puffer am Arbeitsgebiet. Sammler liefern ab; Gaertner/Foerster entnehmen; Versorgungsbedarf wird hier gespeichert. Keep-One ist pro Kiste schaltbar.";
                case WorkerContainerRole.Import: return "Wareneingang der Base. Traeger liefern an; Sortierer leeren in passende Basislager. Pro Item kann ein Min/Max-Puffer gesetzt werden: Max stoppt neue Lieferungen, Min gibt sie wieder frei. Keep-One ist pro Kiste schaltbar.";
                case WorkerContainerRole.Export: return "Warenausgang der Base. Sortierer stellen angeforderte Ware bereit; Traeger liefern zu Felddepots. Keep-One ist pro Kiste schaltbar.";
                default: return "Normale Lagerkiste. Keep-One ist pro Kiste schaltbar.";
            }
        }

        internal static ZNetView GetNetworkView(Container container)
        {
            if (container == null) return null;
            ZNetView view = container.GetComponent<ZNetView>();
            if (view == null) view = container.GetComponentInParent<ZNetView>();
            return view;
        }

        internal static ZDO GetZdo(Container container)
        {
            ZNetView view = GetNetworkView(container);
            return view != null && view.IsValid() ? view.GetZDO() : null;
        }
    }


    /// <summary>
    /// Direct Shift+E compatibility path for normal chests. SmartStorage can replace both the
    /// hover string and parts of the vanilla interaction chain, so the Workers role editor must
    /// not depend on Container.Interact actually reaching our Harmony prefix.
    ///
    /// This runtime resolves the exact object Valheim currently considers hovered via Player's
    /// m_hovering field (reflection keeps us compatible across minor game changes). Container
    /// hover calls are cached as a second source, and a small one-shot proximity/angular lookup
    /// is used only when Shift+E is physically pressed. There is no per-frame world/container scan.
    /// </summary>
    internal static class WorkerContainerDirectInputRuntime
    {
        private static FieldInfo _hoveringField;
        private static bool _hoveringFieldResolved;
        private static Container _lastHoveredContainer;
        private static int _lastHoveredFrame = -1000;
        private static Container _consumedContainer;
        private static int _consumedFrame = -1000;

        internal static void NoteHover(Container container)
        {
            if (container == null) return;
            _lastHoveredContainer = container;
            _lastHoveredFrame = Time.frameCount;
        }

        internal static bool WasConsumedThisFrame(Container container)
        {
            if (container == null || _consumedFrame != Time.frameCount || _consumedContainer == null) return false;
            return ReferenceEquals(container, _consumedContainer) ||
                   WorkerContainerRoleRuntime.GetNetworkView(container) == WorkerContainerRoleRuntime.GetNetworkView(_consumedContainer);
        }

        internal static void Tick()
        {
            try
            {
                Player player = Player.m_localPlayer;
                Plugin plugin = Plugin.Instance;
                if (player == null || plugin == null || plugin.HasOpenWorkersPopup()) return;
                if (!Input.GetKeyDown(KeyCode.E)) return;
                if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) return;

                if (!TryResolveHoveredContainer(player, out Container container) || container == null) return;
                if (LivestockFeedingRuntime.IsFeedingStation(container)) return;

                if (!plugin.TryOpenWorkerContainerMenu(container, player)) return;

                _consumedContainer = container;
                _consumedFrame = Time.frameCount;
                NoteHover(container);

                // SmartStorage/vanilla may have opened the normal inventory earlier in this same
                // frame. Close it immediately; the Workers editor remains the sole UI for Shift+E.
                try
                {
                    InventoryGui gui = InventoryGui.instance;
                    if (gui != null)
                    {
                        MethodInfo hide = typeof(InventoryGui).GetMethod("Hide", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        hide?.Invoke(gui, null);
                    }
                }
                catch { }

                WorkersModalUiRuntime.SetActive(true);
            }
            catch { }
        }

        private static bool TryResolveHoveredContainer(Player player, out Container container)
        {
            container = null;
            if (player == null) return false;

            // 1) Use Valheim's own current hover target. This matches the chest under the crosshair
            // even in third person, where a raw Camera.main ray can hit the player/roof first.
            try
            {
                if (!_hoveringFieldResolved)
                {
                    _hoveringFieldResolved = true;
                    _hoveringField = typeof(Player).GetField("m_hovering", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                object hovering = _hoveringField?.GetValue(player);
                if (hovering is GameObject go && TryResolveContainer(go, out container)) return true;
                if (hovering is Component component && TryResolveContainer(component.gameObject, out container)) return true;
            }
            catch { }

            // 2) Container.GetHoverText/GetHoverName is normally queried every frame. Keep the most
            // recent chest as a compatibility source even if SmartStorage overwrites the final text.
            if (_lastHoveredContainer != null && Time.frameCount - _lastHoveredFrame <= 3)
            {
                if (Vector3.Distance(player.transform.position, _lastHoveredContainer.transform.position) <= 8f)
                {
                    container = _lastHoveredContainer;
                    return true;
                }
            }

            // 3) Last-resort one-shot lookup only on the actual Shift+E keypress. Pick the nearby
            // chest closest to the current crosshair direction instead of trusting the first ray hit.
            Camera cam = Camera.main;
            if (cam == null) return false;
            Collider[] nearby = Physics.OverlapSphere(player.transform.position, 7f, ~0, QueryTriggerInteraction.Collide);
            float bestScore = float.NegativeInfinity;
            Container best = null;
            for (int i = 0; i < nearby.Length; i++)
            {
                Collider col = nearby[i];
                if (col == null || !TryResolveContainer(col.gameObject, out Container candidate) || candidate == null) continue;
                if (LivestockFeedingRuntime.IsFeedingStation(candidate)) continue;

                Vector3 target = candidate.transform.position + Vector3.up * 0.5f;
                Vector3 delta = target - cam.transform.position;
                float distance = delta.magnitude;
                if (distance < 0.01f || distance > 9f) continue;
                float dot = Vector3.Dot(cam.transform.forward, delta / distance);
                if (dot < 0.88f) continue;
                float score = dot * 100f - distance * 1.5f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            container = best;
            return container != null;
        }

        private static bool TryResolveContainer(GameObject go, out Container container)
        {
            container = null;
            if (go == null) return false;
            container = go.GetComponent<Container>() ?? go.GetComponentInParent<Container>();
            if (container != null) return true;

            // Several chest visuals/colliders are siblings under the ZNetView root.
            ZNetView view = go.GetComponentInParent<ZNetView>();
            if (view != null) container = view.GetComponentInChildren<Container>(true);
            return container != null;
        }
    }


    // Intercept the player's alternate interaction before Valheim opens the container.
    // Container.Interact is kept below as a fallback, but Player.Interact is the reliable
    // choke point for Shift+E because the target can be a child collider and vanilla
    // may otherwise consume the interaction before the container prefix can own the UI.
    [HarmonyPatch(typeof(Player), "Interact", typeof(GameObject), typeof(bool), typeof(bool))]
    [HarmonyBefore("local.valheim.smartstorage")]
    internal static class WorkerContainerPlayerInteractPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Player __instance, GameObject go, bool hold, bool alt)
        {
            try
            {
                if (__instance == null || __instance != Player.m_localPlayer || go == null || hold) return true;
                if (WorkersModalUiRuntime.Active) return true;

                // Valheim 1.0.17 can route the alternate-use state through the `alt`
                // argument even when Unity's raw Shift key state is no longer visible here.
                // Accept both paths so Shift+E keeps opening the Workers logistics menu.
                bool alternateInteract = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (!alternateInteract) return true;

                Container container = go.GetComponent<Container>() ?? go.GetComponentInParent<Container>();
                if (container == null)
                {
                    ZNetView rootView = go.GetComponentInParent<ZNetView>();
                    if (rootView != null) container = rootView.GetComponentInChildren<Container>(true);
                }
                if (container == null) return true;
                if (WorkerContainerDirectInputRuntime.WasConsumedThisFrame(container)) return false;
                // Dedicated livestock feeding stations own their interaction completely.
                // Never route Shift+E into the generic logistics-role menu.
                if (LivestockFeedingRuntime.IsFeedingStation(container)) return true;

                Plugin plugin = Plugin.Instance;
                if (plugin == null) return true;

                // When handled, stop Player.Interact completely so vanilla cannot also open/use the chest.
                bool handled = plugin.TryOpenWorkerContainerMenu(container, __instance);
                return !handled;
            }
            catch
            {
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(Container), "Interact", typeof(Humanoid), typeof(bool), typeof(bool))]
    [HarmonyBefore("local.valheim.smartstorage")]
    internal static class WorkerContainerInteractPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Container __instance, Humanoid character, bool hold, bool alt, ref bool __result)
        {
            try
            {
                if (hold || __instance == null || character == null) return true;
                // Feeding troughs/dispensers are dedicated livestock stations, not generic
                // Workers logistics chests. Leave Shift+E to LivestockFeedTroughInteractPatch.
                if (LivestockFeedingRuntime.IsFeedingStation(__instance)) return true;
                if (Player.m_localPlayer == null || character != Player.m_localPlayer) return true;
                if (WorkerContainerDirectInputRuntime.WasConsumedThisFrame(__instance))
                {
                    __result = true;
                    return false;
                }
                // Valheim 1.0.17 can route the alternate-use state through the `alt`
                // argument even when Unity's raw Shift key state is no longer visible here.
                // Accept both paths so Shift+E keeps opening the Workers logistics menu.
                bool alternateInteract = alt || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (!alternateInteract) return true;
                Plugin plugin = Plugin.Instance;
                if (plugin == null) return true;
                __result = plugin.TryOpenWorkerContainerMenu(__instance, character);
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(Container), "GetHoverText")]
    [HarmonyAfter("local.valheim.smartstorage")]
    internal static class WorkerContainerHoverPatch
    {
        private static void AppendWorkersSuffix(Container container, ref string result)
        {
            try
            {
                if (container == null || Plugin.Instance == null) return;
                WorkerContainerDirectInputRuntime.NoteHover(container);
                // Dedicated feeding stations must not advertise the generic logistics-role UI.
                if (LivestockFeedingRuntime.IsFeedingStation(container)) return;

                string current = result ?? string.Empty;
                // SmartStorage also patches Container.GetHoverText and can replace the result in a
                // later postfix. Keep this helper idempotent because we run it both as Postfix and
                // as Finalizer; the Finalizer is the authoritative last writer for compatibility.
                if (current.IndexOf("Logistikrolle", StringComparison.Ordinal) >= 0 ||
                    current.IndexOf("Workers:", StringComparison.Ordinal) >= 0)
                    return;

                WorkerContainerRole role = WorkerContainerRoleRuntime.GetRole(container);
                string roleName = WorkerContainerRoleRuntime.DisplayName(role);
                if (role == WorkerContainerRole.FieldDepot && WorkerContainerRoleRuntime.SupportsAirTransport(container))
                    roleName = $"Luftfrachtkiste - Import + Export ({WorkerContainerRoleRuntime.AirDepotModeDisplayName(WorkerContainerRoleRuntime.GetAirDepotMode(container))})";
                string zoneSuffix = string.Empty;
                if (role == WorkerContainerRole.FieldDepot)
                {
                    string zoneId = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(container);
                    if (!string.IsNullOrEmpty(zoneId)) zoneSuffix = $"\nWorkzone: {FieldDepotWorkzoneRuntime.GetDisplayName(zoneId)}";
                }
                string suffix = $"\nWorkers: {roleName}{zoneSuffix}\n[<color=yellow><b>SHIFT+E</b></color>] Logistikrolle";
                result = current + suffix;
            }
            catch { }
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Container __instance, ref string __result)
        {
            AppendWorkersSuffix(__instance, ref __result);
        }

        // Harmony finalizers run after all prefixes/postfixes. SmartStorage currently rewrites the
        // chest hover string after our ordinary postfix, which made the Workers hint disappear.
        // Re-appending here avoids depending on patch order or the other mod's implementation.
        [HarmonyPriority(Priority.Last)]
        private static void Finalizer(Container __instance, ref string __result)
        {
            AppendWorkersSuffix(__instance, ref __result);
        }
    }

    [HarmonyPatch(typeof(Container), "GetHoverName")]
    [HarmonyAfter("local.valheim.smartstorage")]
    internal static class WorkerContainerHoverNamePatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Container __instance)
        {
            WorkerContainerDirectInputRuntime.NoteHover(__instance);
        }
    }
}
