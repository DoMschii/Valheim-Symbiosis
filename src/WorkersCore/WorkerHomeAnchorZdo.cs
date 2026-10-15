namespace Valheim_Workers
{
    internal static class WorkerHomeAnchorZdo
    {
        internal const string AnchorKind = "vw_home_anchor_kind";
        internal const string OwnerPlayerId = "vw_home_anchor_owner";
        internal const string Assigned = "vw_home_anchor_assigned";
        internal const string WorkerSpecies = "vw_home_anchor_worker_species";
        internal const string WorkerRoleKey = "vw_home_anchor_worker_role";
        internal const string AssignedWorkerZdoId = "vw_home_anchor_worker_zdoid";

        internal static bool IsAssigned(ZDO zdo) => zdo != null && zdo.GetBool(Assigned, false);

        internal static void MarkAssigned(ZDO zdo, string anchorKind, long ownerPlayerId, string species, WorkerRole role)
        {
            MarkAssigned(zdo, anchorKind, ownerPlayerId, species, role, ZDOID.None);
        }

        internal static void MarkAssigned(ZDO zdo, string anchorKind, long ownerPlayerId, string species, WorkerRole role, ZDOID workerZdoId)
        {
            if (zdo == null) return;
            zdo.Set(AnchorKind, anchorKind ?? string.Empty);
            zdo.Set(OwnerPlayerId, ownerPlayerId);
            zdo.Set(Assigned, true);
            zdo.Set(WorkerSpecies, species ?? string.Empty);
            zdo.Set(WorkerRoleKey, (int)role);
            zdo.Set(AssignedWorkerZdoId, workerZdoId);
        }

        internal static ZDOID ReadAssignedWorkerZdoId(ZDO zdo)
        {
            return zdo == null ? ZDOID.None : zdo.GetZDOID(AssignedWorkerZdoId);
        }

        internal static void ClearAssigned(ZDO zdo)
        {
            if (zdo == null) return;
            zdo.Set(Assigned, false);
            zdo.Set(WorkerSpecies, string.Empty);
            zdo.Set(WorkerRoleKey, (int)WorkerRole.None);
            zdo.Set(AssignedWorkerZdoId, ZDOID.None);
        }

        internal static string ReadWorkerSpecies(ZDO zdo) => zdo?.GetString(WorkerSpecies, string.Empty) ?? string.Empty;
        internal static WorkerRole ReadWorkerRole(ZDO zdo) => zdo == null ? WorkerRole.None : (WorkerRole)zdo.GetInt(WorkerRoleKey, 0);
        internal static string ReadAnchorKind(ZDO zdo) => zdo?.GetString(AnchorKind, string.Empty) ?? string.Empty;
    }
}
