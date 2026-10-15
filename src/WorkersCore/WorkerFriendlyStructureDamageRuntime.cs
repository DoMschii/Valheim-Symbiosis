using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Prevents recruited Workers from damaging structures built by the same player and protects
    /// the dedicated Surtling home fire itself from its own permanent heat/damage ticks.
    /// </summary>
    internal static class WorkerFriendlyStructureDamageRuntime
    {
        private const float SurtlingFireFallbackRadius = 2.35f;

        private static bool TryResolveSurtlingHomeTarget(Component target, out WorkerRestStation station)
        {
            station = null;
            if (target == null) return false;

            station = target.GetComponent<WorkerRestStation>() ??
                      target.GetComponentInParent<WorkerRestStation>() ??
                      target.GetComponentInChildren<WorkerRestStation>(true);
            if (station != null && station.StationType == RestStationType.Surtling) return true;
            station = null;

            SurtlingHomeInteractable interactable =
                target.GetComponent<SurtlingHomeInteractable>() ??
                target.GetComponentInParent<SurtlingHomeInteractable>() ??
                target.GetComponentInChildren<SurtlingHomeInteractable>(true);
            if (interactable != null)
            {
                station = interactable.GetComponent<WorkerRestStation>() ??
                          interactable.GetComponentInParent<WorkerRestStation>() ??
                          interactable.GetComponentInChildren<WorkerRestStation>(true);
                return true;
            }

            // Jotunn's cloned fire_pit can keep the actual Fireplace/WearNTear/Destructible on a
            // child that is not always under the same component branch as our interaction marker.
            // Resolve the real Fireplace first, then validate that it belongs to the dedicated home.
            Fireplace fireplace = target.GetComponent<Fireplace>() ??
                                  target.GetComponentInParent<Fireplace>() ??
                                  target.GetComponentInChildren<Fireplace>(true);
            if (fireplace != null && SurtlingHomePieceRuntime.IsSurtlingRestFire(fireplace))
            {
                station = fireplace.GetComponent<WorkerRestStation>() ??
                          fireplace.GetComponentInParent<WorkerRestStation>() ??
                          fireplace.GetComponentInChildren<WorkerRestStation>(true);
                return true;
            }

            Transform root = target.transform != null ? target.transform.root : null;
            string rootName = root != null && root.gameObject != null ? root.gameObject.name : string.Empty;
            string normalizedRoot = SpeciesRoleRegistry.NormalizePrefabName(rootName);
            if (string.Equals(normalizedRoot, SurtlingHomePieceRuntime.PrefabName, System.StringComparison.OrdinalIgnoreCase))
                return true;

            // Compatibility fallback for already placed/older homes: only permit proximity matching
            // for something that actually looks like the campfire component. This prevents nearby
            // unrelated building pieces from accidentally gaining damage immunity.
            string ownName = target.gameObject != null ? SpeciesRoleRegistry.NormalizePrefabName(target.gameObject.name) : string.Empty;
            bool looksLikeFire = fireplace != null ||
                                 string.Equals(ownName, SurtlingHomePieceRuntime.BasePrefabName, System.StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(ownName, SurtlingHomePieceRuntime.PrefabName, System.StringComparison.OrdinalIgnoreCase) ||
                                 (!string.IsNullOrEmpty(ownName) && ownName.IndexOf("fire_pit", System.StringComparison.OrdinalIgnoreCase) >= 0);
            if (!looksLikeFire) return false;

            station = WorkerRestStation.ResolveNearestLoaded(
                RestStationType.Surtling,
                target.transform.position,
                SurtlingFireFallbackRadius);
            return station != null;
        }

        internal static bool IsSurtlingHomeSelfDamage(Component target, HitData hit)
        {
            if (target == null || hit == null) return false;
            if (!TryResolveSurtlingHomeTarget(target, out WorkerRestStation station)) return false;

            Character attacker = null;
            try { attacker = hit.GetAttacker(); } catch { }

            // The dedicated Surtling hearth is deliberately a permanent fire. Environmental/fire
            // damage has no Character attacker, and must never be allowed to destroy that hearth.
            // Do not key this only to m_fire: Valheim/Jotunn damage forwarding can arrive after the
            // elemental split has already been transformed by another ingress path.
            if (attacker == null)
                return true;

            if (attacker is Player) return false;

            ZNetView attackerView = attacker.GetComponent<ZNetView>();
            ZDO attackerZdo = attackerView != null && attackerView.IsValid() ? attackerView.GetZDO() : null;
            if (!WorkerZdo.IsWorker(attackerZdo)) return false;

            string species = SpeciesRoleRegistry.NormalizePrefabName(
                attackerZdo.GetString(WorkerZdo.SpeciesKey, attacker.gameObject != null ? attacker.gameObject.name : string.Empty));
            if (!string.Equals(species, "Surtling", System.StringComparison.OrdinalIgnoreCase)) return false;

            ZNetView homeView = station != null
                ? WorkerRestStationFactory.ResolveNetworkView(station)
                : (target.GetComponent<ZNetView>() ?? target.GetComponentInParent<ZNetView>());
            ZDO homeZdo = homeView != null && homeView.IsValid() ? homeView.GetZDO() : null;
            if (homeZdo == null)
            {
                // Old/migrating placed homes may not expose their assignment immediately. If the
                // damaged object is definitely the dedicated fire, a recruited Surtling may not
                // destroy it while standing/working there.
                return true;
            }

            ZDOID assignedWorker = WorkerHomeAnchorZdo.ReadAssignedWorkerZdoId(homeZdo);
            if (!assignedWorker.Equals(ZDOID.None) && attackerZdo.m_uid.Equals(assignedWorker))
                return true;

            long homeOwner = homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L);
            long workerOwner = attackerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            if (homeOwner != 0L && workerOwner != 0L && homeOwner == workerOwner)
                return true;

            return WorkerHomeAnchorZdo.IsAssigned(homeZdo) &&
                   string.Equals(
                       SpeciesRoleRegistry.NormalizePrefabName(WorkerHomeAnchorZdo.ReadWorkerSpecies(homeZdo)),
                       "Surtling",
                       System.StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsProtectedFriendlyStructure(WearNTear target, HitData hit)
        {
            if (target == null || hit == null) return false;

            Character attacker;
            try { attacker = hit.GetAttacker(); }
            catch { return false; }

            if (attacker == null || attacker is Player) return false;

            ZNetView workerView = attacker.GetComponent<ZNetView>();
            ZDO workerZdo = workerView != null && workerView.IsValid() ? workerView.GetZDO() : null;
            if (!WorkerZdo.IsWorker(workerZdo)) return false;

            long workerOwner = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            if (workerOwner == 0L) return false;

            Piece piece = target.GetComponent<Piece>() ?? target.GetComponentInParent<Piece>();
            if (piece == null) return false;

            long pieceCreator;
            try { pieceCreator = piece.GetCreator(); }
            catch { return false; }

            return pieceCreator != 0L && pieceCreator == workerOwner;
        }
    }

    [HarmonyPatch]
    internal static class WorkerFriendlyWearNTearDamageIngressPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            string[] names = { "Damage", "RPC_Damage" };
            return AccessTools.GetDeclaredMethods(typeof(WearNTear))
                .Where(method => names.Contains(method.Name) &&
                                 method.GetParameters().Any(parameter => parameter.ParameterType == typeof(HitData)))
                .Cast<MethodBase>();
        }

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(WearNTear __instance, object[] __args)
        {
            if (__instance == null || __args == null) return true;

            HitData hit = null;
            for (int i = 0; i < __args.Length; ++i)
            {
                hit = __args[i] as HitData;
                if (hit != null) break;
            }
            if (hit == null) return true;

            if (WorkerFriendlyStructureDamageRuntime.IsSurtlingHomeSelfDamage(__instance, hit))
                return false;

            return !WorkerFriendlyStructureDamageRuntime.IsProtectedFriendlyStructure(__instance, hit);
        }
    }

    /// <summary>
    /// Some fire_pit prefab variants forward incoming damage through Destructible instead of the
    /// root WearNTear. Protect that ingress as well; this is the path that remained visible as the
    /// repeating 10-damage ticks which eventually destroyed the Surtling campfire.
    /// </summary>
    [HarmonyPatch]
    internal static class WorkerFriendlyDestructibleDamageIngressPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            string[] names = { "Damage", "RPC_Damage" };
            return AccessTools.GetDeclaredMethods(typeof(Destructible))
                .Where(method => names.Contains(method.Name) &&
                                 method.GetParameters().Any(parameter => parameter.ParameterType == typeof(HitData)))
                .Cast<MethodBase>();
        }

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Destructible __instance, object[] __args)
        {
            if (__instance == null || __args == null) return true;

            HitData hit = null;
            for (int i = 0; i < __args.Length; ++i)
            {
                hit = __args[i] as HitData;
                if (hit != null) break;
            }
            if (hit == null) return true;

            return !WorkerFriendlyStructureDamageRuntime.IsSurtlingHomeSelfDamage(__instance, hit);
        }
    }
}
