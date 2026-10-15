using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Protects a recruited Surtling from the permanent fire in its own dedicated home.
    ///
    /// v0.5.217 deliberately protects every Character damage ingress used by Valheim
    /// (Damage / RPC_Damage / ApplyDamage) instead of only Character.Damage.  Some fire/burning
    /// paths reach the RPC/apply stage without going through the public Damage method on the
    /// local peer, which was the reason visible 10-damage ticks could still get through.
    /// </summary>
    internal static class SurtlingRestFireProtectionRuntime
    {
        private const float ProtectionRadius = 3.60f;
        private const float ProtectionRadiusSqr = ProtectionRadius * ProtectionRadius;

        internal static bool IsAtOwnSurtlingHome(Character character)
        {
            if (character == null) return false;

            ZNetView view = character.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (!WorkerZdo.IsWorker(zdo)) return false;

            string species = SpeciesRoleRegistry.NormalizePrefabName(
                zdo.GetString(WorkerZdo.SpeciesKey, character.gameObject != null ? character.gameObject.name : string.Empty));
            if (!string.Equals(species, "Surtling", StringComparison.OrdinalIgnoreCase)) return false;

            Vector3 workerPos = character.transform.position;

            // First use the durable bidirectional home assignment.
            WorkerRestStation station = WorkerRestStation.ResolveLoadedForWorker(zdo);
            if (station != null && station.StationType == RestStationType.Surtling && IsNearStation(workerPos, station))
                return true;

            // Compatibility/robustness fallback: old saves and a few assignment transitions can
            // temporarily have no resolvable HomeAnchor ZDOID even though the Worker is physically
            // standing on its Surtling home.  Use the already-loaded station registry only.
            long owner = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            station = WorkerRestStation.ResolveNearestLoaded(RestStationType.Surtling, workerPos, ProtectionRadius, owner);
            return station != null && IsNearStation(workerPos, station);
        }

        private static bool IsNearStation(Vector3 workerPos, WorkerRestStation station)
        {
            if (station == null) return false;

            Transform rest = station.RestPoint;
            if (rest != null && HorizontalSqr(workerPos, rest.position) <= ProtectionRadiusSqr)
                return true;

            return HorizontalSqr(workerPos, station.transform.position) <= ProtectionRadiusSqr;
        }

        private static float HorizontalSqr(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        /// <summary>
        /// Returns true when the current damage method must be cancelled completely.
        /// Environmental/self fire from the home is pure infrastructure damage and is cancelled
        /// before Valheim can create damage text or a burning status.  A real Character attacker
        /// is never wholesale-cancelled; only its fire component is removed by this home guard.
        /// </summary>
        internal static bool HandleDamageIngress(Character character, HitData hit)
        {
            if (character == null || hit == null || hit.m_damage.m_fire <= 0f) return false;
            if (!IsAtOwnSurtlingHome(character)) return false;

            Character attacker = null;
            try { attacker = hit.GetAttacker(); } catch { }

            // Campfire / burning status ticks have no Character attacker.  Self-generated hits are
            // treated the same.  Cancel the complete ingress so the 10-point popup cannot be emitted
            // by RPC_Damage/ApplyDamage after the fire component was zeroed upstream.
            if (attacker == null || attacker == character)
            {
                hit.m_damage.m_fire = 0f;
                return true;
            }

            // Do not grant general invulnerability while resting.  Enemy/player physical damage
            // remains valid; only the home-fire component is stripped.
            hit.m_damage.m_fire = 0f;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class SurtlingRestFireDamageIngressPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            string[] names = { "Damage", "RPC_Damage", "ApplyDamage" };
            return AccessTools.GetDeclaredMethods(typeof(Character))
                .Where(method => names.Contains(method.Name) &&
                                 method.GetParameters().Any(parameter => parameter.ParameterType == typeof(HitData)))
                .Cast<MethodBase>();
        }

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Character __instance, object[] __args)
        {
            if (__instance == null || __args == null) return true;

            HitData hit = null;
            for (int i = 0; i < __args.Length; ++i)
            {
                hit = __args[i] as HitData;
                if (hit != null) break;
            }

            if (hit == null) return true;
            return !SurtlingRestFireProtectionRuntime.HandleDamageIngress(__instance, hit);
        }
    }
}
