using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class WorkerAiController
    {
        private enum GathererPhase
        {
            Harvest = 0,
            Collect = 1
        }

        internal const float TickSeconds = 2f;
        internal const float SchedulerSliceSeconds = 0.20f;
        internal const int MaxDecisionsPerSlice = 1;
        internal const float WorkerSnapshotRefreshSeconds = 5f;
        internal const float LoadedWorkerHeartbeatSeconds = 1.0f;
        internal const int LoadedWorkerHeartbeatBudgetPerSlice = 6;
        private const string SmithLastModuleKey = "vw_smith_last_module";

        private readonly Dictionary<int, WoodTarget> _woodTargets = new Dictionary<int, WoodTarget>();
        private readonly Dictionary<int, StoneTarget> _stoneTargets = new Dictionary<int, StoneTarget>();
        private readonly Dictionary<int, ItemDrop> _gatherTargets = new Dictionary<int, ItemDrop>();
        private readonly Dictionary<int, GathererPhase> _gatherPhases = new Dictionary<int, GathererPhase>();
        private readonly Dictionary<int, ItemDrop> _forestrySeedTargets = new Dictionary<int, ItemDrop>();
        private readonly Dictionary<int, float> _nextGatherSearchAt = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _nextHarvestSearchAt = new Dictionary<int, float>();
        // v0.5.336: Gardener job selection already performs a harvest lookup. Keep that exact
        // result for the immediate execution step instead of scanning the Pickable index twice in
        // the same scheduler decision.
        private readonly Dictionary<int, GardenHarvestTargetResult> _harvestSearchCache = new Dictionary<int, GardenHarvestTargetResult>();
        private readonly Dictionary<int, float> _harvestSearchCacheUntil = new Dictionary<int, float>();
        // v0.5.354: a Garden worker that already proved its planned slots are waiting for depot
        // stock does not need to re-evaluate up to 256 slots every 2-second scheduler decision.
        private readonly Dictionary<int, float> _nextGardenSupplyRetryAt = new Dictionary<int, float>();
        private const float GardenNeedsResourceRetrySeconds = 6f;
        private readonly Dictionary<int, float> _gatherBlockedSince = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _gatherDropBlockedUntil = new Dictionary<int, float>();
        private readonly Dictionary<int, int> _gatherTrackedDropKey = new Dictionary<int, int>();
        private readonly Dictionary<int, float> _gatherBestDistance = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _gatherLastDistanceProgressAt = new Dictionary<int, float>();
        private readonly Dictionary<int, Vector3> _gatherLastWorkerPosition = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, float> _gatherLastWorkerMovementAt = new Dictionary<int, float>();
        // Lumberjack unreachable-target recovery is deliberately local to this controller and per worker.
        // It never changes the stable navigation implementation and never globally blacklists a tree.
        private readonly Dictionary<int, int> _woodTrackedTargetKey = new Dictionary<int, int>();
        private readonly Dictionary<int, float> _woodBestDistance = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _woodLastProgressAt = new Dictionary<int, float>();
        private readonly Dictionary<int, Vector3> _woodPositionAtLastProgress = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, int> _woodBlockedTargetKey = new Dictionary<int, int>();
        private readonly Dictionary<int, float> _woodBlockedTargetUntil = new Dictionary<int, float>();
        // Stonemason unreachable-target recovery mirrors the proven Lumberjack pattern, but stays
        // fully local/per-worker and keys MineRock5 by the exact quarry patch collider. It never
        // changes the stable navigation implementation or globally blacklists a rock.
        private readonly Dictionary<int, int> _stoneTrackedTargetKey = new Dictionary<int, int>();
        private readonly Dictionary<int, float> _stoneBestDistance = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _stoneLastProgressAt = new Dictionary<int, float>();
        private readonly Dictionary<int, Vector3> _stonePositionAtLastProgress = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, int> _stoneBlockedTargetKey = new Dictionary<int, int>();
        private readonly Dictionary<int, float> _stoneBlockedTargetUntil = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _nextWorkerDecisionAt = new Dictionary<int, float>();
        private readonly HashSet<int> _seenWorkerKeys = new HashSet<int>();
        private readonly List<int> _staleWorkerKeys = new List<int>();
        private readonly List<Character> _workerSnapshot = new List<Character>();
        private readonly Action<string> _debugLog;
        private float _nextSchedulerSlice;
        private float _nextScheduleCleanup;
        private float _nextWorkerSnapshotRefresh;
        private float _nextFullWorkerDiscovery;
        private float _nextLoadedWorkerHeartbeat;
        private int _loadedWorkerHeartbeatCursor;

        internal Character LastObservedWorker { get; private set; }

        internal WorkerAiController(Action<string> debugLog)
        {
            _debugLog = debugLog;
        }

        internal double LastInternalTickMs { get; private set; }

        internal void Tick()
        {
            long wholeTickStart = WorkersPerformanceProfiler.Begin();
            try
            {
                TickCore();
            }
            finally
            {
                LastInternalTickMs = WorkersPerformanceProfiler.ElapsedMilliseconds(wholeTickStart);
                if (LastInternalTickMs >= 20.0)
                    BackgroundMonitorRuntime.Note($"PERF WORKER TOTAL: {LastInternalTickMs:0.0}ms snapshot={_workerSnapshot.Count}");
            }
        }

        private void TickCore()
        {
            if (Time.time < _nextSchedulerSlice) return;
            _nextSchedulerSlice = Time.time + SchedulerSliceSeconds;

            Player localPlayer = Player.m_localPlayer;
            if (localPlayer == null) return;

            long snapshotPerfStart = WorkersPerformanceProfiler.Begin();
            RefreshWorkerSnapshot();
            double snapshotPerfMs = WorkersPerformanceProfiler.ElapsedMilliseconds(snapshotPerfStart);
            if (snapshotPerfMs >= 20.0)
                BackgroundMonitorRuntime.Note($"PERF WORKER SNAPSHOT: {snapshotPerfMs:0.0}ms cached={_workerSnapshot.Count}");

            float now = Time.time;
            Vector3 playerPosition = localPlayer.transform.position;

            // v0.5.31 performance pass: the previous loop repeated component/ZDO lookup, collision
            // registration, interactable checks and background heartbeats for every loaded Worker
            // five times per second even though a Worker decision only happens every two seconds.
            // Keep those lifecycle duties on a one-second heartbeat; this remains safely below the
            // four-second background-unload grace and does not change any job/navigation rules.
            if (now >= _nextLoadedWorkerHeartbeat)
            {
                long heartbeatPhaseStart = WorkersPerformanceProfiler.Begin();
                // v0.5.277 anti-hitch: do not process every loaded worker in one frame.
                // With 50+ workers this heartbeat burst was outside the per-worker TickWorker profiler
                // and could therefore stall Worker AI for seconds without producing PERF WORKER lines.
                // Process a small round-robin budget every scheduler slice instead.
                _nextLoadedWorkerHeartbeat = now + LoadedWorkerHeartbeatSeconds;
                int snapshotCount = _workerSnapshot.Count;
                int processed = 0;
                int inspected = 0;
                while (snapshotCount > 0 && processed < LoadedWorkerHeartbeatBudgetPerSlice && inspected < snapshotCount)
                {
                    if (_loadedWorkerHeartbeatCursor >= snapshotCount) _loadedWorkerHeartbeatCursor = 0;
                    Character character = _workerSnapshot[_loadedWorkerHeartbeatCursor++];
                    inspected++;
                    if (character == null || character is Player || character.IsDead()) continue;

                    // v0.5.283: the heartbeat budget must count attempted workers, not only
                    // successful TryGetWorker lookups. Previously a streamed/unloading worker
                    // could fail the lookup and hit continue before processed++, allowing one
                    // scheduler slice to walk the entire snapshot despite the intended budget of 6.
                    processed++;
                    long heartbeatWorkerStart = WorkersPerformanceProfiler.Begin();
                    if (!WorkerFastLookupRuntime.TryGetWorker(character, false, out ZNetView nview, out ZDO zdo))
                    {
                        double failedLookupMs = WorkersPerformanceProfiler.ElapsedMilliseconds(heartbeatWorkerStart);
                        if (failedLookupMs >= 20.0)
                        {
                            string failedName = character.gameObject != null ? character.gameObject.name : "<null>";
                            BackgroundMonitorRuntime.Note($"PERF WORKER HEARTBEAT: {failedLookupMs:0.0}ms stage=lookup-failed name={failedName} key={character.GetInstanceID()}");
                        }
                        continue;
                    }

                    int workerKey = character.GetInstanceID();
                    WorkerFastLookupRuntime.RegisterKnownWorker(character, zdo);

                    // v0.5.358: once a non-Drake Worker is already under background authority and
                    // clearly outside the local physical bubble, do not call ZNetScene.IsAreaReady
                    // from the heartbeat at all. The persistent scheduler owns the ZDO and keeps
                    // transport moving; this loaded Character is only a streamed presentation shell.
                    if (BackgroundWorkerScheduler.CanSkipStableBackgroundPhysicalCheck(character, zdo, playerPosition))
                        continue;

                    // v0.5.357: lifecycle heartbeat is intentionally tiny. Registration owns the
                    // physical/background handoff; presentation-only setup is skipped completely for
                    // streamed characters outside the player's physical bubble. Previously every
                    // heartbeat re-ran interaction/collision/loadout work for background characters.
                    bool physicalNow = BackgroundWorkerScheduler.IsLoadedCharacterPhysical(character);
                    BackgroundWorkerScheduler.RegisterLoadedWorker(character, zdo, physicalNow);
                    if (physicalNow)
                    {
                        TrollLumberjackLoadoutRuntime.EnsureLoadedWorker(character, zdo);
                        WorkerCommandRuntime.EnsureInteractable(character);
                        WorkerCollisionRuntime.Register(character);
                    }

                    if (physicalNow &&
                        !WorkerExternalControlBridge.IsControlled(character) &&
                        !BoatPassengerRuntime.IsTransported(character))
                    {
                        LoxObstacleRecoveryRuntime.Tick(character, zdo);
                        if (AirHaulerWorkRuntime.IsAirHauler(character))
                            WorkerNavigationRuntime.TrackAirHauler(character);
                    }

                    double heartbeatWorkerMs = WorkersPerformanceProfiler.ElapsedMilliseconds(heartbeatWorkerStart);
                    if (heartbeatWorkerMs >= 20.0)
                    {
                        string perfName = character.gameObject != null ? character.gameObject.name : "<null>";
                        BackgroundMonitorRuntime.Note($"PERF WORKER HEARTBEAT: {heartbeatWorkerMs:0.0}ms name={perfName} key={workerKey}");
                    }
                }

                // Cleanup is deliberately infrequent. Build the live key set only when cleanup is due,
                // so the round-robin heartbeat itself stays allocation-free and cheap.
                if (now >= _nextScheduleCleanup)
                {
                    long cleanupPhaseStart = WorkersPerformanceProfiler.Begin();
                    _nextScheduleCleanup = now + 60f;
                    _seenWorkerKeys.Clear();
                    for (int i = 0; i < _workerSnapshot.Count; i++)
                    {
                        Character live = _workerSnapshot[i];
                        if (live != null && !(live is Player) && !live.IsDead()) _seenWorkerKeys.Add(live.GetInstanceID());
                    }
                    _staleWorkerKeys.Clear();
                    foreach (KeyValuePair<int, float> pair in _nextWorkerDecisionAt)
                        if (!_seenWorkerKeys.Contains(pair.Key)) _staleWorkerKeys.Add(pair.Key);
                    for (int i = 0; i < _staleWorkerKeys.Count; i++)
                    {
                        int staleKey = _staleWorkerKeys[i];
                        _nextWorkerDecisionAt.Remove(staleKey);
                        _nextGatherSearchAt.Remove(staleKey);
                        _nextHarvestSearchAt.Remove(staleKey);
                        _harvestSearchCache.Remove(staleKey);
                        _harvestSearchCacheUntil.Remove(staleKey);
                        _gatherPhases.Remove(staleKey);
                        _gatherBlockedSince.Remove(staleKey);
                        ResetGatherTargetProgress(staleKey);
                        ResetWoodTargetProgress(staleKey, clearBlockedTarget: true);
                        ResetStoneTargetProgress(staleKey, clearBlockedTarget: true);
                    }
                    _staleWorkerKeys.Clear();
                    double cleanupPhaseMs = WorkersPerformanceProfiler.ElapsedMilliseconds(cleanupPhaseStart);
                    if (cleanupPhaseMs >= 20.0)
                        BackgroundMonitorRuntime.Note($"PERF WORKER PHASE: {cleanupPhaseMs:0.0}ms stage=scheduler-cleanup snapshot={_workerSnapshot.Count}");
                }

                double heartbeatPhaseMs = WorkersPerformanceProfiler.ElapsedMilliseconds(heartbeatPhaseStart);
                if (heartbeatPhaseMs >= 20.0)
                    BackgroundMonitorRuntime.Note($"PERF WORKER PHASE: {heartbeatPhaseMs:0.0}ms stage=heartbeat-batch snapshot={_workerSnapshot.Count} processed={processed} inspected={inspected}");
            }

            Character nearestWorker = null;
            float nearestWorkerSqr = float.MaxValue;
            int decisionsThisSlice = 0;
            long schedulerLoopStart = WorkersPerformanceProfiler.Begin();

            for (int snapshotIndex = 0; snapshotIndex < _workerSnapshot.Count; snapshotIndex++)
            {
                long lightStageStart = WorkersPerformanceProfiler.Begin();
                Character character = _workerSnapshot[snapshotIndex];
                if (character == null || character is Player || character.IsDead())
                {
                    double invalidCheckMs = WorkersPerformanceProfiler.ElapsedMilliseconds(lightStageStart);
                    if (invalidCheckMs >= 20.0)
                        BackgroundMonitorRuntime.Note($"PERF WORKER PREP: {invalidCheckMs:0.0}ms stage=light-invalid index={snapshotIndex}");
                    continue;
                }

                float workerSqr = (character.transform.position - playerPosition).sqrMagnitude;
                if (workerSqr < nearestWorkerSqr)
                {
                    nearestWorkerSqr = workerSqr;
                    nearestWorker = character;
                }

                int workerKey = character.GetInstanceID();
                if (!_nextWorkerDecisionAt.TryGetValue(workerKey, out float nextDecision))
                {
                    float phase = ((workerKey & 0x7fffffff) % 10) * (TickSeconds / 10f);
                    nextDecision = now + phase;
                    _nextWorkerDecisionAt[workerKey] = nextDecision;
                }

                double lightStageMs = WorkersPerformanceProfiler.ElapsedMilliseconds(lightStageStart);
                if (lightStageMs >= 20.0)
                    BackgroundMonitorRuntime.Note($"PERF WORKER PREP: {lightStageMs:0.0}ms stage=light-scheduler name={character.gameObject.name} key={workerKey} index={snapshotIndex}");

                // Do not resolve network components for Workers that are not due yet. This is the
                // common path and removes the former five-times-per-second ZNetView/ZDO churn.
                if (decisionsThisSlice >= MaxDecisionsPerSlice || now < nextDecision) continue;
                long prepStageStart = WorkersPerformanceProfiler.Begin();
                if (!WorkerFastLookupRuntime.TryGetWorker(character, true, out ZNetView nview, out ZDO zdo))
                {
                    double lookupMs = WorkersPerformanceProfiler.ElapsedMilliseconds(prepStageStart);
                    if (lookupMs >= 20.0)
                        BackgroundMonitorRuntime.Note($"PERF WORKER PREP: {lookupMs:0.0}ms stage=lookup name={character.gameObject.name} key={workerKey}");
                    _nextWorkerDecisionAt[workerKey] = now + 1f;
                    continue;
                }
                double prepLookupMs = WorkersPerformanceProfiler.ElapsedMilliseconds(prepStageStart);
                if (prepLookupMs >= 20.0)
                    BackgroundMonitorRuntime.Note($"PERF WORKER PREP: {prepLookupMs:0.0}ms stage=lookup name={character.gameObject.name} key={workerKey}");

                if (BackgroundWorkerScheduler.CanSkipStableBackgroundPhysicalCheck(character, zdo, playerPosition))
                {
                    _nextWorkerDecisionAt[workerKey] = now + 1f;
                    continue;
                }

                long physicalStageStart = WorkersPerformanceProfiler.Begin();
                bool physical = BackgroundWorkerScheduler.IsLoadedCharacterPhysical(character);
                double physicalStageMs = WorkersPerformanceProfiler.ElapsedMilliseconds(physicalStageStart);
                if (physicalStageMs >= 20.0)
                    BackgroundMonitorRuntime.Note($"PERF WORKER PREP: {physicalStageMs:0.0}ms stage=physical-check name={character.gameObject.name} key={workerKey}");
                if (!physical)
                {
                    _nextWorkerDecisionAt[workerKey] = now + 1f;
                    continue;
                }

                long controlStageStart = WorkersPerformanceProfiler.Begin();
                bool transported = BoatPassengerRuntime.IsTransported(character);
                bool controlled = WorkerExternalControlBridge.IsControlled(character);
                double controlStageMs = WorkersPerformanceProfiler.ElapsedMilliseconds(controlStageStart);
                if (controlStageMs >= 20.0)
                    BackgroundMonitorRuntime.Note($"PERF WORKER PREP: {controlStageMs:0.0}ms stage=control-check name={character.gameObject.name} key={workerKey} transported={transported} controlled={controlled}");
                if (transported || controlled)
                {
                    _nextWorkerDecisionAt[workerKey] = now + 0.5f;
                    continue;
                }

                long watchdogPerfStart = WorkersPerformanceProfiler.Begin();
                if (WorkerRespawnRuntime.TickReturnHomeWatchdog(character, zdo))
                {
                    double watchdogMs = WorkersPerformanceProfiler.ElapsedMilliseconds(watchdogPerfStart);
                    if (watchdogMs >= 20.0)
                        BackgroundMonitorRuntime.Note($"PERF WORKER PREP: {watchdogMs:0.0}ms stage=home-watchdog name={character.gameObject.name} key={workerKey}");
                    _nextWorkerDecisionAt[workerKey] = now + TickSeconds;
                    decisionsThisSlice++;
                    continue;
                }
                double watchdogDoneMs = WorkersPerformanceProfiler.ElapsedMilliseconds(watchdogPerfStart);
                if (watchdogDoneMs >= 20.0)
                    BackgroundMonitorRuntime.Note($"PERF WORKER PREP: {watchdogDoneMs:0.0}ms stage=home-watchdog name={character.gameObject.name} key={workerKey}");

                long respawnPerfStart = WorkersPerformanceProfiler.Begin();
                WorkerRespawnRuntime.Tick(character, zdo);
                double respawnMs = WorkersPerformanceProfiler.ElapsedMilliseconds(respawnPerfStart);
                if (respawnMs >= 20.0)
                    BackgroundMonitorRuntime.Note($"PERF WORKER PREP: {respawnMs:0.0}ms stage=respawn name={character.gameObject.name} key={workerKey}");

                long rolePerfStart = WorkersPerformanceProfiler.Begin();
                WorkerRole role = EnsureCurrentRoleSchema(character, zdo);
                double roleMs = WorkersPerformanceProfiler.ElapsedMilliseconds(rolePerfStart);
                if (roleMs >= 20.0)
                    BackgroundMonitorRuntime.Note($"PERF WORKER PREP: {roleMs:0.0}ms stage=role-schema name={character.gameObject.name} role={role} key={workerKey}");

                long workerPerfStart = WorkersPerformanceProfiler.Begin();
                TickWorker(character, zdo, role);
                double workerPerfMs = WorkersPerformanceProfiler.ElapsedMilliseconds(workerPerfStart);
                if (workerPerfMs >= 20.0)
                {
                    WorkerState perfState = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
                    WorkOrder perfOrder = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
                    string perfName = character != null && character.gameObject != null ? character.gameObject.name : "<null>";
                    BackgroundMonitorRuntime.Note($"PERF WORKER: {workerPerfMs:0.0}ms name={perfName} role={role} order={perfOrder} state={perfState} key={workerKey}");
                }
                _nextWorkerDecisionAt[workerKey] = now + TickSeconds;
                decisionsThisSlice++;
            }

            double schedulerLoopMs = WorkersPerformanceProfiler.ElapsedMilliseconds(schedulerLoopStart);
            if (schedulerLoopMs >= 20.0)
                BackgroundMonitorRuntime.Note($"PERF WORKER PHASE: {schedulerLoopMs:0.0}ms stage=scheduler-loop snapshot={_workerSnapshot.Count} decisions={decisionsThisSlice}");

            LastObservedWorker = nearestWorker;
        }

        private void RefreshWorkerSnapshot()
        {
            if (Time.time < _nextWorkerSnapshotRefresh) return;
            _nextWorkerSnapshotRefresh = Time.time + WorkerSnapshotRefreshSeconds;

            // Hot path: use the lifecycle/MonsterAI cache. Re-scanning every Character in the
            // loaded world every five seconds becomes very expensive in farms with many tameables.
            WorkerFastLookupRuntime.CopyKnownWorkers(_workerSnapshot);

            // v0.5.278 anti-hitch: once the lifecycle cache contains any Worker, never perform a
            // periodic Character.GetAllCharacters() reconciliation from the gameplay hot path.
            // The lifecycle/MonsterAI hooks keep the cache current. A full-world fallback is only
            // allowed when the cache is empty (startup/recovery), and even then at most once/minute.
            // This removes a stop-the-world safety scan that could land inside Worker AI timing.
            if (_workerSnapshot.Count > 0) return;
            if (Time.time < _nextFullWorkerDiscovery) return;
            _nextFullWorkerDiscovery = Time.time + 60f;

            List<Character> all = WorkerCharacterIndexRuntime.GetLiveNonPlayers();
            for (int i = 0; i < all.Count; i++)
            {
                Character character = all[i];
                if (character == null || character.IsDead()) continue;
                if (!WorkerFastLookupRuntime.TryGetWorker(character, false, out _, out ZDO zdo)) continue;
                WorkerFastLookupRuntime.RegisterKnownWorker(character, zdo);
            }

            WorkerFastLookupRuntime.CopyKnownWorkers(_workerSnapshot);
        }

        private WorkerRole EnsureCurrentRoleSchema(Character worker, ZDO zdo)
        {
            WorkerRole current = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            int schema = zdo.GetInt(WorkerZdo.Schema, 1);
            if (schema >= WorkerZdo.CurrentSchema) return current;

            // Schema 8 introduces the persistent combat stance. Existing Workers migrate to
            // the configured default stance so old Workers follow the current server/player default.
            if (schema < 8)
                zdo.Set(WorkerZdo.Stance, (int)WorkerCombatStanceRuntime.DefaultStance);

            string prefabName = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            bool currentRoleAllowed = SpeciesRoleRegistry.CanUseRole(prefabName, current);
            if (SpeciesRoleRegistry.TryGetRole(prefabName, out WorkerRole mappedRole))
            {
                // v0.5.110 / schema 21: manual role switching is retired. Every species returns
                // to one stable internal compatibility role; all of its specialties are exposed
                // as per-worker tasks/priorities on that role. Existing alternate-role workers
                // (Fuling Shaman Baker, ranged Hunter->Guard) migrate without destroying cargo or
                // their current WorkOrder, so an in-progress cycle can resume normally.
                bool collapseLegacySpecialization = schema < 21 && current != mappedRole;
                bool invalidRoleRepair = !currentRoleAllowed;
                if (collapseLegacySpecialization || invalidRoleRepair)
                {
                    int workerKey = worker.GetInstanceID();
                    if (invalidRoleRepair)
                    {
                        ClearLocalTargets(workerKey);
                        WorkerZdo.ClearTarget(zdo);
                        if (WorkerZdo.HasCargo(zdo))
                            WorkerZdo.MarkCargoDropPending(zdo, $"role repair: {current} -> {mappedRole}");
                        zdo.Set(WorkerZdo.State, (int)WorkerState.Idle);
                    }

                    zdo.Set(WorkerZdo.Role, (int)mappedRole);

                    ZDOID homeId = WorkerZdo.ReadHomeAnchorZdoId(zdo);
                    if (homeId != ZDOID.None && ZDOMan.instance != null)
                    {
                        ZDO homeZdo = ZDOMan.instance.GetZDO(homeId);
                        if (homeZdo != null && WorkerHomeAnchorZdo.IsAssigned(homeZdo))
                            homeZdo.Set(WorkerHomeAnchorZdo.WorkerRoleKey, (int)mappedRole);
                    }

                    _debugLog?.Invoke($"Worker role schema collapse: {prefabName} {current} -> {mappedRole} (schema {schema}->{WorkerZdo.CurrentSchema})");
                    current = mappedRole;
                }
            }

            zdo.Set(WorkerZdo.Schema, WorkerZdo.CurrentSchema);
            return current;
        }

        private void TickWorker(Character worker, ZDO zdo, WorkerRole role)
        {
            // v0.5.65: Worker cargo is a strict two-slot inventory. Any legacy overflow is
            // materialized next to the worker, and assignment/task changes never carry stale
            // items into the next job. This runs before Follow so cargo safety is unconditional.
            WorkerCargoRuntime.EnsureTwoSlotInventory(worker, zdo);

            // v0.5.96: door ownership must survive every normal AI early-out. A Butcher/Smith can
            // cross a gate and immediately enter Follow, Combat, Rest or outside-work-area state;
            // the old role-local door tick then never ran again and left animal pens open.
            if (role == WorkerRole.Butcher || role == WorkerRole.Smith)
                WorkerDoorRuntime.MaintainOpenedDoor(worker);

            // Follow is a direct player command and deliberately overrides work-area containment
            // and role jobs until the player cancels it or assigns a new home.
            WorkerState commandedState = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (commandedState == WorkerState.Follow)
            {
                WorkerZdo.ClearTarget(zdo);
                return;
            }

            int workerKey = worker.GetInstanceID();

            // Creature combat is owned by vanilla MonsterAI. If the current stance permits the
            // native target, release Workers job navigation immediately and let vanilla fight.
            MonsterAI monster = worker.GetComponent<MonsterAI>();
            if (monster != null && WorkerCombatStanceRuntime.ShouldYieldToCombat(monster, worker, zdo))
            {
                ClearLocalTargets(workerKey);
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.Combat, worker, $"combat stance {WorkerCombatStanceRuntime.DisplayName(WorkerCombatStanceRuntime.ReadStance(zdo))}");
                return;
            }

            // v0.5.211: thematic provisioning is a shared Worker requirement. A hungry Worker
            // returns to its assigned rest station and waits there instead of dying or silently
            // continuing production. Emergency HP recovery remains protected inside the runtime.
            if (WorkerSustenanceRuntime.TryEvaluatePhysical(worker, zdo, out WorkerState supplyState, out string supplyReason))
            {
                ClearLocalTargets(workerKey);
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, supplyState, worker, supplyReason);
                return;
            }

            // Rest/recovery is shared by every role and has priority over normal jobs. It only
            // becomes active after a real species HomeAnchor has been assigned. Follow remains
            // the explicit player override above.
            if (WorkerRestRuntime.TryEvaluate(worker, zdo, out WorkerState restState, out string restReason))
            {
                ClearLocalTargets(workerKey);
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, restState, worker, restReason);

                if (restState == WorkerState.Resting || restState == WorkerState.Recover)
                {
                    if (WorkerRestRuntime.TryHealAtHome(worker, zdo, out float healed))
                        _debugLog?.Invoke($"Worker rest heal: {worker.gameObject.name} +{healed:0.0} HP");
                }
                return;
            }

            // v0.5.355 performance: movement/interaction states are already executed by
            // WorkerNavigationRuntime every native AI tick. Re-running the complete work-zone,
            // modular-priority and job-discovery pipeline every two seconds while a Worker is
            // simply walking to an already-persisted target caused visible 20-50 ms frame hitches
            // (and multi-second stalls in the Baker path). Keep only the cheap hunger/rest/combat
            // safety checks above, then let navigation finish the concrete action.
            //
            // This fast path is deliberately limited to static logistics/production routes. Moving
            // prey/animal targets still go through the normal decision path so their positions can
            // be refreshed. Navigator v5/path planning itself is untouched.
            WorkOrder fastOrder = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
            if ((commandedState == WorkerState.MoveToTarget || commandedState == WorkerState.Collect || commandedState == WorkerState.Work) &&
                WorkerZdo.TryReadTarget(zdo, out _, out string fastTargetKind, out _) &&
                IsStaticActionFastPath(role, fastOrder, fastTargetKind))
            {
                return;
            }

            if (commandedState == WorkerState.Deliver && role == WorkerRole.Hauler &&
                WorkerZdo.TryReadTarget(zdo, out _, out _, out _))
            {
                return;
            }

            // Garden NeedsResource had its six-second retry guard inside TickGardenSupply, but the
            // expensive common WorkArea + modular Gardener resolver still ran every two seconds
            // before reaching that guard. Honor the same retry window here, before those scans.
            if (role == WorkerRole.Gardener && fastOrder == WorkOrder.Garden &&
                commandedState == WorkerState.NeedsResource && !WorkerZdo.HasCargo(zdo) &&
                _nextGardenSupplyRetryAt.TryGetValue(workerKey, out float gardenRetryAt) &&
                Time.time < gardenRetryAt)
            {
                return;
            }

            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            float homeRadius = Mathf.Max(5f, zdo.GetFloat(WorkerZdo.WorkRadius, 40f));
            Vector3 workCenter = home;
            float workRadius = homeRadius;
            WorkOrder activeWorkOrder = WorkOrder.None;
            bool hasWorkArea = false;

            // v0.1.37 makes WorkArea assignment persistent on the Worker ZDO. Once a Worker has
            // been dispatched to a flag, it keeps the flag's last known center/radius/order even
            // when that flag later leaves the loaded scene. If the assigned flag is loaded again,
            // its live order/radius refreshes the persisted Worker assignment.
            string assignedFlagId = WorkerZdo.ReadWorkAreaId(zdo);
            bool hasPersistedAssignment = WorkerZdo.TryReadWorkArea(
                zdo, out WorkOrder persistedOrder, out Vector3 persistedCenter, out float persistedRadius);

            if (hasPersistedAssignment && WorkerJobProfileRuntime.SupportsOrder(zdo, role, persistedOrder))
            {
                activeWorkOrder = persistedOrder;
                workCenter = persistedCenter;
                workRadius = persistedRadius;
                hasWorkArea = true;

                if (!string.IsNullOrEmpty(assignedFlagId) &&
                    WorkFlagRegistry.TryFindById(assignedFlagId, persistedRadius, out WorkAreaSnapshot assignedLoadedFlag))
                {
                    // Preserve the exact job the player selected in the Worker menu. A flag may expose
                    // multiple compatible jobs for the same role (for example Transportieren + Sortieren).
                    // Older logic re-resolved "the first compatible job" every AI tick and silently
                    // overwrote the player's click, making Workzone/job switching appear broken.
                    bool exactOrderStillEnabled = assignedLoadedFlag.Enabled &&
                                                  WorkerJobProfileRuntime.SupportsOrder(zdo, role, persistedOrder);
                    if (!exactOrderStillEnabled)
                    {
                        WorkerZdo.ClearWorkArea(zdo);
                        hasWorkArea = false;
                    }
                    else
                    {
                        activeWorkOrder = persistedOrder;
                        workCenter = assignedLoadedFlag.Center;
                        workRadius = assignedLoadedFlag.Radius;
                        WorkerZdo.AssignWorkArea(zdo, assignedLoadedFlag.Id, assignedLoadedFlag.DisplayName, activeWorkOrder, workCenter, workRadius);
                    }
                }
            }
            else if (hasPersistedAssignment)
            {
                WorkerZdo.ClearWorkArea(zdo);
            }

            // A Workzone is a real outpost, not just a job radius. A Worker may only work for a
            // flag when its assigned species HomeAnchor is physically inside the same flag area.
            // Keep the explicit assignment persisted so the flag UI can show/fix it, but block AI
            // work until the sleep place has been built/assigned in this Workzone.
            if (hasWorkArea && !WorkerWorkAreaRules.TryValidateHomeForArea(zdo, workCenter, workRadius, out string homeAreaReason))
            {
                ClearLocalTargets(workerKey);
                WorkerZdo.ClearTarget(zdo);
                WorkerState blockedState = WorkerWorkAreaRules.HasRealHome(zdo) ? WorkerState.ReturnHome : WorkerState.Idle;
                SetState(zdo, blockedState, worker, $"work area blocked: {homeAreaReason}");
                return;
            }

            if (!hasWorkArea &&
                WorkFlagRegistry.TryFindBestForWorker(worker, zdo, role, 1000f, 30f, out WorkAreaSnapshot discoveredWorkArea))
            {
                activeWorkOrder = discoveredWorkArea.Order;
                workCenter = discoveredWorkArea.Center;
                workRadius = discoveredWorkArea.Radius;
                hasWorkArea = true;
                WorkerZdo.AssignWorkArea(zdo, discoveredWorkArea.Id, discoveredWorkArea.DisplayName, activeWorkOrder, workCenter, workRadius);
            }

            if (hasWorkArea)
            {
                // v0.5.291: tolerate a short missing-resource wait, then back off only the
                // current task. The shared selector below can immediately hand the worker to
                // another enabled job instead of pinning it forever to NeedsResource.
                if (WorkerResourceBackoffRuntime.Observe(zdo, role, activeWorkOrder, _debugLog))
                {
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                }
            }

            if (hasWorkArea && role == WorkerRole.Gardener)
            {
                WorkOrder gardenerOrder = ResolveGardenerWorkOrder(worker, zdo, activeWorkOrder, workCenter, workRadius);
                if (gardenerOrder == WorkOrder.None)
                {
                    SendHomeWhenNoWork(worker, zdo, "selected gardener tasks are not enabled on this Workzone");
                    return;
                }
                if (gardenerOrder != activeWorkOrder)
                {
                    activeWorkOrder = gardenerOrder;
                    zdo.Set(WorkerZdo.WorkOrderKey, (int)activeWorkOrder);
                    WorkerZdo.ClearTarget(zdo);
                    _debugLog?.Invoke($"Gardener modular task switch: worker={worker.gameObject.name}, order={activeWorkOrder}");
                }
            }

            if (hasWorkArea && role == WorkerRole.Gatherer)
            {
                // Gatherer is a modular two-job role (Sammeln + Ernten). The WorkArea order
                // is only the zone/default module and must not pin the worker to Sammeln when
                // that per-worker task is disabled. Resolve the currently enabled task before
                // the generic IsOrderEnabled guard below, otherwise an Ernten-only Greyling is
                // sent home without ever reaching TickGathererProfileInsideArea().
                WorkerTaskFlags gathererTasks = WorkerJobProfileRuntime.ResolveEffectiveGathererTasks(zdo);
                WorkOrder gathererOrder = WorkerJobProfileRuntime.SelectBestAvailableOrder(
                    zdo, WorkerRole.Gatherer, activeWorkOrder, gathererTasks);
                if (gathererOrder == WorkOrder.None)
                {
                    SendHomeWhenNoWork(worker, zdo, "selected Gatherer tasks are not enabled on this Workzone");
                    return;
                }
                if (gathererOrder != activeWorkOrder)
                {
                    activeWorkOrder = gathererOrder;
                    zdo.Set(WorkerZdo.WorkOrderKey, (int)activeWorkOrder);
                    WorkerZdo.ClearTarget(zdo);
                    _debugLog?.Invoke($"Gatherer modular task switch: worker={worker.gameObject.name}, order={activeWorkOrder}");
                }
            }

            if (hasWorkArea && role == WorkerRole.Hauler)
            {
                WorkOrder haulerOrder = ResolveHaulerWorkOrder(worker, zdo, activeWorkOrder);
                if (haulerOrder == WorkOrder.None)
                {
                    SendHomeWhenNoWork(worker, zdo, "selected logistics tasks are not enabled on this Workzone");
                    return;
                }
                if (haulerOrder != activeWorkOrder)
                {
                    activeWorkOrder = haulerOrder;
                    zdo.Set(WorkerZdo.WorkOrderKey, (int)activeWorkOrder);
                    WorkerZdo.ClearTarget(zdo);
                    _debugLog?.Invoke($"Hauler modular task switch: worker={worker.gameObject.name}, order={activeWorkOrder}");
                }
            }

            if (hasWorkArea && role == WorkerRole.Hunter)
            {
                WorkOrder hunterOrder = ResolveHunterWorkOrder(worker, zdo, activeWorkOrder, workCenter, workRadius);
                if (hunterOrder == WorkOrder.None)
                {
                    SendHomeWhenNoWork(worker, zdo, "Jagen und Bewachen sind deaktiviert");
                    return;
                }
                if (hunterOrder != activeWorkOrder)
                {
                    activeWorkOrder = hunterOrder;
                    zdo.Set(WorkerZdo.WorkOrderKey, (int)activeWorkOrder);
                    WorkerZdo.ClearTarget(zdo);
                    _debugLog?.Invoke($"Hunter modular task switch: worker={worker.gameObject.name}, order={activeWorkOrder}");
                }

                if (activeWorkOrder == WorkOrder.Hunt && !HunterStandMarker.TryFindValid(workCenter, workRadius, out Vector3 hunterStandPoint))
                {
                    if (WorkerJobProfileRuntime.HasTask(zdo, role, WorkerTaskFlags.Guard))
                    {
                        activeWorkOrder = WorkOrder.Guard;
                        zdo.Set(WorkerZdo.WorkOrderKey, (int)activeWorkOrder);
                        WorkerZdo.ClearTarget(zdo);
                    }
                    else
                    {
                        ClearLocalTargets(workerKey);
                        WorkerZdo.ClearTarget(zdo);
                        SendHomeWhenNoWork(worker, zdo, "Jagd blockiert: keine gueltige Jagdplattform (mind. 2 m ueber Boden) in der Workzone");
                        return;
                    }
                }
            }

            if (hasWorkArea && !WorkerJobProfileRuntime.IsOrderEnabled(zdo, role, activeWorkOrder))
            {
                ClearLocalTargets(workerKey);
                WorkerZdo.ClearTarget(zdo);
                SendHomeWhenNoWork(worker, zdo, WorkOrderCatalog.DisplayName(activeWorkOrder) + " ist fuer diesen Worker deaktiviert");
                return;
            }

            // Logistics routes are allowed to leave the WorkArea. A Gatherer carrying cargo may
            // travel back to the HomeAnchor storage, and a Forester without seed cargo may travel
            // home to fetch a real seed/cone from a chest. These routes must run before the generic
            // WorkArea boundary would otherwise turn the Worker around at the flag radius.
            // Gatherers own their cargo routing inside TickGathererInsideArea. Do not route merely
            // because any cargo exists: the multi-stack capacity system must be allowed to keep
            // collecting until slot/weight capacity is reached or no useful targets remain.
            // Gatherer delivery is a distinct persisted phase. Once the collection pass has decided
            // that the current cargo should be delivered, the next AI decision must resolve and keep
            // a concrete FieldDepot target. Previously every decision tick re-entered
            // TickGathererInsideArea(), which immediately cleared the delivery target again and
            // reasserted Deliver. The Worker therefore showed State=Deliver with Current target=none
            // forever and never reached the storage runtime.
            if (hasWorkArea && role == WorkerRole.Gatherer &&
                WorkerZdo.HasCargo(zdo) &&
                (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle) == WorkerState.Deliver)
            {
                TickGathererDelivery(worker, zdo, workCenter);
                return;
            }

            // Stonemasons also own a real cargo loop now. Once a quarry sweep has decided to
            // unload, resolve the chest/depot before the generic WorkArea boundary can pull the
            // Golem back toward the flag. This mirrors the proven Gatherer delivery hand-off.
            if (hasWorkArea && role == WorkerRole.Stonemason && WorkerZdo.HasCargo(zdo) &&
                (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle) == WorkerState.Deliver)
            {
                TickStonemasonDelivery(worker, zdo);
                return;
            }


            // Fisher catch is real persistent Worker cargo. Resolve the delivery target before
            // generic WorkArea boundary handling, just like Gatherer/Stonemason cargo.
            if (hasWorkArea && role == WorkerRole.Fisher && activeWorkOrder == WorkOrder.Fish &&
                WorkerZdo.HasCargo(zdo) &&
                (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle) == WorkerState.Deliver)
            {
                TickFisherDelivery(worker, zdo);
                return;
            }

            // Air Haulers must own their complete cargo cycle before the generic ground-hauler
            // delivery branch runs. Otherwise a Drake that has just collected AirTransport cargo
            // is incorrectly routed toward normal Import/Export storage instead of its Advanced
            // FieldDepot destination.
            if (hasWorkArea && role == WorkerRole.Hauler && activeWorkOrder == WorkOrder.Haul &&
                AirHaulerWorkRuntime.IsAirHauler(worker))
            {
                TickAirHaulerInsideArea(worker, zdo, workCenter, workRadius);
                return;
            }

            // Hauler and Sorter share the same creature role, but NOT the same cargo state machine.
            // A generic "Hauler + cargo" branch here used to steal Sorter cargo before
            // TickSorterInsideArea could resolve its SortImport/SortExport/AirSort route. The
            // worker then fell into the legacy Haul -> Import fallback and stood still with no
            // valid sorter target. Route cargo through the job that owns it instead.
            if (hasWorkArea && role == WorkerRole.Hauler && activeWorkOrder == WorkOrder.Provision && WorkerZdo.HasCargo(zdo))
            {
                TickProvisionerInsideArea(worker, zdo, workCenter, workRadius);
                return;
            }

            if (hasWorkArea && role == WorkerRole.Hauler && activeWorkOrder == WorkOrder.Sort && WorkerZdo.HasCargo(zdo))
            {
                TickSorterInsideArea(worker, zdo, workCenter, workRadius);
                return;
            }

            if (hasWorkArea && role == WorkerRole.Hauler && activeWorkOrder == WorkOrder.Haul && WorkerZdo.HasCargo(zdo))
            {
                // Physical cargo is already counted by BackgroundWorkerScheduler as in-transit.
                // Any leftover pre-pickup transport claim would double-reserve the same demand.
                HaulerDemandClaimRuntime.Release(zdo, "cargo-authoritative");
                TickHaulerDelivery(worker, zdo);
                return;
            }

            // Transport is a logistics-network job, not a circular field job. The assigned flag
            // names/authorizes the network, but its radius must not force an empty Hauler back to
            // the flag between Base and FieldDepot. Sorters remain local Base workers.
            if (hasWorkArea && role == WorkerRole.Hauler && activeWorkOrder == WorkOrder.Haul)
            {
                TickHaulerInsideArea(worker, zdo, workCenter, workRadius);
                return;
            }

            // Provisioning is also a logistics-network job. The supply chest or target rest
            // station may sit outside the decorative WorkFlag radius, so do not let the generic
            // field boundary pull the Versorger back to the flag mid-route.
            if (hasWorkArea && role == WorkerRole.Hauler && activeWorkOrder == WorkOrder.Provision)
            {
                TickProvisionerInsideArea(worker, zdo, workCenter, workRadius);
                return;
            }

            if (hasWorkArea && role == WorkerRole.Gardener && activeWorkOrder == WorkOrder.Forestry && !WorkerZdo.HasCargo(zdo))
            {
                TickForestrySupply(worker, zdo);
                return;
            }

            if (hasWorkArea && role == WorkerRole.Gardener && activeWorkOrder == WorkOrder.Garden && !WorkerZdo.HasCargo(zdo))
            {
                TickGardenSupply(worker, zdo);
                return;
            }

            if (hasWorkArea)
            {
                float distanceToAreaCenter = Vector3.Distance(worker.transform.position, workCenter);
                if (distanceToAreaCenter > workRadius)
                {
                    // v0.5.181: Stonemasons must not walk to the WorkFlag merely to discover that
                    // the quarry is empty. Their target finder already scans the assigned flag radius
                    // from anywhere, so let it resolve a real rock/ore target directly. If none exists,
                    // TickStonemasonInsideArea sends the Golem straight back to its own rest station.
                    // This changes only job dispatch; Navigator v5/pathfinding remains untouched.
                    if (role == WorkerRole.Stonemason)
                    {
                        TickStonemasonInsideArea(worker, zdo, workCenter, workRadius);
                        return;
                    }

                    ClearLocalTargets(workerKey);
                    WorkerZdo.ClearTarget(zdo);
                    SetState(zdo, WorkerState.MoveToWorkArea, worker, $"persistent work area {WorkOrderCatalog.DisplayName(activeWorkOrder)} ({distanceToAreaCenter:0.0} m away)");
                    return;
                }
            }
            else
            {
                float distanceFromHome = Vector3.Distance(worker.transform.position, home);

                // Backwards-compatible fallback while a role has no WorkArea: keep the old
                // home-centred work boundary instead of stranding existing workers.
                if (distanceFromHome > homeRadius)
                {
                    ClearLocalTargets(workerKey);
                    WorkerZdo.ClearTarget(zdo);
                    SetState(zdo, WorkerState.ReturnHome, worker, $"outside home work area ({distanceFromHome:0.0}/{homeRadius:0.0} m)");
                    return;
                }
            }

            switch (role)
            {
                case WorkerRole.Lumberjack:
                    TickLumberjackInsideArea(worker, zdo, workCenter, workRadius);
                    break;

                case WorkerRole.Gatherer:
                    TickGathererProfileInsideArea(worker, zdo, workCenter, workRadius);
                    break;

                case WorkerRole.Hauler:
                    if (activeWorkOrder == WorkOrder.Provision)
                        TickProvisionerInsideArea(worker, zdo, workCenter, workRadius);
                    else if (activeWorkOrder == WorkOrder.Sort)
                        TickSorterInsideArea(worker, zdo, workCenter, workRadius);
                    else
                        TickHaulerInsideArea(worker, zdo, workCenter, workRadius);
                    break;

                case WorkerRole.Gardener:
                    if (activeWorkOrder == WorkOrder.Harvest)
                    {
                        if (!TryTickHarvesterTarget(worker, zdo, workCenter, workRadius))
                        {
                            WorkerTaskFlags gardenerTasks = WorkerJobProfileRuntime.ResolveEffectiveGardenerTasks(zdo);
                            bool hasOtherPlantTask = (gardenerTasks & (WorkerTaskFlags.Garden | WorkerTaskFlags.Forestry | WorkerTaskFlags.Bake)) != 0;
                            if (hasOtherPlantTask)
                            {
                                // v0.5.377: perform the safe-boundary handoff immediately after an
                                // empty harvest sweep. Passing WorkOrder.None lets the centralized
                                // role-specific tie rules choose the next concrete plant job cleanly.
                                WorkOrder nextPlantOrder = ResolveGardenerWorkOrder(worker, zdo, WorkOrder.None, workCenter, workRadius);
                                if (nextPlantOrder != WorkOrder.None && nextPlantOrder != WorkOrder.Harvest)
                                {
                                    zdo.Set(WorkerZdo.WorkOrderKey, (int)nextPlantOrder);
                                    WorkerZdo.ClearTarget(zdo);
                                    SetState(zdo, WorkerState.FindJob, worker, $"harvest sweep complete; handoff -> {nextPlantOrder}");
                                    return;
                                }

                                WorkerZdo.ClearTarget(zdo);
                                SetState(zdo, WorkerState.FindJob, worker, "harvest sweep complete; choose next plant task");
                            }
                            else
                            {
                                SendHomeWhenNoWork(worker, zdo, "no mature harvest targets; waiting at home");
                            }
                        }
                    }
                    else if (activeWorkOrder == WorkOrder.Forestry)
                        TickForestryInsideArea(worker, zdo, workCenter, workRadius);
                    else if (activeWorkOrder == WorkOrder.Garden)
                        TickGardenInsideArea(worker, zdo, workCenter, workRadius);
                    else if (activeWorkOrder == WorkOrder.Bake)
                        TickBakerInsideArea(worker, zdo, workCenter, workRadius);
                    else
                    {
                        SendHomeWhenNoWork(worker, zdo, "no compatible gardener/specialist job enabled");
                    }
                    break;

                case WorkerRole.Stonemason:
                    TickStonemasonInsideArea(worker, zdo, workCenter, workRadius);
                    break;

                case WorkerRole.Baker:
                    TickBakerInsideArea(worker, zdo, workCenter, workRadius);
                    break;

                case WorkerRole.Butcher:
                    WorkerDoorRuntime.Tick(worker, zdo);
                    TickButcherInsideArea(worker, zdo, workCenter, workRadius);
                    break;

                case WorkerRole.Firekeeper:
                    TickFirekeeperInsideArea(worker, zdo, workCenter, workRadius);
                    break;

                case WorkerRole.Smith:
                    WorkerDoorRuntime.Tick(worker, zdo);
                    TickSmithInsideArea(worker, zdo, workCenter, workRadius);
                    break;

                case WorkerRole.Hunter:
                    if (activeWorkOrder == WorkOrder.Hunt)
                        TickHunterInsideArea(worker, zdo, workCenter, workRadius);
                    else if (activeWorkOrder == WorkOrder.Guard)
                        SendGuardStandbyWhenNoWork(worker, zdo, role, workCenter, workRadius, "Wache: sichert die Workzone");
                    else
                        SendHomeWhenNoWork(worker, zdo, "kein kompatibler Hunter-Auftrag aktiv");
                    break;

                // Guard is a real combat/patrol role. Stationary mode holds its post; Deploy mode
                // uses GuardPatrolRuntime and yields to native combat when a hostile target appears.
                case WorkerRole.Guard:
                    if (activeWorkOrder == WorkOrder.Guard)
                        SendGuardStandbyWhenNoWork(worker, zdo, role, workCenter, workRadius, "Wache: sichert die Workzone");
                    else
                        SendHomeWhenNoWork(worker, zdo, "kein kompatibler Wachauftrag aktiv");
                    break;

                case WorkerRole.Fisher:
                    if (activeWorkOrder == WorkOrder.Fish)
                        TickFisherInsideArea(worker, zdo, workCenter, workRadius);
                    else
                        SendHomeWhenNoWork(worker, zdo, "Fischen ist fuer diesen Worker deaktiviert");
                    break;

                case WorkerRole.Miner:
                default:
                    SendHomeWhenNoWork(worker, zdo, "role has no active job implementation");
                    break;
            }
        }


        private void TickFisherInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            if (worker == null || zdo == null) return;

            if (WorkerZdo.HasCargo(zdo))
            {
                zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                TickFisherDelivery(worker, zdo);
                return;
            }

            if (FisherWorkRuntime.TryFindFish(worker, zdo, workCenter, workRadius, out string fishPrefab, out Vector3 fishPosition))
            {
                WorkerZdo.WriteTarget(zdo, fishPrefab, fishPosition, FisherWorkRuntime.TargetKind);
                float distance = Vector3.Distance(worker.transform.position, fishPosition);
                SetState(zdo, distance <= FisherWorkRuntime.CatchRange ? WorkerState.Work : WorkerState.MoveToTarget, worker,
                    $"Fischer: {fishPrefab} ({distance:0.0} m)");
                return;
            }

            SendHomeWhenNoWork(worker, zdo, "Fischer: aktuell kein echter Fisch in Reichweite der Workzone");
        }

        private void TickFisherDelivery(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return;
            if (!WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int cargoAmount) || cargoAmount <= 0)
            {
                WorkerZdo.ClearCargo(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return;
            }

            if (WorkerStorageRuntime.TryFindMatchingDepositContainer(worker, zdo, cargoPrefab, out _, out Vector3 storagePosition))
            {
                WorkerZdo.WriteTarget(zdo, cargoPrefab, storagePosition, WorkerStorageRuntime.DepositTargetKind);
                SetState(zdo, WorkerState.Deliver, worker, $"Fischer-Cargo {cargoPrefab} x{cargoAmount} -> Lager");
                return;
            }

            if (WorkerStorageRuntime.TryFindFieldDepotForCargo(worker, zdo, cargoPrefab, out _, out Vector3 depotPosition))
            {
                WorkerZdo.WriteTarget(zdo, cargoPrefab, depotPosition, WorkerStorageRuntime.FieldDepotDepositTargetKind);
                SetState(zdo, WorkerState.Deliver, worker, $"Fischer-Cargo {cargoPrefab} x{cargoAmount} -> FieldDepot");
                return;
            }

            SendHomeWhenNoWork(worker, zdo, $"Fischer traegt {cargoPrefab} x{cargoAmount}; keine freie passende Lagerkiste");
        }

        private WorkOrder ResolveHaulerWorkOrder(Character worker, ZDO zdo, WorkOrder current)
        {
            WorkerTaskFlags tasks = WorkerJobProfileRuntime.ResolveEffectiveHaulerTasks(zdo);
            bool transport = (tasks & WorkerTaskFlags.Transport) != 0;
            bool sort = (tasks & WorkerTaskFlags.Sort) != 0;
            bool provision = (tasks & WorkerTaskFlags.Provision) != 0;
            if (!transport && !sort && !provision) return WorkOrder.None;

            // Drake/Lox profiles expose transport only. This also prevents legacy logistics
            // assignments from pulling a specialized hauler into a ground logistics state machine.
            if (AirHaulerWorkRuntime.IsAirHauler(worker))
                return transport ? WorkOrder.Haul : WorkOrder.None;

            // Never switch ownership of real cargo mid-cycle. Cargo routes explicitly encode which
            // state machine owns the load and therefore take precedence over idle job selection.
            if (WorkerZdo.HasCargo(zdo))
            {
                if (WorkerZdo.TryReadCargoRoute(zdo, out string route, out _))
                {
                    bool provisionRoute = string.Equals(route, WorkerProvisioningLogisticsRuntime.RouteProvisionHome, StringComparison.Ordinal);
                    bool sorterRoute = string.Equals(route, WorkerLogisticsRuntime.RouteSortImport, StringComparison.Ordinal) ||
                                       string.Equals(route, WorkerLogisticsRuntime.RouteSortExport, StringComparison.Ordinal) ||
                                       string.Equals(route, AirHaulerWorkRuntime.RouteSortInbound, StringComparison.Ordinal) ||
                                       string.Equals(route, AirHaulerWorkRuntime.RouteSortOutbound, StringComparison.Ordinal);
                    bool haulRoute = string.Equals(route, WorkerLogisticsRuntime.RouteHaulInbound, StringComparison.Ordinal) ||
                                     string.Equals(route, WorkerLogisticsRuntime.RouteHaulOutbound, StringComparison.Ordinal) ||
                                     string.Equals(route, AirHaulerWorkRuntime.RouteInbound, StringComparison.Ordinal) ||
                                     string.Equals(route, AirHaulerWorkRuntime.RouteOutbound, StringComparison.Ordinal);
                    if (provisionRoute && provision) return WorkOrder.Provision;
                    if (sorterRoute && sort) return WorkOrder.Sort;
                    if (haulRoute && transport) return WorkOrder.Haul;
                }

                if (current == WorkOrder.Provision && provision) return WorkOrder.Provision;
                if (current == WorkOrder.Sort && sort) return WorkOrder.Sort;
                if (current == WorkOrder.Haul && transport) return WorkOrder.Haul;
                return WorkerJobProfileRuntime.SelectBestEnabledOrder(zdo, WorkerRole.Hauler, current);
            }

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state == WorkerState.MoveToTarget || state == WorkerState.Collect || state == WorkerState.Deliver ||
                state == WorkerState.ReturnHome)
            {
                if (current == WorkOrder.Provision && provision) return WorkOrder.Provision;
                if (current == WorkOrder.Sort && sort) return WorkOrder.Sort;
                if (current == WorkOrder.Haul && transport) return WorkOrder.Haul;
            }

            int enabledCount = (transport ? 1 : 0) + (sort ? 1 : 0) + (provision ? 1 : 0);
            if (enabledCount == 1)
            {
                if (provision) return WorkOrder.Provision;
                if (sort) return WorkOrder.Sort;
                return WorkOrder.Haul;
            }

            // Sort and Provision are independent modules. The shared priority selector decides
            // between real pending work. Since v0.5.377 an equal-priority tie is explicit:
            // Provision > Haul > Sort, while already-started cargo/routes still finish first.
            bool provisionPending = provision && WorkerProvisioningLogisticsRuntime.HasPendingDemand(worker, zdo);
            bool sortPending = sort && WorkerLogisticsRuntime.TryFindSorterSource(worker, zdo, out _, out _, out _, out _, out _);
            bool haulPending = false;
            if (transport && WorkerLogisticsRuntime.TryFindAssignedOutpostFieldDepot(worker, zdo, out _, out _))
            {
                bool atOutpost = WorkerLogisticsRuntime.IsInsideAssignedOutpost(zdo, worker.transform.position, 5f);
                if (atOutpost)
                {
                    haulPending = WorkerStorageRuntime.TryFindLocalReturnHaulSource(worker, zdo, 22f, out _, out _, out _, out _) ||
                                  WorkerLogisticsRuntime.TryFindExportHaulSource(worker, zdo, out _, out _, out _, out _, out _, out _, out _) ||
                                  WorkerStorageRuntime.TryFindHaulSource(worker, zdo, out _, out _, out _, out _);
                }
                else
                {
                    haulPending = WorkerLogisticsRuntime.TryFindExportHaulSource(worker, zdo, out _, out _, out _, out _, out _, out _, out _);
                }
            }

            WorkerTaskFlags pendingTasks = WorkerTaskFlags.None;
            if (haulPending) pendingTasks |= WorkerTaskFlags.Transport;
            if (sortPending) pendingTasks |= WorkerTaskFlags.Sort;
            if (provisionPending) pendingTasks |= WorkerTaskFlags.Provision;
            WorkOrder selectedPending = WorkerJobProfileRuntime.SelectBestAvailableOrder(zdo, WorkerRole.Hauler, current, pendingTasks);
            if (selectedPending != WorkOrder.None) return selectedPending;

            // Nothing concrete is pending. Keep the current enabled module parked in Standby; if
            // the current order is invalid, use the same shared priority selector.
            if (current == WorkOrder.Provision && provision) return WorkOrder.Provision;
            if (current == WorkOrder.Sort && sort) return WorkOrder.Sort;
            if (current == WorkOrder.Haul && transport) return WorkOrder.Haul;
            return WorkerJobProfileRuntime.SelectBestAvailableOrder(zdo, WorkerRole.Hauler, current, tasks);
        }

        private WorkOrder ResolveHunterWorkOrder(Character worker, ZDO zdo, WorkOrder current, Vector3 workCenter, float workRadius)
        {
            WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, WorkerRole.Hunter);
            bool hunt = (tasks & WorkerTaskFlags.Hunt) != 0;
            bool guard = (tasks & WorkerTaskFlags.Guard) != 0;
            if (!hunt && !guard) return WorkOrder.None;

            // Finish a real hunt/cargo cycle before considering another job.
            if (hunt && WorkerZdo.HasCargo(zdo)) return WorkOrder.Hunt;
            if (hunt && WorkerZdo.TryReadTarget(zdo, out _, out string kind, out _) &&
                (string.Equals(kind, "HunterPrey", StringComparison.Ordinal) ||
                 string.Equals(kind, HunterWorkRuntime.LootTargetKind, StringComparison.Ordinal)))
                return WorkOrder.Hunt;

            bool huntPending = hunt &&
                               HunterStandMarker.TryFindValid(workCenter, workRadius, out _) &&
                               HunterWorkRuntime.HasAvailablePrey(zdo, workCenter, workRadius, worker.transform.position);
            bool guardPending = guard;

            WorkerTaskFlags pendingTasks = WorkerTaskFlags.None;
            if (huntPending) pendingTasks |= WorkerTaskFlags.Hunt;
            if (guardPending) pendingTasks |= WorkerTaskFlags.Guard;
            WorkOrder selected = WorkerJobProfileRuntime.SelectBestAvailableOrder(zdo, WorkerRole.Hunter, current, pendingTasks);
            if (selected != WorkOrder.None) return selected;

            // Jagen can remain the parked module when there is temporarily no prey. If Bewachen is
            // enabled it is always considered available above and therefore wins as the fallback.
            return hunt ? WorkOrder.Hunt : WorkOrder.None;
        }

        private static bool IsStaticActionFastPath(WorkerRole role, WorkOrder order, string targetKind)
        {
            if (string.IsNullOrEmpty(targetKind)) return false;

            // Production targets are fixed stations/chests. WorkerNavigationRuntime owns arrival,
            // pickup and Work dispatch, so the high-level scheduler has nothing to rediscover while
            // this target remains persisted.
            if ((role == WorkerRole.Baker || (role == WorkerRole.Gardener && order == WorkOrder.Bake)) &&
                (string.Equals(targetKind, BakerWorkRuntime.TargetKind, StringComparison.Ordinal) ||
                 string.Equals(targetKind, BakerWorkRuntime.InputSourceTargetKind, StringComparison.Ordinal)))
                return true;

            // Ground/Air logistics targets are also fixed container endpoints. This covers Haul,
            // Provision and Sort without hard-coding every individual container target kind.
            if (role == WorkerRole.Hauler)
                return true;

            return false;
        }

        private WorkOrder ResolveGardenerWorkOrder(Character worker, ZDO zdo, WorkOrder current, Vector3 workCenter, float workRadius)
        {
            WorkerTaskFlags tasks = WorkerJobProfileRuntime.ResolveEffectiveGardenerTasks(zdo);
            bool garden = (tasks & WorkerTaskFlags.Garden) != 0;
            bool forestry = (tasks & WorkerTaskFlags.Forestry) != 0;
            bool harvest = (tasks & WorkerTaskFlags.Harvest) != 0;
            bool bake = (tasks & WorkerTaskFlags.Bake) != 0;
            if (!garden && !forestry && !harvest && !bake) return WorkOrder.None;

            // v0.5.354: NeedsResource is already observed/backed off before this resolver runs.
            // While the current Garden module is still inside its grace period (or is the only
            // enabled module), keep it without walking the complete persistent slot queue merely
            // to rediscover that the same depot stock is missing. Once backoff activates, state is
            // changed to FindJob above and the normal multi-task availability selection runs.
            WorkerState schedulerState = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (garden && current == WorkOrder.Garden && schedulerState == WorkerState.NeedsResource &&
                !WorkerResourceBackoffRuntime.IsBlocked(zdo, WorkOrder.Garden))
                return WorkOrder.Garden;

            // Never switch away from a concrete in-progress real-resource cycle. The unified
            // scheduler chooses between jobs only at safe boundaries. Cooking must be checked first:
            // a Shaman can keep an older Garden plan while carrying ingredients for an active recipe.
            // If that happened, the old order of these checks reassigned it to Garden and stranded the
            // real cooking cargo forever.
            if (bake && !WorkerResourceBackoffRuntime.IsBlocked(zdo, WorkOrder.Bake) && GenericCookingWorkRuntime.HasPlan(zdo))
                return WorkOrder.Bake;
            if (garden && !WorkerResourceBackoffRuntime.IsBlocked(zdo, WorkOrder.Garden) &&
                WorkerZdo.TryReadGardenPlan(zdo, out WorkerGardenPlan gardenPlan) &&
                GardenerTaskFilterRuntime.IsGardenPlanAllowed(zdo, gardenPlan) &&
                GardenReplantPlanRuntime.IsPositionInsideAssignedWorkArea(zdo, gardenPlan.Position))
                return WorkOrder.Garden;
            if (forestry && !WorkerResourceBackoffRuntime.IsBlocked(zdo, WorkOrder.Forestry) &&
                WorkerZdo.TryReadForestryPlan(zdo, out WorkerForestryPlan forestryPlan) &&
                GardenerTaskFilterRuntime.IsForestrySpeciesAllowed(zdo, forestryPlan.SaplingPrefab))
                return WorkOrder.Forestry;
            if (WorkerZdo.HasCargo(zdo))
            {
                if (current == WorkOrder.Garden && garden) return WorkOrder.Garden;
                if (current == WorkOrder.Forestry && forestry) return WorkOrder.Forestry;
                if (current == WorkOrder.Bake && bake) return WorkOrder.Bake;
            }

            // Persisted targets also keep ownership until the current action completes.
            if (harvest && current == WorkOrder.Harvest &&
                WorkerZdo.TryReadTarget(zdo, out _, out string currentTargetKind, out _) &&
                string.Equals(currentTargetKind, GardenHarvestTargetFinder.TargetKind, StringComparison.Ordinal))
                return WorkOrder.Harvest;
            if (bake &&
                WorkerZdo.TryReadTarget(zdo, out _, out string bakerTargetKind, out _) &&
                (string.Equals(bakerTargetKind, BakerWorkRuntime.TargetKind, StringComparison.Ordinal) ||
                 string.Equals(bakerTargetKind, BakerWorkRuntime.InputSourceTargetKind, StringComparison.Ordinal)))
                return WorkOrder.Bake;

            bool harvestPending = false;
            if (harvest)
            {
                int workerKey = worker.GetInstanceID();
                if (!_nextHarvestSearchAt.TryGetValue(workerKey, out float nextHarvestSearch) || Time.time >= nextHarvestSearch)
                {
                    GardenHarvestTargetResult harvestTarget = GardenHarvestTargetFinder.FindNearest(
                        workCenter,
                        Mathf.Max(5f, workRadius),
                        worker.transform.position,
                        pickable => GatherHarvestCategoryFilterRuntime.IsHarvestPickableAllowed(zdo, pickable) &&
                                    ProductionDemandRuntime.AllowsHarvestTarget(zdo, pickable));
                    harvestPending = harvestTarget.Target != null;
                    if (harvestPending)
                    {
                        _nextHarvestSearchAt.Remove(workerKey);
                        _harvestSearchCache[workerKey] = harvestTarget;
                        _harvestSearchCacheUntil[workerKey] = Time.time + 1f;
                    }
                    else
                    {
                        _harvestSearchCache.Remove(workerKey);
                        _harvestSearchCacheUntil.Remove(workerKey);
                        _nextHarvestSearchAt[workerKey] = Time.time + 4f;
                    }
                }
            }

            bool gardenPending = false;
            if (garden)
            {
                int pendingGarden = GardenReplantPlanRuntime.GetPendingForWorker(zdo, _gardenSlotScratch, 512);
                for (int i = 0; i < pendingGarden; i++)
                {
                    GardenReplantSlot slot = _gardenSlotScratch[i];
                    if (!slot.PersistentPlan) continue;
                    if (!TryBuildGardenCandidate(worker, zdo, slot, out WorkerGardenPlan candidate)) continue;
                    if (!GardenerTaskFilterRuntime.IsGardenPlanAllowed(zdo, candidate)) continue;
                    gardenPending = true;
                    break;
                }
            }

            bool forestryPending = forestry && ForestryPlanRuntime.TryGetNearestPendingForWorker(worker, zdo, out _);
            bool bakePending = bake && BakerWorkRuntime.HasAvailableWork(worker, zdo, workCenter, workRadius);

            WorkerTaskFlags pendingTasks = WorkerTaskFlags.None;
            if (harvestPending) pendingTasks |= WorkerTaskFlags.Harvest;
            if (gardenPending) pendingTasks |= WorkerTaskFlags.Garden;
            if (forestryPending) pendingTasks |= WorkerTaskFlags.Forestry;
            if (bakePending) pendingTasks |= WorkerTaskFlags.Bake;

            // v0.5.377: equal-priority Harvest/Garden arbitration is centralized in
            // WorkerJobProfileRuntime. It uses the persisted one-action handoff written only after
            // a real harvest/plant completes, so a Shaman performs one harvest and then gives one
            // planting action first refusal instead of sticking to either module forever.

            WorkOrder selectedPending = WorkerJobProfileRuntime.SelectBestAvailableOrder(zdo, WorkerRole.Gardener, current, pendingTasks);
            if (selectedPending != WorkOrder.None) return selectedPending;

            // No concrete work right now: keep the current enabled module, otherwise park on the
            // highest configured priority. The next availability probe can immediately preempt it.
            if (current == WorkOrder.Garden && garden) return WorkOrder.Garden;
            if (current == WorkOrder.Forestry && forestry) return WorkOrder.Forestry;
            if (current == WorkOrder.Harvest && harvest) return WorkOrder.Harvest;
            if (current == WorkOrder.Bake && bake) return WorkOrder.Bake;
            return WorkerJobProfileRuntime.SelectBestAvailableOrder(zdo, WorkerRole.Gardener, current, tasks);
        }


        private void TickHunterInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            if (WorkerZdo.HasCargo(zdo) && WorkerCargoRuntime.IsFull(worker, zdo))
            {
                TickHunterDelivery(worker, zdo);
                return;
            }

            SetState(zdo, WorkerState.FindJob, worker, null);

            if (HunterWorkRuntime.TryEnsureHuntTarget(worker, zdo, workCenter, workRadius, out Character prey, out string huntStatus) && prey != null)
            {
                // Hunter job owns approach/aim timing. Do not hand the quarry to MonsterAI combat: that
                // caused ranged Draugr to enter Combat without ever committing a reliable bow shot.
                MonsterAI ai = worker.GetComponent<MonsterAI>();
                if (ai != null) WorkerCombatStanceRuntime.ClearHunterJobTarget(ai);

                string preyName = SpeciesRoleRegistry.NormalizePrefabName(prey.gameObject.name);
                Vector3 delta = prey.transform.position - worker.transform.position;
                delta.y = 0f;
                float distance = delta.magnitude;

                // v0.5.366: range alone is not a valid firing solution. If terrain, a tree or a
                // structure blocks the ray, move to a small local firing point instead of spending
                // minutes shooting into the same obstacle. Candidate probing is bounded and only
                // happens for an active Hunter target; Navigator v5 itself stays untouched.
                if (distance <= HunterWorkRuntime.ShootRangeHysteresis &&
                    HunterWorkRuntime.TryGetBlockedShotReposition(worker, zdo, prey, workCenter, workRadius, out Vector3 firingPoint, out string losStatus))
                {
                    WorkerZdo.WriteTarget(zdo, preyName, firingPoint, HunterWorkRuntime.VantageTargetKind);
                    SetState(zdo, WorkerState.MoveToTarget, worker, losStatus);
                    return;
                }

                WorkerZdo.WriteTarget(zdo, preyName, prey.transform.position, "HunterPrey");
                SetState(zdo, distance <= HunterWorkRuntime.ShootRange && HunterWorkRuntime.HasClearShot(worker, prey)
                    ? WorkerState.Work
                    : WorkerState.MoveToTarget, worker, huntStatus);
                return;
            }

            if (HunterWorkRuntime.TryGetLootTarget(worker, out ItemDrop loot, out string lootPrefab, out Vector3 lootPosition))
            {
                WorkerZdo.WriteTarget(zdo, lootPrefab, lootPosition, HunterWorkRuntime.LootTargetKind);
                float distance = Vector3.Distance(worker.transform.position, lootPosition);
                SetState(zdo, distance <= GathererWorkRuntime.CollectRadius ? WorkerState.Collect : WorkerState.MoveToTarget, worker,
                    $"Jagdbeute einsammeln: {lootPrefab} ({distance:0.0} m)");
                return;
            }

            if (WorkerZdo.HasCargo(zdo))
            {
                TickHunterDelivery(worker, zdo);
                return;
            }
            SendHomeWhenNoWork(worker, zdo, string.IsNullOrEmpty(huntStatus) ? "keine Jagdbeute / kein Wild gefunden" : huntStatus);
        }

        private void TickHunterDelivery(Character worker, ZDO zdo)
        {
            if (!WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int cargoAmount) || cargoAmount <= 0)
            {
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.FindJob, worker, null);
                return;
            }

            // A Hunter living in a settlement should use the matching normal chest first. FieldDepot
            // remains the outpost fallback, mirroring the local-production rule used by Firekeeper.
            if (WorkerStorageRuntime.TryFindMatchingDepositContainer(worker, zdo, cargoPrefab, out _, out Vector3 storagePosition))
            {
                WorkerZdo.WriteTarget(zdo, cargoPrefab, storagePosition, WorkerStorageRuntime.DepositTargetKind);
                SetState(zdo, WorkerState.Deliver, worker, $"Jagdbeute {cargoPrefab} x{cargoAmount} -> normale Kiste");
                return;
            }

            if (WorkerStorageRuntime.TryFindFieldDepotForCargo(worker, zdo, cargoPrefab, out _, out Vector3 depotPosition))
            {
                WorkerZdo.WriteTarget(zdo, cargoPrefab, depotPosition, WorkerStorageRuntime.FieldDepotDepositTargetKind);
                SetState(zdo, WorkerState.Deliver, worker, $"Jagdbeute {cargoPrefab} x{cargoAmount} -> FieldDepot");
                return;
            }

            WorkerZdo.ClearTarget(zdo);
            SetState(zdo, WorkerState.NeedsResource, worker, $"Jagdbeute {cargoPrefab} x{cargoAmount}, aber keine passende Kiste/FieldDepot verfuegbar");
        }



        private void TickSmithInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            // v0.5.378: a Smith that spent time in an unloaded Workzone may have a short-lived
            // repair catch-up quota. Materialise that quota before normal physical arbitration.
            // The helper is dormant once the quota is gone, so this does not become a scanner.
            BackgroundMaintenanceRuntime.TryConsumePendingRepair(worker, zdo, out _);

            // Visual-only tool: appears only while this Smith is physically repairing a structure.
            RepairHammerVisualRuntime.SyncForWorker(worker, zdo);
            // v0.5.353: Smith production reservations are runtime-lightweight, but their station/output
            // identity is mirrored on the Worker ZDO so a save/reload can restore the claim before
            // another Smith plans the same missing bar.
            FirekeeperWorkRuntime.RefreshSmithProductionClaim(zdo);

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state == WorkerState.MoveToTarget || state == WorkerState.Work || state == WorkerState.Collect) return;
            if (state == WorkerState.Deliver && WorkerZdo.TryReadTarget(zdo, out _, out _, out _)) return;

            WorkerTaskFlags tasks = WorkerJobProfileRuntime.ReadTasks(zdo, WorkerRole.Smith);
            bool metalEnabled = (tasks & WorkerTaskFlags.Smith) != 0;
            bool repairEnabled = (tasks & WorkerTaskFlags.Repair) != 0;
            if (!metalEnabled && !repairEnabled)
            {
                FirekeeperWorkRuntime.ReleaseSmithProductionClaim(zdo);
                SendHomeWhenNoWork(worker, zdo, "Schmieden und Basis reparieren sind deaktiviert");
                return;
            }

            // Cargo cleanup always wins, even if the player has just disabled the metal task. A
            // worker must never strand real ore/fuel/bars in its inventory merely because its
            // priority profile changed while it was carrying something.
            if (WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int cargoAmount) && cargoAmount > 0)
            {
                if (FirekeeperWorkRuntime.TryFindNearestNeedingInputForSmithCargo(worker, zdo, workCenter, workRadius, worker.transform.position,
                        cargoPrefab, out Smelter inputMachine, out Vector3 inputMachinePos, out _))
                {
                    WorkerZdo.WriteTarget(zdo, SpeciesRoleRegistry.NormalizePrefabName(inputMachine.gameObject.name),
                        inputMachinePos, FirekeeperWorkRuntime.SmelterInputTargetKind);
                    bool inRange = WorkerInteractionRuntime.IsInsideAura(worker.transform.position, inputMachinePos);
                    SetState(zdo, inRange ? WorkerState.Work : WorkerState.MoveToTarget, worker,
                        $"Schmied: {cargoPrefab} x{cargoAmount} -> {SpeciesRoleRegistry.NormalizePrefabName(inputMachine.gameObject.name)}");
                    return;
                }

                // No ore station accepted the persisted production claim. From here on the cargo
                // is being re-routed (fuel or storage), so it must no longer reserve output demand.
                FirekeeperWorkRuntime.ReleaseSmithProductionClaim(zdo);

                if (FirekeeperWorkRuntime.TryFindNearestSmithFuelJob(worker, zdo, workCenter, workRadius, worker.transform.position,
                        cargoPrefab, out Smelter fuelMachine, out Vector3 fuelMachinePos, out _))
                {
                    WorkerZdo.WriteTarget(zdo, SpeciesRoleRegistry.NormalizePrefabName(fuelMachine.gameObject.name),
                        fuelMachinePos, FirekeeperWorkRuntime.SmelterTargetKind);
                    bool inRange = WorkerInteractionRuntime.IsInsideAura(worker.transform.position, fuelMachinePos);
                    SetState(zdo, inRange ? WorkerState.Work : WorkerState.MoveToTarget, worker,
                        $"Schmied: Brennstoff {cargoPrefab} x{cargoAmount} -> {SpeciesRoleRegistry.NormalizePrefabName(fuelMachine.gameObject.name)}");
                    return;
                }

                if (WorkerStorageRuntime.TryFindMatchingDepositContainer(worker, zdo, cargoPrefab, out _, out Vector3 storagePosition))
                {
                    WorkerZdo.WriteTarget(zdo, cargoPrefab, storagePosition, WorkerStorageRuntime.DepositTargetKind);
                    SetState(zdo, WorkerState.Deliver, worker, $"Schmied: {cargoPrefab} x{cargoAmount} -> passende Kiste");
                    return;
                }

                if (WorkerStorageRuntime.TryFindFieldDepotForCargo(worker, zdo, cargoPrefab, out _, out Vector3 depotPosition))
                {
                    WorkerZdo.WriteTarget(zdo, cargoPrefab, depotPosition, WorkerStorageRuntime.FieldDepotDepositTargetKind);
                    SetState(zdo, WorkerState.Deliver, worker, $"Schmied: {cargoPrefab} x{cargoAmount} -> FieldDepot");
                    return;
                }

                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.NeedsResource, worker,
                    $"Schmied traegt {cargoPrefab} x{cargoAmount}, aber keine passende Lagerkiste ist frei");
                return;
            }

            // No physical ore is in-flight and this tick is free to choose a new Smith/Repair job.
            // Drop any orphaned reservation left by an interrupted target before arbitration.
            FirekeeperWorkRuntime.ReleaseSmithProductionClaim(zdo);

            int smithPriority = metalEnabled
                ? WorkerJobProfileRuntime.GetPriority(zdo, WorkerRole.Smith, WorkerTaskFlags.Smith)
                : 5;
            int repairPriority = repairEnabled
                ? WorkerJobProfileRuntime.GetPriority(zdo, WorkerRole.Smith, WorkerTaskFlags.Repair)
                : 5;
            string lastSmithModule = zdo.GetString(SmithLastModuleKey, string.Empty);
            bool repairFirst = repairEnabled &&
                               (repairPriority < smithPriority ||
                                (repairPriority == smithPriority && string.Equals(lastSmithModule, "metal", StringComparison.Ordinal)));

            // v0.5.243: priority 4 is now the neutral default for every rest-station job. Smith and
            // Repair share one stable WorkerRole/WorkOrder, so the generic scheduler cannot arbitrate
            // between them. At equal priority alternate which module gets first refusal; otherwise a
            // continuously supplied furnace could starve repairs forever. Explicit unequal priorities
            // still win exactly as configured.
            if (repairFirst && TryAssignBaseRepair(worker, zdo, workCenter, workRadius, out _))
            {
                zdo.Set(SmithLastModuleKey, "repair");
                return;
            }

            if (metalEnabled)
            {
                // Finished bars are real world drops spawned by the machine. Collect those first so
                // furnaces do not pile up output on the ground.
                if (FirekeeperWorkRuntime.TryFindNearestOutputDrop(workCenter, workRadius, worker.transform.position,
                        out ItemDrop outputDrop, out string outputPrefab, out Vector3 outputPosition) && outputDrop != null)
                {
                    WorkerZdo.WriteTarget(zdo, outputPrefab, outputPosition, GathererTargetFinder.TargetKind);
                    float distance = Vector3.Distance(worker.transform.position, outputPosition);
                    SetState(zdo, distance <= GathererWorkRuntime.CollectRadius ? WorkerState.Collect : WorkerState.MoveToTarget,
                        worker, $"Schmied: fertiges Metall aufsammeln -> {outputPrefab}");
                    zdo.Set(SmithLastModuleKey, "metal");
                    return;
                }

                // Fill ore/input before fuel. The source finder only selects real normal/FieldDepot
                // inventory and honours Keep-One protection.
                if (FirekeeperWorkRuntime.TryFindNearestInputJob(worker, zdo, workCenter, workRadius, worker.transform.position,
                        out Smelter smelter, out Container inputSource, out Vector3 inputSourcePos, out string inputPrefab))
                {
                    WorkerZdo.WriteTarget(zdo, inputPrefab, inputSourcePos, FirekeeperWorkRuntime.InputSourceTargetKind);
                    bool inRange = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, inputSourcePos) <= WorkerStorageRuntime.ContainerAccessRadius;
                    SetState(zdo, inRange ? WorkerState.Collect : WorkerState.MoveToTarget, worker,
                        $"Schmied: {inputPrefab} aus {WorkerContainerRoleRuntime.DisplayName(WorkerContainerRoleRuntime.GetRole(inputSource))} holen");
                    zdo.Set(SmithLastModuleKey, "metal");
                    return;
                }

                if (FirekeeperWorkRuntime.TryFindNearestSmithFuelJob(worker, zdo, workCenter, workRadius, worker.transform.position,
                        null, out Smelter fuelTarget, out _, out string fuelPrefab) &&
                    FirekeeperWorkRuntime.TryFindFuelSource(worker, zdo, fuelPrefab, out Container fuelSource,
                        out Vector3 fuelSourcePos))
                {
                    WorkerZdo.WriteTarget(zdo, fuelPrefab, fuelSourcePos, FirekeeperWorkRuntime.FuelSourceTargetKind);
                    bool inRange = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, fuelSourcePos) <= WorkerStorageRuntime.ContainerAccessRadius;
                    SetState(zdo, inRange ? WorkerState.Collect : WorkerState.MoveToTarget, worker,
                        $"Schmied: Brennstoff {fuelPrefab} holen");
                    zdo.Set(SmithLastModuleKey, "metal");
                    return;
                }
            }

            string repairReason = "keine beschaedigte Struktur in der Workzone";
            if (repairEnabled && TryAssignBaseRepair(worker, zdo, workCenter, workRadius, out repairReason))
            {
                zdo.Set(SmithLastModuleKey, "repair");
                return;
            }

            string idleReason = metalEnabled
                ? "Schmied: Metallversorgung erledigt oder keine passenden Rohstoffe"
                : "Reparateur: keine Metallarbeit aktiviert";
            if (repairEnabled) idleReason += " | " + repairReason;
            if (metalEnabled) idleReason += " | " + FirekeeperWorkRuntime.GetDebugStatus();
            SendHomeWhenNoWork(worker, zdo, idleReason);
        }

        private bool TryAssignBaseRepair(Character worker, ZDO zdo, Vector3 workCenter, float workRadius, out string reason)
        {
            reason = "keine beschaedigte Struktur in der Workzone";
            if (!BaseRepairWorkRuntime.TryFindTarget(worker, workCenter, workRadius,
                    out WearNTear target, out Vector3 targetPoint, out string targetName, out reason) || target == null)
                return false;

            WorkerZdo.WriteTarget(zdo, targetName, targetPoint, BaseRepairWorkRuntime.TargetKind);
            float distance = WorkerInteractionRuntime.HorizontalDistance(worker.transform.position, targetPoint);
            bool inRange = distance <= WorkerInteractionRuntime.AccessRadius;
            SetState(zdo, inRange ? WorkerState.Work : WorkerState.MoveToTarget, worker,
                $"Basis reparieren: {reason}");
            return true;
        }

        private void TickButcherInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            // v0.5.378: materialise any unloaded-time slaughter capacity against the real
            // current pen population. This also catches offspring that appeared while the zone
            // was away, while still respecting juvenile protection and the adult Min reserve.
            BackgroundMaintenanceRuntime.TryConsumePendingButcher(worker, zdo, out _);

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state == WorkerState.MoveToTarget || state == WorkerState.Work) return;

            // Pen safety comes first: escaped tamed livestock are physically escorted back to the
            // feeding-trough enclosure before feeding or Min/Max culling is considered.
            if (LivestockPenRuntime.TryAssignEscapedAnimal(worker, zdo, workCenter, workRadius, out Character escaped, out string penReason) && escaped != null)
            {
                WorkerZdo.WriteTarget(zdo, SpeciesRoleRegistry.NormalizePrefabName(escaped.gameObject.name), escaped.transform.position, LivestockPenRuntime.AnimalTargetKind);
                float distance = WorkerInteractionRuntime.HorizontalDistance(worker.transform.position, escaped.transform.position);
                SetState(zdo, distance <= LivestockPenRuntime.GetArrivalRadius(LivestockPenRuntime.AnimalTargetKind) ? WorkerState.Work : WorkerState.MoveToTarget, worker, penReason);
                return;
            }

            // Livestock care comes before culling: keep SmartStorage trough/dispenser inventories
            // supplied from real local storage, then evaluate Min/Max population rules.
            if (LivestockFeedingRuntime.TryMaintain(worker, zdo, out string feedReason))
            {
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.FindJob, worker, feedReason);
                return;
            }

            if (LivestockButcherRuntime.TryFindSurplusTarget(worker, zdo, workCenter, workRadius, out Character target, out string reason) && target != null)
            {
                string name = SpeciesRoleRegistry.NormalizePrefabName(target.gameObject.name);
                WorkerZdo.WriteTarget(zdo, name, target.transform.position, LivestockButcherRuntime.TargetKind);
                float distance = Vector3.Distance(worker.transform.position, target.transform.position);
                SetState(zdo, distance <= LivestockButcherRuntime.WorkRange ? WorkerState.Work : WorkerState.MoveToTarget, worker, reason);
                return;
            }

            SendHomeWhenNoWork(worker, zdo, string.IsNullOrEmpty(reason) ? "Tierbestand innerhalb Min/Max" : reason);
        }

        private void TickBakerInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state == WorkerState.MoveToTarget || state == WorkerState.Work || state == WorkerState.Collect) return;

            // Deliver is owned by the shared physical storage navigation. If a persisted delivery
            // target disappeared, the recovery below re-routes the still-real worker cargo.
            if (state == WorkerState.Deliver && WorkerZdo.TryReadTarget(zdo, out _, out _, out _)) return;

            if (WorkerZdo.HasCargo(zdo) && BakerWorkRuntime.TryResumeCargo(worker, zdo, out string cargoReason))
            {
                _debugLog?.Invoke($"Cook cargo resume: worker={worker.gameObject.name}; {cargoReason}");
                return;
            }

            if (BakerWorkRuntime.TryPlanNext(worker, zdo, workCenter, workRadius, out string reason))
            {
                // Generic cooking may intentionally hold a partially assembled recipe in
                // NeedsResource while waiting for one missing ingredient. Do not overwrite that
                // state with MoveToTarget when no target exists; doing so used to create an idle
                // navigation loop and allowed the legacy production planner to steal the worker.
                WorkerState plannedState = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.FindJob);
                if (plannedState == WorkerState.NeedsResource)
                {
                    _debugLog?.Invoke($"Cook waiting: worker={worker.gameObject.name}; {reason}");
                    return;
                }

                if (WorkerZdo.TryReadTarget(zdo, out _, out _, out _))
                    SetState(zdo, WorkerState.MoveToTarget, worker, reason);
                else
                    SetState(zdo, WorkerState.FindJob, worker, reason);
                return;
            }

            SendHomeWhenNoWork(worker, zdo, reason + " | " + BakerWorkRuntime.GetDebug(zdo));
        }


        private void TickFirekeeperInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            // v0.5.27: the Surtling treats the stone oven as another magical fire-duty target.
            // Refuelling remains resource-free, exactly like campfires/hearths/torches; the fireball
            // is the visible work action. No fuel chest/cargo path is required for the oven.

            // v0.4.27 migration: a Surtling whose HomeAnchor is the dedicated FireHearth must
            // remain free to patrol its KeepFires workzone. Older builds could leave a separate
            // persistent FireAnchor behind, which made the AI stay on that fire forever.
            if (string.Equals(zdo.GetString(WorkerZdo.HomeAnchorType, string.Empty), WorkerHomeProfileCatalog.FireHearthKind, StringComparison.Ordinal) &&
                WorkerZdo.TryReadFireAnchor(zdo, out ZDOID legacyFireId, out _) && !legacyFireId.Equals(ZDOID.None))
            {
                FirekeeperAssignedFireRuntime.ReleaseWorkerFire(zdo);
                WorkerZdo.ClearTarget(zdo);
            }

            // Legacy optional mode: a specifically assigned Fireplace is an exclusive permanent post
            // only when the Surtling does NOT use the combined FireHearth home.
            if (FirekeeperAssignedFireRuntime.TryResolveAssignedFire(worker, zdo, out Vector3 managedFirePosition, out string managedFireName))
            {
                Vector3 flatFire = managedFirePosition; flatFire.y = 0f;
                Vector3 flatCenter = workCenter; flatCenter.y = 0f;
                if ((flatFire - flatCenter).sqrMagnitude > workRadius * workRadius)
                {
                    WorkerZdo.ClearTarget(zdo);
                    SetState(zdo, WorkerState.NeedsResource, worker, $"zugewiesenes Feuer '{managedFireName}' liegt ausserhalb der Workzone");
                    return;
                }

                WorkerZdo.WriteTarget(zdo, managedFireName, managedFirePosition, FirekeeperAssignedFireRuntime.ManagedFireTargetKind);
                bool inFireRange = WorkerInteractionRuntime.IsInsideAura(worker.transform.position, managedFirePosition);
                SetState(zdo, inFireRange ? WorkerState.Work : WorkerState.MoveToTarget, worker, $"Feuerwache -> {managedFireName}");
                return;
            }

            bool allowOvens = FirekeeperTaskFilterRuntime.AllowOvens(zdo);
            bool allowFires = FirekeeperTaskFilterRuntime.AllowFires(zdo);

            // v0.5.102: Surtling specialty = endless heat. A Firekeeper must NEVER consume
            // wood/coal from storage to run a furnace. If an older v0.5.101 save left real fuel in
            // the worker cargo, return it to storage first; never burn that cargo.
            if (WorkerZdo.TryReadCargo(zdo, out string firekeeperCargoPrefab, out int firekeeperCargoAmount) &&
                firekeeperCargoAmount > 0)
            {
                if (WorkerStorageRuntime.TryFindMatchingDepositContainer(worker, zdo, firekeeperCargoPrefab, out _, out Vector3 storagePosition))
                {
                    WorkerZdo.WriteTarget(zdo, firekeeperCargoPrefab, storagePosition, WorkerStorageRuntime.DepositTargetKind);
                    SetState(zdo, WorkerState.Deliver, worker, $"Feuerhueter: Alt-Brennstoff {firekeeperCargoPrefab} -> Lager (Surtling verbraucht keinen Brennstoff)");
                    return;
                }

                if (WorkerStorageRuntime.TryFindFieldDepotForCargo(worker, zdo, firekeeperCargoPrefab, out _, out Vector3 depotPosition))
                {
                    WorkerZdo.WriteTarget(zdo, firekeeperCargoPrefab, depotPosition, WorkerStorageRuntime.FieldDepotDepositTargetKind);
                    SetState(zdo, WorkerState.Deliver, worker, $"Feuerhueter: Alt-Brennstoff {firekeeperCargoPrefab} -> FieldDepot (kostenlose Hitze)");
                    return;
                }

                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.NeedsResource, worker,
                    $"Feuerhueter traegt Alt-Cargo {firekeeperCargoPrefab} x{firekeeperCargoAmount}; keine freie Rueckgabe-Kiste");
                return;
            }

            // Metal furnaces/blast furnaces are fueled directly by the Surtling's magical heat.
            // No fuel chest, no Keep-One transaction and no wood/coal cargo are involved.
            if (allowOvens &&
                FirekeeperWorkRuntime.TryFindNearestNeedingFuel(workCenter, workRadius, worker.transform.position,
                    null, out Smelter realFuelTarget, out Vector3 realFuelPosition, out _))
            {
                string machineName = SpeciesRoleRegistry.NormalizePrefabName(realFuelTarget.gameObject.name);
                WorkerZdo.WriteTarget(zdo, machineName, realFuelPosition, FirekeeperWorkRuntime.SmelterTargetKind);
                bool inFuelRange = WorkerInteractionRuntime.IsInsideAura(worker.transform.position, realFuelPosition);
                SetState(zdo, inFuelRange ? WorkerState.Work : WorkerState.MoveToTarget, worker,
                    $"Feuerhueter: kostenlose Surtling-Hitze -> {machineName}");
                return;
            }

            // Stone oven is a Smelter component, not a Fireplace, so it keeps its own magical
            // adapter. This consumes no item; metal furnaces above use the same free Surtling-heat principle.
            if (allowOvens && FirekeeperOvenRuntime.TryFindNearestNeedingFuel(workCenter, workRadius, worker.transform.position,
                    out Smelter oven, out Vector3 ovenPosition, out string ovenName))
            {
                WorkerZdo.WriteTarget(zdo, ovenName, ovenPosition, FirekeeperOvenRuntime.TargetKind);
                bool inOvenRange = WorkerInteractionRuntime.IsInsideAura(worker.transform.position, ovenPosition);
                SetState(zdo, inOvenRange ? WorkerState.Work : WorkerState.MoveToTarget, worker,
                    $"Steinofen kostenlos nachspeisen -> {ovenName}");
                return;
            }

            if (allowFires)
            {
                bool hasFire = FirekeeperZoneFireRuntime.TryFindNearestNeedingFuel(workCenter, workRadius, worker.transform.position,
                    out Fireplace fire, out Vector3 firePosition, out string fireName);

                if (hasFire)
                {
                    WorkerZdo.WriteTarget(zdo, fireName, firePosition, FirekeeperZoneFireRuntime.ZoneFireTargetKind);
                    bool inRange = WorkerInteractionRuntime.IsInsideAura(worker.transform.position, firePosition);
                    SetState(zdo, inRange ? WorkerState.Work : WorkerState.MoveToTarget, worker, $"Feuer nachspeisen -> {fireName}");
                    return;
                }
            }

            string filterStatus = $"Filter: Feuer/Fackeln={(allowFires ? "AN" : "AUS")}, Oefen/Maschinen={(allowOvens ? "AN" : "AUS")}";
            SendStandbyWhenNoWork(worker, zdo, WorkerRole.Firekeeper, WorkOrder.KeepFires, workCenter, workRadius,
                "alle freigegebenen Feuerquellen und Maschinen in der Workzone sind versorgt | " + filterStatus + " | " +
                FirekeeperWorkRuntime.GetDebugStatus() + " | " + FirekeeperZoneFireRuntime.GetDebugStatus() + " | " + FirekeeperOvenRuntime.GetDebugStatus());
        }

        private void TickStonemasonInsideArea(Character worker, ZDO zdo, Vector3 home, float workRadius)
        {
            int workerKey = worker.GetInstanceID();
            int maxToolTier = WorkerSkillCatalog.GetStoneworkTier(worker);
            StoneResourceLedgerRuntime.TickLoaded(worker, zdo);

            // Quarry cleanup belongs to the Stone Golem itself: before starting another deposit,
            // sweep up real loose Stone/ore ItemDrops in the assigned work area. The normal Gatherer
            // pickup runtime is reused so ownership, physical stack removal and persistent cargo
            // remain exactly the same as every other real ItemDrop collection path.
            if (!WorkerCargoRuntime.IsFull(worker, zdo))
            {
                GathererTargetResult looseStone = GathererTargetFinder.FindNearestAddable(
                    worker, zdo, home, workRadius, worker.transform.position,
                    drop => StoneTargetFinder.IsQuarryDrop(drop));
                if (looseStone.Target != null)
                {
                    _stoneTargets.Remove(workerKey);
                    StonemasonWorkRuntime.ClearBoundTarget(worker);
                    WorkerZdo.WriteTarget(zdo, looseStone.PrefabName, looseStone.Position, GathererTargetFinder.TargetKind);
                    float looseDistance = Vector3.Distance(worker.transform.position, looseStone.Position);
                    SetState(zdo,
                        looseDistance <= GathererWorkRuntime.CollectRadius ? WorkerState.Collect : WorkerState.MoveToTarget,
                        worker,
                        $"Abbau-Ertrag aufsammeln: {looseStone.PrefabName} ({looseDistance:0.0} m), cargo={WorkerCargoRuntime.GetSummary(worker, zdo)}");
                    return;
                }
            }

            // Once no more quarry material fits/is lying nearby, unload the complete real cargo
            // before mining the next target. This prevents the Golem from leaving Stone or ore
            // scattered across the quarry indefinitely.
            if (WorkerZdo.HasCargo(zdo))
            {
                TickStonemasonDelivery(worker, zdo);
                return;
            }

            if (StoneResourceLedgerRuntime.TryGetRemaining(zdo, out int ledgerRemaining) && ledgerRemaining <= 0)
            {
                StonemasonWorkRuntime.Reset(worker);
                SendHomeWhenNoWork(worker, zdo, "stone ledger depleted | " + StoneResourceLedgerRuntime.GetDebugSummary(zdo));
                return;
            }

            if (_stoneTargets.TryGetValue(workerKey, out StoneTarget currentTarget) &&
                StoneTargetFinder.IsUsable(currentTarget, home, workRadius, maxToolTier) &&
                StonemasonRockFilterRuntime.IsTargetAllowed(zdo, currentTarget))
            {
                if (ShouldSkipUnreachableStoneTarget(worker, workerKey, currentTarget))
                {
                    string fallbackName = StoneTargetFinder.NormalizePrefabName(currentTarget.GameObject.name);
                    LocalWorkFallbackRuntime.Activate(zdo, fallbackName, currentTarget.KindKey);
                    StonemasonWorkRuntime.BindTarget(worker, currentTarget);
                    WorkerZdo.WriteTarget(zdo, fallbackName, currentTarget.Position, currentTarget.KindKey);
                    SetState(zdo, WorkerState.Work, worker, "stone target unreachable -> local simulated work fallback");
                    return;
                }
                else
                {
                    Vector3 currentPosition = currentTarget.Position;
                    StonemasonWorkRuntime.BindTarget(worker, currentTarget);
                    WorkerZdo.WriteTarget(zdo, StoneTargetFinder.NormalizePrefabName(currentTarget.GameObject.name), currentPosition, currentTarget.KindKey);
                    float targetDistance = Vector3.Distance(worker.transform.position, ColliderGeometryRuntime.ClosestPointSafe(currentTarget, worker.transform.position));
                    SetState(zdo,
                        targetDistance <= StonemasonWorkRuntime.ApproachDistance ? WorkerState.Work : WorkerState.MoveToTarget,
                        worker,
                        null);
                    return;
                }
            }

            ClearLocalTargets(workerKey);
            SetState(zdo, WorkerState.FindJob, worker, null);

            StoneTargetResult result = StoneTargetFinder.FindNearest(home, workRadius, worker.transform.position, maxToolTier,
                target => StonemasonRockFilterRuntime.IsTargetAllowed(zdo, target) &&
                          ProductionDemandRuntime.AllowsStoneTarget(zdo, target) &&
                          IsStoneTargetAvailableForWorker(workerKey, target));
            if (result.BufferFull)
                _debugLog?.Invoke($"Stone scan collider buffer filled for {worker.gameObject.name}; hits={result.PhysicsHits}. Search stayed local.");

            if (result.Target == null)
            {
                StonemasonWorkRuntime.Reset(worker);
                SendHomeWhenNoWork(worker, zdo, $"stone scan empty | {StoneTargetFinder.GetLastDebugSummary()}");
                return;
            }

            _stoneTargets[workerKey] = result.Target;
            LocalWorkFallbackRuntime.Clear(zdo);
            StonemasonWorkRuntime.BindTarget(worker, result.Target);
            WorkerZdo.WriteTarget(zdo, StoneTargetFinder.NormalizePrefabName(result.Target.GameObject.name), result.Target.Position, result.Target.KindKey);
            float distance = Vector3.Distance(worker.transform.position, ColliderGeometryRuntime.ClosestPointSafe(result.Target, worker.transform.position));
            SetState(zdo,
                distance <= StonemasonWorkRuntime.ApproachDistance ? WorkerState.Work : WorkerState.MoveToTarget,
                worker,
                $"stone target {StoneTargetFinder.NormalizePrefabName(result.Target.GameObject.name)} ({distance:0.0} m), candidates={result.Candidates}, stoneTier={maxToolTier}");
        }

        private void TickStonemasonDelivery(Character worker, ZDO zdo)
        {
            if (!WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int cargoAmount) || cargoAmount <= 0)
            {
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.FindJob, worker, null);
                return;
            }

            // Prefer the player's normal sorted storage, then the local FieldDepot as quarry
            // fallback. Deposits stay physical and use the existing WorkerStorageRuntime.
            if (WorkerStorageRuntime.TryFindMatchingDepositContainer(worker, zdo, cargoPrefab, out _, out Vector3 storagePosition))
            {
                WorkerZdo.WriteTarget(zdo, cargoPrefab, storagePosition, WorkerStorageRuntime.DepositTargetKind);
                SetState(zdo, WorkerState.Deliver, worker, $"Steinmetz-Cargo {cargoPrefab} x{cargoAmount} -> passende Kiste");
                return;
            }

            if (WorkerStorageRuntime.TryFindFieldDepotForCargo(worker, zdo, cargoPrefab, out _, out Vector3 depotPosition))
            {
                WorkerZdo.WriteTarget(zdo, cargoPrefab, depotPosition, WorkerStorageRuntime.FieldDepotDepositTargetKind);
                SetState(zdo, WorkerState.Deliver, worker, $"Steinmetz-Cargo {cargoPrefab} x{cargoAmount} -> FieldDepot");
                return;
            }

            WorkerZdo.ClearTarget(zdo);
            SetState(zdo, WorkerState.NeedsResource, worker, $"Steinmetz traegt {cargoPrefab} x{cargoAmount}, aber keine passende Kiste/FieldDepot ist verfuegbar");
        }

        private void TickLumberjackInsideArea(Character worker, ZDO zdo, Vector3 home, float workRadius)
        {
            int workerKey = worker.GetInstanceID();
            int maxToolTier = WorkerSkillCatalog.GetWoodcutTier(worker);
            WoodResourceLedgerRuntime.TickLoaded(worker, zdo);
            // The ledger is only an unloaded-zone simulation budget. While this worker and its
            // work area are physically loaded, the real TreeBase/log/stump scan below is the
            // authority. A stale background count must never idle a worker beside visible trees.

            if (_woodTargets.TryGetValue(workerKey, out WoodTarget currentTarget) &&
                TreeTargetFinder.IsUsable(currentTarget, home, workRadius, maxToolTier, zdo))
            {
                if (ShouldSkipUnreachableWoodTarget(worker, workerKey, currentTarget))
                {
                    string fallbackName = TreeTargetFinder.NormalizePrefabName(currentTarget.GameObject.name);
                    LocalWorkFallbackRuntime.Activate(zdo, fallbackName, currentTarget.KindKey);
                    WorkerZdo.WriteTarget(zdo, fallbackName, currentTarget.Position, currentTarget.KindKey);
                    SetState(zdo, WorkerState.Work, worker, "wood target unreachable -> local simulated work fallback");
                    return;
                }
                else
                {
                    Vector3 currentPosition = currentTarget.Position;
                WorkerZdo.WriteTarget(zdo, TreeTargetFinder.NormalizePrefabName(currentTarget.GameObject.name), currentPosition, currentTarget.KindKey);
                float targetDistance = Vector3.Distance(worker.transform.position, ColliderGeometryRuntime.ClosestPointSafe(currentTarget, worker.transform.position));
                    SetState(zdo,
                        targetDistance <= WorkerNavigationRuntime.TargetStopDistance ? WorkerState.Work : WorkerState.MoveToTarget,
                        worker,
                        null);
                    return;
                }
            }

            ClearLocalTargets(workerKey);
            SetState(zdo, WorkerState.FindJob, worker, null);

            TreeTargetResult result = TreeTargetFinder.FindNearest(
                home, workRadius, worker.transform.position, maxToolTier, zdo,
                target => ProductionDemandRuntime.AllowsWoodTarget(zdo, target) &&
                          IsWoodTargetAvailableForWorker(workerKey, target));
            if (result.BufferFull)
                _debugLog?.Invoke($"Wood scan collider buffer filled for {worker.gameObject.name}; hits={result.PhysicsHits}. Search stayed local and did not fall back to a global object scan.");

            if (result.Target == null)
            {
                SendHomeWhenNoWork(worker, zdo, "wood scan empty");
                return;
            }

            _woodTargets[workerKey] = result.Target;
            LocalWorkFallbackRuntime.Clear(zdo);
            string previousTarget = zdo.GetString(WorkerZdo.TargetName, string.Empty);
            string previousKind = zdo.GetString(WorkerZdo.TargetKind, string.Empty);
            WorkerZdo.WriteTarget(zdo, result.PrefabName, result.Position, result.TargetKind);
            SetState(zdo, WorkerState.MoveToTarget, worker, null);

            if (!string.Equals(previousTarget, result.PrefabName, StringComparison.Ordinal) ||
                !string.Equals(previousKind, result.TargetKind, StringComparison.Ordinal))
            {
                float targetDistance = Vector3.Distance(worker.transform.position, ColliderGeometryRuntime.ClosestPointSafe(result.Target, worker.transform.position));
                _debugLog?.Invoke($"Lumberjack target selected: worker={worker.gameObject.name}, kind={result.TargetKind}, target={result.PrefabName}, distance={targetDistance:0.0}m, candidates={result.Candidates}, managedSlots={TreeTargetFinder.LastManagedSlotCount}, managedTrees={TreeTargetFinder.LastManagedTreeCandidates}, workRadius={workRadius:0.0}m, woodTier={maxToolTier}");
            }
        }

        private bool TryTickGathererInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            int workerKey = worker.GetInstanceID();

            // v0.4.62: Sammeln and Ernten are separate jobs. A GatherResources worker only
            // touches already-loose world ItemDrops. Mature plants belong exclusively to the
            // explicit Harvest job, so a collector never interrupts its pickup/deposit loop to
            // harvest a crop.
            if (WorkerZdo.HasCargo(zdo))
            {
                if (_gatherTargets.TryGetValue(workerKey, out ItemDrop carriedPassTarget) &&
                    ShouldTemporarilySkipBlockedGatherTarget(worker, workerKey, carriedPassTarget))
                {
                    _gatherTargets.Remove(workerKey);
                    WorkerZdo.ClearTarget(zdo);
                }

                if (!WorkerCargoRuntime.IsFull(worker, zdo))
                {
                    GathererTargetResult refill = GathererTargetFinder.FindNearestAddable(worker, zdo, workCenter, workRadius, worker.transform.position,
                        drop => IsGatherDropAvailableForRetry(drop) &&
                                GatherHarvestCategoryFilterRuntime.IsGatherDropAllowed(zdo, drop) &&
                                ProductionDemandRuntime.AllowsPrefab(zdo, GathererTargetFinder.GetCargoPrefabName(drop)));
                    if (refill.Target != null && WorkerCargoRuntime.GetMaxAddAmount(worker, zdo, refill.PrefabName, Mathf.Max(1, refill.Target.m_itemData.m_stack)) > 0)
                    {
                        _gatherTargets[workerKey] = refill.Target;
                        WorkerZdo.WriteTarget(zdo, refill.PrefabName, refill.Position, GathererTargetFinder.TargetKind);
                        float refillDistance = Vector3.Distance(worker.transform.position, refill.Position);
                        SetState(zdo, refillDistance <= WorkerNavigationRuntime.TargetStopDistance ? WorkerState.Collect : WorkerState.MoveToTarget, worker, $"collect only {WorkerCargoRuntime.GetSummary(worker, zdo)}");
                        return true;
                    }
                }

                _gatherTargets.Remove(workerKey);
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.Deliver, worker, $"collect only: no more addable drops / cargo ready {WorkerCargoRuntime.GetSummary(worker, zdo)} -> FieldDepot");
                return true;
            }

            if (_gatherTargets.TryGetValue(workerKey, out ItemDrop currentTarget) &&
                GathererTargetFinder.IsUsable(currentTarget, workCenter, workRadius) &&
                GatherHarvestCategoryFilterRuntime.IsGatherDropAllowed(zdo, currentTarget) &&
                ProductionDemandRuntime.AllowsPrefab(zdo, GathererTargetFinder.GetCargoPrefabName(currentTarget)) &&
                !ShouldTemporarilySkipBlockedGatherTarget(worker, workerKey, currentTarget))
            {
                Vector3 currentPosition = currentTarget.transform.position;
                string prefabName = GathererTargetFinder.GetCargoPrefabName(currentTarget);
                WorkerZdo.WriteTarget(zdo, prefabName, currentPosition, GathererTargetFinder.TargetKind);
                float targetDistance = Vector3.Distance(worker.transform.position, currentPosition);
                SetState(zdo, targetDistance <= WorkerNavigationRuntime.TargetStopDistance ? WorkerState.Collect : WorkerState.MoveToTarget, worker, "collect only");
                return true;
            }

            _gatherTargets.Remove(workerKey);

            if (_nextGatherSearchAt.TryGetValue(workerKey, out float nextGatherSearch) && Time.time < nextGatherSearch)
            {
                return false;
            }

            SetState(zdo, WorkerState.FindJob, worker, null);
            GathererTargetResult result = GathererTargetFinder.FindNearestAddable(worker, zdo, workCenter, workRadius, worker.transform.position,
                drop => IsGatherDropAvailableForRetry(drop) &&
                        GatherHarvestCategoryFilterRuntime.IsGatherDropAllowed(zdo, drop) &&
                        ProductionDemandRuntime.AllowsPrefab(zdo, GathererTargetFinder.GetCargoPrefabName(drop)));
            if (result.BufferFull)
                _debugLog?.Invoke($"Gatherer collider buffer filled for {worker.gameObject.name}; hits={result.PhysicsHits}. Search stayed local and did not use a global object scan.");

            if (result.Target != null)
            {
                _nextGatherSearchAt.Remove(workerKey);
                _gatherTargets[workerKey] = result.Target;
                WorkerZdo.WriteTarget(zdo, result.PrefabName, result.Position, GathererTargetFinder.TargetKind);
                SetState(zdo, WorkerState.MoveToTarget, worker, "collect only");
                float distance = Vector3.Distance(worker.transform.position, result.Position);
                _debugLog?.Invoke($"Gatherer target: worker={worker.gameObject.name}, item={result.PrefabName}, distance={distance:0.0}m, candidates={result.Candidates}, workRadius={workRadius:0.0}m, carry={WorkerSkillCatalog.GetGatherCarryCapacity(worker)}");
                return true;
            }

            _nextGatherSearchAt[workerKey] = Time.time + 4f;
            return false;
        }

        private void TickGathererProfileInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            WorkerTaskFlags tasks = WorkerJobProfileRuntime.ResolveEffectiveGathererTasks(zdo);
            bool canGather = (tasks & WorkerTaskFlags.Gather) != 0;
            bool canHarvest = (tasks & WorkerTaskFlags.Harvest) != 0;

            if (!canGather && !canHarvest)
            {
                SendHomeWhenNoWork(worker, zdo, "selected Gatherer tasks are not enabled on this Workzone");
                return;
            }

            // Cargo is always finished first. This prevents a combined Harvest+Gather worker from
            // abandoning a real load just because another ripe plant exists nearby.
            if (WorkerZdo.HasCargo(zdo))
            {
                if (canGather)
                {
                    // Cargo belongs to the Gather module until its complete manifest has been
                    // delivered. Keep the persisted module aligned with the real in-flight cycle.
                    zdo.Set(WorkerZdo.WorkOrderKey, (int)WorkOrder.GatherResources);
                    TryTickGathererInsideArea(worker, zdo, workCenter, workRadius);
                    return;
                }

                // If Sammeln was disabled while the worker still carried an older load, unload it
                // once instead of keeping stale cargo forever. No new loose drops are collected.
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.Deliver, worker, "task changed: unload existing cargo");
                return;
            }

            // Use the same shared selector as the other multi-job workers. The first module gets
            // first refusal; if it has no concrete target, the lower-priority module may still work.
            WorkOrder preferred = WorkerJobProfileRuntime.SelectBestAvailableOrder(zdo, WorkerRole.Gatherer,
                (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None), tasks);
            if (preferred == WorkOrder.GatherResources)
            {
                if (canGather && TryTickGathererInsideArea(worker, zdo, workCenter, workRadius))
                {
                    zdo.Set(WorkerZdo.WorkOrderKey, (int)WorkOrder.GatherResources);
                    return;
                }
                if (canHarvest && TryTickHarvesterTarget(worker, zdo, workCenter, workRadius))
                {
                    zdo.Set(WorkerZdo.WorkOrderKey, (int)WorkOrder.Harvest);
                    return;
                }
            }
            else
            {
                if (canHarvest && TryTickHarvesterTarget(worker, zdo, workCenter, workRadius))
                {
                    zdo.Set(WorkerZdo.WorkOrderKey, (int)WorkOrder.Harvest);
                    return;
                }
                if (canGather && TryTickGathererInsideArea(worker, zdo, workCenter, workRadius))
                {
                    zdo.Set(WorkerZdo.WorkOrderKey, (int)WorkOrder.GatherResources);
                    return;
                }
            }

            SendHomeWhenNoWork(worker, zdo, "no enabled gather/harvest work; waiting at home");
        }

        private bool TryTickHarvesterTarget(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            int workerKey = worker.GetInstanceID();
            if (_nextHarvestSearchAt.TryGetValue(workerKey, out float nextHarvestSearch) && Time.time < nextHarvestSearch)
                return false;

            GardenHarvestTargetResult harvest = default;
            bool usedCachedHarvest = false;
            if (_harvestSearchCacheUntil.TryGetValue(workerKey, out float cachedUntil) && Time.time <= cachedUntil &&
                _harvestSearchCache.TryGetValue(workerKey, out GardenHarvestTargetResult cachedHarvest) &&
                cachedHarvest.Target != null &&
                GardenHarvestTargetFinder.IsUsable(cachedHarvest.Target, workCenter, workRadius) &&
                GatherHarvestCategoryFilterRuntime.IsHarvestPickableAllowed(zdo, cachedHarvest.Target) &&
                ProductionDemandRuntime.AllowsHarvestTarget(zdo, cachedHarvest.Target))
            {
                harvest = cachedHarvest;
                usedCachedHarvest = true;
            }
            else
            {
                harvest = GardenHarvestTargetFinder.FindNearest(workCenter, workRadius, worker.transform.position,
                    pickable => GatherHarvestCategoryFilterRuntime.IsHarvestPickableAllowed(zdo, pickable) &&
                                ProductionDemandRuntime.AllowsHarvestTarget(zdo, pickable));
            }
            _harvestSearchCache.Remove(workerKey);
            _harvestSearchCacheUntil.Remove(workerKey);

            if (!usedCachedHarvest && harvest.BufferFull)
                _debugLog?.Invoke($"Garden harvest collider buffer filled for {worker.gameObject.name}; hits={harvest.PhysicsHits}.");

            if (harvest.Target != null)
            {
                string harvestIdentity = GardenHarvestTargetFinder.GetIdentityName(harvest.Target);
                WorkerZdo.WriteTarget(zdo, harvestIdentity, harvest.Position, GardenHarvestTargetFinder.TargetKind);
                SetState(zdo, WorkerState.MoveToTarget, worker, "harvest task");
                float harvestDistance = Vector3.Distance(worker.transform.position, harvest.Position);
                _nextHarvestSearchAt.Remove(workerKey);
                _debugLog?.Invoke($"Harvester target: worker={worker.gameObject.name}, crop={harvestIdentity}, distance={harvestDistance:0.0}m, candidates={harvest.Candidates}");
                return true;
            }

            _nextHarvestSearchAt[workerKey] = Time.time + 4f;
            return false;
        }

        private bool IsWoodTargetAvailableForWorker(int workerKey, WoodTarget target)
        {
            if (target == null || target.GameObject == null) return false;
            if (!_woodBlockedTargetKey.TryGetValue(workerKey, out int blockedKey)) return true;

            if (!_woodBlockedTargetUntil.TryGetValue(workerKey, out float blockedUntil) || Time.time >= blockedUntil)
            {
                _woodBlockedTargetKey.Remove(workerKey);
                _woodBlockedTargetUntil.Remove(workerKey);
                return true;
            }

            return target.GameObject.GetInstanceID() != blockedKey;
        }

        private void ResetWoodTargetProgress(int workerKey, bool clearBlockedTarget = false)
        {
            _woodTrackedTargetKey.Remove(workerKey);
            _woodBestDistance.Remove(workerKey);
            _woodLastProgressAt.Remove(workerKey);
            _woodPositionAtLastProgress.Remove(workerKey);
            if (clearBlockedTarget)
            {
                _woodBlockedTargetKey.Remove(workerKey);
                _woodBlockedTargetUntil.Remove(workerKey);
            }
        }

        private bool ShouldSkipUnreachableWoodTarget(Character worker, int workerKey, WoodTarget target)
        {
            if (worker == null || target == null || target.GameObject == null)
            {
                ResetWoodTargetProgress(workerKey);
                return false;
            }

            float distance = Vector3.Distance(worker.transform.position, ColliderGeometryRuntime.ClosestPointSafe(target, worker.transform.position));
            if (distance <= WorkerNavigationRuntime.TargetStopDistance + 0.35f)
            {
                ResetWoodTargetProgress(workerKey);
                return false;
            }

            int targetKey = target.GameObject.GetInstanceID();
            if (!_woodTrackedTargetKey.TryGetValue(workerKey, out int trackedKey) || trackedKey != targetKey)
            {
                _woodTrackedTargetKey[workerKey] = targetKey;
                _woodBestDistance[workerKey] = distance;
                _woodLastProgressAt[workerKey] = Time.time;
                _woodPositionAtLastProgress[workerKey] = worker.transform.position;
                return false;
            }

            if (!_woodBestDistance.TryGetValue(workerKey, out float bestDistance) || distance < bestDistance - 0.5f)
            {
                _woodBestDistance[workerKey] = distance;
                _woodLastProgressAt[workerKey] = Time.time;
                _woodPositionAtLastProgress[workerKey] = worker.transform.position;
                return false;
            }

            if (!_woodLastProgressAt.TryGetValue(workerKey, out float lastProgressAt))
            {
                _woodLastProgressAt[workerKey] = Time.time;
                _woodPositionAtLastProgress[workerKey] = worker.transform.position;
                return false;
            }

            float stalledSeconds = Time.time - lastProgressAt;
            Vector3 progressAnchor = _woodPositionAtLastProgress.TryGetValue(workerKey, out Vector3 anchor)
                ? anchor
                : worker.transform.position;
            Vector3 moved = worker.transform.position - progressAnchor;
            moved.y = 0f;

            string nav = WorkerNavigationRuntime.GetDebugStatus(worker) ?? string.Empty;
            bool navigatorReportsBlocked = nav.IndexOf("BLOCKED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           nav.IndexOf("path miss", StringComparison.OrdinalIgnoreCase) >= 0;
            bool physicallyStuck = stalledSeconds >= 12f && moved.sqrMagnitude <= 2f * 2f;
            bool hardNoProgress = stalledSeconds >= 25f;

            if (!navigatorReportsBlocked && !physicallyStuck && !hardNoProgress)
                return false;
            if (navigatorReportsBlocked && stalledSeconds < 8f)
                return false;

            // Per-worker temporary skip: another Lumberjack may still use this target normally.
            _woodBlockedTargetKey[workerKey] = targetKey;
            _woodBlockedTargetUntil[workerKey] = Time.time + 60f;
            ResetWoodTargetProgress(workerKey);
            _debugLog?.Invoke($"Lumberjack skipped unreachable {target.KindKey}: worker={worker.gameObject.name}, target={TreeTargetFinder.NormalizePrefabName(target.GameObject.name)}, distance={distance:0.0}m, stalled={stalledSeconds:0}s, nav={nav}");
            return true;
        }

        private bool IsStoneTargetAvailableForWorker(int workerKey, StoneTarget target)
        {
            if (target == null || target.GameObject == null) return false;
            if (!_stoneBlockedTargetKey.TryGetValue(workerKey, out int blockedKey)) return true;

            if (!_stoneBlockedTargetUntil.TryGetValue(workerKey, out float blockedUntil) || Time.time >= blockedUntil)
            {
                _stoneBlockedTargetKey.Remove(workerKey);
                _stoneBlockedTargetUntil.Remove(workerKey);
                return true;
            }

            return target.IdentityKey != blockedKey;
        }

        private void ResetStoneTargetProgress(int workerKey, bool clearBlockedTarget = false)
        {
            _stoneTrackedTargetKey.Remove(workerKey);
            _stoneBestDistance.Remove(workerKey);
            _stoneLastProgressAt.Remove(workerKey);
            _stonePositionAtLastProgress.Remove(workerKey);
            if (clearBlockedTarget)
            {
                _stoneBlockedTargetKey.Remove(workerKey);
                _stoneBlockedTargetUntil.Remove(workerKey);
            }
        }

        private bool ShouldSkipUnreachableStoneTarget(Character worker, int workerKey, StoneTarget target)
        {
            if (worker == null || target == null || target.GameObject == null)
            {
                ResetStoneTargetProgress(workerKey);
                return false;
            }

            float distance = Vector3.Distance(worker.transform.position, ColliderGeometryRuntime.ClosestPointSafe(target, worker.transform.position));
            if (distance <= WorkerNavigationRuntime.TargetStopDistance + 0.35f)
            {
                ResetStoneTargetProgress(workerKey);
                return false;
            }

            int targetKey = target.IdentityKey;
            if (!_stoneTrackedTargetKey.TryGetValue(workerKey, out int trackedKey) || trackedKey != targetKey)
            {
                _stoneTrackedTargetKey[workerKey] = targetKey;
                _stoneBestDistance[workerKey] = distance;
                _stoneLastProgressAt[workerKey] = Time.time;
                _stonePositionAtLastProgress[workerKey] = worker.transform.position;
                return false;
            }

            if (!_stoneBestDistance.TryGetValue(workerKey, out float bestDistance) || distance < bestDistance - 0.5f)
            {
                _stoneBestDistance[workerKey] = distance;
                _stoneLastProgressAt[workerKey] = Time.time;
                _stonePositionAtLastProgress[workerKey] = worker.transform.position;
                return false;
            }

            if (!_stoneLastProgressAt.TryGetValue(workerKey, out float lastProgressAt))
            {
                _stoneLastProgressAt[workerKey] = Time.time;
                _stonePositionAtLastProgress[workerKey] = worker.transform.position;
                return false;
            }

            float stalledSeconds = Time.time - lastProgressAt;
            Vector3 progressAnchor = _stonePositionAtLastProgress.TryGetValue(workerKey, out Vector3 anchor)
                ? anchor
                : worker.transform.position;
            Vector3 moved = worker.transform.position - progressAnchor;
            moved.y = 0f;

            string nav = WorkerNavigationRuntime.GetDebugStatus(worker) ?? string.Empty;
            bool navigatorReportsBlocked = nav.IndexOf("BLOCKED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           nav.IndexOf("path miss", StringComparison.OrdinalIgnoreCase) >= 0;
            bool physicallyStuck = stalledSeconds >= 12f && moved.sqrMagnitude <= 2f * 2f;
            bool hardNoProgress = stalledSeconds >= 25f;

            if (!navigatorReportsBlocked && !physicallyStuck && !hardNoProgress)
                return false;
            if (navigatorReportsBlocked && stalledSeconds < 8f)
                return false;

            _stoneBlockedTargetKey[workerKey] = targetKey;
            _stoneBlockedTargetUntil[workerKey] = Time.time + 60f;
            ResetStoneTargetProgress(workerKey);
            _debugLog?.Invoke($"Stonemason skipped unreachable {target.KindKey}: worker={worker.gameObject.name}, target={StoneTargetFinder.NormalizePrefabName(target.GameObject.name)}, distance={distance:0.0}m, stalled={stalledSeconds:0}s, nav={nav}");
            return true;
        }

        private bool IsGatherDropAvailableForRetry(ItemDrop drop)
        {
            if (drop == null) return false;
            int dropKey = drop.GetInstanceID();
            if (!_gatherDropBlockedUntil.TryGetValue(dropKey, out float blockedUntil)) return true;
            if (Time.time < blockedUntil) return false;
            _gatherDropBlockedUntil.Remove(dropKey);
            return true;
        }

        private void ResetGatherTargetProgress(int workerKey)
        {
            _gatherTrackedDropKey.Remove(workerKey);
            _gatherBestDistance.Remove(workerKey);
            _gatherLastDistanceProgressAt.Remove(workerKey);
            _gatherLastWorkerPosition.Remove(workerKey);
            _gatherLastWorkerMovementAt.Remove(workerKey);
        }

        private bool ShouldTemporarilySkipBlockedGatherTarget(Character worker, int workerKey, ItemDrop target)
        {
            if (worker == null || target == null)
            {
                _gatherBlockedSince.Remove(workerKey);
                ResetGatherTargetProgress(workerKey);
                return false;
            }

            int dropKey = target.GetInstanceID();

            // Water targets are never valid Gatherer work. This is checked in the finder too, but
            // keep this guard for a target that fell/rolled into water after it was selected.
            if (GathererTargetFinder.IsInWater(target))
            {
                _gatherDropBlockedUntil[dropKey] = Time.time + 60f;
                _gatherBlockedSince.Remove(workerKey);
                ResetGatherTargetProgress(workerKey);
                _debugLog?.Invoke($"Gatherer ignored water drop: worker={worker.gameObject.name}, item={GathererTargetFinder.GetCargoPrefabName(target)}");
                return true;
            }

            float distance = Vector3.Distance(worker.transform.position, target.transform.position);
            if (distance <= WorkerNavigationRuntime.TargetStopDistance + 0.35f)
            {
                _gatherBlockedSince.Remove(workerKey);
                ResetGatherTargetProgress(workerKey);
                return false;
            }

            if (!_gatherTrackedDropKey.TryGetValue(workerKey, out int trackedDropKey) || trackedDropKey != dropKey)
            {
                _gatherTrackedDropKey[workerKey] = dropKey;
                _gatherBestDistance[workerKey] = distance;
                _gatherLastDistanceProgressAt[workerKey] = Time.time;
                _gatherLastWorkerPosition[workerKey] = worker.transform.position;
                _gatherLastWorkerMovementAt[workerKey] = Time.time;
                _gatherBlockedSince.Remove(workerKey);
                return false;
            }

            if (!_gatherBestDistance.TryGetValue(workerKey, out float bestDistance) || distance < bestDistance - 0.75f)
            {
                _gatherBestDistance[workerKey] = distance;
                _gatherLastDistanceProgressAt[workerKey] = Time.time;
            }

            Vector3 workerPosition = worker.transform.position;
            if (!_gatherLastWorkerPosition.TryGetValue(workerKey, out Vector3 lastWorkerPosition) ||
                (workerPosition - lastWorkerPosition).sqrMagnitude >= 0.75f * 0.75f)
            {
                _gatherLastWorkerPosition[workerKey] = workerPosition;
                _gatherLastWorkerMovementAt[workerKey] = Time.time;
            }

            string nav = WorkerNavigationRuntime.GetDebugStatus(worker) ?? string.Empty;
            bool navBlocked = nav.IndexOf("BLOCKED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              nav.IndexOf("path miss", StringComparison.OrdinalIgnoreCase) >= 0;
            if (navBlocked)
            {
                if (!_gatherBlockedSince.ContainsKey(workerKey))
                    _gatherBlockedSince[workerKey] = Time.time;
            }
            else
            {
                _gatherBlockedSince.Remove(workerKey);
            }

            _gatherLastDistanceProgressAt.TryGetValue(workerKey, out float lastDistanceProgressAt);
            _gatherLastWorkerMovementAt.TryGetValue(workerKey, out float lastWorkerMovementAt);
            bool blockedForTenSeconds = _gatherBlockedSince.TryGetValue(workerKey, out float blockedSince) &&
                                        Time.time - blockedSince >= 10f;
            bool stationaryWithoutProgress = Time.time - lastDistanceProgressAt >= 10f &&
                                             Time.time - lastWorkerMovementAt >= 10f;
            bool movingButNeverCloser = Time.time - lastDistanceProgressAt >= 20f;

            if (!blockedForTenSeconds && !stationaryWithoutProgress && !movingButNeverCloser)
                return false;

            // Leave the bad drop alone long enough to let the Gatherer work through other drops.
            // The cooldown is temporary in case physics later moves the item back onto reachable land.
            _gatherDropBlockedUntil[dropKey] = Time.time + 60f;
            _gatherBlockedSince.Remove(workerKey);
            ResetGatherTargetProgress(workerKey);
            _debugLog?.Invoke($"Gatherer skipped unreachable drop: worker={worker.gameObject.name}, item={GathererTargetFinder.GetCargoPrefabName(target)}, distance={distance:0.0}m, nav={nav}");
            return true;
        }

        private void TickGathererDelivery(Character worker, ZDO zdo, Vector3 workCenter)
        {
            int workerKey = worker.GetInstanceID();
            _gatherTargets.Remove(workerKey);

            if (!WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int cargoAmount))
            {
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.FindJob, worker, null);
                return;
            }

            // v0.1.96 migration/recovery: older builds could persist SmartReplant's world-wrapper
            // prefab name as cargo. Convert it to the real inventory ItemDrop before routing so
            // FieldDepot capacity checks and the final Inventory.AddItem use the same identity.
            if (GardenWorkRuntime.TryResolveGatherableItemPrefab(cargoPrefab, out string canonicalCargo) &&
                !string.IsNullOrEmpty(canonicalCargo) &&
                !string.Equals(canonicalCargo, cargoPrefab, StringComparison.OrdinalIgnoreCase))
            {
                _debugLog?.Invoke($"Gatherer cargo identity normalized: {cargoPrefab} -> {canonicalCargo} x{cargoAmount}");
                cargoPrefab = canonicalCargo;
                WorkerZdo.SetCargo(zdo, cargoPrefab, cargoAmount);
            }

            if (WorkerStorageRuntime.TryFindFieldDepotForCargo(worker, zdo, cargoPrefab, out _, out Vector3 depotPosition))
            {
                WorkerZdo.WriteTarget(zdo, cargoPrefab, depotPosition, WorkerStorageRuntime.FieldDepotDepositTargetKind);
                SetState(zdo, WorkerState.Deliver, worker, "carrying gathered resources to marked FieldDepot");
                return;
            }

            // Compatibility fallback while a settlement has not marked a FieldDepot yet.
            if (WorkerStorageRuntime.TryFindMatchingDepositContainer(worker, zdo, cargoPrefab, out _, out Vector3 storagePosition))
            {
                WorkerZdo.WriteTarget(zdo, cargoPrefab, storagePosition, WorkerStorageRuntime.DepositTargetKind);
                SetState(zdo, WorkerState.Deliver, worker, "no FieldDepot marked; compatibility route to matching base storage");
                return;
            }

            // Never leave a loaded Gatherer in Deliver without a target. Empty normal chests are
            // valid overflow storage when no matching chest or marked FieldDepot is available.
            if (WorkerStorageRuntime.TryFindAnyNormalDepositContainer(worker, zdo, cargoPrefab, out _, out Vector3 overflowPosition))
            {
                WorkerZdo.WriteTarget(zdo, cargoPrefab, overflowPosition, WorkerStorageRuntime.OverflowDepositTargetKind);
                SetState(zdo, WorkerState.Deliver, worker, "no FieldDepot/matching chest; carrying cargo to free normal chest");
                return;
            }

            WorkerZdo.ClearTarget(zdo);
            SetState(zdo, WorkerState.NeedsResource, worker, $"cargo {cargoPrefab} x{cargoAmount} retained; needs a FieldDepot or free normal chest");
        }

        private void TickHaulerInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            if (AirHaulerWorkRuntime.IsAirHauler(worker))
            {
                TickAirHaulerInsideArea(worker, zdo, workCenter, workRadius);
                return;
            }

            if (WorkerZdo.HasCargo(zdo))
            {
                TickHaulerDelivery(worker, zdo);
                return;
            }

            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            WorkerState currentState = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);

            // Keep an already selected physical source while travelling/collecting. In particular
            // an Export target owns a HaulerDemandClaim; rescanning all requests/Export chests on
            // every AI decision would both waste CPU and churn the reservation.
            if ((currentState == WorkerState.MoveToTarget || currentState == WorkerState.Collect) &&
                WorkerZdo.TryReadTarget(zdo, out _, out string haulTargetKind, out _) &&
                (string.Equals(haulTargetKind, WorkerStorageRuntime.ExportHaulSourceTargetKind, StringComparison.Ordinal) ||
                 string.Equals(haulTargetKind, WorkerStorageRuntime.HaulSourceTargetKind, StringComparison.Ordinal)))
            {
                if (string.Equals(haulTargetKind, WorkerStorageRuntime.ExportHaulSourceTargetKind, StringComparison.Ordinal))
                    HaulerDemandClaimRuntime.Touch(zdo);
                return;
            }

            // Once an empty transporter has completed the Base side of its cycle, ReturnHome must
            // survive subsequent AI decision ticks until Navigation actually reaches the outpost.
            // Without this guard the Haul decision would immediately overwrite ReturnHome.
            if (currentState == WorkerState.ReturnHome &&
                WorkerStorageRuntime.HorizontalDistance(worker.transform.position, home) > WorkerNavigationRuntime.HomeStopDistance + 0.5f)
            {
                HaulerDemandClaimRuntime.Release(zdo, "return-home");
                return;
            }

            // Any non-travelling decision is a replan. Free a stale pre-pickup reservation now;
            // the next outbound source selection below will acquire the exact new slice/source.
            HaulerDemandClaimRuntime.Release(zdo, "replan");

            bool baseTransporter = WorkerLogisticsRuntime.IsBaseTransporter(zdo);
            if (!baseTransporter && !WorkerLogisticsRuntime.TryFindAssignedOutpostFieldDepot(worker, zdo, out _, out _))
            {
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.Idle, worker, "Transport-Aussenposten braucht ein FieldDepot innerhalb seiner Workzone");
                return;
            }

            bool atOutpost = !baseTransporter && WorkerLogisticsRuntime.IsInsideAssignedOutpost(zdo, worker.transform.position, 5f);
            SetState(zdo, WorkerState.FindJob, worker, null);

            if (atOutpost)
            {
                // Field side: harvest/material return load first. This is the preferred loaded leg
                // toward Base and protects all active supply reserves / keep-one anchors.
                if (WorkerStorageRuntime.TryFindLocalReturnHaulSource(worker, zdo, 22f, out _, out string returnPrefab, out int returnAmount, out Vector3 returnSourcePosition))
                {
                    WorkerZdo.SetCargoRoute(zdo, WorkerLogisticsRuntime.RouteHaulInbound, returnSourcePosition);
                    WorkerZdo.WriteTarget(zdo, returnPrefab, returnSourcePosition, WorkerStorageRuntime.HaulSourceTargetKind);
                    float returnDistance = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, returnSourcePosition);
                    SetState(zdo, returnDistance <= WorkerStorageRuntime.ContainerAccessRadius ? WorkerState.Collect : WorkerState.MoveToTarget,
                        worker, $"Aussenposten -> Base: {returnPrefab} x{returnAmount} FieldDepot -> Import");
                    return;
                }

                // If the Base sorter has already staged requested supplies, head to Export. Requests
                // are scoped to this Hauler's own Workzone, so multiple farms cannot steal each
                // other's deliveries.
                if (WorkerLogisticsRuntime.TryFindExportHaulSourceForPhysical(worker, zdo, out _, out string outboundPrefab, out int outboundAmount, out Vector3 exportPosition, out Vector3 depotPosition, out ZDOID depotId, out string depotWorkAreaId))
                {
                    WorkerZdo.SetCargoRoute(zdo, WorkerLogisticsRuntime.RouteHaulOutbound, depotPosition, depotId, depotWorkAreaId);
                    WorkerZdo.WriteTarget(zdo, outboundPrefab, exportPosition, WorkerStorageRuntime.ExportHaulSourceTargetKind);
                    float distance = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, exportPosition);
                    SetState(zdo, distance <= WorkerStorageRuntime.ContainerAccessRadius ? WorkerState.Collect : WorkerState.MoveToTarget,
                        worker, $"Base -> Aussenposten vorbereitet: {outboundPrefab} x{outboundAmount} aus Export holen");
                    return;
                }

                // Catch a FieldDepot that is inside the assigned Workzone but not within the short
                // local pendulum radius around the Worker. This search is still strictly outpost-local.
                if (WorkerStorageRuntime.TryFindHaulSource(worker, zdo, out _, out string cargoPrefab, out int amount, out Vector3 sourcePosition))
                {
                    WorkerZdo.SetCargoRoute(zdo, WorkerLogisticsRuntime.RouteHaulInbound, default);
                    WorkerZdo.WriteTarget(zdo, cargoPrefab, sourcePosition, WorkerStorageRuntime.HaulSourceTargetKind);
                    float distance = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, sourcePosition);
                    SetState(zdo, distance <= WorkerStorageRuntime.ContainerAccessRadius ? WorkerState.Collect : WorkerState.MoveToTarget,
                        worker, $"Aussenposten -> Base: {cargoPrefab} x{amount} FieldDepot -> Import");
                    return;
                }

                SendHomeWhenNoWork(worker, zdo, "Aussenposten bereit: keine Fracht / kein fertiger Export");
                return;
            }

            // Base side: a dedicated Base transporter may claim any remote ground Outpost demand;
            // an Outpost transporter remains restricted to its own WorkArea. Both use the same real
            // Export stock / cargo route and therefore cannot duplicate items.
            if (WorkerLogisticsRuntime.TryFindExportHaulSourceForPhysical(worker, zdo, out _, out string baseOutboundPrefab, out int baseOutboundAmount, out Vector3 baseExportPosition, out Vector3 baseDepotPosition, out ZDOID baseDepotId, out string baseDepotWorkAreaId))
            {
                WorkerZdo.SetCargoRoute(zdo, WorkerLogisticsRuntime.RouteHaulOutbound, baseDepotPosition, baseDepotId, baseDepotWorkAreaId);
                WorkerZdo.WriteTarget(zdo, baseOutboundPrefab, baseExportPosition, WorkerStorageRuntime.ExportHaulSourceTargetKind);
                float distance = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, baseExportPosition);
                string scopeText = baseTransporter ? "naechster Aussenposten" : "eigener Aussenposten";
                SetState(zdo, distance <= WorkerStorageRuntime.ContainerAccessRadius ? WorkerState.Collect : WorkerState.MoveToTarget,
                    worker, $"Base -> {scopeText}: {baseOutboundPrefab} x{baseOutboundAmount} aus Export holen");
                return;
            }

            WorkerZdo.ClearTarget(zdo);
            if (baseTransporter)
                SetState(zdo, WorkerState.ReturnHome, worker, "Base-Transporter: keine offene Aussenposten-Lieferung; zurueck zur Base");
            else
                SetState(zdo, WorkerState.ReturnHome, worker, "Base-Zyklus fertig; leer zurueck zum eigenen Aussenposten");
        }


        private void TickAirHaulerInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);

            // A Drake can become physical again at Valheim's load boundary (~150 m) while its
            // assigned home/outpost is still unloaded. The assignment is persistent, so do not
            // destroy an active air route merely because the home Container component is absent.
            // Actual pickup/deposit operations still require the real loaded Container.
            if (!AirHaulerWorkRuntime.HasAssignedAirDepotEndpoint(worker, zdo, out _))
            {
                BackgroundWorkerScheduler.ClearAirEmptyPickupClaim(zdo);
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.Idle, worker, "Air Hauler braucht ein Erweitertes Felddepot innerhalb seiner Workzone");
                return;
            }

            if (WorkerZdo.TryReadCargo(zdo, out string carriedPrefab, out int carriedAmount))
            {
                // Once the cargo is physically on the Drake, normal in-transit accounting is
                // authoritative. The empty-pickup reservation must no longer count.
                BackgroundWorkerScheduler.ClearAirEmptyPickupClaim(zdo);
                WorkerZdo.TryReadCargoRoute(zdo, out string route, out Vector3 routeTarget);
                if (string.Equals(route, AirHaulerWorkRuntime.RouteOutbound, StringComparison.Ordinal))
                {
                    if (AirHaulerWorkRuntime.TryFindOutpostDestination(worker, zdo, carriedPrefab, routeTarget, out Vector3 outpostDepot))
                    {
                        WorkerZdo.WriteTarget(zdo, carriedPrefab, outpostDepot, AirHaulerWorkRuntime.OutpostDepositTargetKind);
                        SetState(zdo, WorkerState.Deliver, worker, $"Luftfracht: {carriedPrefab} x{carriedAmount} -> Aussenposten");
                        return;
                    }
                    WorkerZdo.ClearTarget(zdo);
                    SetState(zdo, WorkerState.NeedsResource, worker, $"Luftfracht {carriedPrefab}: erweitertes Aussenposten-Depot nicht verfuegbar");
                    return;
                }

                if (AirHaulerWorkRuntime.TryFindBaseDestination(worker, zdo, carriedPrefab, out Vector3 baseDepot))
                {
                    WorkerZdo.SetCargoRoute(zdo, AirHaulerWorkRuntime.RouteInbound, default);
                    WorkerZdo.WriteTarget(zdo, carriedPrefab, baseDepot, AirHaulerWorkRuntime.BaseDepositTargetKind);
                    SetState(zdo, WorkerState.Deliver, worker, $"Luftfracht: {carriedPrefab} x{carriedAmount} -> Base AirDepot");
                    return;
                }
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.NeedsResource, worker, $"Luftfracht {carriedPrefab}: kein Erweitertes Felddepot an der Base verfuegbar");
                return;
            }

            WorkerState currentState = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);

            // Keep an already selected air pickup endpoint while the Drake is travelling/collecting.
            // Re-running air demand + depot discovery every decision tick was both expensive and
            // allowed two physical Drakes to churn the same pre-pickup reservation.
            if ((currentState == WorkerState.MoveToTarget || currentState == WorkerState.Collect) &&
                WorkerZdo.TryReadTarget(zdo, out _, out string currentAirTargetKind, out _) &&
                (string.Equals(currentAirTargetKind, AirHaulerWorkRuntime.BaseSourceTargetKind, StringComparison.Ordinal) ||
                 string.Equals(currentAirTargetKind, AirHaulerWorkRuntime.OutpostSourceTargetKind, StringComparison.Ordinal)))
            {
                return;
            }

            if (currentState == WorkerState.ReturnHome &&
                WorkerStorageRuntime.HorizontalDistance(worker.transform.position, home) > WorkerNavigationRuntime.HomeStopDistance + 0.5f)
            {
                BackgroundWorkerScheduler.ClearAirEmptyPickupClaim(zdo);
                return;
            }

            // Any other empty-air decision is a replan. Drop a stale pre-pickup claim before
            // asking the demand/cache layer for a new source.
            BackgroundWorkerScheduler.ClearAirEmptyPickupClaim(zdo);

            bool atOutpost = WorkerLogisticsRuntime.IsInsideAssignedOutpost(zdo, worker.transform.position, 5f);
            SetState(zdo, WorkerState.FindJob, worker, null);

            if (atOutpost)
            {
                if (AirHaulerWorkRuntime.TryFindInboundSource(worker, zdo, out _, out string inboundPrefab, out int inboundAmount, out Vector3 sourcePosition))
                {
                    WorkerZdo.SetCargoRoute(zdo, AirHaulerWorkRuntime.RouteInbound, default);
                    WorkerZdo.WriteTarget(zdo, inboundPrefab, sourcePosition, AirHaulerWorkRuntime.OutpostSourceTargetKind);
                    float d = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, sourcePosition);
                    SetState(zdo, d <= WorkerStorageRuntime.ContainerAccessRadius ? WorkerState.Collect : WorkerState.MoveToTarget,
                        worker, $"Air Outpost -> Base: {inboundPrefab} x{inboundAmount}");
                    return;
                }

                if (AirHaulerWorkRuntime.TryFindOutboundSource(worker, zdo, out _, out string outboundPrefab, out int outboundAmount, out Vector3 baseSource, out Vector3 outpostPosition))
                {
                    WorkerZdo.SetCargoRoute(zdo, AirHaulerWorkRuntime.RouteOutbound, outpostPosition);
                    WorkerZdo.WriteTarget(zdo, outboundPrefab, baseSource, AirHaulerWorkRuntime.BaseSourceTargetKind);
                    float d = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, baseSource);
                    SetState(zdo, d <= WorkerStorageRuntime.ContainerAccessRadius ? WorkerState.Collect : WorkerState.MoveToTarget,
                        worker, $"Air Base -> Outpost vorbereitet: {outboundPrefab} x{outboundAmount}");
                    return;
                }

                WorkerStorageRuntime.SetDebug(worker, AirHaulerWorkRuntime.GetDiagnosticSummary(worker, zdo));
                SendHomeWhenNoWork(worker, zdo, "Air Hauler bereit: keine Luftfracht");
                return;
            }

            if (AirHaulerWorkRuntime.TryFindOutboundSource(worker, zdo, out _, out string baseOutboundPrefab, out int baseOutboundAmount, out Vector3 baseOutboundSource, out Vector3 baseOutpostPosition))
            {
                WorkerZdo.SetCargoRoute(zdo, AirHaulerWorkRuntime.RouteOutbound, baseOutpostPosition);
                WorkerZdo.WriteTarget(zdo, baseOutboundPrefab, baseOutboundSource, AirHaulerWorkRuntime.BaseSourceTargetKind);
                float d = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, baseOutboundSource);
                SetState(zdo, d <= WorkerStorageRuntime.ContainerAccessRadius ? WorkerState.Collect : WorkerState.MoveToTarget,
                    worker, $"Air Rueckfracht: {baseOutboundPrefab} x{baseOutboundAmount}");
                return;
            }

            WorkerStorageRuntime.SetDebug(worker, AirHaulerWorkRuntime.GetDiagnosticSummary(worker, zdo));
            // Keep the empty Base -> Outpost return as an explicit persistent air route. Physical
            // ReturnHome navigation can still use the HomeAnchor normally, while the background
            // scheduler now has a durable destination after the first 150 m physical leg.
            WorkerZdo.WriteTarget(zdo, AirHaulerWorkRuntime.ReturnHomeTargetName, home, AirHaulerWorkRuntime.ReturnHomeTargetKind);
            // Start a fresh deterministic departure leg immediately. The previous delivery can leave
            // the hybrid latch in PhysicalArrivalActive; if that phase survives into ReturnHome,
            // the Drake remains under physical authority for the whole return flight.
            BackgroundWorkerScheduler.BeginPhysicalAirHybridRoute(
                zdo, worker.transform.position, AirHaulerWorkRuntime.ReturnHomeTargetKind, home);
            SetState(zdo, WorkerState.ReturnHome, worker, "Air Base-Zyklus fertig; leer zurueck zum Landeplatz");
        }

        private void TickProvisionerInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            if (WorkerZdo.HasCargo(zdo))
            {
                TickHaulerDelivery(worker, zdo);
                return;
            }

            // v0.5.336: Keep an already assigned provisioning pickup while the worker is travelling
            // or while navigation is handing the source over to Collect. Re-running TryFindSource()
            // here used to rescan all local storage every AI decision even though a valid source
            // target was already persisted. In large bases this produced the 2-7 second
            // PERF WORKER spikes seen on Provision/MoveToTarget.
            WorkerState provisionState = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if ((provisionState == WorkerState.MoveToTarget || provisionState == WorkerState.Collect) &&
                WorkerZdo.TryReadTarget(zdo, out _, out string provisionTargetKind, out _) &&
                (string.Equals(provisionTargetKind, WorkerProvisioningLogisticsRuntime.SourceTargetKind, StringComparison.Ordinal) ||
                 string.Equals(provisionTargetKind, AirHaulerWorkRuntime.SortOutboundSourceTargetKind, StringComparison.Ordinal)))
            {
                return;
            }

            SetState(zdo, WorkerState.FindJob, worker, null);
            if (WorkerProvisioningLogisticsRuntime.TryFindSource(worker, zdo,
                    out string provisionPrefab, out int provisionAmount, out Vector3 provisionSource,
                    out _, out _, out string provisionDisplay))
            {
                WorkerZdo.WriteTarget(zdo, provisionPrefab, provisionSource, WorkerProvisioningLogisticsRuntime.SourceTargetKind);
                float provisionDistance = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, provisionSource);
                SetState(zdo, provisionDistance <= WorkerStorageRuntime.ContainerAccessRadius ? WorkerState.Collect : WorkerState.MoveToTarget,
                    worker, $"Versorgung: {provisionDisplay} x{provisionAmount} Lager -> Ruhestaette");
                return;
            }

            // v0.5.227: Remote food/fuel shortages are published as Base AirFreight orders. A
            // Versorger at the Base may now physically claim that order, fetch the real resource
            // from an ordinary Base chest and stage it for the Drake. Sorters can still stage the
            // same central order, so either logistics job can keep an outpost supplied.
            WorkerTaskFlags provisionerTasks = WorkerJobProfileRuntime.ResolveEffectiveHaulerTasks(zdo);
            if ((provisionerTasks & WorkerTaskFlags.Transport) != 0 &&
                WorkerProvisioningLogisticsRuntime.TryFindBaseProvisioningSource(worker, zdo,
                    out string baseProvisionPrefab, out int baseProvisionAmount,
                    out Vector3 baseProvisionSource, out Vector3 baseProvisionHub))
            {
                WorkerZdo.SetCargoRoute(zdo, AirHaulerWorkRuntime.RouteSortOutbound, baseProvisionHub);
                WorkerZdo.WriteTarget(zdo, baseProvisionPrefab, baseProvisionSource, AirHaulerWorkRuntime.SortOutboundSourceTargetKind);
                float distance = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, baseProvisionSource);
                SetState(zdo, distance <= WorkerStorageRuntime.ContainerAccessRadius ? WorkerState.Collect : WorkerState.MoveToTarget,
                    worker, $"Versorgungsauftrag Base: {baseProvisionPrefab} x{baseProvisionAmount} Lager -> Luftfracht");
                return;
            }

            WorkerStorageRuntime.SetDebug(worker, "Versorgung: kein lokaler Bedarf und kein Base-Versorgungsauftrag");
            SendStandbyWhenNoWork(worker, zdo, WorkerRole.Hauler, WorkOrder.Provision, workCenter, workRadius,
                "Versorgung: aktuell kein Nachschubbedarf");
        }

        private void TickSorterInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            if (WorkerZdo.HasCargo(zdo))
            {
                // A worker can be reassigned through a WorkFlag while still carrying cargo from a
                // previous Haul job (older saves/builds did not consistently block that path). In
                // Sort mode an unknown/Haul route must never fall through to the generic Hauler
                // fallback, because that waits forever for an Import chest and produces no movement
                // target. Preserve the real cargo and safely return it to an existing matching
                // normal Base chest, then resume normal sorting.
                WorkerZdo.TryReadCargoRoute(zdo, out string carriedRoute, out _);
                bool sorterRoute = string.Equals(carriedRoute, WorkerLogisticsRuntime.RouteSortImport, StringComparison.Ordinal) ||
                                   string.Equals(carriedRoute, WorkerLogisticsRuntime.RouteSortExport, StringComparison.Ordinal) ||
                                   string.Equals(carriedRoute, AirHaulerWorkRuntime.RouteSortInbound, StringComparison.Ordinal) ||
                                   string.Equals(carriedRoute, AirHaulerWorkRuntime.RouteSortOutbound, StringComparison.Ordinal);
                if (string.Equals(carriedRoute, WorkerLogisticsRuntime.RouteSortExport, StringComparison.Ordinal))
                    SorterDemandClaimRuntime.Touch(zdo);
                if (!sorterRoute)
                {
                    SorterDemandClaimRuntime.Release(zdo, "cargo-route-changed");
                    if (WorkerZdo.TryReadCargo(zdo, out string recoveryPrefab, out int recoveryAmount) &&
                        WorkerLogisticsRuntime.TryFindNormalDestination(worker, zdo, recoveryPrefab, out _, out Vector3 recoveryPosition))
                    {
                        WorkerZdo.SetCargoRoute(zdo, WorkerLogisticsRuntime.RouteSortImport, default);
                        WorkerZdo.WriteTarget(zdo, recoveryPrefab, recoveryPosition, WorkerStorageRuntime.SortBaseDepositTargetKind);
                        SetState(zdo, WorkerState.Deliver, worker, $"sorter recovery: returning stale cargo {recoveryPrefab} x{recoveryAmount} to matching Base chest");
                        return;
                    }

                    WorkerZdo.ClearTarget(zdo);
                    SetState(zdo, WorkerState.NeedsResource, worker,
                        $"sorter recovery blocked: carried cargo has no matching normal Base chest; route={(string.IsNullOrEmpty(carriedRoute) ? "none" : carriedRoute)}");
                    return;
                }

                TickHaulerDelivery(worker, zdo);
                return;
            }

            // v0.5.336: v0.5.332 intended this fast-path for the Sorter but accidentally placed
            // it in the Provision branch. Keep an already selected sorter source while travelling
            // or entering Collect so the complete Import/Base/Air source discovery is not repeated.
            WorkerState sorterState = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if ((sorterState == WorkerState.MoveToTarget || sorterState == WorkerState.Collect) &&
                WorkerZdo.TryReadTarget(zdo, out _, out string sorterTargetKind, out _) &&
                (string.Equals(sorterTargetKind, WorkerStorageRuntime.SortImportSourceTargetKind, StringComparison.Ordinal) ||
                 string.Equals(sorterTargetKind, WorkerStorageRuntime.SortBaseSourceTargetKind, StringComparison.Ordinal) ||
                 string.Equals(sorterTargetKind, AirHaulerWorkRuntime.SortInboundSourceTargetKind, StringComparison.Ordinal) ||
                 string.Equals(sorterTargetKind, AirHaulerWorkRuntime.SortOutboundSourceTargetKind, StringComparison.Ordinal)))
            {
                if (string.Equals(sorterTargetKind, WorkerStorageRuntime.SortBaseSourceTargetKind, StringComparison.Ordinal))
                    SorterDemandClaimRuntime.Touch(zdo);
                return;
            }

            // A claim is only valid while the Sorter owns the matching Base->Export staging job.
            // Replanning from an idle/failed state must free an old slice immediately instead of
            // waiting for the safety timeout.
            SorterDemandClaimRuntime.Release(zdo, "replan");
            SetState(zdo, WorkerState.FindJob, worker, null);

            if (WorkerLogisticsRuntime.TryFindSorterSourceForPhysical(worker, zdo, out _, out string prefab, out int amount, out string sourceKind, out Vector3 sourcePosition, out WorkerLogisticsRuntime.SupplyRequest demandRequest))
            {
                if (string.Equals(sourceKind, WorkerStorageRuntime.SortBaseSourceTargetKind, StringComparison.Ordinal))
                {
                    long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
                    int exportStock = WorkerLogisticsRuntime.CountRoleStock(worker, zdo, WorkerContainerRole.Export, prefab);
                    int totalOpenNeed = FieldSupplyDemandCacheRuntime.GetTotalOpenGroundNeed(ownerId, prefab);
                    int stageNeed = Mathf.Max(0, totalOpenNeed - exportStock);
                    if (!SorterDemandClaimRuntime.TryAcquire(zdo, demandRequest, stageNeed, amount, out int claimedAmount, out string claimStatus) || claimedAmount <= 0)
                    {
                        WorkerStorageRuntime.SetDebug(worker, $"sorting outbound waiting: {claimStatus}");
                        SendStandbyWhenNoWork(worker, zdo, WorkerRole.Hauler, WorkOrder.Sort, workCenter, workRadius, claimStatus);
                        return;
                    }
                    amount = claimedAmount;
                }
                else
                {
                    SorterDemandClaimRuntime.Release(zdo, "non-ground-sort-job");
                }

                WorkerZdo.WriteTarget(zdo, prefab, sourcePosition, sourceKind);
                float distance = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, sourcePosition);
                string sorterReason = sourceKind == WorkerStorageRuntime.SortImportSourceTargetKind
                    ? $"sorting inbound: {prefab} x{amount} Import -> Base"
                    : sourceKind == AirHaulerWorkRuntime.SortInboundSourceTargetKind
                        ? $"sorting air inbound: {prefab} x{amount} Advanced FieldDepot -> Base"
                        : sourceKind == AirHaulerWorkRuntime.SortOutboundSourceTargetKind
                            ? $"staging air outbound: {prefab} x{amount} Base -> Advanced FieldDepot"
                            : $"staging outbound: {prefab} x{amount} Base -> Export";
                SetState(zdo, distance <= WorkerStorageRuntime.ContainerAccessRadius ? WorkerState.Collect : WorkerState.MoveToTarget,
                    worker, sorterReason);
                return;
            }

            SorterDemandClaimRuntime.Release(zdo, "no-sort-work");
            string sorterIdle = WorkerLogisticsRuntime.DescribeSorterIdle(worker, zdo);
            WorkerStorageRuntime.SetDebug(worker, sorterIdle);
            SendStandbyWhenNoWork(worker, zdo, WorkerRole.Hauler, WorkOrder.Sort, workCenter, workRadius, sorterIdle);
        }

        private void TickHaulerDelivery(Character worker, ZDO zdo)
        {
            if (!WorkerZdo.TryReadCargo(zdo, out string cargoPrefab, out int amount))
            {
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.FindJob, worker, null);
                return;
            }

            bool hasCargoRoute = WorkerZdo.TryReadCargoRoute(zdo, out string route, out Vector3 routeTarget);
            ZDOID routeTargetId = WorkerZdo.ReadCargoRouteTargetZdoId(zdo);
            string routeTargetWorkAreaId = WorkerZdo.ReadCargoRouteTargetWorkAreaId(zdo);
            if (string.Equals(route, WorkerProvisioningLogisticsRuntime.RouteProvisionHome, StringComparison.Ordinal))
            {
                WorkerProvisioningLogisticsRuntime.TryPrepareDeliveryTarget(worker, zdo, cargoPrefab, amount);
                return;
            }

            if (string.Equals(route, WorkerLogisticsRuntime.RouteHaulOutbound, StringComparison.Ordinal))
            {
                if (WorkerLogisticsRuntime.TryFindRequestedFieldDepotDestination(worker, zdo, cargoPrefab, routeTarget, routeTargetId, routeTargetWorkAreaId, out _, out Vector3 depotPosition))
                {
                    WorkerZdo.WriteTarget(zdo, cargoPrefab, depotPosition, WorkerStorageRuntime.SupplyFieldDepotDepositTargetKind);
                    SetState(zdo, WorkerState.Deliver, worker, $"outbound delivery: {cargoPrefab} x{amount} -> requested FieldDepot");
                    return;
                }
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.NeedsResource, worker, $"outbound cargo {cargoPrefab} x{amount}, but requested FieldDepot is unavailable");
                return;
            }

            if (string.Equals(route, AirHaulerWorkRuntime.RouteSortInbound, StringComparison.Ordinal))
            {
                Vector3 airBasePosition = default;
                bool foundAirBaseDestination = hasCargoRoute &&
                                               WorkerLogisticsRuntime.TryFindNormalDestinationNear(worker, zdo, routeTarget, cargoPrefab, out _, out airBasePosition);
                if (!foundAirBaseDestination)
                    foundAirBaseDestination = WorkerLogisticsRuntime.TryFindNormalDestination(worker, zdo, cargoPrefab, out _, out airBasePosition);
                if (foundAirBaseDestination)
                {
                    WorkerZdo.WriteTarget(zdo, cargoPrefab, airBasePosition, WorkerStorageRuntime.SortBaseDepositTargetKind);
                    SetState(zdo, WorkerState.Deliver, worker, $"sorting air inbound: {cargoPrefab} x{amount} -> matching Base chest");
                    return;
                }
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.NeedsResource, worker, $"AirDepot cargo {cargoPrefab} has no matching normal Base chest");
                return;
            }

            if (string.Equals(route, AirHaulerWorkRuntime.RouteSortOutbound, StringComparison.Ordinal))
            {
                if (AirHaulerWorkRuntime.GetSpecificAirDepotFreeCapacity(worker, zdo, cargoPrefab, routeTarget) > 0)
                {
                    WorkerZdo.WriteTarget(zdo, cargoPrefab, routeTarget, AirHaulerWorkRuntime.SortBaseDepositTargetKind);
                    SetState(zdo, WorkerState.Deliver, worker, $"staging air outbound: {cargoPrefab} x{amount} -> Advanced FieldDepot");
                    return;
                }
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.NeedsResource, worker, $"air outbound staging cargo {cargoPrefab}: Advanced FieldDepot unavailable/full");
                return;
            }

            if (string.Equals(route, WorkerLogisticsRuntime.RouteSortImport, StringComparison.Ordinal))
            {
                Vector3 basePosition = default;
                bool foundBaseDestination = hasCargoRoute &&
                                            WorkerLogisticsRuntime.TryFindNormalDestinationNear(worker, zdo, routeTarget, cargoPrefab, out _, out basePosition);
                if (!foundBaseDestination)
                    foundBaseDestination = WorkerLogisticsRuntime.TryFindNormalDestination(worker, zdo, cargoPrefab, out _, out basePosition);
                if (foundBaseDestination)
                {
                    WorkerZdo.WriteTarget(zdo, cargoPrefab, basePosition, WorkerStorageRuntime.SortBaseDepositTargetKind);
                    SetState(zdo, WorkerState.Deliver, worker, $"sorting inbound: {cargoPrefab} x{amount} -> matching Base chest");
                    return;
                }
                if (WorkerLogisticsRuntime.TryRollbackSorterCargo(worker, zdo, cargoPrefab, amount, route, routeTarget, out string rollbackStatus))
                {
                    SetState(zdo, WorkerState.FindJob, worker, rollbackStatus);
                    return;
                }
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.NeedsResource, worker, $"Import cargo {cargoPrefab} x{amount} preserved on worker; no matching Base chest and rollback unavailable");
                return;
            }

            if (string.Equals(route, WorkerLogisticsRuntime.RouteSortExport, StringComparison.Ordinal))
            {
                SorterDemandClaimRuntime.Touch(zdo);
                if (WorkerLogisticsRuntime.TryFindExportDestination(worker, zdo, cargoPrefab, out _, out Vector3 exportPosition))
                {
                    WorkerZdo.WriteTarget(zdo, cargoPrefab, exportPosition, WorkerStorageRuntime.ExportDepositTargetKind);
                    SetState(zdo, WorkerState.Deliver, worker, $"staging outbound: {cargoPrefab} x{amount} -> Export");
                    return;
                }
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.NeedsResource, worker, $"outbound staging cargo {cargoPrefab} x{amount}, but no Export chest is available");
                return;
            }

            // Existing/legacy cargo defaults to the inbound FieldDepot -> Import path.
            if (WorkerStorageRuntime.TryFindImportContainer(worker, zdo, cargoPrefab, out _, out Vector3 importPosition))
            {
                WorkerZdo.SetCargoRoute(zdo, WorkerLogisticsRuntime.RouteHaulInbound, default);
                WorkerZdo.WriteTarget(zdo, cargoPrefab, importPosition, WorkerStorageRuntime.ImportDepositTargetKind);
                SetState(zdo, WorkerState.Deliver, worker, $"haul delivery: {cargoPrefab} x{amount} -> Import");
                return;
            }

            WorkerZdo.ClearTarget(zdo);
            SetState(zdo, WorkerState.NeedsResource, worker, $"haul carrying {cargoPrefab} x{amount}, but no Import chest is loaded/available");
        }

        private readonly List<GardenReplantSlot> _gardenSlotScratch = new List<GardenReplantSlot>(32);
        private readonly Dictionary<string, int> _wildGrowthSupplyScratch = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _wildGrowthSupplyKeyScratch = new List<string>(16);

        private void TickGardenSupply(Character worker, ZDO zdo)
        {
            int workerKey = worker != null ? worker.GetInstanceID() : 0;
            WorkerState currentState = zdo != null
                ? (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle)
                : WorkerState.Idle;

            // Keep a concrete resource pickup route stable. Navigation owns movement/arrival; the
            // scheduler must not repeat depot/slot discovery while the worker is already travelling.
            if ((currentState == WorkerState.MoveToTarget || currentState == WorkerState.Collect) &&
                WorkerZdo.TryReadTarget(zdo, out _, out string activeTargetKind, out _) &&
                string.Equals(activeTargetKind, WorkerStorageRuntime.GardenResourceSourceTargetKind, StringComparison.Ordinal))
                return;

            // If the last complete pass established "planned slots exist, resource missing", retry
            // at a modest cadence instead of paying that whole pass every two seconds. Inventory
            // arrival can therefore take at most six seconds to wake the gardener, with no polling
            // storm while an outpost waits for a shipment.
            if (currentState == WorkerState.NeedsResource && workerKey != 0 && !WorkerZdo.HasCargo(zdo) &&
                _nextGardenSupplyRetryAt.TryGetValue(workerKey, out float retryAt) && Time.time < retryAt)
                return;

            if (!GardenWorkRuntime.EnsureReady())
            {
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.NeedsResource, worker, "garden crop registry is not ready yet");
                return;
            }

            // If an old assignment became temporarily impossible (missing/keep-one protected), release
            // only the Worker-side claim. The persistent slot remains on the flag and can be retried later.
            if (WorkerZdo.TryReadGardenPlan(zdo, out WorkerGardenPlan existingPlan))
            {
                // v0.5.395: automatic stock-driven seed mode was removed. Old saves may still have
                // an in-flight WorkerGardenPlan with SeedProduction=true; release that transient
                // assignment and rebuild from the permanent player-planned slot below.
                if (existingPlan.SeedProduction)
                {
                    WorkerZdo.ClearGardenPlan(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    _debugLog?.Invoke($"Garden legacy auto-seed plan released: worker={worker.gameObject.name}, slot={existingPlan.SlotId}. Explicit seed ghosts are authoritative now.");
                }
                else if (!GardenReplantPlanRuntime.IsPositionInsideAssignedWorkArea(zdo, existingPlan.Position))
                {
                    WorkerZdo.ClearGardenPlan(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    _debugLog?.Invoke($"Garden stale-plan guard: worker={worker.gameObject.name}, slot={existingPlan.SlotId}, target={existingPlan.Position} is outside assigned Workzone; claim released, cargo preserved.");
                }
                else if (!GardenerTaskFilterRuntime.IsGardenPlanAllowed(zdo, existingPlan) ||
                    !ProductionDemandRuntime.AllowsGardenPlan(zdo, existingPlan))
                {
                    WorkerZdo.ClearGardenPlan(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    _debugLog?.Invoke($"Garden plan released by per-worker planting filter: worker={worker.gameObject.name}, slot={existingPlan.SlotId}, seedMode={existingPlan.SeedProduction}");
                }
                else if (WorkerLogisticsRuntime.TryFindFieldDepotResource(worker, zdo, existingPlan.ResourcePrefab, existingPlan.ResourceAmount, out _, out Vector3 existingStorage))
                {
                    // v0.5.386: WildGrowth supply is a pooled field target, not a one-batch request.
                    // Do not clear the Workzone request merely because the next 3 Thistle/Mushrooms
                    // are locally available. That used to make the Sorter stop staging supplies after
                    // one planting batch, so the Shaman consumed those items and then starved again.
                    // Normal crop/seed requests may still clear once the concrete batch is present;
                    // WildGrowth is cleared later only when its aggregate pool target is satisfied.
                    bool existingWildGrowth = GardenerTaskFilterRuntime.IsWildGrowthPlan(existingPlan);
                    if (!existingWildGrowth && WorkerLogisticsRuntime.ClearSupplyRequestForWorkArea(zdo, existingPlan.ResourcePrefab))
                        _debugLog?.Invoke($"Garden existing-plan local stock satisfied supply request: worker={worker?.gameObject?.name ?? "unknown"}, prefab={existingPlan.ResourcePrefab}");
                    WorkerZdo.WriteTarget(zdo, existingPlan.ResourcePrefab, existingStorage, WorkerStorageRuntime.GardenResourceSourceTargetKind);
                    float distance = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, existingStorage);
                    string reserveText = string.Empty;
                    SetState(zdo, distance <= WorkerStorageRuntime.ContainerStopDistance ? WorkerState.Collect : WorkerState.MoveToTarget,
                        worker, $"replant slot #{existingPlan.SlotId}: fetching {existingPlan.ResourcePrefab} x{existingPlan.ResourceAmount} from FieldDepot ({distance:0.0} m){reserveText}");
                    return;
                }
                if (WorkerLogisticsRuntime.TryFindFieldDepotForWorkArea(worker, zdo, out _, out _))
                {
                    // Release only the transient Worker claim. The persistent player-planned slot
                    // is rebuilt below and its exact planting resource is requested from logistics.
                    WorkerZdo.ClearGardenPlan(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    _debugLog?.Invoke($"Garden plan resource missing; rebuilding explicit planned slot: worker={worker.gameObject.name}, slot={existingPlan.SlotId}, resource={existingPlan.ResourcePrefab}");
                }
                else
                {
                    // Compatibility fallback for old settlements that have not marked a FieldDepot yet.
                    if (WorkerStorageRuntime.TryFindGardenResourceContainer(worker, zdo, existingPlan.ResourcePrefab, existingPlan.ResourceAmount, out _, out Vector3 legacyStorage))
                    {
                        WorkerZdo.WriteTarget(zdo, existingPlan.ResourcePrefab, legacyStorage, WorkerStorageRuntime.GardenResourceSourceTargetKind);
                        float distance = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, legacyStorage);
                        SetState(zdo, distance <= WorkerStorageRuntime.ContainerStopDistance ? WorkerState.Collect : WorkerState.MoveToTarget, worker, "legacy direct garden supply (no FieldDepot marked)");
                        return;
                    }
                    WorkerZdo.ClearGardenPlan(zdo);
                    WorkerZdo.ClearTarget(zdo);
                }
            }

            // v0.3.46: clean old pre-planning transient harvest jobs once encountered. The Shaman is
            // intentionally restricted to player-planned persistent ghost slots only.
            int removedLegacy = GardenReplantPlanRuntime.RemoveLegacyTransientSlotsForWorker(zdo);
            if (removedLegacy > 0)
                _debugLog?.Invoke($"Garden strict-plan cleanup: worker={worker.gameObject.name}, removedLegacyTransient={removedLegacy}");

            // Evaluate several persistent planned slots instead of letting one temporarily blocked crop
            // stall the whole field. Slots stay persistent until a replacement is successfully planted.
            int pending = GardenReplantPlanRuntime.GetPendingForWorker(zdo, _gardenSlotScratch, 256);
            // v0.5.388: reconcile the complete WildGrowth stock target from the already-built
            // pending-slot list. This exact snapshot can shrink/clear old Thistle/Mushroom demand
            // without another world/container scan and prevents one completed slot from killing
            // the supply request for all remaining ghosts.
            SyncWildGrowthSupplyTargets(worker, zdo);
            WildGrowthRuntime.BeginEvaluation();
            int blocked = 0;
            for (int i = 0; i < pending; i++)
            {
                GardenReplantSlot slot = _gardenSlotScratch[i];
                if (!slot.PersistentPlan)
                {
                    blocked++;
                    continue;
                }
                if (!TryBuildGardenCandidate(worker, zdo, slot, out WorkerGardenPlan candidate))
                {
                    blocked++;
                    continue;
                }
                if (!GardenerTaskFilterRuntime.IsGardenPlanAllowed(zdo, candidate) ||
                    !ProductionDemandRuntime.AllowsGardenPlan(zdo, candidate))
                {
                    blocked++;
                    continue;
                }

                Vector3 storagePosition = default;
                bool wildGrowthCandidate = GardenerTaskFilterRuntime.IsWildGrowthPlan(candidate);

                // v0.5.321: A persistent WildGrowth plan can remain active from an older harvest/
                // build even though the physical Workers bush is already standing on that slot.
                // Reconcile the plan against the world BEFORE staging any planting resource.
                // Otherwise a fulfilled blueberry bush can keep requesting Blueberries x5 forever.
                if (wildGrowthCandidate && GardenTargetFinder.HasConfirmedWildGrowthPlantAt(candidate.Position, candidate.Spacing, candidate.PlantPrefab, candidate.SlotId))
                {
                    bool completed = GardenReplantPlanRuntime.CompleteForWorker(zdo, candidate.SlotId);
                    if (completed)
                    {
                        // Do not clear the pooled request here. Other pending slots of the same
                        // resource may still need stock. The exact v0.5.388 reconciliation above
                        // shrinks/removes the request on the next safe Garden evaluation.
                        _debugLog?.Invoke($"WildGrowth confirmed slot reconciled: worker={worker?.gameObject?.name ?? "unknown"}, slot={candidate.SlotId}, plant={candidate.PlantPrefab}; slot-owned bush found, pooled demand will be reconciled from remaining ghosts.");
                    }
                    blocked++;
                    continue;
                }

                // v0.5.323: Keep the aggregate WildGrowth supply request, but do NOT make the
                // gardener wait until the complete field demand has arrived. v0.5.319 fixed the
                // repeated 5-item logistics request by staging the whole field target, but the same
                // target was also used as a hard planting gate. With many planned bushes this left
                // the Shaman in NeedsResource/target=none even though enough stock for the next
                // ghost already existed. Logistics may keep refilling toward the aggregate target
                // while the gardener immediately works any slot for which one planting batch
                // (+ protected Keep-one reserve, when enabled) is locally available.
                if (wildGrowthCandidate && WorkerLogisticsRuntime.TryGetFieldDepotPoolStock(worker, zdo, candidate.ResourcePrefab,
                        out int wildPoolStock, out int wildUsableStock, out int wildProtectedReserve))
                {
                    // v0.5.382: FieldDepots are one Workzone pool. The previous path inspected only
                    // the depot nearest the WorkFlag center. If that chest happened to contain no
                    // mushrooms/thistle while another assigned FieldDepot did, the Shaman waited
                    // forever although the Workzone as a whole had enough material. Aggregate demand
                    // is now compared against the whole pool, while the actual pickup still chooses
                    // the nearest compatible depot that can satisfy one planting batch.
                    int wildTarget = GetWildGrowthSupplyTarget(candidate, wildProtectedReserve);
                    int immediateNeed = Mathf.Max(1, candidate.ResourceAmount);

                    // The exact pooled target was already synchronized once from the complete
                    // pending list above. Keep the manifest alive even when current stock reaches
                    // the target: Need becomes zero naturally, and after one planting consumes
                    // material the same request immediately becomes actionable again without a
                    // clear/recreate/rescan cycle.
                    if (wildUsableStock < immediateNeed ||
                        !WorkerLogisticsRuntime.TryFindFieldDepotResource(worker, zdo, candidate.ResourcePrefab, immediateNeed, out _, out Vector3 wildDepotPosition))
                    {
                        WildGrowthRuntime.MarkMissingResource(candidate);
                        WorkerZdo.ClearTarget(zdo);
                        blocked++;
                        _debugLog?.Invoke($"WildGrowth waiting: worker={worker?.gameObject?.name ?? "unknown"}, prefab={candidate.ResourcePrefab}, pool={wildPoolStock}, usable={wildUsableStock}/{immediateNeed}, protected={wildProtectedReserve}, aggregateTarget={wildTarget}; next ghost cannot be planted yet.");
                        continue;
                    }

                    storagePosition = wildDepotPosition;
                    if (wildPoolStock < wildTarget)
                        _debugLog?.Invoke($"WildGrowth planting while pooled refill continues: worker={worker?.gameObject?.name ?? "unknown"}, prefab={candidate.ResourcePrefab}, pool={wildPoolStock}, usable={wildUsableStock}, immediateNeed={immediateNeed}, aggregateTarget={wildTarget}.");
                    else
                        _debugLog?.Invoke($"WildGrowth pooled stock currently satisfies target: worker={worker?.gameObject?.name ?? "unknown"}, prefab={candidate.ResourcePrefab}, pool={wildPoolStock}/{wildTarget}; request remains dormant until stock falls or ghosts complete.");
                }
                else if (!WorkerLogisticsRuntime.TryFindFieldDepotResource(worker, zdo, candidate.ResourcePrefab, candidate.ResourceAmount, out _, out storagePosition))
                {
                    if (wildGrowthCandidate) WildGrowthRuntime.MarkMissingResource(candidate);
                    if (WorkerLogisticsRuntime.TryFindFieldDepotForWorkArea(worker, zdo, out _, out _))
                    {
                        RequestGardenSupplyFallback(worker, zdo, candidate);
                    }
                    else if (WorkerStorageRuntime.TryFindGardenResourceContainer(worker, zdo, candidate.ResourcePrefab, candidate.ResourceAmount, out _, out storagePosition))
                    {
                        // No depot exists: retain legacy direct-storage behavior.
                    }
                    else
                    {
                        blocked++;
                        continue;
                    }

                    if (storagePosition == default)
                    {
                        blocked++;
                        continue;
                    }
                }

                // Normal crops may clear the request as soon as one planting batch is locally
                // available. WildGrowth already cleared only after its complete staged field demand
                // was present above.
                if (!wildGrowthCandidate && WorkerLogisticsRuntime.ClearSupplyRequestForWorkArea(zdo, candidate.ResourcePrefab))
                    _debugLog?.Invoke($"Garden local stock satisfied supply request: worker={worker?.gameObject?.name ?? "unknown"}, prefab={candidate.ResourcePrefab}");
                WorkerZdo.SetGardenPlan(zdo, candidate);
                if (wildGrowthCandidate) WildGrowthRuntime.MarkSelected(candidate);
                WorkerZdo.WriteTarget(zdo, candidate.ResourcePrefab, storagePosition, WorkerStorageRuntime.GardenResourceSourceTargetKind);
                float d = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, storagePosition);
                string reserveInfo = string.Empty;
                SetState(zdo, d <= WorkerStorageRuntime.ContainerStopDistance ? WorkerState.Collect : WorkerState.MoveToTarget,
                    worker, $"replant slot #{candidate.SlotId}: fetching {candidate.ResourcePrefab} x{candidate.ResourceAmount} ({d:0.0} m){reserveInfo}; skippedBlocked={blocked}");
                _debugLog?.Invoke($"Garden slot selected: worker={worker.gameObject.name}, slot={candidate.SlotId}, harvest={candidate.HarvestPrefab}, plant={candidate.PlantPrefab}, resource={candidate.ResourcePrefab} x{candidate.ResourceAmount}, blockedBefore={blocked}");
                return;
            }

            if (pending > 0)
            {
                if (workerKey != 0) _nextGardenSupplyRetryAt[workerKey] = Time.time + GardenNeedsResourceRetrySeconds;
                WorkerZdo.ClearTarget(zdo);
                string wildInfo = WildGrowthRuntime.LastPendingCount > 0
                    ? $"; WildGrowth {WildGrowthRuntime.GetDebugSummary()}"
                    : string.Empty;
                SetState(zdo, WorkerState.NeedsResource, worker, $"{pending} pending replant slots; missing resources were requested for FieldDepot where possible{wildInfo}");
                return;
            }

            // v0.1.81+: Gardeners only plant into explicit persistent/replant slots.
            // Do not fetch arbitrary crop markers and scatter plants around the Workzone anymore.
            if (workerKey != 0) _nextGardenSupplyRetryAt.Remove(workerKey);
            SendHomeWhenNoWork(worker, zdo, "garden has no pending planned/replant slots");
        }

        private void TickGardenInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            List<WorkerZdo.CargoStack> cargoStacks = WorkerZdo.ReadCargoStacks(zdo);
            if (cargoStacks == null || cargoStacks.Count == 0)
            {
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.NeedsResource, worker, "garden is waiting for planting resources from base storage");
                return;
            }

            // A Workzone refresh/change can legitimately clear the Worker-side garden-plan claim while
            // the real planting resource is already in any cargo slot. Rebuild the exact persistent plan
            // from whichever stack actually satisfies it instead of assuming slot 0 is authoritative.
            bool hasPlan = WorkerZdo.TryReadGardenPlan(zdo, out WorkerGardenPlan plan);
            if (!hasPlan)
            {
                for (int i = 0; i < cargoStacks.Count && !hasPlan; i++)
                {
                    WorkerZdo.CargoStack stack = cargoStacks[i];
                    if (stack.Amount <= 0 || string.IsNullOrEmpty(stack.Prefab)) continue;
                    if (TryRecoverGardenPlanFromCargo(worker, zdo, stack.Prefab, stack.Amount, out WorkerGardenPlan recoveredPlan))
                    {
                        plan = recoveredPlan;
                        hasPlan = true;
                    }
                }
            }

            if (hasPlan && !GardenReplantPlanRuntime.IsPositionInsideAssignedWorkArea(zdo, plan.Position))
            {
                WorkerZdo.ClearGardenPlan(zdo);
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.FindJob, worker,
                    $"stale garden slot #{plan.SlotId} is outside assigned Workzone; re-evaluating local plans");
                _debugLog?.Invoke($"Garden movement guard rejected remote slot: worker={worker.gameObject.name}, slot={plan.SlotId}, target={plan.Position}; cargo preserved.");
                return;
            }

            if (hasPlan)
            {
                int matchingAmount = WorkerZdo.GetCargoAmount(zdo, plan.ResourcePrefab);
                if (matchingAmount < plan.ResourceAmount)
                {
                    // Keep unrelated cargo. It may belong to the next planned cycle (e.g. carrots for
                    // seed production while carrot seeds for normal planting are in another slot).
                    WorkerZdo.ClearTarget(zdo);
                    SetState(zdo, WorkerState.NeedsResource, worker,
                        $"replant slot #{plan.SlotId} needs {plan.ResourcePrefab} x{plan.ResourceAmount}; cargo has {matchingAmount}");
                    return;
                }

                WorkerZdo.WriteTarget(zdo, plan.PlantPrefab, plan.Position, GardenTargetFinder.PlantTargetKind);
                float planDistance = Vector3.Distance(worker.transform.position, plan.Position);
                SetState(zdo, planDistance <= WorkerNavigationRuntime.TargetStopDistance ? WorkerState.Work : WorkerState.MoveToTarget, worker,
                    $"replanting saved slot #{plan.SlotId} at exact harvested position");
                return;
            }

            // Cargo exists but currently belongs to no pending garden slot. Preserve it here instead
            // of dropping every stack merely because the first stack is not useful. FindJob can
            // re-evaluate persistent slots and the normal unused-cargo cleanup can handle truly stale items.
            WorkerZdo.ClearTarget(zdo);
            SetState(zdo, WorkerState.FindJob, worker, "garden cargo has no current planned slot; re-evaluating all cargo slots");
        }

        private void SyncWildGrowthSupplyTargets(Character worker, ZDO workerZdo)
        {
            _wildGrowthSupplyScratch.Clear();
            if (workerZdo == null) return;

            for (int i = 0; i < _gardenSlotScratch.Count; i++)
            {
                GardenReplantSlot slot = _gardenSlotScratch[i];
                if (!slot.PersistentPlan || !WildGrowthRuntime.IsWildGrowthSlot(slot)) continue;
                if (!WildGrowthRuntime.TryBuildPlan(slot, out WorkerGardenPlan plan)) continue;
                if (!GardenerTaskFilterRuntime.IsGardenPlanAllowed(workerZdo, plan) ||
                    !ProductionDemandRuntime.AllowsGardenPlan(workerZdo, plan)) continue;
                if (string.IsNullOrWhiteSpace(plan.ResourcePrefab) || plan.ResourceAmount <= 0) continue;

                _wildGrowthSupplyScratch.TryGetValue(plan.ResourcePrefab, out int current);
                _wildGrowthSupplyScratch[plan.ResourcePrefab] = current + Mathf.Max(1, plan.ResourceAmount);
            }

            if (worker != null && _wildGrowthSupplyScratch.Count > 0)
            {
                // Keep-last is a real non-consumable reserve. Count it on top of the remaining
                // planting cost so the final ghost is not starved by a protected last item.
                _wildGrowthSupplyKeyScratch.Clear();
                foreach (string key in _wildGrowthSupplyScratch.Keys)
                    _wildGrowthSupplyKeyScratch.Add(key);

                for (int i = 0; i < _wildGrowthSupplyKeyScratch.Count; i++)
                {
                    string prefab = _wildGrowthSupplyKeyScratch[i];
                    if (WorkerLogisticsRuntime.TryGetFieldDepotPoolStock(worker, workerZdo, prefab,
                            out _, out _, out int protectedReserve) && protectedReserve > 0)
                        _wildGrowthSupplyScratch[prefab] = Mathf.Max(1, _wildGrowthSupplyScratch[prefab]) + protectedReserve;
                }
            }

            WorkerLogisticsRuntime.SyncWildGrowthSupplyForWorkArea(workerZdo, _wildGrowthSupplyScratch);
        }

        private int GetWildGrowthSupplyTarget(WorkerGardenPlan plan, int protectedReserve = 0)
        {
            int perPlant = Mathf.Max(1, plan.ResourceAmount);
            int matchingPending = 0;
            for (int i = 0; i < _gardenSlotScratch.Count; i++)
            {
                GardenReplantSlot slot = _gardenSlotScratch[i];
                if (!slot.PersistentPlan || !WildGrowthRuntime.IsWildGrowthSlot(slot)) continue;
                if (!WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(slot.HarvestPrefab, out WorkersWildFloraRuntime.Definition def)) continue;
                if (!string.Equals(def.ResourcePrefab, plan.ResourcePrefab, StringComparison.OrdinalIgnoreCase)) continue;
                matchingPending++;
            }

            return Mathf.Max(perPlant, matchingPending * perPlant) + Mathf.Max(0, protectedReserve);
        }

        private void RequestGardenSupplyFallback(Character worker, ZDO workerZdo, WorkerGardenPlan plan)
        {
            if (workerZdo == null) return;

            string requestPrefab = plan.ResourcePrefab;
            bool wildGrowth = GardenerTaskFilterRuntime.IsWildGrowthPlan(plan);
            // The persistent ghost decides WHAT is grown. Logistics only supplies that ghost's real
            // input resource: seeds for ordinary crops, mature vegetables for explicitly planned
            // seed plants, and harvested flora materials for WildGrowth. No stock threshold may
            // switch the crop type anymore.
            int targetStock;
            if (wildGrowth)
            {
                WorkerLogisticsRuntime.TryFindFieldDepotForWorkArea(worker, workerZdo, out Container wildDepot, out _);
                targetStock = GetWildGrowthSupplyTarget(plan, wildDepot != null && WorkerContainerRoleRuntime.ProtectLastItem(wildDepot) ? 1 : 0);
            }
            else
            {
                targetStock = Mathf.Max(plan.ResourceAmount, WorkerLogisticsRuntime.DefaultFieldSupplyTarget);
            }

            if (WorkerLogisticsRuntime.RequestSupplyForWorkArea(workerZdo, requestPrefab, targetStock))
            {
                _debugLog?.Invoke($"Garden supply fallback requested: worker={worker?.gameObject?.name ?? "unknown"}, prefab={requestPrefab}, target={targetStock}, explicitPlan=true");
            }
        }

        private bool TryBuildGardenCandidate(Character worker, ZDO zdo, GardenReplantSlot slot, out WorkerGardenPlan candidate)
        {
            candidate = default;

            // v0.5.395: the permanent ghost is the sole source of truth. A planned carrot stays a
            // carrot; a planned carrot-seed plant stays a seed plant. Inventory levels, FieldDepot
            // Keep values and old seed-production triggers can no longer substitute another recipe.
            if (WildGrowthRuntime.IsWildGrowthSlot(slot))
                return WildGrowthRuntime.TryBuildPlan(slot, out candidate);

            if (GardenWorkRuntime.TryGetFallbackReplantPlan(slot.HarvestPrefab, out string resource, out string plant, out int amount, out float spacing))
            {
                candidate = new WorkerGardenPlan(slot.SlotId, slot.HarvestPrefab, plant, resource, amount, spacing,
                    slot.Position, false, 0, 0, slot.RotationY);
                return true;
            }

            return false;
        }

        private bool TryRecoverGardenPlanFromCargo(Character worker, ZDO zdo, string cargoPrefab, int cargoAmount, out WorkerGardenPlan recoveredPlan)
        {
            recoveredPlan = default;
            if (worker == null || zdo == null || string.IsNullOrEmpty(cargoPrefab) || cargoAmount <= 0) return false;

            int pending = GardenReplantPlanRuntime.GetPendingForWorker(zdo, _gardenSlotScratch, 256);
            for (int i = 0; i < pending; i++)
            {
                GardenReplantSlot slot = _gardenSlotScratch[i];
                if (!slot.PersistentPlan)
                    continue;
                if (!TryBuildGardenCandidate(worker, zdo, slot, out WorkerGardenPlan candidate))
                    continue;
                if (!GardenerTaskFilterRuntime.IsGardenPlanAllowed(zdo, candidate))
                    continue;

                if (!string.Equals(candidate.ResourcePrefab, cargoPrefab, StringComparison.OrdinalIgnoreCase) || cargoAmount < candidate.ResourceAmount)
                    continue;

                WorkerZdo.SetGardenPlan(zdo, candidate);
                recoveredPlan = candidate;
                _debugLog?.Invoke($"Garden plan recovered from cargo: worker={worker.gameObject.name}, slot={candidate.SlotId}, cargo={cargoPrefab} x{cargoAmount}, plant={candidate.PlantPrefab}");
                return true;
            }

            return false;
        }

        private void TickForestrySupply(Character worker, ZDO zdo)
        {
            int workerKey = worker.GetInstanceID();
            _forestrySeedTargets.Remove(workerKey);

            WorkerForestryPlan plan;
            if (!WorkerZdo.TryReadForestryPlan(zdo, out plan) || !ForestryPlanRuntime.PersistentSlotExistsForWorker(zdo, plan.SlotId) ||
                !GardenerTaskFilterRuntime.IsForestrySpeciesAllowed(zdo, plan.SaplingPrefab))
            {
                WorkerZdo.ClearForestryPlan(zdo);
                if (!ForestryPlanRuntime.TryGetNearestPendingForWorker(worker, zdo, out ForestryPlanSlot slot))
                {
                    SendHomeWhenNoWork(worker, zdo, "forestry waiting: no planned tree slots");
                    return;
                }
                plan = new WorkerForestryPlan(slot.SlotId, slot.SeedPrefab, slot.SaplingPrefab, slot.Position, slot.RotationY);
                WorkerZdo.SetForestryPlan(zdo, plan);
                _debugLog?.Invoke($"Forestry plan assigned: worker={worker.gameObject.name}, slot={slot.SlotId}, seed={slot.SeedPrefab}, sapling={slot.SaplingPrefab}");
            }

            if (!ForestryWorkRuntime.CanWorkerPlant(worker, plan.SeedPrefab, plan.SaplingPrefab, out int requiredPlantTier))
            {
                WorkerZdo.ClearForestryPlan(zdo);
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.FindJob, worker, $"forestry tier blocked: {plan.SaplingPrefab} requires PlantSeeds tier {requiredPlantTier}; selecting another planned slot");
                return;
            }

            string desiredSeed = plan.SeedPrefab;
            if (WorkerLogisticsRuntime.TryFindFieldDepotResource(worker, zdo, desiredSeed, 1, out _, out Vector3 depotPosition))
            {
                WorkerZdo.WriteTarget(zdo, desiredSeed, depotPosition, WorkerStorageRuntime.SeedSourceTargetKind);
                float distance = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, depotPosition);
                SetState(zdo, distance <= WorkerStorageRuntime.ContainerStopDistance ? WorkerState.Collect : WorkerState.MoveToTarget,
                    worker, $"fetching planned forestry seed {desiredSeed} from FieldDepot ({distance:0.0} m)");
                return;
            }

            // Do not let one unavailable tree seed monopolize Forestry. The current plan remains
            // authoritative when its seed exists; only when it is unavailable do we temporarily
            // prefer another active planned slot whose exact seed is already present locally.
            // This keeps mixed Beech/Birch/etc. plans productive without changing navigation or
            // silently converting one planned tree species into another.
            bool SeedAvailableLocally(string seedPrefab)
            {
                if (WorkerLogisticsRuntime.TryFindFieldDepotResource(worker, zdo, seedPrefab, 1, out _, out _))
                    return true;
                return WorkerStorageRuntime.TryFindGardenResourceContainer(worker, zdo, seedPrefab, 1, out _, out _);
            }

            if (ForestryPlanRuntime.TryGetNearestPendingWithAvailableSeedForWorker(worker, zdo, plan.SlotId,
                    SeedAvailableLocally, out ForestryPlanSlot readySlot))
            {
                plan = new WorkerForestryPlan(readySlot.SlotId, readySlot.SeedPrefab, readySlot.SaplingPrefab, readySlot.Position, readySlot.RotationY);
                WorkerZdo.SetForestryPlan(zdo, plan);
                desiredSeed = plan.SeedPrefab;
                zdo.Set("vw_last_supply_request", $"SWITCHED:{desiredSeed}:slot={plan.SlotId}");
                _debugLog?.Invoke($"Forestry seed unavailable; switched to ready planned slot: worker={worker.gameObject.name}, slot={plan.SlotId}, seed={desiredSeed}, sapling={plan.SaplingPrefab}");

                if (WorkerLogisticsRuntime.TryFindFieldDepotResource(worker, zdo, desiredSeed, 1, out _, out depotPosition))
                {
                    WorkerZdo.WriteTarget(zdo, desiredSeed, depotPosition, WorkerStorageRuntime.SeedSourceTargetKind);
                    float distance = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, depotPosition);
                    SetState(zdo, distance <= WorkerStorageRuntime.ContainerStopDistance ? WorkerState.Collect : WorkerState.MoveToTarget,
                        worker, $"switched forestry plan; fetching available {desiredSeed} from FieldDepot ({distance:0.0} m)");
                    return;
                }

                if (WorkerStorageRuntime.TryFindGardenResourceContainer(worker, zdo, desiredSeed, 1, out _, out Vector3 readyStoragePosition))
                {
                    WorkerZdo.WriteTarget(zdo, desiredSeed, readyStoragePosition, WorkerStorageRuntime.SeedSourceTargetKind);
                    float distance = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, readyStoragePosition);
                    SetState(zdo, distance <= WorkerStorageRuntime.ContainerStopDistance ? WorkerState.Collect : WorkerState.MoveToTarget,
                        worker, $"switched forestry plan; fetching available {desiredSeed} ({distance:0.0} m)");
                    return;
                }
            }

            if (WorkerLogisticsRuntime.RequestSupplyForWorkArea(zdo, desiredSeed, WorkerLogisticsRuntime.DefaultFieldSupplyTarget))
            {
                long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
                int baseOrder = WorkerLogisticsRuntime.GetBaseAirSupplyOrder(ownerId, desiredSeed);
                int outpostNeed = AirHaulerWorkRuntime.GetOutpostAirOutstandingNeed(ownerId, desiredSeed);
                zdo.Set("vw_last_supply_request", $"{desiredSeed}:{WorkerLogisticsRuntime.DefaultFieldSupplyTarget}:base={baseOrder}:need={outpostNeed}");
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.NeedsResource, worker, $"requested planned forestry seed {desiredSeed} -> Base order {baseOrder}, outpost need {outpostNeed}");
                return;
            }
            zdo.Set("vw_last_supply_request", $"FAILED:{desiredSeed}:{WorkerLogisticsRuntime.DefaultFieldSupplyTarget}");

            // Legacy fallback for old bases without FieldDepot: fetch exactly the seed required by the plan.
            if (WorkerStorageRuntime.TryFindGardenResourceContainer(worker, zdo, desiredSeed, 1, out Container source, out Vector3 storagePosition))
            {
                WorkerZdo.WriteTarget(zdo, desiredSeed, storagePosition, WorkerStorageRuntime.SeedSourceTargetKind);
                float distance = WorkerStorageRuntime.HorizontalDistance(worker.transform.position, storagePosition);
                SetState(zdo, distance <= WorkerStorageRuntime.ContainerStopDistance ? WorkerState.Collect : WorkerState.MoveToTarget,
                    worker, $"legacy direct forestry supply: {desiredSeed} ({distance:0.0} m)");
                return;
            }

            WorkerZdo.ClearTarget(zdo);
            SetState(zdo, WorkerState.NeedsResource, worker, $"planned forestry slot #{plan.SlotId} waiting for {desiredSeed}");
        }

        private void TickForestryInsideArea(Character worker, ZDO zdo, Vector3 workCenter, float workRadius)
        {
            int workerKey = worker.GetInstanceID();
            _forestrySeedTargets.Remove(workerKey);

            if (!WorkerZdo.TryReadForestryPlan(zdo, out WorkerForestryPlan plan) || !ForestryPlanRuntime.PersistentSlotExistsForWorker(zdo, plan.SlotId) ||
                !GardenerTaskFilterRuntime.IsForestrySpeciesAllowed(zdo, plan.SaplingPrefab))
            {
                WorkerZdo.ClearForestryPlan(zdo);
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.FindJob, worker, "forestry plan missing/stale; selecting next planned slot");
                return;
            }

            if (!ForestryWorkRuntime.CanWorkerPlant(worker, plan.SeedPrefab, plan.SaplingPrefab, out int requiredPlantTier))
            {
                WorkerZdo.ClearForestryPlan(zdo);
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.FindJob, worker, $"forestry tier blocked in work area: {plan.SaplingPrefab} requires PlantSeeds tier {requiredPlantTier}");
                return;
            }

            if (!WorkerZdo.HasCargo(zdo))
            {
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.NeedsResource, worker, $"planned forestry slot #{plan.SlotId} needs {plan.SeedPrefab}");
                return;
            }

            if (!WorkerZdo.TryReadCargo(zdo, out string cargoSeed, out int cargoAmount) || cargoAmount <= 0 ||
                !string.Equals(cargoSeed, plan.SeedPrefab, StringComparison.OrdinalIgnoreCase))
            {
                bool dropped = WorkerZdo.HasCargo(zdo) && WorkerCargoRuntime.DropAllCargo(worker, zdo, "forestry cargo no longer matches planned slot");
                WorkerZdo.ClearTarget(zdo);
                SetState(zdo, WorkerState.NeedsResource, worker, dropped
                    ? $"forestry dropped obsolete cargo; slot #{plan.SlotId} needs {plan.SeedPrefab}"
                    : "forestry cargo does not match planned slot seed");
                return;
            }

            Vector3 point = plan.Position;
            float distanceToSlot = Vector3.Distance(worker.transform.position, point);
            if (distanceToSlot <= WorkerNavigationRuntime.TargetStopDistance)
            {
                WorkerZdo.WriteTarget(zdo, plan.SaplingPrefab, point, ForestryTargetFinder.PlantTargetKind);
                SetState(zdo, WorkerState.Work, worker, $"planned forestry slot #{plan.SlotId} ({distanceToSlot:0.0} m)");
                return;
            }

            Vector3 approachPoint = ForestryTargetFinder.GetPlannedSlotApproachPoint(worker.transform.position, point);
            WorkerZdo.WriteTarget(zdo, plan.SaplingPrefab, approachPoint, ForestryTargetFinder.PlantTargetKind);
            SetState(zdo, WorkerState.MoveToTarget, worker,
                $"planned forestry slot #{plan.SlotId} ({distanceToSlot:0.0} m; approach {Vector3.Distance(worker.transform.position, approachPoint):0.0} m)");
        }

        private void SendGuardStandbyWhenNoWork(Character worker, ZDO zdo, WorkerRole role,
            Vector3 workCenter, float workRadius, string reason)
        {
            if (worker == null || zdo == null) return;

            if (GuardBehaviorRuntime.Read(zdo) != GuardBehaviorMode.Deploy ||
                !GuardPatrolRuntime.TryResolve(worker, zdo, workCenter, workRadius, out Vector3 patrolPoint, out string patrolLabel))
            {
                SendStandbyWhenNoWork(worker, zdo, role, WorkOrder.Guard, workCenter, workRadius, reason);
                return;
            }

            int workerKey = worker.GetInstanceID();
            ClearLocalTargets(workerKey);

            bool sameTarget = WorkerZdo.TryReadTarget(zdo, out string oldName, out string oldKind, out Vector3 oldPoint) &&
                              string.Equals(oldKind, WorkerStandbyRuntime.TargetKind, StringComparison.Ordinal) &&
                              string.Equals(oldName, patrolLabel, StringComparison.Ordinal) &&
                              (oldPoint - patrolPoint).sqrMagnitude <= 0.04f;
            if (!sameTarget)
                WorkerZdo.WriteTarget(zdo, patrolLabel, patrolPoint, WorkerStandbyRuntime.TargetKind);

            Vector3 horizontal = patrolPoint - worker.transform.position;
            horizontal.y = 0f;
            float distance = horizontal.magnitude;
            string suffix = distance <= GuardPatrolRuntime.StopDistance + 0.10f
                ? $"; beobachtet Sektor {patrolLabel}"
                : $"; patrouilliert zu {patrolLabel}";
            SetState(zdo, WorkerState.Standby, worker, reason + suffix);
        }

        private void SendStandbyWhenNoWork(Character worker, ZDO zdo, WorkerRole role, WorkOrder order,
            Vector3 workCenter, float workRadius, string reason)
        {
            if (worker == null || zdo == null) return;

            int workerKey = worker.GetInstanceID();
            ClearLocalTargets(workerKey);

            if (!WorkerStandbyRuntime.TryResolve(worker, zdo, role, order, workCenter, workRadius,
                    out Vector3 standbyPoint, out string standbyLabel))
            {
                SendHomeWhenNoWork(worker, zdo, reason);
                return;
            }

            bool sameTarget = WorkerZdo.TryReadTarget(zdo, out string oldName, out string oldKind, out Vector3 oldPoint) &&
                              string.Equals(oldKind, WorkerStandbyRuntime.TargetKind, StringComparison.Ordinal) &&
                              string.Equals(oldName, standbyLabel, StringComparison.Ordinal) &&
                              (oldPoint - standbyPoint).sqrMagnitude <= 0.04f;
            if (!sameTarget)
                WorkerZdo.WriteTarget(zdo, standbyLabel, standbyPoint, WorkerStandbyRuntime.TargetKind);

            float stopDistance = WorkerStandbyRuntime.GetStopDistance(zdo, role, order);
            float distance = Vector3.Distance(worker.transform.position, standbyPoint);
            string suffix = distance <= stopDistance + 0.10f
                ? $"; Bereitschaft an {standbyLabel}"
                : $"; geht in Bereitschaft zu {standbyLabel}";
            SetState(zdo, WorkerState.Standby, worker, reason + suffix);
        }

        private void SendHomeWhenNoWork(Character worker, ZDO zdo, string reason)
        {
            if (worker == null || zdo == null) return;

            int workerKey = worker.GetInstanceID();
            ClearLocalTargets(workerKey);
            WorkerZdo.ClearTarget(zdo);

            // Only a real assigned species HomeAnchor counts as a rest station. Workers without
            // one remain Idle so this global convenience rule never invents a fake home position.
            if (!WorkerWorkAreaRules.HasRealHome(zdo))
            {
                SetState(zdo, WorkerState.Idle, worker, reason + "; no real HomeAnchor assigned");
                return;
            }

            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            float distance = Vector3.Distance(worker.transform.position, home);
            float homeStopDistance = WorkerNavigationRuntime.GetHomeStopDistance(zdo);
            WorkerState desired = distance <= homeStopDistance + 0.10f
                ? WorkerState.WaitAtHome
                : WorkerState.ReturnHome;
            SetState(zdo, desired, worker, reason + (desired == WorkerState.WaitAtHome ? "; waiting at rest station" : "; returning to rest station"));
        }

        private void ClearLocalTargets(int workerKey)
        {
            _woodTargets.Remove(workerKey);
            _stoneTargets.Remove(workerKey);
            StonemasonWorkRuntime.ClearBoundTarget(workerKey);
            _gatherTargets.Remove(workerKey);
            _forestrySeedTargets.Remove(workerKey);
        }

        private void SetState(ZDO zdo, WorkerState state, Character worker, string reason)
        {
            WorkerState previous = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);

            // Diagnostics must not dirty a networked ZDO every decision when nothing changed.
            // Refresh the same reason at most every 10 seconds; state/reason changes are still
            // persisted immediately, so the debug UI remains useful without constant ZDO churn.
            long nowTicks = DateTime.UtcNow.Ticks;
            string diagnosticReason = !string.IsNullOrEmpty(reason) ? reason : (previous != state ? $"state {previous} -> {state}" : string.Empty);
            if (!string.IsNullOrEmpty(diagnosticReason))
            {
                string previousReason = zdo.GetString(WorkerZdo.LastDecisionReason, string.Empty);
                long previousTicks = zdo.GetLong(WorkerZdo.LastDecisionTicks, 0L);
                bool refreshDue = previousTicks <= 0L || nowTicks - previousTicks >= TimeSpan.TicksPerSecond * 10L;
                if (previous != state || !string.Equals(previousReason, diagnosticReason, StringComparison.Ordinal) || refreshDue)
                {
                    zdo.Set(WorkerZdo.LastDecisionReason, diagnosticReason);
                    zdo.Set(WorkerZdo.LastDecisionTicks, nowTicks);
                }
            }

            if (previous == state) return;

            zdo.Set(WorkerZdo.State, (int)state);
            if (!string.IsNullOrEmpty(reason))
                _debugLog?.Invoke($"Worker state: {worker.gameObject.name} {previous} -> {state}; {reason}");
            else
                _debugLog?.Invoke($"Worker state: {worker.gameObject.name} {previous} -> {state}");
        }
    }
}
