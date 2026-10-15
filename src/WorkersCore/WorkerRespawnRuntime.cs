using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Conservative emergency recovery for loaded Workers that become trapped in water.
    /// This is intentionally separate from WorkerNavigationRuntime: recovery must never
    /// refactor or compete with the stable navigator. Cargo and persistent job metadata stay intact.
    /// </summary>
    internal static class WorkerRespawnRuntime
    {
        private sealed class Sample
        {
            internal Vector3 LastPosition;
            internal float LastSampleAt;
            internal float StuckSince;
            internal float LastRecoveryAt;
            internal Vector3 WaterAnchorPosition;
            internal float WaterAnchorSince;
            internal float LastHomeDistance;
            internal float HomeNoProgressSince;
            internal float HomeWindowStartDistance;
            internal float BestHomeDistance;
            internal float NearHomeDeadlockSince;
        }

        private static readonly Dictionary<int, Sample> Samples = new Dictionary<int, Sample>();
        private static readonly MethodInfo IsSwimmingMethod = typeof(Character).GetMethod(
            "IsSwimming",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
        private static readonly MethodInfo ZdoSetPositionMethod = typeof(ZDO).GetMethod(
            "SetPosition",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(Vector3) },
            null);

        private static Action<string> _debugLog;
        private static bool _enabled = true;
        private static float _waterStuckSeconds = 18f;
        private const float SampleSeconds = 1f;
        private const float MeaningfulMoveMeters = 0.45f;
        private const float WaterAnchorRadiusMeters = 2.2f;
        private const float RecoveryCooldownSeconds = 30f;
        private const float HomeRequiredProgressMeters = 1.5f;
        private const float HomeNoProgressRescueSeconds = 14f;
        private const float NearHomeDeadlockDistanceMeters = 30f;
        private const float NearHomeDeadlockSpeedMetersPerSecond = 0.15f;
        private const float NearHomeDeadlockSeconds = 8f;
        private const string HomeWatchdogStartTicksKey = "vw_home_watchdog_start_ticks";
        private const string HomeWatchdogStartDistanceKey = "vw_home_watchdog_start_distance";
        private const float DirectHomeWatchdogSpeedMetersPerSecond = 0.15f;
        private const float DirectHomeWatchdogSeconds = 8f;

        internal static void Initialize(Action<string> debugLog)
        {
            _debugLog = debugLog;
            Samples.Clear();
        }

        internal static void Configure(bool enabled, float waterStuckSeconds)
        {
            _enabled = enabled;
            _waterStuckSeconds = Mathf.Clamp(waterStuckSeconds, 8f, 120f);
        }

        internal static bool TickReturnHomeWatchdog(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null || !WorkerZdo.IsWorker(zdo)) return false;

            ZNetView nview = worker.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return false;

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            bool returningHome = state == WorkerState.ReturnHome || state == WorkerState.ReturnHomeForRest;
            if (!returningHome || WorkerZdo.HasCargo(zdo))
            {
                ResetDirectHomeWatchdog(zdo);
                return false;
            }

            ZDOID homeId = WorkerZdo.ReadHomeAnchorZdoId(zdo);
            string homeType = zdo.GetString(WorkerZdo.HomeAnchorType, string.Empty);
            if (homeId == ZDOID.None || string.IsNullOrEmpty(homeType))
            {
                ResetDirectHomeWatchdog(zdo);
                return false;
            }

            Vector3 current = worker.transform.position;
            Vector3 home = WorkerZdo.ReadHome(zdo, current);
            Vector3 flat = home - current;
            flat.y = 0f;
            float distance = flat.magnitude;

            Vector3 velocity = worker.GetVelocity();
            velocity.y = 0f;
            float speed = velocity.magnitude;

            if (distance <= 2f || speed > DirectHomeWatchdogSpeedMetersPerSecond)
            {
                ResetDirectHomeWatchdog(zdo);
                return false;
            }

            long now = DateTime.UtcNow.Ticks;
            long startTicks = zdo.GetLong(HomeWatchdogStartTicksKey, 0L);
            if (startTicks <= 0L || startTicks > now)
            {
                zdo.Set(HomeWatchdogStartTicksKey, now);
                zdo.Set(HomeWatchdogStartDistanceKey, distance);
                return false;
            }

            double elapsed = TimeSpan.FromTicks(now - startTicks).TotalSeconds;
            if (elapsed < DirectHomeWatchdogSeconds) return false;

            // This rescue is deliberately self-contained and does not rely on the in-memory Sample table.
            // It runs from the central AI loop and therefore cannot be reset by navigation/path replans.
            Vector3 rescue = home + Vector3.up * 0.75f;
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
            worker.transform.position = rescue;
            try { ZdoSetPositionMethod?.Invoke(zdo, new object[] { rescue }); } catch { }

            Rigidbody body = worker.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            ResetDirectHomeWatchdog(zdo);
            SetDecision(zdo, $"AUTO-RESCUE: central ReturnHome watchdog {elapsed:0.0}s / {distance:0.0}m -> HomeAnchor");
            _debugLog?.Invoke($"Worker central home watchdog rescue: {worker.gameObject.name} -> {homeType} at {rescue}");
            return true;
        }

        internal static string GetHomeWatchdogDebug(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return "inactive";
            long startTicks = zdo.GetLong(HomeWatchdogStartTicksKey, 0L);
            if (startTicks <= 0L) return "inactive";
            long now = DateTime.UtcNow.Ticks;
            double elapsed = startTicks > now ? 0d : TimeSpan.FromTicks(now - startTicks).TotalSeconds;
            float startDistance = zdo.GetFloat(HomeWatchdogStartDistanceKey, 0f);
            return $"{Math.Min(elapsed, DirectHomeWatchdogSeconds):0.0}/{DirectHomeWatchdogSeconds:0.0}s | start {startDistance:0.0}m | LIVE";
        }

        private static void ResetDirectHomeWatchdog(ZDO zdo)
        {
            if (zdo == null) return;
            zdo.Set(HomeWatchdogStartTicksKey, 0L);
            zdo.Set(HomeWatchdogStartDistanceKey, 0f);
        }

        internal static void Tick(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null || !WorkerZdo.IsWorker(zdo)) return;

            ZNetView nview = worker.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

            int key = worker.GetInstanceID();
            if (!Samples.TryGetValue(key, out Sample sample))
            {
                sample = new Sample { LastPosition = worker.transform.position, LastSampleAt = Time.time };
                Samples[key] = sample;
                return;
            }

            if (Time.time - sample.LastSampleAt < SampleSeconds) return;

            Vector3 current = worker.transform.position;
            Vector3 delta = current - sample.LastPosition;
            delta.y = 0f;
            float moved = delta.magnitude;
            sample.LastPosition = current;
            sample.LastSampleAt = Time.time;

            // Independent home-progress watchdog. This also catches shallow-water cases where
            // Character.IsSwimming() stays false although the Worker is visibly trapped in a ditch.
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            bool returningHome = state == WorkerState.ReturnHome || state == WorkerState.ReturnHomeForRest;
            if (returningHome && !WorkerZdo.HasCargo(zdo))
            {
                ZDOID homeIdForProgress = WorkerZdo.ReadHomeAnchorZdoId(zdo);
                string homeTypeForProgress = zdo.GetString(WorkerZdo.HomeAnchorType, string.Empty);
                if (homeIdForProgress != ZDOID.None && !string.IsNullOrEmpty(homeTypeForProgress))
                {
                    Vector3 homeForProgress = WorkerZdo.ReadHome(zdo, current);
                    Vector3 flatHomeDelta = homeForProgress - current;
                    flatHomeDelta.y = 0f;
                    float homeDistance = flatHomeDelta.magnitude;
                    if (homeDistance > 2.0f)
                    {
                        // Strong near-home deadlock guard: if an empty worker is already close to its
                        // HomeAnchor but is effectively stationary, do not let tiny water drift/path replans
                        // keep it trapped forever. This is intentionally independent of the water toggle.
                        Vector3 planarVelocity = worker.GetVelocity();
                        planarVelocity.y = 0f;
                        float planarSpeed = planarVelocity.magnitude;
                        if (homeDistance <= NearHomeDeadlockDistanceMeters && planarSpeed <= NearHomeDeadlockSpeedMetersPerSecond)
                        {
                            if (sample.NearHomeDeadlockSince <= 0f) sample.NearHomeDeadlockSince = Time.time;
                            if (Time.time - sample.NearHomeDeadlockSince >= NearHomeDeadlockSeconds &&
                                (sample.LastRecoveryAt <= 0f || Time.time - sample.LastRecoveryAt >= RecoveryCooldownSeconds))
                            {
                                RescueToHome(worker, zdo, sample, current, homeTypeForProgress,
                                    $"AUTO-RESCUE: ReturnHome near-home deadlock ({homeDistance:0.0} m, {planarSpeed:0.00} m/s for {NearHomeDeadlockSeconds:0}s) -> HomeAnchor");
                                return;
                            }
                        }
                        else
                        {
                            sample.NearHomeDeadlockSince = 0f;
                        }

                        // Progress is measured over a fixed observation window. Path replans, bobbing,
                        // small sideways movement and tiny per-tick distance changes must NOT reset it.
                        // The worker has to make at least HomeRequiredProgressMeters of real net progress
                        // toward its HomeAnchor before a new observation window begins.
                        if (sample.HomeNoProgressSince <= 0f || sample.HomeWindowStartDistance <= 0f)
                        {
                            sample.HomeNoProgressSince = Time.time;
                            sample.HomeWindowStartDistance = homeDistance;
                            sample.BestHomeDistance = homeDistance;
                        }
                        else
                        {
                            sample.BestHomeDistance = Mathf.Min(sample.BestHomeDistance, homeDistance);
                            float netProgress = sample.HomeWindowStartDistance - sample.BestHomeDistance;
                            if (netProgress >= HomeRequiredProgressMeters)
                            {
                                sample.HomeNoProgressSince = Time.time;
                                sample.HomeWindowStartDistance = homeDistance;
                                sample.BestHomeDistance = homeDistance;
                            }
                        }

                        sample.LastHomeDistance = homeDistance;

                        if (Time.time - sample.HomeNoProgressSince >= HomeNoProgressRescueSeconds &&
                            Time.time - sample.LastRecoveryAt >= RecoveryCooldownSeconds)
                        {
                            float achieved = Mathf.Max(0f, sample.HomeWindowStartDistance - sample.BestHomeDistance);
                            RescueToHome(worker, zdo, sample, current, homeTypeForProgress,
                                $"AUTO-RESCUE: ReturnHome progress only {achieved:0.0}/{HomeRequiredProgressMeters:0.0} m in {HomeNoProgressRescueSeconds:0}s ({homeDistance:0.0} m left) -> HomeAnchor");
                            return;
                        }
                    }
                    else
                    {
                        sample.LastHomeDistance = homeDistance;
                        sample.HomeNoProgressSince = 0f;
                        sample.HomeWindowStartDistance = 0f;
                        sample.BestHomeDistance = 0f;
                        sample.NearHomeDeadlockSince = 0f;
                    }
                }
            }
            else
            {
                sample.LastHomeDistance = 0f;
                sample.HomeNoProgressSince = 0f;
                sample.HomeWindowStartDistance = 0f;
                sample.BestHomeDistance = 0f;
                sample.NearHomeDeadlockSince = 0f;
            }

            // The ReturnHome progress watchdog is safety-critical and intentionally independent
            // from the optional water-rescue toggle. The F8 setting only controls water recovery.
            if (!_enabled)
            {
                sample.StuckSince = 0f;
                sample.WaterAnchorSince = 0f;
                sample.WaterAnchorPosition = current;
                return;
            }

            bool swimming = IsSwimming(worker);

            // v0.5.114: Neck fishers are expected to swim as part of their real job. Do not
            // interpret normal fishing movement/holding near a target as a water-stuck failure.
            // The generic ReturnHome progress watchdog above still remains active.
            WorkerRole activeRole = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            WorkOrder activeOrder = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
            if (swimming && activeRole == WorkerRole.Fisher && activeOrder == WorkOrder.Fish &&
                WorkerJobProfileRuntime.HasTask(zdo, WorkerRole.Fisher, WorkerTaskFlags.Fish))
            {
                sample.StuckSince = 0f;
                sample.WaterAnchorSince = 0f;
                sample.WaterAnchorPosition = current;
                return;
            }

            if (!swimming)
            {
                sample.StuckSince = 0f;
                sample.WaterAnchorSince = 0f;
                sample.WaterAnchorPosition = current;
                return;
            }

            // Do not treat tiny swimming/bobbing motion as real progress. A Lox can move a little
            // forward/backward forever without escaping a ditch; anchor a small water area and
            // only reset the timer once it actually leaves that area.
            if (sample.WaterAnchorSince <= 0f)
            {
                sample.WaterAnchorPosition = current;
                sample.WaterAnchorSince = Time.time;
                sample.StuckSince = Time.time;
                return;
            }

            Vector3 anchorDelta = current - sample.WaterAnchorPosition;
            anchorDelta.y = 0f;
            if (anchorDelta.magnitude >= WaterAnchorRadiusMeters)
            {
                sample.WaterAnchorPosition = current;
                sample.WaterAnchorSince = Time.time;
                sample.StuckSince = Time.time;
                return;
            }

            // Large one-second movement inside the same small anchor area is still not useful
            // progress, but retain the old signal for diagnostics and very active swimmers.
            if (moved >= MeaningfulMoveMeters && Time.time - sample.WaterAnchorSince < _waterStuckSeconds)
                return;

            if (sample.StuckSince <= 0f) sample.StuckSince = sample.WaterAnchorSince;
            if (Time.time - sample.WaterAnchorSince < _waterStuckSeconds) return;
            if (Time.time - sample.LastRecoveryAt < RecoveryCooldownSeconds) return;

            ZDOID homeId = WorkerZdo.ReadHomeAnchorZdoId(zdo);
            string homeType = zdo.GetString(WorkerZdo.HomeAnchorType, string.Empty);
            if (homeId == ZDOID.None || string.IsNullOrEmpty(homeType))
            {
                SetDecision(zdo, "water-stuck: no HomeAnchor for safe recovery");
                sample.StuckSince = Time.time;
                return;
            }

            RescueToHome(worker, zdo, sample, current, homeType,
                $"AUTO-RESCUE: water-stuck -> HomeAnchor ({homeType})");
        }

        private static void RescueToHome(Character worker, ZDO zdo, Sample sample, Vector3 current, string homeType, string reason)
        {
            Vector3 home = WorkerZdo.ReadHome(zdo, current);
            Vector3 rescue = home + Vector3.up * 0.75f;

            // Keep persistent role/work/cargo data intact. Only transient movement is reset.
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
            worker.transform.position = rescue;
            try { ZdoSetPositionMethod?.Invoke(zdo, new object[] { rescue }); } catch { }

            Rigidbody body = worker.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            sample.LastPosition = rescue;
            sample.StuckSince = 0f;
            sample.WaterAnchorSince = 0f;
            sample.WaterAnchorPosition = rescue;
            sample.LastHomeDistance = 0f;
            sample.HomeNoProgressSince = 0f;
            sample.HomeWindowStartDistance = 0f;
            sample.BestHomeDistance = 0f;
            sample.NearHomeDeadlockSince = 0f;
            sample.LastRecoveryAt = Time.time;
            SetDecision(zdo, reason);
            _debugLog?.Invoke($"Worker auto-rescue: {worker.gameObject.name} -> {homeType} at {rescue}; {reason}");
        }

        private static void SetDecision(ZDO zdo, string reason)
        {
            if (zdo == null || string.IsNullOrEmpty(reason)) return;
            zdo.Set(WorkerZdo.LastDecisionReason, reason);
            zdo.Set(WorkerZdo.LastDecisionTicks, DateTime.UtcNow.Ticks);
        }

        private static bool IsSwimming(Character worker)
        {
            try
            {
                if (IsSwimmingMethod == null) return false;
                object result = IsSwimmingMethod.Invoke(worker, null);
                return result is bool value && value;
            }
            catch
            {
                return false;
            }
        }
    }
}
