using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Groups AirFreight chests by the Workzone circle that contains them.
    /// Shared Min/Max rules are stored on that WorkFlag so every AirFreight chest in the same
    /// Workzone sees one combined automatic-delivery ceiling instead of multiplying the limit
    /// once per chest.
    /// </summary>
    internal static class AirFreightGroupRuntime
    {
        internal const string GroupIdKey = "vw_air_group_id";

        internal static bool IsAirFreight(ZDO zdo)
        {
            if (zdo == null) return false;
            WorkerContainerRole role = (WorkerContainerRole)Mathf.Clamp(zdo.GetInt(WorkerContainerRoleRuntime.RoleKey, 0), 0, 3);
            return role == WorkerContainerRole.FieldDepot && zdo.GetBool(WorkerContainerRoleRuntime.AirTransportKey, false);
        }

        internal static bool TryResolveGroup(Container container, out string groupId, out ZDO flagZdo)
        {
            groupId = string.Empty;
            flagZdo = null;
            if (container == null || !WorkerContainerRoleRuntime.SupportsAirTransport(container)) return false;
            ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
            if (zdo == null) return false;

            // v0.5.342: explicit FieldDepot -> Workzone assignment is authoritative. AirFreight
            // grouping now uses the same logistics identity as ground FieldDepot pooling.
            string assignedWorkAreaId = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(zdo);
            if (!string.IsNullOrEmpty(assignedWorkAreaId))
            {
                groupId = assignedWorkAreaId;
                WorkFlagRegistry.TryGetPersistentState(groupId, out flagZdo);
                if (flagZdo == null && WorkFlagRegistry.TryFindById(groupId, 40f, out WorkAreaSnapshot loadedZone) && loadedZone.Flag != null)
                    flagZdo = loadedZone.Flag.GetNetworkView()?.GetZDO();
                zdo.Set(GroupIdKey, groupId);
                PersistentContainerRegistryRuntime.Remember(zdo);
                return true;
            }

            // Compatibility only: keep a group id that an older build already persisted. New
            // unassigned depots are intentionally NOT auto-bound by geometry; Shift+E merely
            // suggests the containing Workzone and the player confirms it.
            string legacyGroupId = zdo.GetString(GroupIdKey, string.Empty);
            if (!string.IsNullOrEmpty(legacyGroupId))
            {
                groupId = legacyGroupId;
                WorkFlagRegistry.TryGetPersistentState(groupId, out flagZdo);
                return true;
            }
            return false;
        }

        internal static bool TryResolveGroup(ZDO containerZdo, out string groupId, out ZDO flagZdo)
        {
            groupId = string.Empty;
            flagZdo = null;
            if (!IsAirFreight(containerZdo)) return false;

            string assignedWorkAreaId = FieldDepotWorkzoneRuntime.GetAssignedWorkAreaId(containerZdo);
            if (!string.IsNullOrEmpty(assignedWorkAreaId))
            {
                groupId = assignedWorkAreaId;
                WorkFlagRegistry.TryGetPersistentState(groupId, out flagZdo);
                containerZdo.Set(GroupIdKey, groupId);
                PersistentContainerRegistryRuntime.Remember(containerZdo);
                return true;
            }

            groupId = containerZdo.GetString(GroupIdKey, string.Empty);
            if (!string.IsNullOrEmpty(groupId) && WorkFlagRegistry.TryGetPersistentState(groupId, out flagZdo) && flagZdo != null)
                return true;

            // No explicit assignment and no legacy persisted group: remain standalone.
            // The UI can suggest a containing Workzone, but no durable binding is inferred here.
            return !string.IsNullOrEmpty(groupId);
        }

        internal static bool TryGetGroupId(Container container, out string groupId)
        {
            return TryResolveGroup(container, out groupId, out _);
        }

        internal static bool TryGetGroupId(ZDO zdo, out string groupId)
        {
            return TryResolveGroup(zdo, out groupId, out _);
        }

        internal static bool IsMemberOfGroup(ZDO zdo, string groupId)
        {
            if (!IsAirFreight(zdo) || string.IsNullOrEmpty(groupId)) return false;
            string current = zdo.GetString(GroupIdKey, string.Empty);
            if (string.IsNullOrEmpty(current))
            {
                TryResolveGroup(zdo, out current, out _);
            }
            return string.Equals(current, groupId, StringComparison.Ordinal);
        }

        internal static void ForEachGroupMember(ZDO source, Action<ZDO> visitor)
        {
            if (source == null || visitor == null) return;
            if (!TryResolveGroup(source, out string groupId, out _) || string.IsNullOrEmpty(groupId))
            {
                visitor(source);
                return;
            }

            HashSet<ZDOID> seen = new HashSet<ZDOID>();
            Action<ZDO, long, Vector3> visitCached = (zdo, owner, position) =>
            {
                if (zdo == null || !IsMemberOfGroup(zdo, groupId) || !seen.Add(zdo.m_uid)) return;
                visitor(zdo);
            };
            WorkerProvisioningLogisticsRuntime.ForEachCachedBaseAirHub(visitCached);
            WorkerProvisioningLogisticsRuntime.ForEachCachedOutpostAirHub(visitCached);

            // Newly loaded/converted AirFreight chests can exist for a fraction of a second before
            // the topology audit consumes its dirty flag. Supplement from the loaded depot index
            // instead of walking every persistent world container.
            AdvancedFieldDepotRuntime.ForEachLoadedDepot(container =>
            {
                if (container == null || !WorkerContainerRoleRuntime.SupportsAirTransport(container)) return;
                ZDO zdo = WorkerContainerRoleRuntime.GetZdo(container);
                if (zdo == null || !IsMemberOfGroup(zdo, groupId) || !seen.Add(zdo.m_uid)) return;
                visitor(zdo);
            });

            if (seen.Add(source.m_uid)) visitor(source);
        }

        internal static int GetCombinedStock(ZDO source, string prefab)
        {
            if (source == null || string.IsNullOrWhiteSpace(prefab)) return 0;
            if (!TryResolveGroup(source, out string groupId, out _) || string.IsNullOrEmpty(groupId))
            {
                ContainerLedgerRuntime.EnsureSnapshotFromPersistentZdo(source);
                return Mathf.Max(0, ContainerLedgerRuntime.GetSnapshotAmount(source, prefab)) +
                       AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(source, prefab);
            }

            long total = 0L;
            HashSet<ZDOID> seen = new HashSet<ZDOID>();
            Action<ZDO> count = zdo =>
            {
                if (zdo == null || !IsMemberOfGroup(zdo, groupId) || !seen.Add(zdo.m_uid)) return;
                ContainerLedgerRuntime.EnsureSnapshotFromPersistentZdo(zdo);
                total += Mathf.Max(0, ContainerLedgerRuntime.GetSnapshotAmount(zdo, prefab));
                total += Mathf.Max(0, AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(zdo, prefab));
                if (total > int.MaxValue) total = int.MaxValue;
            };

            Action<ZDO, long, Vector3> countCached = (zdo, owner, position) => count(zdo);
            WorkerProvisioningLogisticsRuntime.ForEachCachedBaseAirHub(countCached);
            WorkerProvisioningLogisticsRuntime.ForEachCachedOutpostAirHub(countCached);
            AdvancedFieldDepotRuntime.ForEachLoadedDepot(container =>
            {
                if (container == null || !WorkerContainerRoleRuntime.SupportsAirTransport(container)) return;
                count(WorkerContainerRoleRuntime.GetZdo(container));
            });
            count(source);

            return total >= int.MaxValue ? int.MaxValue : (int)Math.Max(0L, total);
        }

        internal static string DescribeGroup(Container container)
        {
            if (!TryResolveGroup(container, out string groupId, out ZDO flagZdo) || string.IsNullOrEmpty(groupId))
                return "diese Luftfrachtkiste";
            string display = WorkFlagZdo.ReadDisplayName(flagZdo);
            return string.IsNullOrWhiteSpace(display) ? "diese Workzone" : display;
        }
    }
}
