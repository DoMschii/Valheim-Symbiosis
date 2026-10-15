using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal enum WorkerRestReason
    {
        None = 0,
        Night = 1,
        Recovery = 2
    }

    /// <summary>
    /// Shared rest/recovery layer for every Worker role.
    ///
    /// Design rules:
    /// - only the network owner reaches this runtime (WorkerAiController already enforces that),
    /// - healing requires a real assigned species HomeAnchor, not only the legacy position,
    /// - night/recovery never teleports a Worker; it returns home through the normal navigation layer,
    /// - health and day/night APIs are resolved once and invoked defensively so a Valheim signature
    ///   change fails closed instead of generating AccessTools/reflection spam every frame.
    /// </summary>
    internal static class WorkerRestRuntime
    {
        internal const float RecoveryEnterFraction = 0.35f;
        internal const float RecoveryExitFraction = 0.85f;
        internal const float HomeRestDistance = 3.5f;
        internal const float HunterStandRestDistance = 0.45f;
        internal const float StoneGolemRestDistance = 0.65f;

        internal static float GetRestDistance(ZDO zdo)
        {
            if (zdo != null)
            {
                string homeKind = zdo.GetString(WorkerZdo.HomeAnchorType, string.Empty);
                if (string.Equals(homeKind, WorkerHomeProfileCatalog.HunterStandKind, StringComparison.Ordinal))
                    return HunterStandRestDistance;
                if (string.Equals(homeKind, WorkerHomeProfileCatalog.StoneGolemShelterKind, StringComparison.Ordinal))
                    return StoneGolemRestDistance;
            }
            return HomeRestDistance;
        }
        internal const float HealIntervalSeconds = 5f;
        internal const float HealFractionPerPulse = 0.02f;
        internal const float CombatWakeSeconds = 10f;

        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly MethodInfo GetHealthMethod = typeof(Character).GetMethod("GetHealth", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly MethodInfo GetMaxHealthMethod = typeof(Character).GetMethod("GetMaxHealth", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly MethodInfo HealWithTextMethod = typeof(Character).GetMethod("Heal", InstanceFlags, null, new[] { typeof(float), typeof(bool) }, null);
        private static readonly MethodInfo HealMethod = typeof(Character).GetMethod("Heal", InstanceFlags, null, new[] { typeof(float) }, null);

        private static readonly FieldInfo EnvInstanceField = typeof(EnvMan).GetField("instance", StaticFlags);
        private static readonly PropertyInfo EnvInstanceProperty = typeof(EnvMan).GetProperty("instance", StaticFlags);
        private static readonly MethodInfo IsNightMethod = typeof(EnvMan).GetMethod("IsNight", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly FieldInfo SmoothDayFractionField = typeof(EnvMan).GetField("m_smoothDayFraction", InstanceFlags);

        private sealed class HealthSnapshot
        {
            internal float Health;
            internal float MaxHealth;
            internal float NextProbe;
        }

        private static readonly Dictionary<int, float> NextHealPulse = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> LastHealth = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> CombatWakeUntil = new Dictionary<int, float>();
        private static readonly Dictionary<int, HealthSnapshot> HealthCache = new Dictionary<int, HealthSnapshot>();
        private static float _nextNightProbe;
        private static bool _cachedNight;
        private static Action<string> _debugLog;
        private static bool _reportedHealthUnavailable;
        private static bool _reportedNightFallback;

        internal static void Initialize(Action<string> debugLog)
        {
            _debugLog = debugLog;
            if ((GetHealthMethod == null || GetMaxHealthMethod == null) && !_reportedHealthUnavailable)
            {
                _reportedHealthUnavailable = true;
                _debugLog?.Invoke("Worker rest health API unavailable: GetHealth/GetMaxHealth was not found. Night return/rest stays available, but low-HP recovery/healing fails closed.");
            }

            if (IsNightMethod == null && SmoothDayFractionField != null && !_reportedNightFallback)
            {
                _reportedNightFallback = true;
                _debugLog?.Invoke("EnvMan.IsNight() not found; Worker rest will use cached m_smoothDayFraction fallback for night detection.");
            }
        }

        internal static void Shutdown()
        {
            NextHealPulse.Clear();
            LastHealth.Clear();
            CombatWakeUntil.Clear();
            HealthCache.Clear();
            _nextNightProbe = 0f;
            _cachedNight = false;
            _debugLog = null;
            _reportedHealthUnavailable = false;
            _reportedNightFallback = false;
        }

        internal static bool HasAssignedHome(ZDO zdo)
        {
            return WorkerZdo.IsWorker(zdo) && !string.IsNullOrEmpty(zdo.GetString(WorkerZdo.HomeAnchorType, string.Empty));
        }

        internal static bool IsNight()
        {
            // SmartCore/debug UI may call this every OnGUI. Keep the actual reflected EnvMan probe
            // throttled so reflection invocation never becomes a per-frame operation.
            if (Time.time < _nextNightProbe) return _cachedNight;
            _nextNightProbe = Time.time + 0.75f;

            object env = null;
            try
            {
                env = EnvInstanceField != null
                    ? EnvInstanceField.GetValue(null)
                    : EnvInstanceProperty?.GetValue(null, null);
            }
            catch { }
            if (env == null)
            {
                _cachedNight = false;
                return false;
            }

            if (IsNightMethod != null)
            {
                try
                {
                    object value = IsNightMethod.Invoke(env, null);
                    if (value is bool night)
                    {
                        _cachedNight = night;
                        return night;
                    }
                }
                catch { }
            }

            if (SmoothDayFractionField != null)
            {
                try
                {
                    object value = SmoothDayFractionField.GetValue(env);
                    if (value is float fraction)
                    {
                        // Vanilla night spans the late-evening/early-morning part of the normalized day.
                        // This is only a compatibility fallback when the current build no longer exposes IsNight().
                        _cachedNight = fraction < 0.20f || fraction > 0.80f;
                        return _cachedNight;
                    }
                }
                catch { }
            }

            _cachedNight = false;
            return false;
        }

        internal static bool TryGetHealth(Character worker, out float health, out float maxHealth)
        {
            health = 0f;
            maxHealth = 0f;
            if (worker == null || GetHealthMethod == null || GetMaxHealthMethod == null) return false;

            int workerKey = worker.GetInstanceID();
            if (HealthCache.TryGetValue(workerKey, out HealthSnapshot cached) && Time.time < cached.NextProbe)
            {
                health = cached.Health;
                maxHealth = cached.MaxHealth;
                return maxHealth > 0.01f;
            }

            try
            {
                object current = GetHealthMethod.Invoke(worker, null);
                object maximum = GetMaxHealthMethod.Invoke(worker, null);
                if (!(current is float hp) || !(maximum is float max) || max <= 0.01f) return false;

                health = hp;
                maxHealth = max;
                HealthCache[workerKey] = new HealthSnapshot
                {
                    Health = hp,
                    MaxHealth = max,
                    NextProbe = Time.time + 0.5f
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Evaluates the shared rest layer. When handled is true the caller must not select/execute
        /// a normal role job during this AI tick.
        /// </summary>
        internal static bool TryEvaluate(Character worker, ZDO zdo, out WorkerState desiredState, out string reasonText)
        {
            desiredState = WorkerState.Idle;
            reasonText = null;
            if (worker == null || !WorkerZdo.IsWorker(zdo)) return false;

            WorkerState currentState = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            int workerKey = worker.GetInstanceID();

            bool haveHealth = TryGetHealth(worker, out float health, out float maxHealth);
            float healthFraction = haveHealth ? Mathf.Clamp01(health / maxHealth) : 1f;
            bool tookDamage = false;
            if (haveHealth)
            {
                if (LastHealth.TryGetValue(workerKey, out float previousHealth) && health + 0.01f < previousHealth)
                    tookDamage = true;
                LastHealth[workerKey] = health;
            }

            if ((currentState == WorkerState.Resting || currentState == WorkerState.Recover || currentState == WorkerState.Combat) && tookDamage)
            {
                CombatWakeUntil[workerKey] = Time.time + CombatWakeSeconds;
                zdo.Set(WorkerZdo.RestReason, (int)WorkerRestReason.None);
                desiredState = WorkerState.Combat;
                reasonText = "rest interrupted by damage; vanilla combat window";
                return true;
            }

            if (CombatWakeUntil.TryGetValue(workerKey, out float wakeUntil))
            {
                if (Time.time < wakeUntil)
                {
                    desiredState = WorkerState.Combat;
                    reasonText = "awake after damage";
                    return true;
                }
                CombatWakeUntil.Remove(workerKey);
                if (currentState == WorkerState.Combat)
                    currentState = WorkerState.Idle;
            }

            // A Worker needs a real species home before Workers grants sleep/recovery healing.
            // Legacy home coordinates remain useful for work boundaries but are not a bed/camp.
            if (!HasAssignedHome(zdo))
            {
                if (IsRestState(currentState))
                {
                    zdo.Set(WorkerZdo.RestReason, (int)WorkerRestReason.None);
                    desiredState = WorkerState.Idle;
                    reasonText = "rest cancelled: no assigned HomeAnchor";
                    return true;
                }
                return false;
            }

            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            float distanceHome = Vector3.Distance(worker.transform.position, home);
            float restDistance = GetRestDistance(zdo);
            bool atHome = distanceHome <= restDistance;
            bool night = IsNight();
            WorkerRestReason storedReason = (WorkerRestReason)zdo.GetInt(WorkerZdo.RestReason, (int)WorkerRestReason.None);

            // Rare natural cave Trolls are deliberately irreplaceable T3 workers. Once they have
            // a real Lehnplatz, they play more cautiously than ordinary workers and break off work
            // at 50% HP instead of waiting until the generic 35% emergency threshold.
            bool isTroll = string.Equals(
                SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name),
                "Troll",
                StringComparison.OrdinalIgnoreCase);
            float recoveryEnter = isTroll ? 0.50f : RecoveryEnterFraction;
            float recoveryExit = isTroll ? 0.90f : RecoveryExitFraction;
            bool recoveryRequested = haveHealth && healthFraction <= recoveryEnter;
            if ((currentState == WorkerState.Recover || storedReason == WorkerRestReason.Recovery) && haveHealth && healthFraction < recoveryExit)
                recoveryRequested = true;

            if (recoveryRequested)
            {
                zdo.Set(WorkerZdo.RestReason, (int)WorkerRestReason.Recovery);
                desiredState = atHome ? WorkerState.Recover : WorkerState.ReturnHomeForRest;
                reasonText = atHome
                    ? $"recovering at home ({healthFraction * 100f:0}% HP)"
                    : $"low HP -> home recovery ({healthFraction * 100f:0}% HP)";
                return true;
            }

            // Recovery finished. Stay home if it is still night, otherwise release the Worker back to its role.
            if (currentState == WorkerState.Recover || storedReason == WorkerRestReason.Recovery)
            {
                if (night)
                {
                    zdo.Set(WorkerZdo.RestReason, (int)WorkerRestReason.Night);
                    desiredState = atHome ? WorkerState.Resting : WorkerState.ReturnHomeForRest;
                    reasonText = "recovered; remaining home for the night";
                    return true;
                }

                zdo.Set(WorkerZdo.RestReason, (int)WorkerRestReason.None);
                desiredState = WorkerState.Idle;
                reasonText = "recovery complete";
                return true;
            }

            // A Surtling assigned to a concrete Fireplace is on permanent fire-watch duty.
            // Recovery above still has priority when the Worker is badly hurt, but ordinary
            // night sleep must never pull an assigned Firekeeper away from its post.
            WorkerRole workerRole = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            bool assignedFireWatch =
                workerRole == WorkerRole.Firekeeper &&
                WorkerZdo.TryReadFireAnchor(zdo, out ZDOID assignedFireId, out _) &&
                !assignedFireId.Equals(ZDOID.None);

            if (night && assignedFireWatch)
            {
                if (storedReason == WorkerRestReason.Night)
                    zdo.Set(WorkerZdo.RestReason, (int)WorkerRestReason.None);

                if (currentState == WorkerState.Resting || currentState == WorkerState.ReturnHomeForRest)
                {
                    desiredState = WorkerState.FindJob;
                    reasonText = "night rest bypassed: assigned Firekeeper stays on post";
                    return true;
                }

                return false;
            }

            // Active transport deliveries are allowed to finish through the night.
            // Rest must never turn a loaded Hauler around mid-route. Low-HP recovery above
            // still has priority, so this only bypasses the ordinary night-rest branch.
            bool activeTransportDelivery =
                workerRole == WorkerRole.Hauler &&
                WorkerZdo.HasCargo(zdo) &&
                WorkerZdo.TryReadCargoRoute(zdo, out string activeRoute, out _) &&
                !string.IsNullOrEmpty(activeRoute);

            if (night && activeTransportDelivery)
            {
                if (storedReason == WorkerRestReason.Night)
                    zdo.Set(WorkerZdo.RestReason, (int)WorkerRestReason.None);

                // If night started while the Worker was already being sent home for rest,
                // release that rest state back to the transport AI without touching cargo/route.
                if (currentState == WorkerState.Resting || currentState == WorkerState.ReturnHomeForRest)
                {
                    desiredState = WorkerState.FindJob;
                    reasonText = "night rest deferred: active transport delivery";
                    return true;
                }

                return false;
            }

            // Gatherers must not carry collected resources into their bed overnight. If night
            // begins with cargo aboard, finish exactly one unload to the FieldDepot first. The
            // next evaluation sees empty cargo and ordinary night rest resumes. Low-HP recovery
            // above still overrides this rule.
            bool gathererNeedsUnload = workerRole == WorkerRole.Gatherer && WorkerZdo.HasCargo(zdo);
            if (night && gathererNeedsUnload)
            {
                if (storedReason == WorkerRestReason.Night)
                    zdo.Set(WorkerZdo.RestReason, (int)WorkerRestReason.None);

                desiredState = WorkerState.Deliver;
                reasonText = "night rest deferred: Gatherer unloads cargo first";
                return true;
            }

            if (night)
            {
                zdo.Set(WorkerZdo.RestReason, (int)WorkerRestReason.Night);
                desiredState = atHome ? WorkerState.Resting : WorkerState.ReturnHomeForRest;
                reasonText = atHome ? "night rest at HomeAnchor" : "night -> returning to HomeAnchor";
                return true;
            }

            if (currentState == WorkerState.Resting || currentState == WorkerState.ReturnHomeForRest || storedReason == WorkerRestReason.Night)
            {
                zdo.Set(WorkerZdo.RestReason, (int)WorkerRestReason.None);
                desiredState = WorkerState.Idle;
                reasonText = "morning -> resume work";
                return true;
            }

            return false;
        }

        internal static bool TryHealAtHome(Character worker, ZDO zdo, out float amount)
        {
            amount = 0f;
            if (worker == null || !HasAssignedHome(zdo)) return false;
            if (!TryGetHealth(worker, out float health, out float maxHealth)) return false;
            if (health >= maxHealth - 0.01f) return false;

            Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
            if (Vector3.Distance(worker.transform.position, home) > GetRestDistance(zdo)) return false;

            int workerKey = worker.GetInstanceID();
            if (NextHealPulse.TryGetValue(workerKey, out float next) && Time.time < next) return false;
            NextHealPulse[workerKey] = Time.time + HealIntervalSeconds;

            amount = Mathf.Min(maxHealth - health, Mathf.Max(1f, maxHealth * HealFractionPerPulse));
            if (amount <= 0f) return false;

            try
            {
                if (HealWithTextMethod != null)
                    HealWithTextMethod.Invoke(worker, new object[] { amount, true });
                else if (HealMethod != null)
                    HealMethod.Invoke(worker, new object[] { amount });
                else
                    return false;

                float healedHealth = Mathf.Min(maxHealth, health + amount);
                LastHealth[workerKey] = healedHealth;
                HealthCache[workerKey] = new HealthSnapshot
                {
                    Health = healedHealth,
                    MaxHealth = maxHealth,
                    NextProbe = Time.time + 0.5f
                };
                return true;
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Worker rest heal failed safely: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        internal static WorkerRestReason GetReason(ZDO zdo)
        {
            return zdo == null
                ? WorkerRestReason.None
                : (WorkerRestReason)zdo.GetInt(WorkerZdo.RestReason, (int)WorkerRestReason.None);
        }

        internal static bool IsRestState(WorkerState state)
        {
            return state == WorkerState.ReturnHomeForRest || state == WorkerState.Resting || state == WorkerState.Recover;
        }
    }
}
