using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// v0.5.342: explicit Workzone identity for every FieldDepot.
    ///
    /// The Workzone is the durable logistics address. Concrete chest ZDO ids are only temporary
    /// execution targets while a worker is walking to a chest. Several FieldDepots with the same
    /// Workzone id therefore form one logical depot pool.
    /// </summary>
    internal static class FieldDepotWorkzoneRuntime
    {
        internal const string WorkAreaBindingKey = "vw_field_depot_work_area_id";
        internal const string ExplicitBindingKey = "vw_field_depot_work_area_explicit";
        internal const string AutoAssignmentHandledKey = "vw_field_depot_auto_work_area_handled";

        internal static string GetAssignedWorkAreaId(Container depot)
        {
            return GetAssignedWorkAreaId(WorkerContainerRoleRuntime.GetZdo(depot));
        }

        internal static string GetAssignedWorkAreaId(ZDO depotZdo)
        {
            if (depotZdo == null) return string.Empty;
            return depotZdo.GetString(WorkAreaBindingKey, string.Empty) ?? string.Empty;
        }

        internal static bool IsExplicit(Container depot)
        {
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            return zdo != null && zdo.GetBool(ExplicitBindingKey, false);
        }

        internal static bool SetAssignedWorkArea(Container depot, string workAreaId)
        {
            if (depot == null || WorkerContainerRoleRuntime.GetRole(depot) != WorkerContainerRole.FieldDepot) return false;
            ZNetView view = WorkerContainerRoleRuntime.GetNetworkView(depot);
            if (view == null || !view.IsValid() || !view.IsOwner()) return false;
            ZDO zdo = view.GetZDO();
            if (zdo == null) return false;

            string clean = (workAreaId ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(clean))
            {
                if (!WorkFlagRegistry.TryGetPersistentState(clean, out ZDO flagZdo) || flagZdo == null)
                {
                    if (!WorkFlagRegistry.TryFindById(clean, 40f, out WorkAreaSnapshot snapshot) || snapshot.Flag == null)
                        return false;
                    flagZdo = snapshot.Flag.GetNetworkView()?.GetZDO();
                }
                if (flagZdo != null)
                {
                    long ownerId = zdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L);
                    long flagOwner = WorkFlagZdo.ReadOwner(flagZdo);
                    if (ownerId != 0L && flagOwner != 0L && ownerId != flagOwner) return false;
                }
            }

            zdo.Set(WorkAreaBindingKey, clean);
            zdo.Set(ExplicitBindingKey, !string.IsNullOrEmpty(clean));
            zdo.Set(AutoAssignmentHandledKey, true);
            if (WorkerContainerRoleRuntime.SupportsAirTransport(depot))
                zdo.Set(AirFreightGroupRuntime.GroupIdKey, clean);

            PersistentContainerRegistryRuntime.Remember(zdo);
            FieldSupplyDemandCacheRuntime.OnRoleOrPolicyChanged(depot);
            WorkerProvisioningLogisticsRuntime.RequestPersistentProvisionAudit();
            return true;
        }

        internal static bool TryAutoAssignNearestWorkArea(Container depot, out string workAreaId, out string displayName)
        {
            workAreaId = string.Empty;
            displayName = string.Empty;
            if (depot == null || WorkerContainerRoleRuntime.GetRole(depot) != WorkerContainerRole.FieldDepot) return false;

            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return false;
            if (!string.IsNullOrEmpty(GetAssignedWorkAreaId(zdo)))
            {
                zdo.Set(AutoAssignmentHandledKey, true);
                return false;
            }
            if (zdo.GetBool(AutoAssignmentHandledKey, false)) return false;

            long ownerId = zdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L);
            var options = new System.Collections.Generic.List<WorkAreaSnapshot>();
            WorkFlagRegistry.CollectOwnedSnapshots(ownerId, 40f, depot.transform.position, options);

            bool found = false;
            WorkAreaSnapshot best = default;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < options.Count; i++)
            {
                WorkAreaSnapshot option = options[i];
                if (string.IsNullOrEmpty(option.Id)) continue;
                if (option.Distance >= bestDistance) continue;
                found = true;
                best = option;
                bestDistance = option.Distance;
            }

            // No Workzone exists yet: keep the automatic assignment open so a later menu-open/load
            // can still bind the depot after the player has built a WorkFlag.
            if (!found) return false;

            if (!SetAssignedWorkArea(depot, best.Id)) return false;
            workAreaId = best.Id;
            displayName = string.IsNullOrWhiteSpace(best.DisplayName) ? best.Id : best.DisplayName;
            return true;
        }

        internal static bool TrySuggestContainingWorkArea(Container depot, out string workAreaId, out string displayName)
        {
            workAreaId = string.Empty;
            displayName = string.Empty;
            if (depot == null) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(depot);
            if (zdo == null) return false;
            long ownerId = zdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L);
            if (!WorkFlagRegistry.TryFindContainingAnyFlag(ownerId, depot.transform.position,
                    out string id, out ZDO flagZdo, out _, out _)) return false;
            if (string.IsNullOrEmpty(id)) return false;
            workAreaId = id;
            displayName = flagZdo != null ? WorkFlagZdo.ReadDisplayName(flagZdo) : string.Empty;
            if (string.IsNullOrWhiteSpace(displayName)) displayName = WorkFlagRegistry.TryGetDisplayName(id, id);
            return true;
        }

        internal static string GetDisplayName(string workAreaId)
        {
            if (string.IsNullOrEmpty(workAreaId)) return "Nicht zugewiesen";
            string display = WorkFlagRegistry.TryGetDisplayName(workAreaId, string.Empty);
            return string.IsNullOrWhiteSpace(display) ? workAreaId : display;
        }

        internal static bool MatchesWorkArea(Container depot, string workAreaId)
        {
            if (depot == null || string.IsNullOrEmpty(workAreaId)) return false;
            return string.Equals(GetAssignedWorkAreaId(depot), workAreaId, StringComparison.Ordinal);
        }

        internal static bool MatchesWorkArea(ZDO depotZdo, string workAreaId)
        {
            if (depotZdo == null || string.IsNullOrEmpty(workAreaId)) return false;
            return string.Equals(GetAssignedWorkAreaId(depotZdo), workAreaId, StringComparison.Ordinal);
        }

        internal static bool IsCompatibleWithWorkerWorkArea(Container depot, ZDO workerZdo)
        {
            if (depot == null || workerZdo == null) return false;
            string workerWorkAreaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            string depotWorkAreaId = GetAssignedWorkAreaId(depot);
            if (string.IsNullOrEmpty(workerWorkAreaId) || string.IsNullOrEmpty(depotWorkAreaId)) return true;
            return string.Equals(workerWorkAreaId, depotWorkAreaId, StringComparison.Ordinal);
        }

        internal static bool IsCompatibleWithWorkerWorkArea(ZDO depotZdo, ZDO workerZdo)
        {
            if (depotZdo == null || workerZdo == null) return false;
            string workerWorkAreaId = WorkerZdo.ReadWorkAreaId(workerZdo);
            string depotWorkAreaId = GetAssignedWorkAreaId(depotZdo);
            if (string.IsNullOrEmpty(workerWorkAreaId) || string.IsNullOrEmpty(depotWorkAreaId)) return true;
            return string.Equals(workerWorkAreaId, depotWorkAreaId, StringComparison.Ordinal);
        }
    }
}
