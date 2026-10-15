using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Server-authoritative remote editing for Cartography-table outpost management.
    /// Loaded/local flags can still be edited directly; unloaded flags and persistent workers on a
    /// multiplayer client are changed through routed RPCs so the authoritative world ZDO is updated.
    /// </summary>
    internal static class CartographyRemoteRuntime
    {
        private const string RpcEditRequest = "VW_Cartography_EditFlag_v1";
        private const string RpcEditResult = "VW_Cartography_EditFlagResult_v1";
        private const string RpcWorkerEditRequest = "VW_Cartography_EditWorker_v1";
        private const string RpcWorkerEditResult = "VW_Cartography_EditWorkerResult_v1";

        private const int WorkerEditTaskLevel = 1;
        private const int WorkerEditSupplyTarget = 2;

        private static ZRoutedRpc _registeredOn;

        internal static void TickRegistration()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, _registeredOn)) return;

            try
            {
                rpc.Register<ZPackage>(RpcEditRequest, new Action<long, ZPackage>(RPC_EditRequest));
                rpc.Register<ZPackage>(RpcEditResult, new Action<long, ZPackage>(RPC_EditResult));
                rpc.Register<ZPackage>(RpcWorkerEditRequest, new Action<long, ZPackage>(RPC_WorkerEditRequest));
                rpc.Register<ZPackage>(RpcWorkerEditResult, new Action<long, ZPackage>(RPC_WorkerEditResult));
                _registeredOn = rpc;
                Plugin.Log?.LogInfo("Cartography remote-edit RPC registered.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Cartography RPC registration failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        internal static bool CanRequestRemoteEdit()
        {
            try
            {
                if (ZNet.instance == null || ZRoutedRpc.instance == null) return false;
                if (ZNet.instance.IsServer()) return true;
                return ZNet.instance.GetServerPeer() != null;
            }
            catch
            {
                return false;
            }
        }

        internal static bool RequestEdit(string flagId, string requestedName, float requestedRadius, long playerId)
        {
            if (string.IsNullOrEmpty(flagId) || playerId == 0L) return false;
            TickRegistration();

            try
            {
                if (ZNet.instance == null || ZRoutedRpc.instance == null) return false;

                if (ZNet.instance.IsServer())
                {
                    ProcessEdit(0L, playerId, flagId, requestedName, requestedRadius, true);
                    return true;
                }

                ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
                if (serverPeer == null) return false;

                ZPackage pkg = new ZPackage();
                pkg.Write(playerId);
                pkg.Write(flagId ?? string.Empty);
                pkg.Write(requestedName ?? string.Empty);
                pkg.Write(requestedRadius);
                ZRoutedRpc.instance.InvokeRoutedRPC(serverPeer.m_uid, RpcEditRequest, new object[] { pkg });
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Cartography remote edit request failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        internal static bool RequestWorkerTaskLevel(string flagId, ZDO workerZdo, WorkerTaskFlags task, int level, long playerId)
        {
            if (workerZdo == null || string.IsNullOrEmpty(flagId) || playerId == 0L || task == WorkerTaskFlags.None) return false;
            return RequestWorkerEdit(flagId, workerZdo.m_uid.ToString(), WorkerEditTaskLevel, (int)task, Mathf.Clamp(level, 0, 4), playerId);
        }

        internal static bool RequestWorkerSupplyTarget(string flagId, ZDO workerZdo, int target, long playerId)
        {
            if (workerZdo == null || string.IsNullOrEmpty(flagId) || playerId == 0L) return false;
            return RequestWorkerEdit(flagId, workerZdo.m_uid.ToString(), WorkerEditSupplyTarget, Mathf.Clamp(target, 0, 999), 0, playerId);
        }

        private static bool RequestWorkerEdit(string flagId, string workerUid, int operation, int arg1, int arg2, long playerId)
        {
            if (string.IsNullOrEmpty(flagId) || string.IsNullOrEmpty(workerUid) || playerId == 0L) return false;
            TickRegistration();

            try
            {
                if (ZNet.instance == null || ZRoutedRpc.instance == null) return false;
                if (ZNet.instance.IsServer())
                {
                    ProcessWorkerEdit(0L, playerId, flagId, workerUid, operation, arg1, arg2, true);
                    return true;
                }

                ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
                if (serverPeer == null) return false;
                ZPackage pkg = new ZPackage();
                pkg.Write(playerId);
                pkg.Write(flagId ?? string.Empty);
                pkg.Write(workerUid ?? string.Empty);
                pkg.Write(operation);
                pkg.Write(arg1);
                pkg.Write(arg2);
                ZRoutedRpc.instance.InvokeRoutedRPC(serverPeer.m_uid, RpcWorkerEditRequest, new object[] { pkg });
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Cartography worker edit request failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void RPC_EditRequest(long sender, ZPackage pkg)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;

                long playerId = pkg.ReadLong();
                string flagId = pkg.ReadString();
                string requestedName = pkg.ReadString();
                float requestedRadius = pkg.ReadSingle();
                ProcessEdit(sender, playerId, flagId, requestedName, requestedRadius, false);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Cartography edit RPC failed: {ex.GetType().Name}: {ex.Message}");
                SendResult(sender, false, string.Empty, string.Empty, 0f, "Fernbearbeitung fehlgeschlagen: ungueltige RPC-Daten.");
            }
        }

        private static void RPC_WorkerEditRequest(long sender, ZPackage pkg)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;
                long playerId = pkg.ReadLong();
                string flagId = pkg.ReadString();
                string workerUid = pkg.ReadString();
                int operation = pkg.ReadInt();
                int arg1 = pkg.ReadInt();
                int arg2 = pkg.ReadInt();
                ProcessWorkerEdit(sender, playerId, flagId, workerUid, operation, arg1, arg2, false);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Cartography worker edit RPC failed: {ex.GetType().Name}: {ex.Message}");
                SendWorkerResult(sender, false, string.Empty, string.Empty, 0, 0, 0, "Worker-Fernbearbeitung fehlgeschlagen: ungueltige RPC-Daten.");
            }
        }

        private static bool ValidateRemotePlayer(long sender, long playerId, bool localServerCall, out string error)
        {
            error = string.Empty;
            if (playerId == 0L)
            {
                error = "Ungueltige Spieleridentitaet.";
                return false;
            }
            if (localServerCall || sender == 0L) return true;

            try
            {
                ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(sender) : null;
                ZDO playerZdo = peer != null && ZDOMan.instance != null ? ZDOMan.instance.GetZDO(peer.m_characterID) : null;
                long authoritativePlayerId = playerZdo != null ? playerZdo.GetLong(ZDOVars.s_playerID, 0L) : 0L;
                if (authoritativePlayerId != 0L && authoritativePlayerId == playerId) return true;
            }
            catch { }

            error = "Spieleridentitaet konnte serverseitig nicht bestaetigt werden.";
            return false;
        }

        private static void ProcessEdit(long sender, long playerId, string flagId, string requestedName, float requestedRadius, bool localServerCall)
        {
            if (string.IsNullOrEmpty(flagId) || playerId == 0L)
            {
                SendResult(sender, false, flagId, string.Empty, 0f, "Ungueltige Aussenposten-Anfrage.");
                return;
            }

            if (!ValidateRemotePlayer(sender, playerId, localServerCall, out string identityError))
            {
                SendResult(sender, false, flagId, string.Empty, 0f, identityError);
                return;
            }

            if (!WorkFlagRegistry.TryGetPersistentState(flagId, out ZDO flagZdo) || flagZdo == null)
            {
                SendResult(sender, false, flagId, string.Empty, 0f, "Aussenposten wurde im Welt-ZDO nicht gefunden.");
                return;
            }

            long owner = WorkFlagZdo.ReadOwner(flagZdo);
            if (owner != 0L && owner != playerId)
            {
                SendResult(sender, false, flagId, WorkFlagZdo.ReadDisplayName(flagZdo), WorkFlagZdo.ReadRadius(flagZdo, 30f), "Aussenposten gehoert einem anderen Spieler.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(requestedName))
                WorkFlagZdo.SetDisplayName(flagZdo, requestedName);
            WorkFlagZdo.SetRadius(flagZdo, requestedRadius);
            WorkFlagRegistry.RememberPersistent(flagZdo);

            string savedName = WorkFlagZdo.ReadDisplayName(flagZdo);
            float savedRadius = WorkFlagZdo.ReadRadius(flagZdo, 30f);
            Vector3 center = flagZdo.GetPosition();
            int updatedWorkers = WorkFlagRegistry.UpdatePersistentAssignedWorkerAreaMetadata(flagId, savedName, center, savedRadius);

            string message = $"{savedName} gespeichert, Radius {savedRadius:0} m. Persistente Worker aktualisiert: {updatedWorkers}.";
            if (localServerCall)
                Plugin.Instance?.HandleRemoteOutpostEditResult(true, flagId, savedName, savedRadius, message);
            else
                SendResult(sender, true, flagId, savedName, savedRadius, message);
        }

        private static void ProcessWorkerEdit(long sender, long playerId, string flagId, string workerUid, int operation, int arg1, int arg2, bool localServerCall)
        {
            if (string.IsNullOrEmpty(flagId) || string.IsNullOrEmpty(workerUid) || playerId == 0L)
            {
                SendWorkerResult(sender, false, flagId, workerUid, operation, arg1, arg2, "Ungueltige Worker-Anfrage.");
                return;
            }
            if (!ValidateRemotePlayer(sender, playerId, localServerCall, out string identityError))
            {
                SendWorkerResult(sender, false, flagId, workerUid, operation, arg1, arg2, identityError);
                return;
            }
            if (!WorkFlagRegistry.TryGetPersistentState(flagId, out ZDO flagZdo) || flagZdo == null)
            {
                SendWorkerResult(sender, false, flagId, workerUid, operation, arg1, arg2, "Aussenposten wurde im Welt-ZDO nicht gefunden.");
                return;
            }
            long flagOwner = WorkFlagZdo.ReadOwner(flagZdo);
            if (flagOwner != 0L && flagOwner != playerId)
            {
                SendWorkerResult(sender, false, flagId, workerUid, operation, arg1, arg2, "Aussenposten gehoert einem anderen Spieler.");
                return;
            }

            ZDO workerZdo = FindRegisteredWorker(workerUid);
            if (workerZdo == null || !WorkerZdo.IsWorker(workerZdo))
            {
                SendWorkerResult(sender, false, flagId, workerUid, operation, arg1, arg2, "Worker wurde im persistenten Register nicht gefunden.");
                return;
            }
            if (!string.Equals(WorkerZdo.ReadWorkAreaId(workerZdo), flagId, StringComparison.Ordinal))
            {
                SendWorkerResult(sender, false, flagId, workerUid, operation, arg1, arg2, "Worker gehoert nicht mehr zu diesem Aussenposten.");
                return;
            }
            long workerOwner = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            if (workerOwner != 0L && workerOwner != playerId)
            {
                SendWorkerResult(sender, false, flagId, workerUid, operation, arg1, arg2, "Worker gehoert einem anderen Spieler.");
                return;
            }

            string species = SpeciesRoleRegistry.NormalizePrefabName(workerZdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
            WorkerRole role = WorkerJobProfileRuntime.NormalizeRuntimeRole(workerZdo);
            bool success;
            string message;

            if (operation == WorkerEditTaskLevel)
            {
                WorkerTaskFlags task = (WorkerTaskFlags)arg1;
                int level = Mathf.Clamp(arg2, 0, 4);
                success = WorkerJobProfileRuntime.TrySetTaskLevel(workerZdo, role, task, level);
                string taskName = Plugin.GetWorkerTaskPriorityDisplayNameForRemote(task);
                message = success
                    ? $"{species}: {taskName} -> {(level <= 0 ? "Aus" : "Prioritaet " + level)}."
                    : $"{species}: {taskName} konnte nicht geaendert werden.";
            }
            else if (operation == WorkerEditSupplyTarget)
            {
                int target = Mathf.Clamp(arg1, 0, 999);
                ZDO home = WorkerSustenanceRuntime.ResolveHomeZdo(workerZdo);
                if (home == null)
                {
                    success = false;
                    message = $"{species}: Ruhestaette fuer Versorgung nicht aufloesbar.";
                }
                else
                {
                    WorkerSustenanceRuntime.SetTargetStock(workerZdo, target);
                    int saved = WorkerSustenanceRuntime.GetTargetStock(workerZdo);
                    success = saved == target;
                    message = success
                        ? $"{species}: Versorgungs-Ziel -> {(saved <= 0 ? "AUS" : saved.ToString())}."
                        : $"{species}: Versorgungs-Ziel konnte nicht gespeichert werden.";
                    arg1 = saved;
                }
            }
            else
            {
                success = false;
                message = "Unbekannte Worker-Fernbearbeitung.";
            }

            if (localServerCall)
                Plugin.Instance?.HandleRemoteOutpostWorkerEditResult(success, flagId, workerUid, message);
            else
                SendWorkerResult(sender, success, flagId, workerUid, operation, arg1, arg2, message);
        }

        private static ZDO FindRegisteredWorker(string workerUid)
        {
            if (string.IsNullOrEmpty(workerUid)) return null;
            ZDO found = null;
            BackgroundWorkerScheduler.ForEachRegisteredWorker(candidate =>
            {
                if (found != null || candidate == null) return;
                if (string.Equals(candidate.m_uid.ToString(), workerUid, StringComparison.Ordinal)) found = candidate;
            });
            return found;
        }

        private static void SendResult(long targetPeer, bool success, string flagId, string savedName, float savedRadius, string message)
        {
            if (targetPeer == 0L)
            {
                if (!string.IsNullOrEmpty(flagId))
                    Plugin.Instance?.HandleRemoteOutpostEditResult(success, flagId, savedName, savedRadius, message);
                return;
            }

            try
            {
                if (ZRoutedRpc.instance == null) return;
                ZPackage pkg = new ZPackage();
                pkg.Write(success);
                pkg.Write(flagId ?? string.Empty);
                pkg.Write(savedName ?? string.Empty);
                pkg.Write(savedRadius);
                pkg.Write(message ?? string.Empty);
                ZRoutedRpc.instance.InvokeRoutedRPC(targetPeer, RpcEditResult, new object[] { pkg });
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Cartography edit result RPC failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void SendWorkerResult(long targetPeer, bool success, string flagId, string workerUid, int operation, int arg1, int arg2, string message)
        {
            if (targetPeer == 0L)
            {
                Plugin.Instance?.HandleRemoteOutpostWorkerEditResult(success, flagId, workerUid, message);
                return;
            }
            try
            {
                if (ZRoutedRpc.instance == null) return;
                ZPackage pkg = new ZPackage();
                pkg.Write(success);
                pkg.Write(flagId ?? string.Empty);
                pkg.Write(workerUid ?? string.Empty);
                pkg.Write(operation);
                pkg.Write(arg1);
                pkg.Write(arg2);
                pkg.Write(message ?? string.Empty);
                ZRoutedRpc.instance.InvokeRoutedRPC(targetPeer, RpcWorkerEditResult, new object[] { pkg });
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Cartography worker edit result RPC failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RPC_EditResult(long sender, ZPackage pkg)
        {
            try
            {
                if (pkg == null) return;
                if (ZNet.instance != null && !ZNet.instance.IsServer())
                {
                    ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
                    if (serverPeer != null && sender != serverPeer.m_uid) return;
                }

                bool success = pkg.ReadBool();
                string flagId = pkg.ReadString();
                string savedName = pkg.ReadString();
                float savedRadius = pkg.ReadSingle();
                string message = pkg.ReadString();
                Plugin.Instance?.HandleRemoteOutpostEditResult(success, flagId, savedName, savedRadius, message);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Cartography edit-result RPC failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RPC_WorkerEditResult(long sender, ZPackage pkg)
        {
            try
            {
                if (pkg == null) return;
                if (ZNet.instance != null && !ZNet.instance.IsServer())
                {
                    ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
                    if (serverPeer != null && sender != serverPeer.m_uid) return;
                }

                bool success = pkg.ReadBool();
                string flagId = pkg.ReadString();
                string workerUid = pkg.ReadString();
                pkg.ReadInt(); // operation
                pkg.ReadInt(); // arg1
                pkg.ReadInt(); // arg2
                string message = pkg.ReadString();
                Plugin.Instance?.HandleRemoteOutpostWorkerEditResult(success, flagId, workerUid, message);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Cartography worker edit-result RPC failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
