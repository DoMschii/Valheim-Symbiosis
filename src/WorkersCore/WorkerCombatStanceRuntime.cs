using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Persistent combat posture for recruited Workers.
    ///
    /// Aggressive: vanilla tamed MonsterAI may acquire hostile targets normally.
    /// Defensive: proactive targets are suppressed; the Worker may retaliate against the creature
    /// that actually damaged it for a short window.
    /// Passive: proactive creature/static combat targets are suppressed and alert state is cleared.
    /// Role work attacks such as Lumberjack tree chopping are separate and remain available.
    ///
    /// Valheim 1.0.15 note:
    /// MonsterAI no longer exposes the old callable SetTarget(Character) API, and SetAlerted(bool)
    /// is not publicly callable from this assembly. Target cleanup therefore uses cached, guarded
    /// reflection for the current MonsterAI target fields / BaseAI alert method. Enemy acquisition
    /// is filtered at BaseAI.FindEnemy instead of patching a non-existent SetTarget method.
    /// </summary>
    internal static class WorkerCombatStanceRuntime
    {
        internal const float DefensiveRetaliationSeconds = 20f;

        private static WorkerStance _defaultStance = WorkerStance.Defensive;
        internal static WorkerStance DefaultStance => _defaultStance;

        internal static void ConfigureDefaultStance(WorkerStance stance)
        {
            _defaultStance = stance >= WorkerStance.Aggressive && stance <= WorkerStance.Passive
                ? stance
                : WorkerStance.Defensive;
        }

        private const BindingFlags InstanceAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticAny = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // Cached once. No AccessTools lookup/log spam in AI ticks.
        private static readonly FieldInfo TargetCreatureField = typeof(MonsterAI).GetField("m_targetCreature", InstanceAny);
        private static readonly FieldInfo TargetStaticField = typeof(MonsterAI).GetField("m_targetStatic", InstanceAny);
        private static readonly MethodInfo SetAlertedMethod = typeof(BaseAI).GetMethod(
            "SetAlerted",
            InstanceAny,
            null,
            new[] { typeof(bool) },
            null);
        private static readonly MethodInfo IsEnemyMethod = ResolveIsEnemyMethod();

        private sealed class RetaliationRecord
        {
            internal Character Attacker;
            internal float ExpiresAt;
        }

        private static readonly Dictionary<int, RetaliationRecord> Retaliation = new Dictionary<int, RetaliationRecord>();
        private static readonly Dictionary<int, float> NextGuardScanAt = new Dictionary<int, float>();
        private static readonly Dictionary<int, Character> CachedGuardTargets = new Dictionary<int, Character>();

        internal static WorkerStance ReadStance(ZDO zdo)
        {
            int raw = zdo != null ? zdo.GetInt(WorkerZdo.Stance, (int)DefaultStance) : (int)DefaultStance;
            return raw >= (int)WorkerStance.Aggressive && raw <= (int)WorkerStance.Passive
                ? (WorkerStance)raw
                : DefaultStance;
        }

        internal static WorkerStance SetStance(Character worker, ZDO zdo, WorkerStance stance)
        {
            if (stance < WorkerStance.Aggressive || stance > WorkerStance.Passive)
                stance = WorkerStance.Defensive;
            zdo?.Set(WorkerZdo.Stance, (int)stance);
            ApplyImmediately(worker, zdo, stance);
            return stance;
        }

        internal static WorkerStance CycleStance(Character worker, ZDO zdo)
        {
            WorkerStance current = ReadStance(zdo);
            WorkerStance next = current == WorkerStance.Aggressive
                ? WorkerStance.Defensive
                : current == WorkerStance.Defensive
                    ? WorkerStance.Passive
                    : WorkerStance.Aggressive;

            zdo?.Set(WorkerZdo.Stance, (int)next);
            ApplyImmediately(worker, zdo, next);
            return next;
        }

        internal static string DisplayName(WorkerStance stance)
        {
            switch (stance)
            {
                case WorkerStance.Aggressive: return "Aggressiv";
                case WorkerStance.Passive: return "Nicht angreifen";
                default: return "Defensiv";
            }
        }

        internal static bool ShouldYieldToCombat(MonsterAI ai, Character worker, ZDO zdo)
        {
            if (ai == null || worker == null || zdo == null) return false;
            Character target = ai.GetTargetCreature();
            if (target == null || target.IsDead() || !CanTarget(worker, zdo, target)) return false;
            if (HunterWorkRuntime.IsAssignedPrey(worker, target, zdo))
            {
                // Hunting is Worker-owned job combat. Keep position/aim/shot timing in HunterWorkRuntime
                // instead of yielding the whole worker to vanilla MonsterAI.
                HunterWorkRuntime.NotePreyPosition(worker, target);
                return false;
            }
            return true;
        }

        internal static bool SetHunterJobTarget(MonsterAI ai, Character worker, ZDO zdo, Character target)
        {
            if (ai == null || worker == null || zdo == null || target == null || target.IsDead() || TargetCreatureField == null) return false;
            if (!HunterWorkRuntime.IsAssignedPrey(worker, target, zdo)) return false;
            try
            {
                TargetCreatureField.SetValue(ai, target);
                ClearStaticTarget(ai);
                SetAlertedSafe(ai, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void ClearHunterJobTarget(MonsterAI ai)
        {
            ClearCreatureTarget(ai);
        }

        // Work animations such as the Firekeeper rekindle cast must not inherit an old
        // vanilla combat target. A stale MonsterAI target can steer projectile attacks away
        // from the work object even after the Worker has physically turned toward it.
        internal static void ClearWorkAttackTargets(MonsterAI ai)
        {
            if (ai == null) return;
            ClearCreatureTarget(ai);
            ClearStaticTarget(ai);
            SetAlertedSafe(ai, false);
        }

        private static void ApplyImmediately(Character worker, ZDO zdo, WorkerStance stance)
        {
            if (worker == null) return;
            MonsterAI ai = worker.GetComponent<MonsterAI>();
            if (ai == null) return;

            if (stance == WorkerStance.Aggressive)
            {
                Character target = ai.GetTargetCreature();
                if (target != null && !CanTarget(worker, zdo, target))
                    ClearCreatureTarget(ai);
                return;
            }

            Character current = ai.GetTargetCreature();
            if (current != null && !CanTarget(worker, zdo, current))
                ClearCreatureTarget(ai);

            // Defensive/passive workers do not proactively attack static targets/buildings.
            ClearStaticTarget(ai);

            if (stance == WorkerStance.Passive || ai.GetTargetCreature() == null)
                SetAlertedSafe(ai, false);
        }

        private static bool CanTarget(Character worker, ZDO zdo, Character target)
        {
            if (target == null) return true;
            if (worker == null || zdo == null) return false;

            if (target is Player player && player.GetPlayerID() == zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L))
                return false;

            // Hunting is a work action, like tree chopping, not the Worker's general combat posture.
            // A Hunter may therefore shoot its explicitly assigned Deer/Boar quarry even while the
            // player keeps normal creature combat on Defensive or Passive.
            if (HunterWorkRuntime.IsAssignedPrey(worker, target, zdo)) return true;

            // Guard is also an explicit work action. Vanilla FindEnemy decides what is actually hostile;
            // Workers only constrains that target to the assigned Workzone. This lets a ranged Draugr or
            // later Guard roles use their native combat without turning Defensive/Passive into free-roam aggro.
            if (IsGuardTargetAllowed(worker, zdo, target)) return true;

            WorkerStance stance = ReadStance(zdo);
            if (stance == WorkerStance.Aggressive) return true;
            if (stance == WorkerStance.Passive) return false;

            RetaliationRecord record = GetValidRetaliation(worker);
            return record != null && record.Attacker == target;
        }

        private static bool IsGuardJobActive(ZDO zdo)
        {
            if (zdo == null) return false;
            WorkOrder order = (WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None);
            if (order != WorkOrder.Guard) return false;
            WorkerRole role = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            return role == WorkerRole.Hunter || role == WorkerRole.Guard;
        }

        private static bool IsGuardTargetAllowed(Character worker, ZDO zdo, Character target)
        {
            if (worker == null || zdo == null || target == null || target.IsDead() || !IsGuardJobActive(zdo))
                return false;

            // Never allow a Worker to guard-attack its assigned player. Friendly/faction decisions are
            // still made by vanilla FindEnemy before this helper is reached for proactive acquisition.
            if (target is Player player && player.GetPlayerID() == zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L))
                return false;

            if (!WorkerZdo.TryReadWorkArea(zdo, out WorkOrder order, out Vector3 center, out float radius) || order != WorkOrder.Guard)
                return false;

            float leash = Mathf.Max(5f, radius) + 2f;
            Vector3 delta = target.transform.position - center;
            delta.y = 0f;
            return delta.sqrMagnitude <= leash * leash;
        }

        private static RetaliationRecord GetValidRetaliation(Character worker)
        {
            if (worker == null) return null;

            int key = worker.GetInstanceID();
            if (!Retaliation.TryGetValue(key, out RetaliationRecord record) || record == null)
                return null;

            if (Time.time > record.ExpiresAt || record.Attacker == null || record.Attacker.IsDead())
            {
                Retaliation.Remove(key);
                return null;
            }

            return record;
        }

        private static bool TryGetWorker(MonsterAI ai, out Character worker, out ZNetView nview, out ZDO zdo)
        {
            return WorkerFastLookupRuntime.TryGetWorker(ai, true, out worker, out nview, out zdo);
        }

        internal static Character FilterFindEnemy(BaseAI baseAi, Character vanillaResult)
        {
            MonsterAI ai = baseAi as MonsterAI;
            if (ai == null || !TryGetWorker(ai, out Character worker, out _, out ZDO zdo))
                return vanillaResult;

            // Guard duty first accepts vanilla acquisition, but a raised guard post must not be
            // limited by the creature's short default aggro bubble. If vanilla has no usable target,
            // perform a throttled hostile-character scan up to the configured bow range.
            if (IsGuardJobActive(zdo))
            {
                if (vanillaResult != null && IsGuardTargetAllowed(worker, zdo, vanillaResult))
                    return vanillaResult;
                return TryFindGuardEnemy(worker, zdo);
            }

            WorkerStance stance = ReadStance(zdo);
            if (stance == WorkerStance.Passive)
                return null;

            if (stance == WorkerStance.Defensive)
            {
                RetaliationRecord record = GetValidRetaliation(worker);
                return record != null ? record.Attacker : null;
            }

            // Aggressive still never targets its assigned player.
            return vanillaResult != null && CanTarget(worker, zdo, vanillaResult) ? vanillaResult : null;
        }

        internal static Character TryFindGuardEnemy(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return null;
            int key = worker.GetInstanceID();
            float now = Time.time;

            if (CachedGuardTargets.TryGetValue(key, out Character cached) && cached != null && !cached.IsDead() &&
                IsGuardTargetAllowed(worker, zdo, cached) && IsHostile(worker, cached))
            {
                Vector3 cd = cached.transform.position - worker.transform.position;
                cd.y = 0f;
                if (cd.sqrMagnitude <= HunterWorkRuntime.ShootRangeHysteresis * HunterWorkRuntime.ShootRangeHysteresis)
                    return cached;
            }

            if (NextGuardScanAt.TryGetValue(key, out float next) && now < next) return null;
            NextGuardScanAt[key] = now + 0.75f;
            CachedGuardTargets.Remove(key);

            if (!WorkerZdo.TryReadWorkArea(zdo, out WorkOrder order, out Vector3 center, out float workRadius) || order != WorkOrder.Guard)
                return null;

            float scanRadius = Mathf.Min(Mathf.Max(5f, workRadius), HunterWorkRuntime.ShootRange);
            float bestSqr = scanRadius * scanRadius;
            Character best = null;
            List<Character> candidates = WorkerCharacterIndexRuntime.GetUntamedNonPlayers();
            for (int i = 0; i < candidates.Count; i++)
            {
                Character candidate = candidates[i];
                if (candidate == null || candidate == worker || candidate.IsDead()) continue;
                if (!IsGuardTargetAllowed(worker, zdo, candidate) || !IsHostile(worker, candidate)) continue;

                Vector3 fromGuard = candidate.transform.position - worker.transform.position;
                fromGuard.y = 0f;
                float sqr = fromGuard.sqrMagnitude;
                if (sqr > bestSqr) continue;

                Vector3 fromZone = candidate.transform.position - center;
                fromZone.y = 0f;
                if (fromZone.sqrMagnitude > workRadius * workRadius) continue;

                best = candidate;
                bestSqr = sqr;
            }

            if (best != null) CachedGuardTargets[key] = best;
            return best;
        }

        private static bool IsHostile(Character worker, Character candidate)
        {
            if (worker == null || candidate == null || IsEnemyMethod == null) return false;
            try
            {
                object result = IsEnemyMethod.Invoke(null, new object[] { worker, candidate });
                return result is bool hostile && hostile;
            }
            catch
            {
                return false;
            }
        }

        private static MethodInfo ResolveIsEnemyMethod()
        {
            MethodInfo[] methods = typeof(BaseAI).GetMethods(StaticAny);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != "IsEnemy" || method.ReturnType != typeof(bool)) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 2) continue;
                if (typeof(Character).IsAssignableFrom(parameters[0].ParameterType) &&
                    typeof(Character).IsAssignableFrom(parameters[1].ParameterType))
                    return method;
            }
            return null;
        }

        internal static void NoteDamage(MonsterAI ai, float damage, Character attacker)
        {
            if (damage <= 0f || attacker == null || attacker.IsDead()) return;
            if (!TryGetWorker(ai, out Character worker, out _, out ZDO zdo)) return;
            if (ReadStance(zdo) != WorkerStance.Defensive) return;

            if (attacker is Player player && player.GetPlayerID() == zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L))
                return;

            Retaliation[worker.GetInstanceID()] = new RetaliationRecord
            {
                Attacker = attacker,
                ExpiresAt = Time.time + DefensiveRetaliationSeconds
            };
        }

        internal static void EnforceBeforeVanilla(MonsterAI ai)
        {
            if (!TryGetWorker(ai, out Character worker, out _, out ZDO zdo)) return;
            WorkerStance stance = ReadStance(zdo);

            // v0.2.54: proactive Guard acquisition must not depend on vanilla FindEnemy being called.
            // Scan on the normal throttled cadence every AI update and inject the concrete hostile
            // target into MonsterAI. This makes the configured Hunter/Guard range a real detection
            // range instead of merely extending attacks after vanilla aggro already happened.
            if (IsGuardJobActive(zdo))
            {
                Character acquired = TryFindGuardEnemy(worker, zdo);
                if (acquired != null)
                {
                    TrySetCreatureTarget(ai, acquired);
                    SetAlertedSafe(ai, true);
                }
            }

            Character target = ai.GetTargetCreature();
            bool hunterJobTarget = target != null && HunterWorkRuntime.IsAssignedPrey(worker, target, zdo);
            bool guardJobTarget = target != null && IsGuardTargetAllowed(worker, zdo, target);
            if (target != null && !CanTarget(worker, zdo, target))
            {
                ClearCreatureTarget(ai);
                hunterJobTarget = false;
                guardJobTarget = false;
            }

            if (stance != WorkerStance.Aggressive)
                ClearStaticTarget(ai);

            // Passive suppresses ordinary combat, but explicit Hunter/Guard job combat still needs
            // MonsterAI's alerted state so the native ranged/melee attack can run.
            if (!hunterJobTarget && !guardJobTarget && (stance == WorkerStance.Passive || (stance == WorkerStance.Defensive && ai.GetTargetCreature() == null)))
                SetAlertedSafe(ai, false);
        }

        private static void TrySetCreatureTarget(MonsterAI ai, Character target)
        {
            if (ai == null || target == null || TargetCreatureField == null) return;
            try
            {
                TargetCreatureField.SetValue(ai, target);
            }
            catch
            {
                // Guard acquisition is retried on the next throttled scan.
            }
        }

        private static void ClearCreatureTarget(MonsterAI ai)
        {
            if (ai == null || TargetCreatureField == null) return;
            try
            {
                TargetCreatureField.SetValue(ai, null);
            }
            catch
            {
                // Fail closed at the acquisition filter; never spam logs from an AI tick.
            }
        }

        private static void ClearStaticTarget(MonsterAI ai)
        {
            if (ai == null || TargetStaticField == null) return;
            try
            {
                TargetStaticField.SetValue(ai, null);
            }
            catch
            {
                // Optional cleanup only.
            }
        }

        private static void SetAlertedSafe(BaseAI ai, bool alerted)
        {
            if (ai == null || SetAlertedMethod == null) return;
            try
            {
                SetAlertedMethod.Invoke(ai, new object[] { alerted });
            }
            catch
            {
                // Optional state cleanup only; target suppression remains authoritative.
            }
        }
    }

    /// <summary>
    /// Current Valheim MonsterAI acquires proactive enemies through BaseAI.FindEnemy.
    /// Filtering the result avoids depending on the removed/non-public MonsterAI.SetTarget API.
    /// </summary>
    [HarmonyPatch(typeof(BaseAI), "FindEnemy")]
    internal static class WorkerCombatStanceFindEnemyPatch
    {
        private static void Postfix(BaseAI __instance, ref Character __result)
        {
            __result = WorkerCombatStanceRuntime.FilterFindEnemy(__instance, __result);
        }
    }

    [HarmonyPatch(typeof(MonsterAI), "OnDamaged")]
    internal static class WorkerCombatStanceOnDamagedPatch
    {
        // Prefix is intentional: if vanilla OnDamaged reacts immediately, the retaliation window
        // already exists when the next AI update/acquisition step runs.
        private static void Prefix(MonsterAI __instance, float __0, Character __1)
        {
            WorkerCombatStanceRuntime.NoteDamage(__instance, __0, __1);
        }
    }

}
