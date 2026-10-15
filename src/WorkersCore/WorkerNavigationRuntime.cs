using System;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Applies Workers movement after Valheim's own MonsterAI.UpdateAI has run.
    /// Worker-owned travel uses Character movement input directly; BaseAI.MoveTo remains only as a
    /// compatibility fallback and is no longer the primary locomotion path.
    ///
    /// This ordering is deliberate: MonsterAI/AnimalAI still receive their complete native update.
    /// Workers then overwrite only the final movement input for Worker-owned travel states. This
    /// avoids the old fight where MonsterAI chose an idle/wander command and Workers called MoveTo
    /// afterwards while both systems kept separate path/steering state.
    /// </summary>
    internal static class WorkerNavigationRuntime
    {
        internal const float HomeStopDistance = 3.0f;

        internal static float GetHomeStopDistance(ZDO zdo)
        {
            if (zdo != null)
            {
                string homeKind = zdo.GetString(WorkerZdo.HomeAnchorType, string.Empty);
                if (string.Equals(homeKind, WorkerHomeProfileCatalog.HunterStandKind, StringComparison.Ordinal))
                    return WorkerRestRuntime.HunterStandRestDistance;
                // v0.5.181: the Stone Golem is visually huge, so the generic 3 m arrival radius
                // looks like a teleport when the furniture presentation places it on RestPoint.
                // Make it physically walk almost all the way into the rock ring first.
                if (string.Equals(homeKind, WorkerHomeProfileCatalog.StoneGolemShelterKind, StringComparison.Ordinal))
                    return WorkerRestRuntime.StoneGolemRestDistance;
            }
            return HomeStopDistance;
        }
        internal const float TargetStopDistance = 3.0f;

        private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MethodInfo MoveToMethod = typeof(BaseAI).GetMethod(
            "MoveTo",
            InstanceFlags,
            null,
            new[] { typeof(float), typeof(Vector3), typeof(float), typeof(bool) },
            null);
        private static readonly MethodInfo StopMovingMethod = typeof(BaseAI).GetMethod(
            "StopMoving",
            InstanceFlags,
            null,
            Type.EmptyTypes,
            null);
        private static readonly MethodInfo ResetPatrolPointMethod = typeof(BaseAI).GetMethod(
            "ResetPatrolPoint",
            InstanceFlags,
            null,
            Type.EmptyTypes,
            null);
        private static readonly MethodInfo SetPatrolPointPositionMethod = typeof(BaseAI).GetMethod(
            "SetPatrolPoint",
            InstanceFlags,
            null,
            new[] { typeof(Vector3) },
            null);
        private static readonly MethodInfo ZdoSetPositionMethod = typeof(ZDO).GetMethod(
            "SetPosition",
            InstanceFlags,
            null,
            new[] { typeof(Vector3) },
            null);

        private static Action<string> _debugLog;
        private static bool _reportedMoveToMissing;
        private static bool _reportedMoveFailure;
        private static float _stuckSeconds = 5.5f;
        private static float _nextGlobalRoutePlanAt;
        private const float RouteWaypointReachDistance = 0.90f;
        private const float RouteWaypointStopDistance = 0.55f;
        private const float DirectTurnDegreesPerSecond = 420f;
        private const float DirectArrivalFloor = 0.45f;
        private const float CliffProbeForwardMeters = 2.2f;
        private const float CliffProbeUpMeters = 2.5f;
        private const float CliffProbeDownMeters = 7.0f;
        private const float DefaultMaxSafeDropMeters = 2.4f;
        private const float LoxMaxSafeDropMeters = 1.8f;
        // v0.5.354: cliff safety used Physics.RaycastAll twice (or up to eight times when blocked)
        // on every navigation frame for every moving ground Worker. Keep one reusable hit buffer and
        // cache the immediate safety decision briefly; route/path semantics stay unchanged.
        private const float CliffProbeCacheSeconds = 0.20f;
        private static readonly RaycastHit[] CliffProbeHits = new RaycastHit[32];

        private sealed class NavigationProgress
        {
            internal Vector3 Destination;
            internal Vector3 LastPosition;
            internal float BestDistance;
            internal float LastProgressTime;
            internal int RecoveryAttempt;
            internal string Status;
            internal List<Vector3> Route;
            internal int RouteIndex;
            internal float NextPlanAllowedAt;
            internal float BestWaypointDistance;
            internal float NextDirectProbeAt;
            internal bool DirectPathBlocked;
            internal int LastPlannerExpanded;
            internal bool LastPlannerMiss;
            internal string RouteSource;
            internal string LastNativeStatus;
            internal Vector3 AirDestination;
            internal float AirCruiseHeight;
            internal float NextCliffProbeAt;
            internal Vector3 CliffProbeInput;
            internal Vector3 CliffProbeOutput;
            internal bool CliffProbeOverride;
            internal string CliffProbeStatus;
        }

        private sealed class StorageDeliveryProgress
        {
            internal Vector3 Target;
            internal float BestHorizontalDistance;
            internal float LastProgressTime;
        }

        private static readonly Dictionary<int, NavigationProgress> Progress = new Dictionary<int, NavigationProgress>();
        private static readonly Dictionary<int, StorageDeliveryProgress> StorageDeliveryProgressByWorker = new Dictionary<int, StorageDeliveryProgress>();
        private static readonly Dictionary<int, Character> TrackedAirHaulers = new Dictionary<int, Character>();
        private static readonly Dictionary<int, int> AirNavigationFrame = new Dictionary<int, int>();
        private static readonly Dictionary<int, bool> AirPresentationVisibility = new Dictionary<int, bool>();
        private static readonly List<int> StaleAirHaulerKeys = new List<int>();
        private static readonly List<int> AirHaulerTickKeys = new List<int>();
        private static readonly Dictionary<int, float> StaticHoldNextEnforce = new Dictionary<int, float>();
        private const float StaticHoldReflectionSeconds = 0.25f;

        internal static bool NavigationApiAvailable => true;

        internal static void TrackAirHauler(Character worker)
        {
            if (!IsAirHauler(worker)) return;
            TrackedAirHaulers[worker.GetInstanceID()] = worker;
        }

        /// <summary>
        /// Valheim can stop calling MonsterAI.UpdateAI for a loaded creature when no player is in
        /// its active AI range. Air transport must not stall in that situation. LateUpdate calls
        /// this after normal UpdateAI processing: if the native AI already ran this frame, the
        /// Postfix has marked the Drake and nothing is repeated; otherwise Workers advances the
        /// existing AirHauler state/navigation once on its own.
        ///
        /// This intentionally applies only to loaded Drake workers. Fully unloaded world zones
        /// still remain ZDO-only and are not force-loaded by Workers.
        /// </summary>
        internal static void TickIndependentAirHaulers(float dt)
        {
            if (TrackedAirHaulers.Count == 0) return;

            // Work on a stable key snapshot. TryApplyAfterVanilla can call TrackAirHauler(), and
            // touching TrackedAirHaulers while enumerating the dictionary invalidates its enumerator.
            // A key snapshot keeps registration/removal safe without changing navigation behaviour.
            AirHaulerTickKeys.Clear();
            foreach (int key in TrackedAirHaulers.Keys)
                AirHaulerTickKeys.Add(key);

            StaleAirHaulerKeys.Clear();
            for (int i = 0; i < AirHaulerTickKeys.Count; i++)
            {
                int key = AirHaulerTickKeys[i];
                if (!TrackedAirHaulers.TryGetValue(key, out Character worker))
                    continue;

                if (worker == null || worker.IsDead())
                {
                    StaleAirHaulerKeys.Add(key);
                    continue;
                }

                ZNetView nview = worker.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) continue;
                ZDO zdo = nview.GetZDO();
                if (!WorkerZdo.IsWorker(zdo) || !IsAirHauler(worker))
                {
                    StaleAirHaulerKeys.Add(key);
                    continue;
                }

                if (BoatPassengerRuntime.IsTransported(worker))
                {
                    SetAirHaulerPhysicalPresentation(worker, true);
                    MonsterAI passengerAi = worker.GetComponent<MonsterAI>();
                    if (passengerAi != null) StopMoving(passengerAi);
                    worker.SetMoveDir(Vector3.zero);
                    continue;
                }

                if (WorkerExternalControlBridge.IsControlled(worker))
                {
                    SetAirHaulerPhysicalPresentation(worker, true);
                    continue;
                }

                // A Drake can remain as a live Unity Character beyond Valheim's active player zone.
                // Once the scheduler has virtual authority, independent physical steering must stop
                // immediately or the stale Character and the moving ZDO fight each other forever.
                if (!BackgroundWorkerScheduler.IsLoadedCharacterPhysical(worker))
                {
                    SetAirHaulerPhysicalPresentation(worker, false);
                    MonsterAI virtualAi = worker.GetComponent<MonsterAI>();
                    if (virtualAi != null) StopMoving(virtualAi);
                    worker.SetMoveDir(Vector3.zero);
                    StaleAirHaulerKeys.Add(key);
                    continue;
                }

                SetAirHaulerPhysicalPresentation(worker, true);
                if (AirNavigationFrame.TryGetValue(key, out int frame) && frame == Time.frameCount)
                    continue;

                MonsterAI ai = worker.GetComponent<MonsterAI>();
                if (ai == null) continue;
                TryApplyAfterVanilla(ai, Mathf.Max(dt, 0.001f));
            }

            for (int i = 0; i < StaleAirHaulerKeys.Count; i++)
            {
                int key = StaleAirHaulerKeys[i];
                TrackedAirHaulers.Remove(key);
                AirNavigationFrame.Remove(key);
                AirPresentationVisibility.Remove(key);
                Progress.Remove(key);
            }
            StaleAirHaulerKeys.Clear();
            AirHaulerTickKeys.Clear();
        }

        internal static bool ShouldRunNativeMonsterAi(MonsterAI ai)
        {
            if (ai == null) return true;
            if (!WorkerFastLookupRuntime.TryGetWorker(ai, false, out Character worker, out _, out ZDO zdo)) return true;

            bool isAirHauler = IsAirHauler(worker);

            if (BoatPassengerRuntime.IsTransported(worker))
            {
                if (isAirHauler) SetAirHaulerPhysicalPresentation(worker, true);
                StopMoving(ai);
                worker.SetMoveDir(Vector3.zero);
                return false;
            }

            // Optional external controllers (for example a mount add-on) temporarily own the physical
            // Worker and therefore keep native creature state alive while the add-on supplies final controls.
            if (isAirHauler && WorkerExternalControlBridge.IsControlled(worker))
            {
                SetAirHaulerPhysicalPresentation(worker, true);
                return true;
            }

            // v0.5.35: one authority rule for EVERY Worker species. Merely being streamed/loaded
            // by Valheim no longer permits MonsterAI to run. Outside the player's physical bubble
            // the persistent scheduler owns work progress; the lingering Character is presentation
            // only and must not pathfind, scan, chase or write movement into the ZDO.
            if (!BackgroundWorkerScheduler.IsLoadedCharacterPhysical(worker))
            {
                if (isAirHauler) SetAirHaulerPhysicalPresentation(worker, false);
                StopMoving(ai);
                worker.SetMoveDir(Vector3.zero);
                return false;
            }

            if (isAirHauler) SetAirHaulerPhysicalPresentation(worker, true);
            return true;
        }

        internal static void ConfigureRecovery(float stuckSeconds, float recoveryStepDistance)
        {
            // v0.1.80: repathing is deliberately slow. Direct travel gets several seconds to
            // make real goal progress before the bounded ground-route planner runs.
            _stuckSeconds = Mathf.Clamp(stuckSeconds, 4.5f, 8f);
        }

        internal static string GetDebugStatus(Character worker)
        {
            if (worker == null) return "idle";
            return Progress.TryGetValue(worker.GetInstanceID(), out NavigationProgress progress) && !string.IsNullOrEmpty(progress.Status)
                ? progress.Status
                : "direct steering / no cached route";
        }

        internal static void Initialize(Action<string> debugLog)
        {
            _debugLog = debugLog;
            TrollLumberjackLoadoutRuntime.Initialize(debugLog);
            LumberjackWorkRuntime.Initialize(debugLog);
            StonemasonWorkRuntime.Initialize(debugLog);
            GathererWorkRuntime.Initialize(debugLog);
            HunterWorkRuntime.Initialize(debugLog);
            FisherWorkRuntime.Initialize(debugLog);
            ForestryWorkRuntime.Initialize(debugLog);
            GardenWorkRuntime.Initialize(debugLog);
            GardenHarvestRuntime.Initialize(debugLog);
            WorkerStorageRuntime.Initialize(debugLog);
            GreydwarfThroneRestRuntime.Initialize(debugLog);
            if (MoveToMethod == null)
                _debugLog?.Invoke("Navigation note: BaseAI.MoveTo(float, Vector3, float, bool) was not found. Direct Character steering remains available; only compatibility fallback is disabled.");
        }

        internal static void Shutdown()
        {
            GreydwarfThroneRestRuntime.Shutdown();
            WorkerStorageRuntime.Shutdown();
            GardenHarvestRuntime.Shutdown();
            GardenWorkRuntime.Shutdown();
            ForestryWorkRuntime.Shutdown();
            FisherWorkRuntime.Shutdown();
            HunterWorkRuntime.Shutdown();
            GathererWorkRuntime.Shutdown();
            StonemasonWorkRuntime.Shutdown();
            LumberjackWorkRuntime.Shutdown();
            TrollLumberjackLoadoutRuntime.Shutdown();
            _debugLog = null;
            _reportedMoveFailure = false;
            _reportedMoveToMissing = false;
            Progress.Clear();
            TrackedAirHaulers.Clear();
            AirNavigationFrame.Clear();
            AirPresentationVisibility.Clear();
            StaleAirHaulerKeys.Clear();
            AirHaulerTickKeys.Clear();
            StaticHoldNextEnforce.Clear();
            WorkerNativeRoutePlanner.ResetDiagnostics();
            _nextGlobalRoutePlanAt = 0f;
        }

        /// <summary>
        /// Runs after Valheim's native MonsterAI.UpdateAI. Returns true when Workers applied a
        /// movement/hold command for this frame. The native tick is never suppressed here.
        /// </summary>
        internal static bool TryApplyAfterVanilla(MonsterAI ai, float dt)
        {
            if (ai == null) return false;
            if (MoveToMethod == null && !_reportedMoveToMissing)
            {
                _reportedMoveToMissing = true;
                _debugLog?.Invoke("Worker navigation: native MoveTo unavailable; continuing with direct Character steering.");
            }

            if (!WorkerFastLookupRuntime.TryGetWorker(ai, true, out Character worker, out _, out ZDO zdo)) return false;

            if (BoatPassengerRuntime.IsTransported(worker))
            {
                StopMoving(ai);
                worker.SetMoveDir(Vector3.zero);
                return true;
            }

            // Background authority applies to every worker. This prevents the
            // Workers steering layer from doing target/navigation work on a Character that Valheim
            // happens to keep loaded outside the player's real simulation bubble.
            if (!BackgroundWorkerScheduler.IsLoadedCharacterPhysical(worker) && !WorkerExternalControlBridge.IsControlled(worker))
            {
                StopMoving(ai);
                worker.SetMoveDir(Vector3.zero);
                return false;
            }

            if (WorkerExternalControlBridge.IsControlled(worker))
            {
                AirNavigationFrame[worker.GetInstanceID()] = Time.frameCount;
                return WorkerExternalControlBridge.TryApplyControls(worker, dt);
            }

            if (IsAirHauler(worker))
            {
                // Do not let a lingering Character continue Workers steering after physical ->
                // background authority has switched. The scheduler owns the persistent route now.
                if (!BackgroundWorkerScheduler.IsLoadedCharacterPhysical(worker)) return false;
                TrackAirHauler(worker);
                AirNavigationFrame[worker.GetInstanceID()] = Time.frameCount;
            }

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            GreydwarfThroneRestRuntime.ReleaseIfNeeded(worker, zdo, state);
            if (WorkerRestAnimationRuntime.ReleaseIfNeeded(worker, state))
            {
                // Stone Golem native wake-up/collapse presentation owns the body for this short
                // transition. Keep navigation idle until the authored build-up has finished.
                StopMoving(ai);
                worker.SetMoveDir(Vector3.zero);
                return true;
            }

            // A recruited Lox uses Valheim's real tame flag so it stays friendly, but its vanilla
            // Tameable interaction can also persist a MonsterAI follow target. That native target
            // must never compete with Worker jobs. Follow is exclusively owned by WorkerState.Follow;
            // every other Worker state clears any stale vanilla follow command before work movement.
            // This is intentionally species-agnostic and does not alter the stable pathfinding logic.
            if (state != WorkerState.Follow && ai.GetFollowTarget() != null)
            {
                SetNativeFollowTarget(ai, null);
                _debugLog?.Invoke($"Worker navigation cleared stale native follow target: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)} state={state}");
            }

            // Follow remains the explicit player command. For all other states, a stance-approved
            // native combat target takes precedence over Workers movement/work overrides.
            if (state != WorkerState.Follow && WorkerCombatStanceRuntime.ShouldYieldToCombat(ai, worker, zdo))
            {
                LumberjackWorkRuntime.Reset(worker);
                    StonemasonWorkRuntime.Reset(worker);
                if (state != WorkerState.Combat)
                    zdo.Set(WorkerZdo.State, (int)WorkerState.Combat);
                return false;
            }

            switch (state)
            {
                case WorkerState.Follow:
                {
                    LumberjackWorkRuntime.Reset(worker);
                    StonemasonWorkRuntime.Reset(worker);
                    Player ownerPlayer = FindAssignedPlayer(zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L));
                    if (ownerPlayer == null || ownerPlayer.IsDead())
                    {
                        if (ai.GetFollowTarget() != null)
                            SetNativeFollowTarget(ai, null);
                        return false;
                    }

                    GameObject desiredTarget = ownerPlayer.gameObject;
                    if (ai.GetFollowTarget() != desiredTarget)
                    {
                        SetNativeFollowTarget(ai, desiredTarget);
                        _debugLog?.Invoke($"Follow target applied: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)} -> {ownerPlayer.GetPlayerName()}");
                    }

                    // Vanilla MonsterAI owns the actual follow locomotion. Workers only keeps the
                    // command target synchronized and does not replace Valheim's movement here.
                    return true;
                }

                case WorkerState.ReturnHomeForRest:
                {
                    LumberjackWorkRuntime.Reset(worker);
                    StonemasonWorkRuntime.Reset(worker);
                    Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
                    float restDistance = WorkerRestRuntime.GetRestDistance(zdo);
                    if (TravelDistance(worker, worker.transform.position, home) <= restDistance)
                    {
                        StopMoving(ai);
                        SetPatrolPoint(ai, home);
                        WorkerZdo.ClearTarget(zdo);
                        WorkerRestReason reason = WorkerRestRuntime.GetReason(zdo);
                        WorkerState arrivedState = reason == WorkerRestReason.Recovery
                            ? WorkerState.Recover
                            : WorkerState.Resting;
                        zdo.Set(WorkerZdo.State, (int)arrivedState);
                        _debugLog?.Invoke($"Navigation arrived home for rest: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)} -> {arrivedState}");
                        return true;
                    }

                    return TryMoveTo(ai, worker, dt, home, restDistance, true);
                }

                case WorkerState.Resting:
                case WorkerState.Recover:
                {
                    LumberjackWorkRuntime.Reset(worker);
                    StonemasonWorkRuntime.Reset(worker);
                    if (ai.GetFollowTarget() != null)
                        SetNativeFollowTarget(ai, null);
                    Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
                    float restDistance = WorkerRestRuntime.GetRestDistance(zdo);
                    if (TravelDistance(worker, worker.transform.position, home) > restDistance)
                    {
                        return TryMoveTo(ai, worker, dt, home, restDistance, true);
                    }
                    // v0.5.168: furniture presentation and MonsterAI must agree on one exact hold point.
                    // Previously the presentation snapped the Worker to WorkerRestPoint while the AI patrol
                    // point remained the HomeAnchor. On large homes such as the Troll-Lehnplatz those points
                    // are visibly offset, so vanilla AI and the presentation layer pulled the creature back
                    // and forth every frame (the visible resting jitter). Keep all travel/range checks on the
                    // durable HomeAnchor, but once home use the loaded RestPoint as the static AI hold target.
                    Vector3 restHoldPoint = ResolveRestHoldPoint(zdo, home);
                    MaintainStaticHold(ai, worker, restHoldPoint, true);
                    bool specializedRestPose = GreydwarfThroneRestRuntime.TryMaintainSeat(worker, zdo);
                    if (!specializedRestPose)
                    {
                        WorkerRestStationPresentationRuntime.TryMaintain(worker, zdo);
                        WorkerRestAnimationRuntime.TryMaintain(worker, zdo);
                    }
                    return true;
                }

                case WorkerState.ReturnHome:
                {
                    LumberjackWorkRuntime.Reset(worker);
                    StonemasonWorkRuntime.Reset(worker);
                    Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
                    float homeStopDistance = GetHomeStopDistance(zdo);
                    if (TravelDistance(worker, worker.transform.position, home) <= homeStopDistance)
                    {
                        StopMoving(ai);
                        SetPatrolPoint(ai, home);
                        WorkerZdo.ClearTarget(zdo);
                        zdo.Set(WorkerZdo.State, (int)WorkerState.WaitAtHome);
                        _debugLog?.Invoke($"Navigation arrived home and is waiting: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)}");
                        return true;
                    }

                    return TryMoveTo(ai, worker, dt, home, homeStopDistance, true);
                }

                case WorkerState.WaitAtHome:
                {
                    LumberjackWorkRuntime.Reset(worker);
                    StonemasonWorkRuntime.Reset(worker);
                    if (ai.GetFollowTarget() != null)
                        SetNativeFollowTarget(ai, null);
                    Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
                    float homeStopDistance = GetHomeStopDistance(zdo);
                    if (TravelDistance(worker, worker.transform.position, home) > homeStopDistance + 0.10f)
                    {
                        zdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
                        return TryMoveTo(ai, worker, dt, home, homeStopDistance, true);
                    }
                    // Use the same authored RestPoint for both vanilla AI hold and visual presentation.
                    // This removes the HomeAnchor <-> RestPoint tug-of-war without touching pathfinding.
                    Vector3 restHoldPoint = ResolveRestHoldPoint(zdo, home);
                    MaintainStaticHold(ai, worker, restHoldPoint, true);
                    WorkerZdo.ClearTarget(zdo);
                    bool specializedRestPose = GreydwarfThroneRestRuntime.TryMaintainSeat(worker, zdo);
                    if (!specializedRestPose)
                    {
                        WorkerRestStationPresentationRuntime.TryMaintain(worker, zdo);
                        WorkerRestAnimationRuntime.TryMaintain(worker, zdo);
                    }
                    return true;
                }

                case WorkerState.Standby:
                {
                    LumberjackWorkRuntime.Reset(worker);
                    StonemasonWorkRuntime.Reset(worker);
                    if (ai.GetFollowTarget() != null)
                        SetNativeFollowTarget(ai, null);

                    if (!WorkerZdo.TryReadTarget(zdo, out _, out string standbyKind, out Vector3 standbyPoint) ||
                        !string.Equals(standbyKind, WorkerStandbyRuntime.TargetKind, StringComparison.Ordinal))
                    {
                        zdo.Set(WorkerZdo.State, WorkerWorkAreaRules.HasRealHome(zdo)
                            ? (int)WorkerState.ReturnHome
                            : (int)WorkerState.Idle);
                        return true;
                    }

                    WorkerRole standbyRole = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                    WorkOrder standbyOrder = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
                    float stopDistance = WorkerStandbyRuntime.GetStopDistance(zdo, standbyRole, standbyOrder);
                    if (TravelDistance(worker, worker.transform.position, standbyPoint) > stopDistance + 0.10f)
                        return TryMoveTo(ai, worker, dt, standbyPoint, stopDistance, false);

                    // Keep the explicit post target persisted. WorkerAiController continues polling
                    // priorities every decision tick and can replace Standby immediately when real
                    // work appears (for example prey spawning while a Hunter guards its platform).
                    StopMoving(ai);
                    SetPatrolPoint(ai, standbyPoint);
                    MaintainStaticHold(ai, worker, standbyPoint, true);
                    return true;
                }

                case WorkerState.MoveToWorkArea:
                {
                    LumberjackWorkRuntime.Reset(worker);
                    StonemasonWorkRuntime.Reset(worker);

                    // v0.5.183: Stone Golems/Stonemasons never use the flag center as a travel
                    // destination. Older builds could persist MoveToWorkArea in the Worker ZDO,
                    // so merely preventing new assignments was not enough: an existing Golem
                    // would keep walking to the flag after updating the mod. Migrate that stale
                    // state in-place before any movement happens. The AI controller will scan the
                    // assigned quarry radius directly and either choose a real stone/ore target or
                    // return to its rest station when no work exists. Navigator v5 itself is unchanged.
                    WorkerRole moveRole = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                    if (moveRole == WorkerRole.Stonemason)
                    {
                        StopMoving(ai);
                        WorkerZdo.ClearTarget(zdo);
                        zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                        _debugLog?.Invoke($"Navigation migrated stale MoveToWorkArea -> FindJob for Stonemason: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)}");
                        return true;
                    }

                    if (!WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 workCenter, out float workRadius))
                    {
                        zdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
                        return true;
                    }

                    float enterDistance = Mathf.Clamp(workRadius * 0.35f, 4f, 12f);
                    if (TravelDistance(worker, worker.transform.position, workCenter) <= enterDistance)
                    {
                        StopMoving(ai);
                        SetPatrolPoint(ai, workCenter);
                        WorkerZdo.ClearTarget(zdo);
                        zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                        _debugLog?.Invoke($"Navigation entered work area: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)}");
                        return true;
                    }

                    return TryMoveTo(ai, worker, dt, workCenter, enterDistance, true);
                }

                case WorkerState.MoveToTarget:
                {
                    LumberjackWorkRuntime.Reset(worker);
                    StonemasonWorkRuntime.Reset(worker);
                    if (!WorkerZdo.TryReadTarget(zdo, out string targetName, out string targetKind, out Vector3 targetPosition))
                    {
                        zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                        return true;
                    }

                    bool storageTarget =
                        string.Equals(targetKind, WorkerStorageRuntime.SeedSourceTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, WorkerStorageRuntime.GardenResourceSourceTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, WorkerStorageRuntime.HaulSourceTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, WorkerStorageRuntime.ExportHaulSourceTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, WorkerStorageRuntime.SortImportSourceTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, WorkerStorageRuntime.SortBaseSourceTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, WorkerProvisioningLogisticsRuntime.SourceTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, AirHaulerWorkRuntime.OutpostSourceTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, AirHaulerWorkRuntime.BaseSourceTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, AirHaulerWorkRuntime.SortInboundSourceTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, AirHaulerWorkRuntime.SortOutboundSourceTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, FirekeeperWorkRuntime.FuelSourceTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, FirekeeperWorkRuntime.InputSourceTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, BakerWorkRuntime.InputSourceTargetKind, StringComparison.Ordinal);
                    bool machineTarget =
                        string.Equals(targetKind, FirekeeperWorkRuntime.SmelterTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, FirekeeperWorkRuntime.SmelterInputTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, FirekeeperAssignedFireRuntime.ManagedFireTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, FirekeeperZoneFireRuntime.ZoneFireTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, FirekeeperOvenRuntime.TargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, BakerWorkRuntime.TargetKind, StringComparison.Ordinal);
                    bool repairTarget = string.Equals(targetKind, BaseRepairWorkRuntime.TargetKind, StringComparison.Ordinal);
                    bool hunterPreyTarget = string.Equals(targetKind, "HunterPrey", StringComparison.Ordinal);
                    bool hunterVantageTarget = string.Equals(targetKind, HunterWorkRuntime.VantageTargetKind, StringComparison.Ordinal);
                    bool butcherTarget = string.Equals(targetKind, LivestockButcherRuntime.TargetKind, StringComparison.Ordinal);
                    bool livestockPenTarget = LivestockPenRuntime.IsTargetKind(targetKind);
                    bool stonemasonStoneTarget =
                        string.Equals(targetKind, StoneTarget.MineRock5Kind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, StoneTarget.MineRockKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, StoneTarget.DestructibleKind, StringComparison.Ordinal);
                    bool interactionTarget = storageTarget || machineTarget || repairTarget;
                    float arrivalDistance = (interactionTarget || hunterPreyTarget || butcherTarget || livestockPenTarget)
                        ? WorkerInteractionRuntime.HorizontalDistance(worker.transform.position, targetPosition)
                        : Vector3.Distance(worker.transform.position, targetPosition);
                    float arrivalRadius = hunterPreyTarget
                        ? HunterWorkRuntime.ShootRange
                        : hunterVantageTarget
                            ? TargetStopDistance
                        : butcherTarget
                            ? LivestockButcherRuntime.WorkRange
                        : livestockPenTarget
                            ? LivestockPenRuntime.GetArrivalRadius(targetKind)
                        : stonemasonStoneTarget
                            ? StonemasonWorkRuntime.ApproachDistance
                            : (interactionTarget ? WorkerInteractionRuntime.AccessRadius : TargetStopDistance);

                    if (arrivalDistance <= arrivalRadius)
                    {
                        LocalWorkFallbackRuntime.ResetNavigationProbe(worker);
                        if (interactionTarget) WorkerInteractionRuntime.ClearApproach(worker);
                        StopMoving(ai);
                        WorkerState arrivalState =
                            string.Equals(targetKind, GathererTargetFinder.TargetKind, StringComparison.Ordinal) ||
                            string.Equals(targetKind, GardenHarvestTargetFinder.TargetKind, StringComparison.Ordinal) ||
                            string.Equals(targetKind, ForestryTargetFinder.SeedTargetKind, StringComparison.Ordinal) ||
                            string.Equals(targetKind, HunterWorkRuntime.LootTargetKind, StringComparison.Ordinal) ||
                            storageTarget
                                ? WorkerState.Collect
                                : WorkerState.Work;
                        zdo.Set(WorkerZdo.State, (int)arrivalState);
                        _debugLog?.Invoke($"Navigation arrived at work target: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)} -> {arrivalState}");
                        return true;
                    }

                    bool localFallbackEligible =
                        string.Equals(targetKind, GathererTargetFinder.TargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, GardenHarvestTargetFinder.TargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, GardenTargetFinder.PlantTargetKind, StringComparison.Ordinal) ||
                        string.Equals(targetKind, ForestryTargetFinder.PlantTargetKind, StringComparison.Ordinal) ||
                        // v0.5.42: Station/specialist work follows the same Palworld-style rule:
                        // walking is presentation, but a proven navigation stall must not cancel work.
                        machineTarget || repairTarget || hunterPreyTarget || butcherTarget || livestockPenTarget ||
                        (storageTarget &&
                         !string.Equals(targetKind, AirHaulerWorkRuntime.OutpostSourceTargetKind, StringComparison.Ordinal) &&
                         !string.Equals(targetKind, AirHaulerWorkRuntime.BaseSourceTargetKind, StringComparison.Ordinal));
                    if (localFallbackEligible && LocalWorkFallbackRuntime.ShouldActivateForNavigation(worker, zdo, targetName, targetKind, targetPosition))
                    {
                        StopMoving(ai);
                        WorkerState fallbackState =
                            string.Equals(targetKind, GathererTargetFinder.TargetKind, StringComparison.Ordinal) ||
                            string.Equals(targetKind, GardenHarvestTargetFinder.TargetKind, StringComparison.Ordinal)
                                ? WorkerState.Collect
                                : WorkerState.Work;
                        zdo.Set(WorkerZdo.State, (int)fallbackState);
                        _debugLog?.Invoke($"Local work fallback activated: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)} target={targetName} kind={targetKind} distance={arrivalDistance:0.0}m -> {fallbackState}");
                        return true;
                    }

                    Vector3 destination = interactionTarget
                        ? WorkerInteractionRuntime.GetApproachPoint(worker, targetPosition)
                        : targetPosition;
                    float moveStopDistance = interactionTarget
                        ? WorkerInteractionRuntime.ApproachStopDistance
                        : arrivalRadius;

                    // Stonework is special: the persisted target is deliberately the real collider
                    // surface. Feeding that point directly into path probing makes the quarry rock
                    // look like the obstacle blocking the route. Navigate to a fixed point just in
                    // front of the selected face instead, then keep the normal surface-distance test
                    // above as the authority for switching to Work. No navigation-core behaviour is
                    // changed for any other worker.
                    if (stonemasonStoneTarget && StonemasonWorkRuntime.TryGetNavigationApproachPoint(worker, out Vector3 stoneApproach))
                    {
                        destination = stoneApproach;
                        moveStopDistance = 0.45f;
                    }

                    return TryMoveTo(ai, worker, dt, destination, moveStopDistance, true);
                }

                case WorkerState.Collect:
                {
                    LumberjackWorkRuntime.Reset(worker);
                    StonemasonWorkRuntime.Reset(worker);
                    StopMoving(ai);
                    WorkerZdo.TryReadTarget(zdo, out _, out string collectKind, out _);
                    if (string.Equals(collectKind, GardenHarvestTargetFinder.TargetKind, StringComparison.Ordinal))
                        GardenHarvestRuntime.TryHarvest(worker, zdo);
                    else if (string.Equals(collectKind, WorkerStorageRuntime.SeedSourceTargetKind, StringComparison.Ordinal))
                        WorkerStorageRuntime.TryCollectSeedFromStorage(worker, zdo);
                    else if (string.Equals(collectKind, WorkerStorageRuntime.GardenResourceSourceTargetKind, StringComparison.Ordinal))
                        WorkerStorageRuntime.TryCollectGardenResourceFromStorage(worker, zdo);
                    else if (string.Equals(collectKind, WorkerStorageRuntime.HaulSourceTargetKind, StringComparison.Ordinal))
                        WorkerStorageRuntime.TryCollectHaulCargo(worker, zdo);
                    else if (string.Equals(collectKind, AirHaulerWorkRuntime.OutpostSourceTargetKind, StringComparison.Ordinal) ||
                             string.Equals(collectKind, AirHaulerWorkRuntime.BaseSourceTargetKind, StringComparison.Ordinal))
                        AirHaulerWorkRuntime.TryCollect(worker, zdo, collectKind);
                    else if (string.Equals(collectKind, WorkerProvisioningLogisticsRuntime.SourceTargetKind, StringComparison.Ordinal))
                        WorkerProvisioningLogisticsRuntime.TryCollect(worker, zdo);
                    else if (string.Equals(collectKind, WorkerStorageRuntime.ExportHaulSourceTargetKind, StringComparison.Ordinal) ||
                             string.Equals(collectKind, WorkerStorageRuntime.SortImportSourceTargetKind, StringComparison.Ordinal) ||
                             string.Equals(collectKind, WorkerStorageRuntime.SortBaseSourceTargetKind, StringComparison.Ordinal) ||
                             string.Equals(collectKind, AirHaulerWorkRuntime.SortInboundSourceTargetKind, StringComparison.Ordinal) ||
                             string.Equals(collectKind, AirHaulerWorkRuntime.SortOutboundSourceTargetKind, StringComparison.Ordinal))
                        WorkerLogisticsRuntime.TryCollectLogisticsCargo(worker, zdo, collectKind);
                    else if (string.Equals(collectKind, ForestryTargetFinder.SeedTargetKind, StringComparison.Ordinal))
                        ForestryWorkRuntime.TryCollectSeed(worker, zdo);
                    else if (string.Equals(collectKind, FirekeeperWorkRuntime.FuelSourceTargetKind, StringComparison.Ordinal))
                        FirekeeperWorkRuntime.TryCollectFuel(worker, zdo);
                    else if (string.Equals(collectKind, FirekeeperWorkRuntime.InputSourceTargetKind, StringComparison.Ordinal))
                        FirekeeperWorkRuntime.TryCollectInput(worker, zdo);
                    else if (string.Equals(collectKind, BakerWorkRuntime.InputSourceTargetKind, StringComparison.Ordinal))
                        BakerWorkRuntime.TryCollectInput(worker, zdo);
                    else if (string.Equals(collectKind, HunterWorkRuntime.LootTargetKind, StringComparison.Ordinal))
                        HunterWorkRuntime.TryCollectLoot(worker, zdo);
                    else
                        GathererWorkRuntime.TryCollect(worker, zdo);
                    return true;
                }

                case WorkerState.NeedsResource:
                {
                    LumberjackWorkRuntime.Reset(worker);
                    StonemasonWorkRuntime.Reset(worker);

                    WorkerRole waitingRole = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                    WorkOrder waitingOrder = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
                    if (waitingRole == WorkerRole.Gardener &&
                        (waitingOrder == WorkOrder.Forestry || waitingOrder == WorkOrder.Garden) &&
                        !WorkerZdo.HasCargo(zdo))
                    {
                        // With the logistics network active, Gardeners/Foresters wait in their work
                        // zone for the requested FieldDepot supply instead of walking back through the
                        // whole base. Old settlements without a FieldDepot retain the Home fallback.
                        if (WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 supplyCenter, out float supplyRadius) &&
                            WorkerLogisticsRuntime.TryFindFieldDepotForWorkArea(worker, zdo, out _, out _))
                        {
                            float enter = Mathf.Clamp(supplyRadius * 0.35f, 3f, 10f);
                            if (Vector3.Distance(worker.transform.position, supplyCenter) > enter)
                                return TryMoveTo(ai, worker, dt, supplyCenter, enter, true);
                            MaintainStaticHold(ai, worker, supplyCenter, true);
                            return true;
                        }

                        Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
                        if (Vector3.Distance(worker.transform.position, home) > HomeStopDistance)
                            return TryMoveTo(ai, worker, dt, home, HomeStopDistance, true);
                        MaintainStaticHold(ai, worker, home, true);
                        return true;
                    }

                    if (WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 waitingCenter, out _))
                        MaintainStaticHold(ai, worker, waitingCenter, true);
                    else
                        MaintainStaticHold(ai, worker, worker.transform.position, false);
                    return true;
                }

                case WorkerState.Deliver:
                {
                    LumberjackWorkRuntime.Reset(worker);
                    StonemasonWorkRuntime.Reset(worker);

                    if (WorkerZdo.TryReadTarget(zdo, out _, out string deliveryKind, out Vector3 storageTarget) &&
                        (string.Equals(deliveryKind, WorkerStorageRuntime.DepositTargetKind, StringComparison.Ordinal) ||
                         string.Equals(deliveryKind, WorkerStorageRuntime.OverflowDepositTargetKind, StringComparison.Ordinal) ||
                         string.Equals(deliveryKind, WorkerStorageRuntime.FieldDepotDepositTargetKind, StringComparison.Ordinal) ||
                         string.Equals(deliveryKind, WorkerStorageRuntime.ImportDepositTargetKind, StringComparison.Ordinal) ||
                         string.Equals(deliveryKind, WorkerStorageRuntime.SupplyFieldDepotDepositTargetKind, StringComparison.Ordinal) ||
                         string.Equals(deliveryKind, WorkerStorageRuntime.SortBaseDepositTargetKind, StringComparison.Ordinal) ||
                         string.Equals(deliveryKind, WorkerStorageRuntime.ExportDepositTargetKind, StringComparison.Ordinal) ||
                         string.Equals(deliveryKind, WorkerProvisioningLogisticsRuntime.HomeDepositTargetKind, StringComparison.Ordinal) ||
                         string.Equals(deliveryKind, AirHaulerWorkRuntime.BaseDepositTargetKind, StringComparison.Ordinal) ||
                         string.Equals(deliveryKind, AirHaulerWorkRuntime.OutpostDepositTargetKind, StringComparison.Ordinal) ||
                         string.Equals(deliveryKind, AirHaulerWorkRuntime.SortBaseDepositTargetKind, StringComparison.Ordinal)))
                    {
                        bool groundDelivery =
                            !string.Equals(deliveryKind, AirHaulerWorkRuntime.BaseDepositTargetKind, StringComparison.Ordinal) &&
                            !string.Equals(deliveryKind, AirHaulerWorkRuntime.OutpostDepositTargetKind, StringComparison.Ordinal);
                        string deliveryTargetName = string.Empty;
                        WorkerZdo.TryReadTarget(zdo, out deliveryTargetName, out _, out _);
                        bool localDeliveryFallback = groundDelivery &&
                                                     LocalWorkFallbackRuntime.ShouldActivateForNavigation(
                                                         worker, zdo, deliveryTargetName, deliveryKind, storageTarget);

                        if (WorkerStorageRuntime.IsInsideContainerAura(worker.transform.position, storageTarget) || localDeliveryFallback)
                        {
                            StorageDeliveryProgressByWorker.Remove(worker.GetInstanceID());
                            WorkerStorageRuntime.ClearContainerApproach(worker);
                            StopMoving(ai);
                            if (string.Equals(deliveryKind, AirHaulerWorkRuntime.BaseDepositTargetKind, StringComparison.Ordinal) ||
                                string.Equals(deliveryKind, AirHaulerWorkRuntime.OutpostDepositTargetKind, StringComparison.Ordinal))
                                AirHaulerWorkRuntime.TryDeposit(worker, zdo, deliveryKind);
                            else if (string.Equals(deliveryKind, WorkerStorageRuntime.FieldDepotDepositTargetKind, StringComparison.Ordinal))
                                WorkerStorageRuntime.TryDepositCargoToFieldDepot(worker, zdo);
                            else if (string.Equals(deliveryKind, WorkerStorageRuntime.ImportDepositTargetKind, StringComparison.Ordinal))
                                WorkerStorageRuntime.TryDepositCargoToImport(worker, zdo);
                            else if (string.Equals(deliveryKind, WorkerProvisioningLogisticsRuntime.HomeDepositTargetKind, StringComparison.Ordinal))
                                WorkerProvisioningLogisticsRuntime.TryDeposit(worker, zdo);
                            else if (string.Equals(deliveryKind, WorkerStorageRuntime.SupplyFieldDepotDepositTargetKind, StringComparison.Ordinal) ||
                                     string.Equals(deliveryKind, WorkerStorageRuntime.SortBaseDepositTargetKind, StringComparison.Ordinal) ||
                                     string.Equals(deliveryKind, WorkerStorageRuntime.ExportDepositTargetKind, StringComparison.Ordinal) ||
                                     string.Equals(deliveryKind, AirHaulerWorkRuntime.SortBaseDepositTargetKind, StringComparison.Ordinal))
                                WorkerLogisticsRuntime.TryDepositLogisticsCargo(worker, zdo, deliveryKind);
                            else
                                WorkerStorageRuntime.TryDepositCargoToStorage(worker, zdo);
                            if (localDeliveryFallback) LocalWorkFallbackRuntime.Clear(zdo);
                            return true;
                        }

                        Vector3 approach = WorkerStorageRuntime.GetContainerApproachPoint(worker, storageTarget);
                        return TryMoveTo(ai, worker, dt, approach, WorkerStorageRuntime.ContainerApproachStopDistance, true);
                    }

                    // v0.1.99: Never turn a transiently missing delivery target into a physical
                    // world drop. Haulers (and Gatherers immediately after pickup) intentionally
                    // enter Deliver before the next decision tick assigns the destination chest.
                    // The old generic fallback could therefore spill real cargo at the WorkArea
                    // center during that short hand-off window. Keep the cargo on the worker and
                    // let WorkerAiController resolve/re-resolve the proper FieldDepot/Import target.
                    MaintainStaticHold(ai, worker, worker.transform.position, false);
                    return true;
                }

                case WorkerState.Work:
                    // Hold after the native tick so vanilla wandering cannot pull the worker away,
                    // then let the role-specific work runtime perform its native/real world action.
                    StopMoving(ai);
                    WorkerRole role = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                    if (role == WorkerRole.Lumberjack)
                        LumberjackWorkRuntime.TryTick(ai, worker, zdo);
                    else if (role == WorkerRole.Stonemason)
                        StonemasonWorkRuntime.TryTick(ai, worker, zdo);
                    else if (role == WorkerRole.Gardener && (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None) == WorkOrder.Forestry)
                        ForestryWorkRuntime.TryPlant(worker, zdo);
                    else if (role == WorkerRole.Gardener && (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None) == WorkOrder.Garden)
                        GardenWorkRuntime.TryPlant(worker, zdo);
                    else if (role == WorkerRole.Hunter)
                    {
                        HunterWorkRuntime.TryTickRangedAttack(worker, zdo, out _);
                    }
                    else if (role == WorkerRole.Fisher)
                    {
                        FisherWorkRuntime.TryCatch(worker, zdo);
                    }
                    else if (role == WorkerRole.Baker ||
                             (role == WorkerRole.Gardener && (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None) == WorkOrder.Bake))
                    {
                        // Fuling/Greydwarf Shamans keep the stable Gardener species role, but their
                        // per-worker task profile can select the Cook/Bake work order. Production
                        // movement already uses the shared Baker target kinds, so the Work state must
                        // dispatch by active work order as well as by stable role. Without this branch
                        // a Shaman reached the cauldron/prep table, entered Work, then stood there
                        // forever because only the legacy dedicated Baker role executed the recipe.
                        BakerWorkRuntime.TryWork(worker, zdo);
                    }
                    else if (role == WorkerRole.Butcher)
                    {
                        if (!LivestockPenRuntime.TryWork(worker, zdo))
                            LivestockButcherRuntime.TryWork(worker, zdo);
                    }
                    else if (role == WorkerRole.Smith)
                    {
                        if (WorkerZdo.TryReadTarget(zdo, out _, out string smithTargetKind, out _))
                        {
                            if (string.Equals(smithTargetKind, FirekeeperWorkRuntime.SmelterInputTargetKind, StringComparison.Ordinal))
                                FirekeeperWorkRuntime.TryAddInput(worker, zdo);
                            else if (string.Equals(smithTargetKind, FirekeeperWorkRuntime.SmelterTargetKind, StringComparison.Ordinal))
                                FirekeeperWorkRuntime.TryRefuel(worker, zdo);
                            else if (string.Equals(smithTargetKind, BaseRepairWorkRuntime.TargetKind, StringComparison.Ordinal))
                                BaseRepairWorkRuntime.TryTickRepair(worker, zdo, out _);
                        }
                    }
                    else if (role == WorkerRole.Firekeeper)
                    {
                        if (WorkerZdo.TryReadTarget(zdo, out _, out string fireTargetKind, out _))
                        {
                            if (string.Equals(fireTargetKind, FirekeeperAssignedFireRuntime.ManagedFireTargetKind, StringComparison.Ordinal))
                                FirekeeperAssignedFireRuntime.TickAtAssignedFire(worker, zdo);
                            else if (string.Equals(fireTargetKind, FirekeeperZoneFireRuntime.ZoneFireTargetKind, StringComparison.Ordinal))
                                FirekeeperZoneFireRuntime.TryTend(worker, zdo);
                            else if (string.Equals(fireTargetKind, FirekeeperOvenRuntime.TargetKind, StringComparison.Ordinal))
                                FirekeeperOvenRuntime.TryTend(worker, zdo);
                            else if (string.Equals(fireTargetKind, FirekeeperWorkRuntime.SmelterTargetKind, StringComparison.Ordinal))
                                FirekeeperWorkRuntime.TryMagicallyRefuel(worker, zdo);
                        }
                    }
                    return true;

                default:
                    return false;
            }
        }

        internal static void SetNativeFollowTarget(MonsterAI ai, GameObject target)
        {
            if (ai == null) return;

            if (target != null && ResetPatrolPointMethod != null)
            {
                try { ResetPatrolPointMethod.Invoke(ai, null); }
                catch { }
            }

            ai.SetFollowTarget(target);
        }

        private static Player FindAssignedPlayer(long playerId)
        {
            if (playerId == 0L) return null;

            Player local = Player.m_localPlayer;
            if (local != null && !local.IsDead() && local.GetPlayerID() == playerId) return local;

            List<Player> players = WorkerCharacterIndexRuntime.GetLivePlayers();
            for (int i = 0; i < players.Count; i++)
            {
                Player player = players[i];
                if (player != null && !player.IsDead() && player.GetPlayerID() == playerId)
                    return player;
            }

            return null;
        }

        private static bool TryMoveTo(BaseAI ai, Character worker, float dt, Vector3 destination, float stopDistance, bool run)
        {
            if (worker == null) return DriveDirect(ai, worker, dt, destination, stopDistance, run);

            // Drake Air Haulers deliberately bypass the entire ground-navigation stack. Their
            // vanilla BaseAI already knows how to fly, avoid terrain and maintain airborne movement.
            // Workers only supplies the high-level destination; no native/coarse ground route is built.
            if (IsAirHauler(worker))
                return TryMoveAirNative(ai, worker, dt, destination, stopDistance, run);

            int key = worker.GetInstanceID();
            Vector3 position = worker.transform.position;
            float distance = Vector3.Distance(position, destination);
            float now = Time.unscaledTime;

            if (!Progress.TryGetValue(key, out NavigationProgress progress) ||
                Vector3.Distance(progress.Destination, destination) > 1.0f)
            {
                progress = new NavigationProgress
                {
                    Destination = destination,
                    LastPosition = position,
                    BestDistance = distance,
                    LastProgressTime = now,
                    RecoveryAttempt = 0,
                    Status = $"direct route | d={distance:0.0}m",
                    Route = null,
                    RouteIndex = 0,
                    NextPlanAllowedAt = now,
                    BestWaypointDistance = float.MaxValue,
                    NextDirectProbeAt = now,
                    DirectPathBlocked = false,
                    LastPlannerExpanded = 0,
                    LastPlannerMiss = false,
                    RouteSource = null,
                    LastNativeStatus = null
                };
                Progress[key] = progress;
            }

            // Meaningful navigation progress is measured against the actual goal, not by raw
            // movement. A Worker that runs in circles used to reset the stuck timer forever simply
            // because its transform moved by ~1 m. Lateral movement does not reset this timer; if the
            // Worker has not become measurably closer to the goal for the full stuck window,
            // Workers builds one coarse detour route.
            bool closer = distance < progress.BestDistance - 0.40f;
            bool physicallyMoved = Vector3.Distance(progress.LastPosition, position) >= 1.0f;
            if (closer)
            {
                progress.BestDistance = distance;
                progress.LastProgressTime = now;
            }
            if (physicallyMoved)
            {
                progress.LastPosition = position;
            }

            // v0.1.94: cheap, throttled body-line probe. If a real wall blocks the direct line,
            // do not wait for the generic 5.5 s no-progress timer before asking the bounded A*
            // fallback for a route. This probe runs at most a few times per second per travelling
            // Worker and only while no cached route exists.
            if ((progress.Route == null || progress.Route.Count == 0) && now >= progress.NextDirectProbeAt)
            {
                progress.NextDirectProbeAt = now + 0.35f;
                progress.DirectPathBlocked = !WorkerGroundRoutePlanner.IsDirectPathClear(position, destination);
                if (!progress.DirectPathBlocked)
                {
                    progress.LastPlannerMiss = false;
                    progress.LastPlannerExpanded = 0;
                }
            }

            if (progress.Route == null || progress.Route.Count == 0)
            {
                if (progress.DirectPathBlocked)
                {
                    progress.Status = progress.LastPlannerMiss
                        ? $"direct BLOCKED | last path miss {progress.LastPlannerExpanded} nodes | d={distance:0.0}m"
                        : $"direct BLOCKED -> path request | d={distance:0.0}m";
                }
                else if (physicallyMoved && !closer)
                {
                    progress.Status = $"direct detour | d={distance:0.0}m";
                }
                else
                {
                    progress.Status = $"direct route | d={distance:0.0}m";
                }
            }

            if (distance <= stopDistance)
            {
                progress.Route = null;
                progress.RouteIndex = 0;
                progress.RouteSource = null;
                progress.Status = $"arrived | d={distance:0.0}m";
                return DriveDirect(ai, worker, dt, destination, stopDistance, run);
            }

            // Once a high-level route exists, keep it. Do not continually recalculate it. Direct Character steering handles the segment between two cached waypoints.
            if (progress.Route != null && progress.RouteIndex < progress.Route.Count)
            {
                // v0.1.93: route corners must actually be reached. The old 2.6 m acceptance radius
                // was larger than the 1.5 m planner grid and could therefore skip the first detour
                // waypoint immediately after a route was built. That collapsed a valid wall detour
                // back into a direct line to the final target. Keep the corner radius deliberately
                // smaller than one grid cell and smaller than normal base-wall geometry.
                while (progress.RouteIndex < progress.Route.Count &&
                       Vector3.Distance(position, progress.Route[progress.RouteIndex]) <= RouteWaypointReachDistance)
                {
                    progress.RouteIndex++;
                    progress.LastProgressTime = now;
                    progress.LastPosition = position;
                    progress.BestWaypointDistance = float.MaxValue;
                }

                if (progress.RouteIndex < progress.Route.Count)
                {
                    Vector3 waypoint = progress.Route[progress.RouteIndex];
                    float waypointDistance = Vector3.Distance(position, waypoint);
                    if (waypointDistance < progress.BestWaypointDistance - 0.35f)
                    {
                        progress.BestWaypointDistance = waypointDistance;
                        progress.LastProgressTime = now;
                    }
                    string routeLabel = string.IsNullOrEmpty(progress.RouteSource) ? "path" : progress.RouteSource;
                    progress.Status = $"{routeLabel} path {progress.RouteIndex + 1}/{progress.Route.Count} | wp={waypointDistance:0.0}m | target={distance:0.0}m";

                    // Never micromanage the creature into the exact centre of a grid cell. A generous
                    // waypoint radius lets the Worker pass corners without
                    // braking/turning at every anchor. Replanning still only happens after the full
                    // no-goal-progress window.
                    if (now - progress.LastProgressTime < _stuckSeconds)
                        return DriveDirect(ai, worker, dt, waypoint, RouteWaypointStopDistance, run);

                    progress.Route = null;
                    progress.RouteIndex = 0;
                    progress.RouteSource = null;
                    progress.BestWaypointDistance = float.MaxValue;
                    progress.RecoveryAttempt++;
                    progress.Status = $"cached path stalled -> replan #{progress.RecoveryAttempt}";
                }
            }

            bool needsDetour = progress.DirectPathBlocked || now - progress.LastProgressTime >= _stuckSeconds;
            if (needsDetour)
            {
                // Route planning is globally staggered so several workers cannot perform their
                // bounded A* search in the same frame. A known blocked direct line gets priority
                // immediately; ordinary open-ground stalls still use the old no-progress timer.
                if (now >= progress.NextPlanAllowedAt && now >= _nextGlobalRoutePlanAt)
                {
                    _nextGlobalRoutePlanAt = now + 0.80f;
                    progress.NextPlanAllowedAt = now + (progress.DirectPathBlocked ? 2.5f : 2.0f);

                    List<Vector3> route = null;
                    int expanded = 0;
                    string routeSource = null;

                    // v0.2.10: let Valheim solve Valheim geometry. We only borrow the native
                    // Pathfinding.GetPath waypoints; Workers still applies the final Character
                    // movement after MonsterAI, preserving the stable v0.1.80 direct steering.
                    if (WorkerNativeRoutePlanner.TryBuildRoute(ai, position, destination, out route, out string nativeStatus) &&
                        route != null && route.Count > 0)
                    {
                        routeSource = "native";
                        progress.LastNativeStatus = nativeStatus;
                    }
                    else
                    {
                        progress.LastNativeStatus = nativeStatus;
                        if (WorkerGroundRoutePlanner.TryBuildRoute(position, destination, stopDistance, out route, out expanded) &&
                            route != null && route.Count > 0)
                        {
                            routeSource = "coarse";
                        }
                    }

                    if (route != null && route.Count > 0)
                    {
                        progress.Route = route;
                        progress.RouteIndex = 0;
                        progress.RouteSource = routeSource;
                        progress.LastProgressTime = now;
                        progress.LastPosition = position;
                        progress.BestDistance = distance;
                        progress.BestWaypointDistance = Vector3.Distance(position, route[0]);
                        progress.RecoveryAttempt++;
                        progress.LastPlannerExpanded = expanded;
                        progress.LastPlannerMiss = false;
                        progress.DirectPathBlocked = false;
                        progress.Status = routeSource == "native"
                            ? $"new native path {route.Count} pts"
                            : $"new coarse fallback {route.Count} pts / {expanded} nodes";
                        ResetPatrolPoint(ai);
                        _debugLog?.Invoke($"Worker navigator v5 {routeSource} path #{progress.RecoveryAttempt}: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)} waypoints={route.Count}, expanded={expanded}, target={distance:0.0}m, native={progress.LastNativeStatus}");
                        return DriveDirect(ai, worker, dt, route[0], RouteWaypointStopDistance, run);
                    }

                    // Both Valheim's native navmesh route and our bounded emergency grid failed.
                    // Keep cargo/targets intact and retry later rather than inventing a bad route.
                    progress.LastPlannerExpanded = expanded;
                    progress.LastPlannerMiss = true;
                    progress.LastPosition = position;
                    if (!progress.DirectPathBlocked)
                        progress.LastProgressTime = now;
                    progress.Status = progress.DirectPathBlocked
                        ? $"BLOCKED | native miss + coarse miss {expanded} | retry"
                        : $"path search miss -> direct | d={distance:0.0}m";
                    _debugLog?.Invoke($"Worker navigator v5 path miss: {SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name)} expanded={expanded}, target={distance:0.0}m, directBlocked={progress.DirectPathBlocked}, native={progress.LastNativeStatus}");
                }
                else
                {
                    progress.Status = progress.DirectPathBlocked
                        ? $"direct BLOCKED; waiting path slot | d={distance:0.0}m"
                        : $"stuck; waiting path slot | d={distance:0.0}m";
                }
            }

            return DriveDirect(ai, worker, dt, destination, stopDistance, run);
        }


        private static float TravelDistance(Character worker, Vector3 a, Vector3 b)
        {
            if (!IsAirHauler(worker)) return Vector3.Distance(a, b);
            Vector3 delta = b - a;
            delta.y = 0f;
            return delta.magnitude;
        }

        private static bool IsAirHauler(Character worker)
        {
            if (worker == null) return false;
            return string.Equals(
                SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name),
                "Drake",
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Background AirHaulers may keep a sleeping Unity Character instance alive even though the
        /// scheduler owns the route. Hide that stale presentation without deactivating the network
        /// object; when the return leg reaches the player-relative handoff the same renderers are
        /// released immediately for the physical final approach. forceRenderingOff preserves each
        /// renderer's original enabled/LOD state.
        /// </summary>
        internal static void SetAirHaulerPhysicalPresentation(Character worker, bool visible)
        {
            if (worker == null || !IsAirHauler(worker)) return;

            // A return-home visual shadow remains simulation-authoritative. Do not let the
            // MonsterAI and scheduler fight over visibility while that approach is active.
            if (!visible)
            {
                try
                {
                    ZNetView nview = worker.GetComponent<ZNetView>();
                    ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                    if (BackgroundWorkerScheduler.ShouldKeepAirHaulerVisibleDuringReturn(zdo))
                        visible = true;
                }
                catch { }
            }

            int key = worker.GetInstanceID();
            if (AirPresentationVisibility.TryGetValue(key, out bool current) && current == visible)
                return;
            try
            {
                Renderer[] renderers = worker.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer != null) renderer.forceRenderingOff = !visible;
                }
                AirPresentationVisibility[key] = visible;
            }
            catch { }
        }

        private static bool TryMoveAirNative(BaseAI ai, Character worker, float dt, Vector3 destination, float stopDistance, bool run)
        {
            int key = worker.GetInstanceID();
            Vector3 position = worker.transform.position;
            float distance = Vector3.Distance(position, destination);
            float now = Time.unscaledTime;

            if (!Progress.TryGetValue(key, out NavigationProgress progress))
            {
                progress = new NavigationProgress();
                Progress[key] = progress;
            }
            bool newAirLeg = (progress.AirDestination - destination).sqrMagnitude > 4f;
            progress.Destination = destination;
            progress.LastPosition = position;
            progress.BestDistance = distance;
            progress.LastProgressTime = now;
            progress.Route = null;
            progress.RouteIndex = 0;
            progress.RouteSource = "air";
            if (newAirLeg || progress.AirCruiseHeight <= 0f)
            {
                progress.AirDestination = destination;
                // Travel high enough that roofs, towers and normal terrain clutter are well below
                // the physical Drake. The height is fixed per leg so it cannot ratchet upward every tick.
                progress.AirCruiseHeight = Mathf.Max(position.y, destination.y) + 30f;
            }

            if (distance <= Mathf.Max(stopDistance, DirectArrivalFloor))
            {
                progress.Status = $"air arrived | d={distance:0.0}m";
                StopMoving(ai);
                StopDirect(worker);
                return true;
            }

            // A vanilla Drake does not use MoveTo like a ground creature. Its normal MonsterAI
            // continually applies circling/attack-style steering, which can make a recruited
            // AirHauler oscillate toward/away from the player instead of actually leaving the
            // loaded zone. We still let BaseAI.MoveTo update the flying creature's native flight
            // state, but Workers owns the final steering vector for a logistics leg. This keeps
            // the departure fully physical/visible while guaranteeing progress toward the real
            // AirFreight target. Once Valheim unloads the Drake, BackgroundWorkerScheduler takes
            // over the same persistent route as before.
            if (ai is MonsterAI monster && monster.GetFollowTarget() != null)
                SetNativeFollowTarget(monster, null);

            bool nativeMoved = InvokeMoveTo(ai, dt, destination, stopDistance, run);

            Vector3 flat = destination - position;
            flat.y = 0f;
            float horizontalDistance = flat.magnitude;
            if (horizontalDistance > 0.01f)
            {
                Vector3 flatDirection = flat / horizontalDistance;

                // Cruise high above buildings/trees. Only descend once the Drake is close to its
                // endpoint, preventing long shallow approaches through roofs and walls.
                float desiredFlightY;
                if (horizontalDistance > 35f) desiredFlightY = progress.AirCruiseHeight;
                else
                {
                    float t = Mathf.Clamp01(horizontalDistance / 35f);
                    float approachY = destination.y + 5f;
                    desiredFlightY = Mathf.Lerp(approachY, progress.AirCruiseHeight, t);
                }
                float vertical = Mathf.Clamp((desiredFlightY - position.y) / Mathf.Max(horizontalDistance, 10f), -0.30f, 0.45f);
                Vector3 moveDirection = new Vector3(flatDirection.x, vertical, flatDirection.z).normalized;

                Vector3 facing = flatDirection;
                if (facing.sqrMagnitude > 0.0001f)
                {
                    Quaternion desiredRotation = Quaternion.LookRotation(facing, Vector3.up);
                    worker.transform.rotation = Quaternion.RotateTowards(worker.transform.rotation, desiredRotation, 240f * Mathf.Max(dt, Time.fixedDeltaTime));
                }

                worker.SetMoveDir(moveDirection);
                worker.SetRun(run);
                worker.SetWalk(!run);
                progress.Status = $"air workers-direct | h={horizontalDistance:0.0}m | d={distance:0.0}m";
                return true;
            }

            progress.Status = nativeMoved
                ? $"air native fallback | d={distance:0.0}m"
                : $"air native MoveTo unavailable | d={distance:0.0}m";
            return nativeMoved;
        }

        private static bool DriveDirect(BaseAI ai, Character worker, float dt, Vector3 destination, float stopDistance, bool run)
        {
            if (worker == null)
                return InvokeMoveTo(ai, dt, destination, stopDistance, run);

            // v0.1.80: own the final locomotion input instead of asking BaseAI to maintain a
            // second path state after MonsterAI already ran. This is intentionally simple:
            // walk straight toward the current coarse goal; only the existing stuck timer may
            // promote the goal to a cached detour waypoint.
            Vector3 delta = destination - worker.transform.position;
            delta.y = 0f;
            float distance = delta.magnitude;
            float arrival = Mathf.Max(DirectArrivalFloor, stopDistance);
            if (distance <= arrival || delta.sqrMagnitude < 0.0004f)
            {
                StopDirect(worker);
                return true;
            }

            if (ai is MonsterAI monster && monster.GetFollowTarget() != null)
                monster.SetFollowTarget(null);

            Vector3 direction = delta / distance;

            // Safety layer only: do not let a Worker blindly walk off a cliff because the current
            // direct/path waypoint happens to lie beyond a sharp drop. v0.5.354 keeps the exact
            // behaviour but does not run two+ physics raycasts on every rendered AI frame. A cached
            // result is reused for 0.20 s and immediately invalidated when steering turns sharply.
            int navKey = worker.GetInstanceID();
            if (Progress.TryGetValue(navKey, out NavigationProgress navProgress))
            {
                float now = Time.unscaledTime;
                bool directionChanged = navProgress.CliffProbeInput.sqrMagnitude < 0.0001f ||
                                        Vector3.Dot(navProgress.CliffProbeInput, direction) < 0.96f;
                if (directionChanged || now >= navProgress.NextCliffProbeAt)
                {
                    bool overridden = TryChooseSafeTravelDirection(worker, direction, out Vector3 safeDirection, out string safetyStatus);
                    navProgress.NextCliffProbeAt = now + CliffProbeCacheSeconds;
                    navProgress.CliffProbeInput = direction;
                    navProgress.CliffProbeOverride = overridden;
                    navProgress.CliffProbeOutput = overridden ? safeDirection : direction;
                    navProgress.CliffProbeStatus = safetyStatus;
                }

                if (navProgress.CliffProbeOverride)
                {
                    direction = navProgress.CliffProbeOutput;
                    if (!string.IsNullOrEmpty(navProgress.CliffProbeStatus))
                        navProgress.Status = navProgress.CliffProbeStatus;
                }
            }
            else if (TryChooseSafeTravelDirection(worker, direction, out Vector3 safeDirection, out _))
            {
                direction = safeDirection;
            }

            // A fully blocked cliff probe intentionally returns Vector3.zero. The old code passed
            // that into Quaternion.LookRotation every frame, producing thousands of Unity warnings
            // and log writes (and doing no useful movement). Stop cleanly and let the existing
            // stuck/replan timer request another route.
            if (direction.sqrMagnitude < 0.0001f)
            {
                StopDirect(worker);
                return false;
            }
            direction.Normalize();

            // Smoothly face the travel direction. Character.UpdateRotation will also see the same
            // move vector later in the frame, so this does not create a competing steering target.
            Quaternion desired = Quaternion.LookRotation(direction, Vector3.up);
            float turnStep = DirectTurnDegreesPerSecond * Mathf.Max(dt, Time.fixedDeltaTime);
            worker.transform.rotation = Quaternion.RotateTowards(worker.transform.rotation, desired, turnStep);

            worker.SetMoveDir(direction);
            worker.SetRun(run);
            worker.SetWalk(!run);
            return true;
        }

        private static void StopDirect(Character worker)
        {
            if (worker == null) return;
            worker.SetMoveDir(Vector3.zero);
            worker.SetRun(false);
            worker.SetWalk(false);
        }


        private static bool TryChooseSafeTravelDirection(Character worker, Vector3 desiredDirection, out Vector3 safeDirection, out string status)
        {
            safeDirection = desiredDirection;
            status = null;
            if (worker == null || desiredDirection.sqrMagnitude < 0.0001f) return false;

            if (!IsUnsafeDropAhead(worker, desiredDirection, out float drop))
                return false;

            // Prefer the smallest detour first so the stable navigator remains in control of the
            // high-level route. We only protect the immediate step from a dangerous ledge.
            float[] angles = { -35f, 35f, -65f, 65f, -90f, 90f };
            for (int i = 0; i < angles.Length; i++)
            {
                Vector3 candidate = Quaternion.Euler(0f, angles[i], 0f) * desiredDirection;
                candidate.y = 0f;
                candidate.Normalize();
                if (!IsUnsafeDropAhead(worker, candidate, out _))
                {
                    safeDirection = candidate;
                    status = $"cliff safety detour {angles[i]:0}deg | drop={drop:0.0}m";
                    return true;
                }
            }

            // No safe immediate direction found: stop instead of stepping into the drop. The
            // existing stuck/replan logic will then ask the normal navigator for another route.
            safeDirection = Vector3.zero;
            status = $"CLIFF BLOCKED | drop={drop:0.0}m | waiting replan";
            return true;
        }

        private static bool IsUnsafeDropAhead(Character worker, Vector3 direction, out float dropMeters)
        {
            dropMeters = 0f;
            Vector3 flat = direction;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.0001f) return false;
            flat.Normalize();

            float bodyScale = Mathf.Max(1f, worker.GetRadius());
            float forward = Mathf.Max(CliffProbeForwardMeters, bodyScale + 0.8f);
            Vector3 currentOrigin = worker.transform.position + Vector3.up * CliffProbeUpMeters;
            Vector3 aheadOrigin = worker.transform.position + flat * forward + Vector3.up * CliffProbeUpMeters;

            if (!TrySampleWalkSurface(currentOrigin, worker, out float currentY))
                currentY = worker.transform.position.y;

            if (!TrySampleWalkSurface(aheadOrigin, worker, out float aheadY))
            {
                dropMeters = CliffProbeDownMeters;
                return true;
            }

            dropMeters = currentY - aheadY;
            float maxDrop = string.Equals(SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name), "Lox", StringComparison.OrdinalIgnoreCase)
                ? LoxMaxSafeDropMeters
                : DefaultMaxSafeDropMeters;
            return dropMeters > maxDrop;
        }

        private static bool TrySampleWalkSurface(Vector3 origin, Character worker, out float surfaceY)
        {
            surfaceY = 0f;
            int hitCount;
            try
            {
                hitCount = Physics.RaycastNonAlloc(origin, Vector3.down, CliffProbeHits,
                    CliffProbeUpMeters + CliffProbeDownMeters, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            }
            catch
            {
                return false;
            }
            if (hitCount <= 0) return false;

            float bestY = float.NegativeInfinity;
            int limit = Mathf.Min(hitCount, CliffProbeHits.Length);
            for (int i = 0; i < limit; i++)
            {
                RaycastHit hit = CliffProbeHits[i];
                Collider c = hit.collider;
                CliffProbeHits[i] = default;
                if (c == null) continue;
                if (worker != null && c.transform != null && c.transform.IsChildOf(worker.transform)) continue;
                if (hit.point.y > bestY) bestY = hit.point.y;
            }
            if (float.IsNegativeInfinity(bestY)) return false;
            surfaceY = bestY;
            return true;
        }

        private static bool InvokeMoveTo(BaseAI ai, float dt, Vector3 destination, float stopDistance, bool run)
        {
            try
            {
                // A recruited monster may still have a native follow order. Clear it before the
                // Workers command so follow movement cannot compete with ReturnHome/MoveToTarget.
                if (ai is MonsterAI monster && monster.GetFollowTarget() != null)
                    monster.SetFollowTarget(null);

                object result = MoveToMethod.Invoke(ai, new object[] { dt, destination, stopDistance, run });
                return result is bool;
            }
            catch (Exception ex)
            {
                if (!_reportedMoveFailure)
                {
                    _reportedMoveFailure = true;
                    Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    _debugLog?.Invoke($"Worker native MoveTo failed after vanilla AI tick. {inner.GetType().Name}: {inner.Message}");
                }
                return false;
            }
        }

        private static void ResetPatrolPoint(BaseAI ai)
        {
            if (ResetPatrolPointMethod == null || ai == null) return;
            try { ResetPatrolPointMethod.Invoke(ai, null); }
            catch { }
        }

        private static Vector3 ResolveRestHoldPoint(ZDO zdo, Vector3 fallbackHome)
        {
            if (zdo == null) return fallbackHome;
            try
            {
                WorkerRestStation station = WorkerRestStation.ResolveLoadedForWorker(zdo);
                Transform rest = station != null ? station.RestPoint : null;
                if (rest != null) return rest.position;
            }
            catch { }
            return fallbackHome;
        }

        private static void MaintainStaticHold(BaseAI ai, Character worker, Vector3 patrolPoint, bool setPatrolPoint)
        {
            if (worker == null) return;

            // Keep visible locomotion suppressed every AI frame with cheap direct movement input.
            // Reflection-based BaseAI.StopMoving/SetPatrolPoint only needs periodic reinforcement.
            worker.SetMoveDir(Vector3.zero);
            worker.SetRun(false);

            int key = worker.GetInstanceID();
            float now = Time.unscaledTime;
            if (StaticHoldNextEnforce.TryGetValue(key, out float next) && now < next) return;
            StaticHoldNextEnforce[key] = now + StaticHoldReflectionSeconds;
            StopMoving(ai);
            if (setPatrolPoint) SetPatrolPoint(ai, patrolPoint);
        }

        internal static void StopMoving(BaseAI ai)
        {
            if (StopMovingMethod == null) return;
            try { StopMovingMethod.Invoke(ai, null); }
            catch { }
        }

        private static void SetPatrolPoint(BaseAI ai, Vector3 point)
        {
            if (SetPatrolPointPositionMethod == null) return;
            try { SetPatrolPointPositionMethod.Invoke(ai, new object[] { point }); }
            catch { }
        }
    }

    [HarmonyPatch(typeof(MonsterAI), "UpdateAI")]
    internal static class MonsterAiWorkerNavigationPatch
    {
        // Only virtual AirHaulers suppress native MonsterAI. All other Workers still run the
        // complete vanilla AI first, preserving the stable ground-navigation behaviour.
        private static bool Prefix(MonsterAI __instance, out bool __state)
        {
            // v0.5.91 hot-path gate: this Harmony patch runs for every MonsterAI in the loaded
            // area, including every farm animal. Resolve the Workers flag once up front and skip
            // all Workers-specific pre/post work for normal creatures. Recruited workers are
            // promoted immediately by WorkerFastLookupRuntime.RegisterKnownWorker.
            __state = false;
            if (__instance == null) return true;
            if (!WorkerFastLookupRuntime.TryGetWorker(__instance, false, out _, out _, out _))
                return true;

            __state = true;
            WorkerCombatStanceRuntime.EnforceBeforeVanilla(__instance);
            return WorkerNavigationRuntime.ShouldRunNativeMonsterAi(__instance);
        }

        private static void Postfix(MonsterAI __instance, float dt, bool __state)
        {
            if (!__state) return;
            GuardBehaviorRuntime.EnforceAfterVanilla(__instance);
            WorkerNavigationRuntime.TryApplyAfterVanilla(__instance, dt);
        }
    }
}
