namespace Valheim_Workers
{
    // Compatibility wrapper for the existing Greydwarf home implementation. The persistent keys
    // remain unchanged; v0.1.21 merely centralizes them so every species-specific home can share
    // one stable ZDO schema.
    internal static class GreydwarfHomeAnchorZdo
    {
        internal const string AnchorKind = WorkerHomeAnchorZdo.AnchorKind;
        internal const string OwnerPlayerId = WorkerHomeAnchorZdo.OwnerPlayerId;
        internal const string Assigned = WorkerHomeAnchorZdo.Assigned;
        internal const string WorkerSpecies = WorkerHomeAnchorZdo.WorkerSpecies;
        internal const string WorkerRoleKey = WorkerHomeAnchorZdo.WorkerRoleKey;

        internal const string GreydwarfNestKind = "GreydwarfNest";

        internal static bool IsAssigned(ZDO zdo) => WorkerHomeAnchorZdo.IsAssigned(zdo);

        internal static void MarkAssigned(ZDO zdo, long ownerPlayerId, string species, WorkerRole role) =>
            WorkerHomeAnchorZdo.MarkAssigned(zdo, GreydwarfNestKind, ownerPlayerId, species, role);

        internal static string ReadWorkerSpecies(ZDO zdo) => WorkerHomeAnchorZdo.ReadWorkerSpecies(zdo);
        internal static WorkerRole ReadWorkerRole(ZDO zdo) => WorkerHomeAnchorZdo.ReadWorkerRole(zdo);
    }
}
