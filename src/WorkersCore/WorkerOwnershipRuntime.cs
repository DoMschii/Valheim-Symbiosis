using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Keeps worker authority compatible with Valheim's ZDO ownership model.
    ///
    /// A client never grants itself ownership directly. When the recruited worker belongs to the
    /// local player and that player is physically close to the worker, the client sends a request
    /// to the worker's CURRENT ZNetView owner. The current owner validates the requesting player's
    /// persistent PlayerID and proximity, then performs ZDO.SetOwner(sender).
    ///
    /// This mirrors Valheim's request/owner-handoff pattern used by networked interactables and
    /// avoids two peers simulating the same Worker AI.
    /// </summary>
    internal sealed class WorkerOwnershipRuntime
    {
        internal const float TickSeconds = 0.50f;
        internal const float RequestCooldownSeconds = 2.0f;
        private const float WorkerSnapshotRefreshSeconds = 2.50f;
        private const int MaxWorkersPerSlice = 8;
        private const string RpcRequestOwnership = "VW_RequestWorkerOwnership_v1";

        private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MethodInfo ZdoGetOwnerMethod = typeof(ZDO).GetMethod(
            "GetOwner", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly MethodInfo ZdoSetOwnerMethod = typeof(ZDO).GetMethod(
            "SetOwner", InstanceFlags, null, new[] { typeof(long) }, null);
        private static readonly MethodInfo ZdoManGetSessionIdMethod = typeof(ZDOMan).GetMethod(
            "GetSessionID", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        private static readonly MethodInfo ZdoManGetMyIdMethod = typeof(ZDOMan).GetMethod(
            "GetMyID", InstanceFlags, null, Type.EmptyTypes, null);

        private readonly Dictionary<int, ZNetView> _registeredViews = new Dictionary<int, ZNetView>();
        private readonly Dictionary<int, float> _lastRequestAt = new Dictionary<int, float>();
        private readonly List<Character> _workerScratch = new List<Character>();
        private readonly Action<string> _debugLog;
        private float _nextTick;
        private float _nextWorkerSnapshotRefresh;
        private int _workerCursor;
        private float _lastHandoffRadius = 80f;
        private bool _reportedMissingOwnershipApi;
        private bool _reportedRegistrationFailure;

        internal WorkerOwnershipRuntime(Action<string> debugLog)
        {
            _debugLog = debugLog;
        }

        internal bool OwnershipApiAvailable => ZdoGetOwnerMethod != null && ZdoSetOwnerMethod != null;

        internal void Tick(float handoffRadius)
        {
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + TickSeconds;
            _lastHandoffRadius = Mathf.Max(10f, handoffRadius);

            if (!OwnershipApiAvailable)
            {
                if (!_reportedMissingOwnershipApi)
                {
                    _reportedMissingOwnershipApi = true;
                    _debugLog?.Invoke("Worker ownership handoff disabled: current Valheim ZDO.GetOwner()/SetOwner(long) API was not found.");
                }
                return;
            }

            Player localPlayer = Player.m_localPlayer;
            long localPlayerId = localPlayer != null ? localPlayer.GetPlayerID() : 0L;
            Vector3 localPosition = localPlayer != null ? localPlayer.transform.position : Vector3.zero;
            float radiusSqr = _lastHandoffRadius * _lastHandoffRadius;

            if (Time.time >= _nextWorkerSnapshotRefresh || _workerScratch.Count == 0)
            {
                WorkerFastLookupRuntime.CopyKnownWorkers(_workerScratch);
                _nextWorkerSnapshotRefresh = Time.time + WorkerSnapshotRefreshSeconds;
                if (_workerCursor >= _workerScratch.Count) _workerCursor = 0;
            }

            int workerCount = _workerScratch.Count;
            if (workerCount == 0) return;

            int inspected = 0;
            int processed = 0;
            while (inspected < workerCount && processed < MaxWorkersPerSlice)
            {
                if (_workerCursor >= workerCount) _workerCursor = 0;
                Character character = _workerScratch[_workerCursor++];
                inspected++;
                processed++;

                if (!WorkerFastLookupRuntime.TryGetWorker(character, false, out ZNetView nview, out ZDO zdo)) continue;

                EnsureRpcRegistered(character, nview);

                if (localPlayer == null || nview.IsOwner()) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != localPlayerId) continue;
                if ((character.transform.position - localPosition).sqrMagnitude > radiusSqr) continue;

                long currentOwner = ReadOwner(zdo);
                if (currentOwner == 0L)
                {
                    long localPeerId = ReadLocalPeerId();
                    if (localPeerId != 0L && TrySetOwner(zdo, localPeerId))
                    {
                        _debugLog?.Invoke($"Worker was network-ownerless; assigned player claimed authority: {SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name)}");
                        continue;
                    }
                }

                int key = character.GetInstanceID();
                float lastRequest = _lastRequestAt.TryGetValue(key, out float previous) ? previous : -999f;
                if (Time.time - lastRequest < RequestCooldownSeconds) continue;

                _lastRequestAt[key] = Time.time;
                try
                {
                    nview.InvokeRPC(RpcRequestOwnership, new object[] { localPlayerId });
                }
                catch (Exception ex)
                {
                    if (!_reportedRegistrationFailure)
                    {
                        _reportedRegistrationFailure = true;
                        _debugLog?.Invoke($"Worker ownership request failed. {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }

        internal string GetDebugOwnerStatus(Character worker)
        {
            if (worker == null) return "unknown";
            ZNetView nview = worker.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return "invalid";
            if (nview.IsOwner()) return "LOCAL";

            int key = worker.GetInstanceID();
            if (_lastRequestAt.TryGetValue(key, out float requestedAt) && Time.time - requestedAt <= RequestCooldownSeconds + 0.75f)
                return "REMOTE (handoff requested)";

            ZDO zdo = nview.GetZDO();
            long networkOwner = ReadOwner(zdo);
            if (networkOwner == 0L) return "UNOWNED (claim pending)";

            Player localPlayer = Player.m_localPlayer;
            if (zdo != null && localPlayer != null && zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) == localPlayer.GetPlayerID())
            {
                float distance = Vector3.Distance(localPlayer.transform.position, worker.transform.position);
                if (distance <= _lastHandoffRadius)
                    return OwnershipApiAvailable ? "REMOTE (handoff eligible)" : "REMOTE (handoff API unavailable)";
                return $"REMOTE (approach worker; {distance:0} m > {_lastHandoffRadius:0} m)";
            }

            return "REMOTE";
        }

        internal void Shutdown()
        {
            _registeredViews.Clear();
            _lastRequestAt.Clear();
            _workerScratch.Clear();
            _nextWorkerSnapshotRefresh = 0f;
            _workerCursor = 0;
        }

        private void EnsureRpcRegistered(Character worker, ZNetView nview)
        {
            int key = worker.GetInstanceID();
            if (_registeredViews.TryGetValue(key, out ZNetView existing) && existing == nview) return;

            try
            {
                nview.Register<long>(RpcRequestOwnership, new Action<long, long>((sender, claimedPlayerId) =>
                    HandleOwnershipRequest(worker, nview, sender, claimedPlayerId)));
                _registeredViews[key] = nview;
            }
            catch (Exception ex)
            {
                if (!_reportedRegistrationFailure)
                {
                    _reportedRegistrationFailure = true;
                    _debugLog?.Invoke($"Worker ownership RPC registration failed. {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void HandleOwnershipRequest(Character worker, ZNetView nview, long sender, long claimedPlayerId)
        {
            if (worker == null || nview == null || !nview.IsValid() || !nview.IsOwner()) return;

            ZDO zdo = nview.GetZDO();
            if (!WorkerZdo.IsWorker(zdo)) return;

            long assignedPlayerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            if (assignedPlayerId == 0L || claimedPlayerId != assignedPlayerId) return;

            Player requester = FindRequestingPlayer(sender, claimedPlayerId);
            if (requester == null) return;

            // Do not hand a Worker to a peer that is nowhere near it. This also prevents a remote
            // owner from transferring authority based solely on a forged persistent PlayerID.
            float allowedDistance = _lastHandoffRadius + 10f;
            if (Vector3.Distance(requester.transform.position, worker.transform.position) > allowedDistance) return;

            try
            {
                if (TrySetOwner(zdo, sender))
                    _debugLog?.Invoke($"Worker network ownership handed off to assigned player: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)}");
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _debugLog?.Invoke($"Worker ownership handoff failed. {inner.GetType().Name}: {inner.Message}");
            }
        }


        private static long ReadOwner(ZDO zdo)
        {
            if (zdo == null || ZdoGetOwnerMethod == null) return 0L;
            try
            {
                object value = ZdoGetOwnerMethod.Invoke(zdo, null);
                return value is long owner ? owner : 0L;
            }
            catch
            {
                return 0L;
            }
        }

        private static bool TrySetOwner(ZDO zdo, long owner)
        {
            if (zdo == null || owner == 0L || ZdoSetOwnerMethod == null) return false;
            ZdoSetOwnerMethod.Invoke(zdo, new object[] { owner });
            return true;
        }

        private static long ReadLocalPeerId()
        {
            try
            {
                if (ZdoManGetSessionIdMethod != null)
                {
                    object value = ZdoManGetSessionIdMethod.Invoke(null, null);
                    if (value is long sessionId) return sessionId;
                }

                if (ZdoManGetMyIdMethod != null && ZDOMan.instance != null)
                {
                    object value = ZdoManGetMyIdMethod.Invoke(ZDOMan.instance, null);
                    if (value is long myId) return myId;
                }
            }
            catch
            {
            }

            return 0L;
        }

        private static Player FindRequestingPlayer(long sender, long claimedPlayerId)
        {
            List<Player> players = WorkerCharacterIndexRuntime.GetLivePlayers();
            for (int i = 0; i < players.Count; i++)
            {
                Player player = players[i];
                if (player == null || player.IsDead()) continue;
                if (player.GetPlayerID() != claimedPlayerId) continue;

                ZNetView playerView = player.GetComponent<ZNetView>();
                ZDO playerZdo = playerView != null && playerView.IsValid() ? playerView.GetZDO() : null;
                if (playerZdo == null) continue;

                try
                {
                    object owner = ZdoGetOwnerMethod.Invoke(playerZdo, null);
                    if (owner is long peerOwner && peerOwner == sender)
                        return player;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }
    }
}
