using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Optional command layer for normal tamed Wolves. Wolves stay vanilla livestock/companions;
    /// this does not recruit them as Workers and does not give them a Worker home or inventory.
    /// Shift+E toggles a lightweight patrol around the position where the command was issued.
    /// Normal E remains vanilla Follow/Stay and cancels an active patrol before Valheim handles it.
    /// </summary>
    internal static class WolfPatrolRuntime
    {
        private const string EnabledKey = "vw_wolf_patrol_enabled";
        private const string CenterXKey = "vw_wolf_patrol_center_x";
        private const string CenterYKey = "vw_wolf_patrol_center_y";
        private const string CenterZKey = "vw_wolf_patrol_center_z";
        private const string WaypointXKey = "vw_wolf_patrol_waypoint_x";
        private const string WaypointYKey = "vw_wolf_patrol_waypoint_y";
        private const string WaypointZKey = "vw_wolf_patrol_waypoint_z";
        private const string WaypointIndexKey = "vw_wolf_patrol_waypoint_index";
        private const string HoldUntilKey = "vw_wolf_patrol_hold_until";
        private const string WaypointStartedKey = "vw_wolf_patrol_waypoint_started";

        private const int PatrolPointCount = 8;
        private const float ReachDistance = 1.35f;
        private const double HoldSeconds = 2.75;
        private const double WaypointTimeoutSeconds = 18.0;

        private static readonly BindingFlags InstanceAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MethodInfo MoveToMethod = typeof(BaseAI).GetMethod(
            "MoveTo", InstanceAny, null,
            new[] { typeof(float), typeof(Vector3), typeof(float), typeof(bool) }, null);
        private static readonly MethodInfo StopMovingMethod = typeof(BaseAI).GetMethod(
            "StopMoving", InstanceAny, null, Type.EmptyTypes, null);
        private static readonly MethodInfo SetPatrolPointMethod = typeof(BaseAI).GetMethod(
            "SetPatrolPoint", InstanceAny, null, new[] { typeof(Vector3) }, null);
        private static readonly MethodInfo ResetPatrolPointMethod = typeof(BaseAI).GetMethod(
            "ResetPatrolPoint", InstanceAny, null, Type.EmptyTypes, null);

        private sealed class EligibilityProbe
        {
            internal MonsterAI Ai;
            internal Character Character;
            internal bool IsWolf;
        }

        // UpdateAI is a hot path for every loaded creature. Resolve species only once per MonsterAI;
        // normal creatures then leave through one dictionary lookup. Only actual wolves read patrol ZDO state.
        private static readonly Dictionary<int, EligibilityProbe> ProbeCache = new Dictionary<int, EligibilityProbe>();
        private static float _radius = 18f;
        private static float _nextCleanupAt;

        internal static float Radius => _radius;

        internal static void ConfigureRadius(float radius)
        {
            _radius = Mathf.Clamp(radius, 5f, 50f);
        }

        internal static void ClearCaches()
        {
            ProbeCache.Clear();
            _nextCleanupAt = 0f;
        }

        internal static bool IsEligible(Tameable tameable, out Character wolf, out ZDO zdo)
        {
            wolf = null;
            zdo = null;
            if (tameable == null) return false;

            try { wolf = tameable.GetComponent<Character>() ?? tameable.GetComponentInParent<Character>(); }
            catch { wolf = null; }
            if (!IsWolfCharacter(wolf)) return false;

            zdo = GetZdo(wolf);
            if (zdo == null || WorkerZdo.IsWorker(zdo)) return false;
            try { return wolf.IsTamed(); }
            catch { return zdo.GetBool("tamed", false); }
        }

        internal static bool IsPatrolling(ZDO zdo) => zdo != null && zdo.GetBool(EnabledKey, false);

        internal static bool TryHandleInteraction(Tameable tameable, Humanoid user, bool hold, bool alt, out bool result)
        {
            result = false;
            if (hold || !IsEligible(tameable, out Character wolf, out ZDO zdo)) return false;

            if (!alt)
            {
                // Vanilla E is still Follow/Stay. Do not let a stale patrol point fight the native command.
                if (IsPatrolling(zdo)) Disable(wolf, zdo);
                return false;
            }

            if (IsPatrolling(zdo))
                Disable(wolf, zdo);
            else
                Enable(wolf, zdo);

            result = true;
            return true;
        }

        internal static string BuildHoverExtra(Tameable tameable)
        {
            if (!IsEligible(tameable, out _, out ZDO zdo)) return string.Empty;
            if (IsPatrolling(zdo))
                return $"<color=#8FD18F>Patrouille AKTIV</color> • Radius {_radius:0} m\n[<color=yellow><b>Shift+E</b></color>] Patrouille beenden  •  [<color=yellow><b>E</b></color>] normales Folgen/Bleiben";
            return $"[<color=yellow><b>Shift+E</b></color>] Patrouille hier starten ({_radius:0} m)";
        }

        internal static void ApplyAfterVanilla(MonsterAI ai, float dt)
        {
            if (!TryResolveWolf(ai, out Character wolf)) return;
            ZDO zdo = GetZdo(wolf);
            if (zdo == null || !IsPatrolling(zdo)) return;

            bool tamed;
            try { tamed = wolf.IsTamed(); }
            catch { tamed = zdo.GetBool("tamed", false); }
            if (!tamed || wolf.IsDead())
            {
                Disable(wolf, zdo);
                return;
            }

            // Patrol owns locomotion, but native wolf combat stays authoritative. A fight interrupts
            // travel naturally; after the target disappears the same saved waypoint resumes.
            Character combatTarget = null;
            try { combatTarget = ai.GetTargetCreature(); } catch { }
            if (combatTarget != null && !combatTarget.IsDead()) return;

            try
            {
                if (ai.GetFollowTarget() != null)
                    ai.SetFollowTarget(null);
            }
            catch { }

            Vector3 center = ReadCenter(zdo, wolf.transform.position);
            TrySetPatrolPoint(ai, center);

            Vector3 waypoint = ReadWaypoint(zdo, center);
            Vector3 delta = waypoint - wolf.transform.position;
            delta.y = 0f;
            float distance = delta.magnitude;
            long nowTicks = DateTime.UtcNow.Ticks;

            long started = zdo.GetLong(WaypointStartedKey, 0L);
            bool timedOut = started > 0L && new TimeSpan(Math.Max(0L, nowTicks - started)).TotalSeconds >= WaypointTimeoutSeconds;
            if (distance <= ReachDistance || timedOut)
            {
                long holdUntil = zdo.GetLong(HoldUntilKey, 0L);
                if (!timedOut && holdUntil <= 0L)
                {
                    holdUntil = DateTime.UtcNow.AddSeconds(HoldSeconds).Ticks;
                    zdo.Set(HoldUntilKey, holdUntil);
                }

                if (!timedOut && nowTicks < holdUntil)
                {
                    TryStopMoving(ai);
                    return;
                }

                int next = (zdo.GetInt(WaypointIndexKey, 0) + 1) % PatrolPointCount;
                SelectWaypoint(zdo, center, wolf, next);
                waypoint = ReadWaypoint(zdo, center);
            }

            if (MoveToMethod != null)
            {
                try
                {
                    MoveToMethod.Invoke(ai, new object[] { Mathf.Max(0.001f, dt), waypoint, ReachDistance * 0.65f, false });
                    return;
                }
                catch { }
            }

            // Compatibility fallback only if BaseAI.MoveTo changed. Native movement input remains
            // cheap and keeps the wolf moving toward the persisted patrol point.
            Vector3 move = waypoint - wolf.transform.position;
            move.y = 0f;
            if (move.sqrMagnitude > 0.05f)
            {
                wolf.SetMoveDir(move.normalized);
                wolf.SetRun(false);
            }
        }

        private static void Enable(Character wolf, ZDO zdo)
        {
            if (wolf == null || zdo == null) return;
            Vector3 center = wolf.transform.position;
            zdo.Set(EnabledKey, true);
            zdo.Set(CenterXKey, center.x);
            zdo.Set(CenterYKey, center.y);
            zdo.Set(CenterZKey, center.z);
            zdo.Set(WaypointIndexKey, 0);
            zdo.Set(HoldUntilKey, 0L);
            zdo.Set(WaypointStartedKey, 0L);
            SelectWaypoint(zdo, center, wolf, 0);

            try
            {
                MonsterAI ai = wolf.GetComponent<MonsterAI>() ?? wolf.GetComponentInChildren<MonsterAI>(true);
                if (ai != null)
                {
                    ai.SetFollowTarget(null);
                    TrySetPatrolPoint(ai, center);
                }
            }
            catch { }
        }

        private static void Disable(Character wolf, ZDO zdo)
        {
            if (zdo != null)
            {
                zdo.Set(EnabledKey, false);
                zdo.Set(HoldUntilKey, 0L);
                zdo.Set(WaypointStartedKey, 0L);
            }

            if (wolf == null) return;
            try
            {
                MonsterAI ai = wolf.GetComponent<MonsterAI>() ?? wolf.GetComponentInChildren<MonsterAI>(true);
                if (ai != null) ResetPatrolPointMethod?.Invoke(ai, null);
            }
            catch { }
        }

        private static bool TryResolveWolf(MonsterAI ai, out Character wolf)
        {
            wolf = null;
            if (ai == null) return false;
            int key = ai.GetInstanceID();
            if (ProbeCache.TryGetValue(key, out EligibilityProbe cached) && cached != null && cached.Ai == ai)
            {
                if (!cached.IsWolf || cached.Character == null) return false;
                wolf = cached.Character;
                return true;
            }

            Character character = null;
            try { character = ai.GetComponent<Character>() ?? ai.GetComponentInParent<Character>(); }
            catch { }
            bool isWolf = IsWolfCharacter(character);
            ProbeCache[key] = new EligibilityProbe { Ai = ai, Character = character, IsWolf = isWolf };
            CleanupProbeCacheIfNeeded();
            if (!isWolf) return false;
            wolf = character;
            return true;
        }

        private static bool IsWolfCharacter(Character character)
        {
            if (character == null || character is Player || character.gameObject == null) return false;
            string prefab;
            try { prefab = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name); }
            catch { return false; }
            return string.Equals(prefab, "Wolf", StringComparison.OrdinalIgnoreCase);
        }

        private static ZDO GetZdo(Character character)
        {
            if (character == null) return null;
            try
            {
                ZNetView view = character.GetComponent<ZNetView>();
                return view != null && view.IsValid() ? view.GetZDO() : null;
            }
            catch { return null; }
        }

        private static Vector3 ReadCenter(ZDO zdo, Vector3 fallback)
        {
            if (zdo == null) return fallback;
            return new Vector3(
                zdo.GetFloat(CenterXKey, fallback.x),
                zdo.GetFloat(CenterYKey, fallback.y),
                zdo.GetFloat(CenterZKey, fallback.z));
        }

        private static Vector3 ReadWaypoint(ZDO zdo, Vector3 fallback)
        {
            if (zdo == null) return fallback;
            return new Vector3(
                zdo.GetFloat(WaypointXKey, fallback.x),
                zdo.GetFloat(WaypointYKey, fallback.y),
                zdo.GetFloat(WaypointZKey, fallback.z));
        }

        private static void SelectWaypoint(ZDO zdo, Vector3 center, Character wolf, int index)
        {
            if (zdo == null) return;
            index = ((index % PatrolPointCount) + PatrolPointCount) % PatrolPointCount;
            float ring = Mathf.Max(2.5f, _radius * 0.72f);
            float angle = (22.5f + index * (360f / PatrolPointCount)) * Mathf.Deg2Rad;
            Vector3 point = center + new Vector3(Mathf.Cos(angle) * ring, 0f, Mathf.Sin(angle) * ring);
            try
            {
                if (ZoneSystem.instance != null)
                    point.y = ZoneSystem.instance.GetGroundHeight(point) + 0.05f;
                else if (wolf != null)
                    point.y = wolf.transform.position.y;
            }
            catch
            {
                if (wolf != null) point.y = wolf.transform.position.y;
            }

            zdo.Set(WaypointIndexKey, index);
            zdo.Set(WaypointXKey, point.x);
            zdo.Set(WaypointYKey, point.y);
            zdo.Set(WaypointZKey, point.z);
            zdo.Set(HoldUntilKey, 0L);
            zdo.Set(WaypointStartedKey, DateTime.UtcNow.Ticks);
        }

        private static void TrySetPatrolPoint(BaseAI ai, Vector3 center)
        {
            if (ai == null || SetPatrolPointMethod == null) return;
            try { SetPatrolPointMethod.Invoke(ai, new object[] { center }); }
            catch { }
        }

        private static void TryStopMoving(BaseAI ai)
        {
            if (ai == null || StopMovingMethod == null) return;
            try { StopMovingMethod.Invoke(ai, null); }
            catch { }
        }

        private static void CleanupProbeCacheIfNeeded()
        {
            float now = Time.realtimeSinceStartup;
            if (ProbeCache.Count < 512 || now < _nextCleanupAt) return;
            _nextCleanupAt = now + 30f;
            List<int> stale = null;
            foreach (KeyValuePair<int, EligibilityProbe> pair in ProbeCache)
            {
                EligibilityProbe probe = pair.Value;
                if (probe == null || probe.Ai == null || probe.Character == null)
                {
                    if (stale == null) stale = new List<int>();
                    stale.Add(pair.Key);
                }
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++) ProbeCache.Remove(stale[i]);
        }
    }

    [HarmonyPatch(typeof(Tameable), "Interact")]
    internal static class TamedWolfPatrolInteractPatch
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(Tameable __instance, Humanoid user, bool hold, bool alt, ref bool __result)
        {
            if (!WolfPatrolRuntime.TryHandleInteraction(__instance, user, hold, alt, out bool result))
                return true;
            __result = result;
            return false;
        }
    }

    [HarmonyPatch(typeof(Tameable), "GetHoverText")]
    internal static class TamedWolfPatrolHoverPatch
    {
        [HarmonyPriority(Priority.Low)]
        private static void Postfix(Tameable __instance, ref string __result)
        {
            string extra = WolfPatrolRuntime.BuildHoverExtra(__instance);
            if (string.IsNullOrEmpty(extra)) return;
            __result = string.IsNullOrEmpty(__result) ? extra : __result + "\n" + extra;
        }
    }

    [HarmonyPatch(typeof(MonsterAI), "UpdateAI")]
    internal static class TamedWolfPatrolAiPatch
    {
        [HarmonyPriority(Priority.Low)]
        private static void Postfix(MonsterAI __instance, float dt)
        {
            WolfPatrolRuntime.ApplyAfterVanilla(__instance, dt);
        }
    }
}
