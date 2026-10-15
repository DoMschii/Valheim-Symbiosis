using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Persistent one-to-one assignment between a Firekeeper Surtling and a real Valheim Fireplace.
    /// The Surtling is the heat source: while the assignment is valid, UpdateFireplace temporarily
    /// sees infinite fuel. We never add Wood/Resin/Coal and never modify Smelter fuel logic.
    /// </summary>
    internal static class FirekeeperAssignedFireRuntime
    {
        internal const string ManagedFireTargetKind = "firekeeper_assigned_fire";
        internal const string FireAssigned = "vw_firekeeper_fire_assigned";
        internal const string FireOwnerPlayerId = "vw_firekeeper_fire_owner";
        internal const string FireWorkerZdoId = "vw_firekeeper_fire_worker_zdoid";

        private static readonly FieldInfo InfiniteFuelField = typeof(Fireplace).GetField("m_infiniteFuel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo IsBurningMethod = typeof(Fireplace).GetMethod("IsBurning", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

        private sealed class TendState
        {
            internal float NextProbeAt;
            internal float NextAttackAt;
            internal bool TendedEmptyFuel;
            internal bool AimPrimed;
            internal float AimReadyAt;
            internal string Debug = "standing fire watch";
        }

        private static readonly Dictionary<int, TendState> TendStates = new Dictionary<int, TendState>();
        private static readonly List<Character> WorkerScratch = new List<Character>();

        private static Character _pendingWorker;
        private static long _pendingPlayerId;
        private static float _pendingUntil;

        internal static void BeginFireSelection(Character worker, ZDO workerZdo, Player player)
        {
            if (worker == null || workerZdo == null || player == null) return;
            _pendingWorker = worker;
            _pendingPlayerId = player.GetPlayerID();
            _pendingUntil = Time.unscaledTime + 30f;
            Plugin.Instance?.SetExternalStatus("Feuerauswahl aktiv: Benutze innerhalb von 30 Sekunden mit E das gewuenschte Feuer.");
        }

        internal static void CancelFireSelection()
        {
            _pendingWorker = null;
            _pendingPlayerId = 0L;
            _pendingUntil = 0f;
        }

        internal static bool IsSelectingFor(Humanoid user)
        {
            if (!(user is Player player) || _pendingWorker == null || Time.unscaledTime > _pendingUntil)
            {
                CancelFireSelection();
                return false;
            }
            return _pendingPlayerId == player.GetPlayerID();
        }

        internal static bool TryInteract(Fireplace fireplace, Humanoid user)
        {
            if (fireplace == null || !(user is Player player) || !IsSelectingFor(user)) return false;

            Character worker = _pendingWorker;
            CancelFireSelection();
            if (worker == null || worker.IsDead())
            {
                Plugin.Instance?.SetExternalStatus("Feuerzuweisung abgebrochen: Surtling ist nicht mehr verfuegbar.");
                return true;
            }

            ZNetView fireView = fireplace.GetComponent<ZNetView>();
            if (fireView == null || !fireView.IsValid()) return false;
            ZDO fireZdo = fireView.GetZDO();
            if (fireZdo == null) return false;
            if (!fireView.IsOwner())
            {
                Plugin.Instance?.SetExternalStatus("Feuer ist REMOTE-owned. Bleib kurz in der Naehe und starte die Feuerauswahl erneut.");
                return true;
            }

            ZNetView workerView = worker.GetComponent<ZNetView>();
            ZDO workerZdo = workerView != null && workerView.IsValid() ? workerView.GetZDO() : null;
            if (!WorkerZdo.IsWorker(workerZdo) || workerView == null || !workerView.IsOwner())
            {
                Plugin.Instance?.SetExternalStatus("Surtling ist REMOTE-owned oder kein gueltiger Worker.");
                return true;
            }
            if ((WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None) != WorkerRole.Firekeeper ||
                workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != player.GetPlayerID())
            {
                Plugin.Instance?.SetExternalStatus("Dieses Feuer kann nur einem eigenen Surtling-Feuerwaechter zugewiesen werden.");
                return true;
            }

            if (fireZdo.GetBool(FireAssigned, false))
            {
                long assignedOwner = fireZdo.GetLong(FireOwnerPlayerId, 0L);
                if (assignedOwner != 0L && assignedOwner != player.GetPlayerID())
                {
                    Plugin.Instance?.SetExternalStatus("Dieses Feuer gehoert bereits zu einem Feuerwaechter eines anderen Spielers.");
                    return true;
                }
                ReleaseFire(fireZdo, true);
            }

            ReleasePreviousFire(workerZdo);
            fireZdo.Set(FireAssigned, true);
            fireZdo.Set(FireOwnerPlayerId, player.GetPlayerID());
            fireZdo.Set(FireWorkerZdoId, workerZdo.m_uid);
            WorkerZdo.AssignFireAnchor(workerZdo, fireZdo.m_uid, fireplace.transform.position);
            WorkerZdo.ClearTarget(workerZdo);
            workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);

            Plugin.Instance?.SetExternalStatus("Feuer dem Surtling zugewiesen. Solange er Dienst hat, verbraucht dieses Feuer keinen Brennstoff.");
            return true;
        }

        internal static string AppendHoverText(Fireplace fireplace, string original)
        {
            if (fireplace == null) return original ?? string.Empty;
            ZNetView view = fireplace.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null) return original ?? string.Empty;

            bool assigned = zdo.GetBool(FireAssigned, false);
            string line = assigned
                ? "\n<color=#ff9b42>Workers: Surtling-Feuerwaechter zugewiesen</color>"
                : (Player.m_localPlayer != null && IsSelectingFor(Player.m_localPlayer)
                    ? "\n<color=#ff9b42>Workers: [E] Dieses Feuer zuweisen</color>"
                    : string.Empty);
            return (original ?? string.Empty) + line;
        }

        internal static bool TryResolveAssignedFire(Character worker, ZDO workerZdo, out Vector3 position, out string displayName)
        {
            position = Vector3.zero;
            displayName = "Feuer";
            if (worker == null || workerZdo == null || !WorkerZdo.TryReadFireAnchor(workerZdo, out ZDOID fireId, out Vector3 stored))
                return false;

            // The worker-side anchor is the durable source of truth. During world load the worker
            // can be instantiated before the fireplace ZDO is available. Never erase a valid
            // assignment merely because the target is temporarily unloaded.
            position = stored;
            if (fireId.Equals(ZDOID.None)) return false;
            if (ZDOMan.instance == null)
            {
                displayName = "zugewiesenes Feuer (wird geladen)";
                return true;
            }

            ZDO fireZdo = ZDOMan.instance.GetZDO(fireId);
            if (fireZdo == null)
            {
                displayName = "zugewiesenes Feuer (nicht geladen)";
                return true;
            }

            ZDOID assignedWorker = fireZdo.GetZDOID(FireWorkerZdoId);
            bool fireMarkedAssigned = fireZdo.GetBool(FireAssigned, false);

            // Heal one-sided persistence after save/load. If the worker still owns this anchor and
            // the fireplace is not explicitly assigned to another worker, restore the mirror keys
            // on the fireplace ZDO instead of dropping the worker assignment.
            if (!fireMarkedAssigned || assignedWorker.Equals(ZDOID.None))
            {
                long ownerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
                fireZdo.Set(FireAssigned, true);
                fireZdo.Set(FireOwnerPlayerId, ownerId);
                fireZdo.Set(FireWorkerZdoId, workerZdo.m_uid);
                assignedWorker = workerZdo.m_uid;
            }

            // A real conflicting assignment must never be stolen automatically. Keep our worker
            // anchor intact for diagnostics, but do not claim the foreign fireplace.
            if (!assignedWorker.Equals(workerZdo.m_uid))
            {
                displayName = "zugewiesenes Feuer (Konflikt)";
                return false;
            }

            position = fireZdo.GetPosition();
            // Refresh the stored fallback position while the fireplace is available.
            WorkerZdo.AssignFireAnchor(workerZdo, fireZdo.m_uid, position);
            displayName = "zugewiesenes Feuer";
            return true;
        }

        internal static void TickAtAssignedFire(Character worker, ZDO workerZdo)
        {
            // Movement is intentionally handled by WorkerNavigationRuntime. While assigned, this
            // is a permanent day/night duty post. The Fireplace patch supplies the actual heat;
            // this runtime gives the duty visible behaviour when the real fire goes out.
            if (!TryResolveAssignedFire(worker, workerZdo, out Vector3 firePosition, out _))
            {
                WorkerZdo.ClearTarget(workerZdo);
                workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                if (worker != null) TendStates.Remove(worker.GetInstanceID());
                return;
            }

            if (worker == null) return;
            int key = worker.GetInstanceID();
            if (!TendStates.TryGetValue(key, out TendState state))
            {
                state = new TendState();
                TendStates[key] = state;
            }

            if (Time.time < state.NextProbeAt) return;
            state.NextProbeAt = Time.time + 0.40f;

            Fireplace fire = FindLoadedAssignedFire(workerZdo, firePosition);
            if (fire == null)
            {
                state.Debug = "assigned fire not loaded";
                return;
            }

            if (!TryReadBurning(fire, out bool burning))
            {
                state.Debug = "standing fire watch (burn-state unavailable)";
                return;
            }

            bool zeroFuel = TryReadFuel(fire, out float fuel) && fuel <= 0.01f;
            if (!zeroFuel) state.TendedEmptyFuel = false;

            // Managed fires use the Surtling as their heat source. Because the existing infinite-fuel
            // compatibility patch can keep a zero-fuel fire technically burning, treat the first
            // zero-fuel observation as the visible "rekindle" moment as well. After that, only a
            // real burn-state drop (rain/smothering/etc.) retriggers the animation.
            bool shouldRekindle = !burning || (zeroFuel && !state.TendedEmptyFuel);
            if (!shouldRekindle)
            {
                state.AimPrimed = false;
                state.Debug = "standing fire watch: fire burning";
                return;
            }

            // Face the real fireplace for presentation only. Do NOT call Character.StartAttack here:
            // the native Surtling attack owns its projectile aim and can shoot wildly even when the
            // character is facing the fireplace. Firekeeping is a work animation, not combat.
            Vector3 fireAimPoint = fire.transform.position + Vector3.up * 0.20f;
            Vector3 aim = fireAimPoint - (worker.transform.position + Vector3.up * 1.05f);
            Vector3 flatAim = fireAimPoint - worker.transform.position;
            flatAim.y = 0f;
            if (flatAim.sqrMagnitude > 0.01f)
                worker.transform.rotation = Quaternion.LookRotation(flatAim.normalized, Vector3.up);
            if (aim.sqrMagnitude > 0.01f)
                worker.SetLookDir(aim.normalized, 0f);

            MonsterAI monsterAi = worker.GetComponent<MonsterAI>();
            WorkerCombatStanceRuntime.ClearWorkAttackTargets(monsterAi);

            state.Debug = !burning ? "fire out -> visual rekindle cast" : "fuel empty -> visual fire-tending cast";
            if (Time.time < state.NextAttackAt) return;

            bool animated = FirekeeperAnimationRuntime.TryPlayCast(worker);
            state.NextAttackAt = Time.time + (animated ? 2.2f : 0.85f);
            state.AimPrimed = false;
            if (zeroFuel) state.TendedEmptyFuel = true;
        }




        private static bool TryReadFuel(Fireplace fireplace, out float fuel)
        {
            fuel = 0f;
            if (fireplace == null) return false;
            try
            {
                ZNetView view = fireplace.GetComponent<ZNetView>();
                if (view == null) view = fireplace.GetComponentInParent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null) return false;
                fuel = zdo.GetFloat("fuel", 0f);
                return true;
            }
            catch { return false; }
        }

        internal static string GetTendDebug(Character worker)
        {
            if (worker == null) return "fire watch unavailable";
            return TendStates.TryGetValue(worker.GetInstanceID(), out TendState state)
                ? state.Debug
                : "standing fire watch";
        }

        private static Fireplace FindLoadedAssignedFire(ZDO workerZdo, Vector3 position)
        {
            if (workerZdo == null || !WorkerZdo.TryReadFireAnchor(workerZdo, out ZDOID fireId, out _)) return null;
            Collider[] hits;
            try { hits = Physics.OverlapSphere(position, 2.5f); }
            catch { return null; }
            if (hits == null) return null;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider hit = hits[i];
                if (hit == null) continue;
                Fireplace fire = hit.GetComponentInParent<Fireplace>();
                if (fire == null) continue;
                ZNetView view = fire.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo != null && zdo.m_uid.Equals(fireId)) return fire;
            }
            return null;
        }

        private static bool TryReadBurning(Fireplace fireplace, out bool burning)
        {
            burning = true;
            if (fireplace == null || IsBurningMethod == null) return false;
            try
            {
                object value = IsBurningMethod.Invoke(fireplace, null);
                if (value is bool result)
                {
                    burning = result;
                    return true;
                }
            }
            catch { }
            return false;
        }

        internal static string DescribeBackground(ZDO workerZdo)
        {
            if (workerZdo == null || !WorkerZdo.TryReadFireAnchor(workerZdo, out ZDOID fireId, out _))
                return "firekeeping:no-assigned-fire";
            if (fireId.Equals(ZDOID.None) || ZDOMan.instance == null) return "firekeeping:assigned-offline";
            ZDO fireZdo = ZDOMan.instance.GetZDO(fireId);
            return fireZdo != null && fireZdo.GetBool(FireAssigned, false)
                ? "firekeeping:assigned-fire-active"
                : "firekeeping:assigned-fire-missing";
        }

        internal static bool IsManaged(Fireplace fireplace)
        {
            if (fireplace == null) return false;
            ZNetView view = fireplace.GetComponent<ZNetView>();
            if (view == null) view = fireplace.GetComponentInParent<ZNetView>();
            ZDO fireZdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (fireZdo == null) return false;

            // v0.5.134: the dedicated Surtling rest station is always a permanent heat source.
            // It never consumes Wood, even before a worker is assigned to it.
            if (SurtlingHomePieceRuntime.IsSurtlingRestFire(fireplace)) return true;

            // A legacy/normal fireplace can still become a permanent heat source while occupied.
            // This deliberately does NOT create a worker FireAnchor, so the Firekeeper remains
            // free to patrol its Workzone and tend other fires/torches.
            if (WorkerHomeAnchorZdo.IsAssigned(fireZdo) &&
                string.Equals(WorkerHomeAnchorZdo.ReadWorkerSpecies(fireZdo), "Surtling", StringComparison.OrdinalIgnoreCase) &&
                WorkerHomeAnchorZdo.ReadWorkerRole(fireZdo) == WorkerRole.Firekeeper)
            {
                ZDOID homeWorkerId = WorkerHomeAnchorZdo.ReadAssignedWorkerZdoId(fireZdo);
                if (!homeWorkerId.Equals(ZDOID.None) && ZDOMan.instance != null)
                {
                    ZDO homeWorker = ZDOMan.instance.GetZDO(homeWorkerId);
                    if (WorkerZdo.IsWorker(homeWorker) &&
                        (WorkerRole)homeWorker.GetInt(WorkerZdo.Role, (int)WorkerRole.None) == WorkerRole.Firekeeper)
                        return true;
                }
            }

            if (!fireZdo.GetBool(FireAssigned, false)) return false;
            ZDOID workerId = fireZdo.GetZDOID(FireWorkerZdoId);
            if (workerId.Equals(ZDOID.None) || ZDOMan.instance == null) return false;
            ZDO workerZdo = ZDOMan.instance.GetZDO(workerId);
            if (!WorkerZdo.IsWorker(workerZdo)) return false;
            if ((WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None) != WorkerRole.Firekeeper) return false;
            if (!WorkerZdo.TryReadFireAnchor(workerZdo, out ZDOID workerFireId, out _)) return false;
            return workerFireId.Equals(fireZdo.m_uid);
        }

        internal static bool TryEnableInfiniteFuel(Fireplace fireplace, out bool original)
        {
            original = false;
            if (fireplace == null || InfiniteFuelField == null) return false;
            try
            {
                original = (bool)InfiniteFuelField.GetValue(fireplace);
                if (!IsManaged(fireplace)) return false;
                if (!original) InfiniteFuelField.SetValue(fireplace, true);
                return true;
            }
            catch { return false; }
        }

        internal static void RestoreInfiniteFuel(Fireplace fireplace, bool original, bool changed)
        {
            if (!changed || fireplace == null || InfiniteFuelField == null) return;
            try { InfiniteFuelField.SetValue(fireplace, original); } catch { }
        }

        internal static void ReleaseWorkerFire(ZDO workerZdo)
        {
            if (workerZdo == null) return;
            if (WorkerZdo.TryReadFireAnchor(workerZdo, out ZDOID fireId, out _) && !fireId.Equals(ZDOID.None) && ZDOMan.instance != null)
            {
                ZDO fireZdo = ZDOMan.instance.GetZDO(fireId);
                if (fireZdo != null && fireZdo.GetZDOID(FireWorkerZdoId).Equals(workerZdo.m_uid))
                    ClearFire(fireZdo);
            }
            WorkerZdo.ClearFireAnchor(workerZdo);
        }

        private static void ReleasePreviousFire(ZDO workerZdo)
        {
            if (workerZdo == null) return;
            if (!WorkerZdo.TryReadFireAnchor(workerZdo, out ZDOID oldFireId, out _) || oldFireId.Equals(ZDOID.None) || ZDOMan.instance == null)
                return;
            ZDO oldFire = ZDOMan.instance.GetZDO(oldFireId);
            if (oldFire != null && oldFire.GetZDOID(FireWorkerZdoId).Equals(workerZdo.m_uid)) ClearFire(oldFire);
            WorkerZdo.ClearFireAnchor(workerZdo);
        }

        private static void ReleaseFire(ZDO fireZdo, bool clearWorker)
        {
            if (fireZdo == null) return;
            ZDOID workerId = fireZdo.GetZDOID(FireWorkerZdoId);
            if (clearWorker && !workerId.Equals(ZDOID.None) && ZDOMan.instance != null)
            {
                ZDO workerZdo = ZDOMan.instance.GetZDO(workerId);
                if (workerZdo != null) WorkerZdo.ClearFireAnchor(workerZdo);
            }
            ClearFire(fireZdo);
        }

        private static void ClearFire(ZDO fireZdo)
        {
            fireZdo.Set(FireAssigned, false);
            fireZdo.Set(FireOwnerPlayerId, 0L);
            fireZdo.Set(FireWorkerZdoId, ZDOID.None);
        }

        private static Character FindNearestOwnedSurtling(Vector3 center, float radius, long playerId)
        {
            Character best = null;
            float bestSqr = radius * radius;
            WorkerFastLookupRuntime.CopyKnownWorkers(WorkerScratch);
            for (int i = 0; i < WorkerScratch.Count; i++)
            {
                Character character = WorkerScratch[i];
                if (character == null || character is Player || character.IsDead()) continue;
                if (!string.Equals(SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name), "Surtling", StringComparison.OrdinalIgnoreCase)) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (!WorkerZdo.IsWorker(zdo)) continue;
                if ((WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None) != WorkerRole.Firekeeper) continue;
                if (zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != playerId) continue;
                float sqr = (character.transform.position - center).sqrMagnitude;
                if (sqr > bestSqr) continue;
                bestSqr = sqr;
                best = character;
            }
            return best;
        }
    }

    [HarmonyPatch(typeof(Fireplace), "Interact")]
    internal static class FirekeeperFireInteractPatch
    {
        private static bool Prefix(Fireplace __instance, Humanoid user, bool hold, bool alt, ref bool __result)
        {
            // Dedicated Surtling rest stations never expose vanilla fuel interaction. If Valheim's
            // raycast selects the cloned Fireplace component instead of our helper component, route
            // the same E press back into the Worker-home interaction.
            SurtlingHomeInteractable home = SurtlingHomePieceRuntime.ResolveInteractable(__instance);
            if (home != null)
            {
                __result = !hold && home.Interact(user, hold, alt);
                return false;
            }

            if (hold || !FirekeeperAssignedFireRuntime.IsSelectingFor(user)) return true;
            __result = FirekeeperAssignedFireRuntime.TryInteract(__instance, user);
            return false;
        }
    }

    [HarmonyPatch(typeof(Fireplace), "GetHoverText")]
    internal static class FirekeeperFireHoverPatch
    {
        private static void Postfix(Fireplace __instance, ref string __result)
        {
            SurtlingHomeInteractable home = SurtlingHomePieceRuntime.ResolveInteractable(__instance);
            if (home != null)
            {
                __result = home.GetHoverText();
                return;
            }

            SurtlingSignalFireInteractable signal = __instance != null
                ? (__instance.GetComponent<SurtlingSignalFireInteractable>() ?? __instance.GetComponentInParent<SurtlingSignalFireInteractable>())
                : null;
            if (signal != null)
            {
                __result = signal.GetHoverText();
                return;
            }

            __result = FirekeeperAssignedFireRuntime.AppendHoverText(__instance, __result);
        }
    }

    [HarmonyPatch(typeof(Fireplace), "UpdateFireplace")]
    internal static class FirekeeperInfiniteFuelPatch
    {
        private struct State { internal bool Original; internal bool Changed; }

        private static void Prefix(Fireplace __instance, out State __state)
        {
            __state = new State();
            __state.Changed = FirekeeperAssignedFireRuntime.TryEnableInfiniteFuel(__instance, out __state.Original);
        }

        private static void Postfix(Fireplace __instance, State __state)
        {
            FirekeeperAssignedFireRuntime.RestoreInfiniteFuel(__instance, __state.Original, __state.Changed);
        }
    }

    [HarmonyPatch(typeof(Character), "OnDeath")]
    internal static class FirekeeperDeathFireReleasePatch
    {
        private static void Prefix(Character __instance)
        {
            try
            {
                if (__instance == null || __instance is Player) return;
                ZNetView view = __instance.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (!WorkerZdo.IsWorker(zdo)) return;
                if ((WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None) != WorkerRole.Firekeeper) return;
                FirekeeperAssignedFireRuntime.ReleaseWorkerFire(zdo);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("Firekeeper fire-release on death failed: " + ex.GetBaseException().Message);
            }
        }
    }
}
