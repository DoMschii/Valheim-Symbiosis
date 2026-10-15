using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    internal static class WorkerHomeAssignmentRuntime
    {
        private static readonly List<Character> LoadedWorkerScratch = new List<Character>();

        internal static void Link(ZDO workerZdo, ZDO homeZdo, Vector3 homePosition, string anchorKind, long ownerPlayerId, string species, WorkerRole role)
        {
            if (workerZdo == null || homeZdo == null) return;

            // v0.5.133: HomeAnchors are a strict 1:1 relationship. Reassigning a Worker must
            // release its previous station first; assigning an occupied station must also detach
            // the previous resident. This is centralized here so every species/home type follows
            // the same rule, including older interaction paths and background recovery code.
            ZDO previousHome = ResolveHome(workerZdo);
            if (previousHome != null && !previousHome.m_uid.Equals(homeZdo.m_uid))
            {
                ZDOID previousAssigned = WorkerHomeAnchorZdo.ReadAssignedWorkerZdoId(previousHome);
                if (previousAssigned.Equals(ZDOID.None) || previousAssigned.Equals(workerZdo.m_uid))
                    WorkerHomeAnchorZdo.ClearAssigned(previousHome);
            }

            if (WorkerHomeAnchorZdo.IsAssigned(homeZdo))
            {
                ZDOID previousResidentId = WorkerHomeAnchorZdo.ReadAssignedWorkerZdoId(homeZdo);
                if (!previousResidentId.Equals(ZDOID.None) && !previousResidentId.Equals(workerZdo.m_uid) && ZDOMan.instance != null)
                {
                    ZDO previousResident = ZDOMan.instance.GetZDO(previousResidentId);
                    if (WorkerZdo.IsWorker(previousResident) && WorkerZdo.ReadHomeAnchorZdoId(previousResident).Equals(homeZdo.m_uid))
                        WorkerZdo.ClearHomeAssignment(previousResident);
                }
                WorkerHomeAnchorZdo.ClearAssigned(homeZdo);
            }

            WorkerZdo.AssignHome(workerZdo, homePosition, anchorKind, homeZdo.m_uid);
            WorkerHomeAnchorZdo.MarkAssigned(homeZdo, anchorKind, ownerPlayerId, species, role, workerZdo.m_uid);

            // v0.5.449: HunterStand geometry/home sync is now event-driven. The stand no longer
            // runs a permanent Update loop just to keep an already-static home point aligned.
            if (string.Equals(anchorKind, WorkerHomeProfileCatalog.HunterStandKind, StringComparison.Ordinal))
                HunterStandMarker.NotifyHomeLinked(homeZdo.m_uid);

            // v0.5.439: finish the home transaction only after BOTH sides of the durable
            // Worker <-> Home link exist. AssignHome() already announces a topology change,
            // but at that instant the Home ZDO has not been marked assigned yet. Initialising
            // sustenance and refreshing the targeted demand again here makes a fresh assignment
            // immediately usable even when the player had no starter food in the inventory.
            // This stays event-driven: one known Worker/Home pair is touched, no world scan.
            WorkerSustenanceRuntime.EnsureInitialized(workerZdo);
            WorkerProvisioningLogisticsRuntime.NotifyWorkerSupplyChanged(workerZdo, false);

            BackgroundWorkerScheduler.RegisterPersistentWorkerFromHome(homeZdo);
        }

        internal static bool ReleaseFromHome(ZDO homeZdo, Vector3 homePosition, out bool workerUpdated)
        {
            workerUpdated = false;
            if (homeZdo == null) return false;

            ZDO workerZdo = ResolveWorker(homeZdo, homePosition);
            if (workerZdo != null && WorkerZdo.IsWorker(workerZdo))
            {
                WorkerZdo.ClearHomeAssignment(workerZdo);
                workerUpdated = true;
            }

            WorkerHomeAnchorZdo.ClearAssigned(homeZdo);
            return true;
        }

        internal static bool ReleaseForWorker(ZDO workerZdo)
        {
            if (workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return false;

            ZDO homeZdo = ResolveHome(workerZdo);
            if (homeZdo != null)
            {
                ZDOID assigned = WorkerHomeAnchorZdo.ReadAssignedWorkerZdoId(homeZdo);
                if (assigned.Equals(ZDOID.None) || assigned.Equals(workerZdo.m_uid))
                    WorkerHomeAnchorZdo.ClearAssigned(homeZdo);
            }

            WorkerZdo.ClearHomeAssignment(workerZdo);
            return true;
        }

        internal static bool ReleaseForWorkerDeath(ZDO workerZdo)
        {
            if (workerZdo == null || !WorkerZdo.IsWorker(workerZdo)) return false;
            ZDO homeZdo = ResolveHome(workerZdo);
            if (homeZdo == null) return false;

            ZDOID assigned = WorkerHomeAnchorZdo.ReadAssignedWorkerZdoId(homeZdo);
            if (!assigned.Equals(ZDOID.None) && !assigned.Equals(workerZdo.m_uid))
                return false;

            WorkerHomeAnchorZdo.ClearAssigned(homeZdo);
            return true;
        }


        internal static Character ResolveLoadedWorkerForHome(ZDO homeZdo, Vector3 homePosition)
        {
            if (homeZdo == null) return null;

            long homeOwner = homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L);
            string expectedSpecies = SpeciesRoleRegistry.NormalizePrefabName(WorkerHomeAnchorZdo.ReadWorkerSpecies(homeZdo));
            WorkerRole expectedRole = WorkerHomeAnchorZdo.ReadWorkerRole(homeZdo);
            string expectedKind = WorkerHomeAnchorZdo.ReadAnchorKind(homeZdo);

            Character best = null;
            ZDO bestZdo = null;
            float bestScore = float.MaxValue;

            WorkerFastLookupRuntime.CopyKnownWorkers(LoadedWorkerScratch);
            for (int i = 0; i < LoadedWorkerScratch.Count; i++)
            {
                Character character = LoadedWorkerScratch[i];
                if (character == null || character.IsDead()) continue;
                if (!WorkerFastLookupRuntime.TryGetWorker(character, false, out _, out ZDO candidate)) continue;

                long workerOwner = candidate.GetLong(WorkerZdo.OwnerPlayerId, 0L);
                if (homeOwner != 0L && workerOwner != homeOwner) continue;

                string workerSpecies = SpeciesRoleRegistry.NormalizePrefabName(candidate.GetString(WorkerZdo.SpeciesKey, string.Empty));
                if (string.IsNullOrEmpty(workerSpecies))
                    workerSpecies = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);
                if (!string.IsNullOrEmpty(expectedSpecies) &&
                    !string.Equals(expectedSpecies, workerSpecies, StringComparison.OrdinalIgnoreCase))
                    continue;

                WorkerRole workerRole = (WorkerRole)candidate.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
                if (expectedRole != WorkerRole.None && workerRole != WorkerRole.None && expectedRole != workerRole)
                    continue;

                string workerKind = candidate.GetString(WorkerZdo.HomeAnchorType, string.Empty);
                if (!string.IsNullOrEmpty(expectedKind) && !string.IsNullOrEmpty(workerKind) &&
                    !string.Equals(expectedKind, workerKind, StringComparison.Ordinal))
                    continue;

                Vector3 persistedHome = WorkerZdo.ReadHome(candidate, character.transform.position);
                float homeDistance = Vector3.Distance(persistedHome, homePosition);
                float physicalDistance = Vector3.Distance(character.transform.position, homePosition);

                // Explicit Shift+E on a rest station is a strong recovery signal. Older saves can
                // contain a stale reverse HomeAnchor ZDOID, so a visibly resting/nearby Worker must
                // be allowed to repair that link. Requiring both distances to be large prevents a
                // neighbouring station from stealing an unrelated Worker.
                if (homeDistance > 15f && physicalDistance > 15f) continue;

                ZDOID workerHomeId = WorkerZdo.ReadHomeAnchorZdoId(candidate);
                float score = Mathf.Min(homeDistance, physicalDistance * 1.25f);
                if (workerHomeId.Equals(homeZdo.m_uid)) score -= 1000f;
                else if (!workerHomeId.Equals(ZDOID.None)) score += 2f;
                if (string.IsNullOrEmpty(workerSpecies)) score += 2f;
                if (string.IsNullOrEmpty(workerKind)) score += 1f;

                if (score >= bestScore) continue;
                bestScore = score;
                best = character;
                bestZdo = candidate;
            }

            if (best != null && bestZdo != null)
            {
                RepairBidirectionalLink(homeZdo, bestZdo);
                Plugin.Log?.LogInfo($"Worker home loaded recovery repaired link. Home={homeZdo.m_uid}, Worker={bestZdo.m_uid}, Species={SpeciesRoleRegistry.NormalizePrefabName(best.gameObject.name)}, score={bestScore:0.00}.");
            }

            return best;
        }

        internal static ZDO ResolveWorker(ZDO homeZdo, Vector3 homePosition)
        {
            if (homeZdo == null) return null;

            ZDOID storedWorkerId = WorkerHomeAnchorZdo.ReadAssignedWorkerZdoId(homeZdo);
            if (!storedWorkerId.Equals(ZDOID.None) && ZDOMan.instance != null)
            {
                ZDO direct = ZDOMan.instance.GetZDO(storedWorkerId);
                if (WorkerZdo.IsWorker(direct))
                {
                    RepairBidirectionalLink(homeZdo, direct);
                    return direct;
                }
            }

            // Universal HomeAnchor recovery for every rest-station family. Older saves can keep a
            // stale Home -> Worker ZDOID even though the Worker still exists. Do not make each
            // rest piece solve this independently: score loaded Workers first, then (only on an
            // explicit menu/release request) fall back to the persistent ZDO set. Species + role +
            // owner prevent adjacent rest stations from stealing each other's Worker, while the
            // persisted home position handles old Workers whose reverse ZDOID was never written.
            ZDO bestLoaded = null;
            float bestLoadedScore = float.MaxValue;
            WorkerFastLookupRuntime.CopyKnownWorkers(LoadedWorkerScratch);
            for (int i = 0; i < LoadedWorkerScratch.Count; i++)
            {
                Character character = LoadedWorkerScratch[i];
                if (character == null || character.IsDead()) continue;
                if (!WorkerFastLookupRuntime.TryGetWorker(character, false, out _, out ZDO candidate)) continue;
                if (!TryScoreWorkerForHome(homeZdo, homePosition, candidate, character.gameObject.name, out float score)) continue;
                if (score >= bestLoadedScore) continue;
                bestLoadedScore = score;
                bestLoaded = candidate;
            }

            if (bestLoaded != null)
            {
                RepairBidirectionalLink(homeZdo, bestLoaded);
                return bestLoaded;
            }

            ZDO persistent = FindPersistentWorkerForHome(homeZdo, homePosition);
            if (persistent != null)
            {
                RepairBidirectionalLink(homeZdo, persistent);
                return persistent;
            }

            return null;
        }

        private static bool TryScoreWorkerForHome(ZDO homeZdo, Vector3 homePosition, ZDO workerZdo, string speciesHint, out float score)
        {
            score = float.MaxValue;
            if (homeZdo == null || !WorkerZdo.IsWorker(workerZdo)) return false;

            long homeOwner = homeZdo.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L);
            long workerOwner = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            if (homeOwner != 0L && workerOwner != homeOwner) return false;

            string expectedSpecies = SpeciesRoleRegistry.NormalizePrefabName(WorkerHomeAnchorZdo.ReadWorkerSpecies(homeZdo));
            string workerSpecies = SpeciesRoleRegistry.NormalizePrefabName(workerZdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
            if (string.IsNullOrEmpty(workerSpecies))
                workerSpecies = SpeciesRoleRegistry.NormalizePrefabName(speciesHint ?? string.Empty);
            if (!string.IsNullOrEmpty(expectedSpecies) && !string.IsNullOrEmpty(workerSpecies) &&
                !string.Equals(expectedSpecies, workerSpecies, StringComparison.OrdinalIgnoreCase))
                return false;

            WorkerRole expectedRole = WorkerHomeAnchorZdo.ReadWorkerRole(homeZdo);
            WorkerRole workerRole = (WorkerRole)workerZdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            if (expectedRole != WorkerRole.None && workerRole != WorkerRole.None && expectedRole != workerRole)
                return false;

            ZDOID workerHomeId = WorkerZdo.ReadHomeAnchorZdoId(workerZdo);
            if (!workerHomeId.Equals(ZDOID.None) && workerHomeId.Equals(homeZdo.m_uid))
            {
                score = 0f;
                return true;
            }

            // Older saves can carry a stale reverse Worker -> Home ZDOID. Do not reject the
            // candidate merely because that old Home still exists: compare both persisted Home
            // positions below and only keep the other assignment when it is clearly the better
            // geometric match.
            ZDO otherAssignedHome = null;
            if (!workerHomeId.Equals(ZDOID.None) && ZDOMan.instance != null)
            {
                ZDO otherHome = ZDOMan.instance.GetZDO(workerHomeId);
                if (otherHome != null && !otherHome.m_uid.Equals(homeZdo.m_uid) && WorkerHomeAnchorZdo.IsAssigned(otherHome))
                    otherAssignedHome = otherHome;
            }

            string homeKind = WorkerHomeAnchorZdo.ReadAnchorKind(homeZdo);
            string workerKind = workerZdo.GetString(WorkerZdo.HomeAnchorType, string.Empty);
            if (!string.IsNullOrEmpty(homeKind) && !string.IsNullOrEmpty(workerKind) &&
                !string.Equals(homeKind, workerKind, StringComparison.Ordinal))
                return false;

            Vector3 workerHome = WorkerZdo.ReadHome(workerZdo, workerZdo.GetPosition());
            float distance = Vector3.Distance(workerHome, homePosition);

            // Custom visual pieces do not all place their network/root transform exactly at the
            // rest point. The old 1.5 m test therefore failed for Wurzelnest/Troll/Golem/etc.
            // Twelve metres is still local to the station, and species/role/owner/kind matching
            // above keeps neighbouring stations deterministic.
            if (distance > 12f) return false;

            float penalty = 0f;
            if (otherAssignedHome != null)
            {
                float otherDistance = Vector3.Distance(workerHome, otherAssignedHome.GetPosition());
                if (otherDistance + 1.5f < distance) return false;
                penalty += 3f;
            }
            if (string.IsNullOrEmpty(workerSpecies)) penalty += 2f;
            if (string.IsNullOrEmpty(workerKind)) penalty += 2f;
            if (!workerHomeId.Equals(ZDOID.None)) penalty += 1f;
            score = distance + penalty;
            return true;
        }

        private static ZDO FindPersistentWorkerForHome(ZDO homeZdo, Vector3 homePosition)
        {
            ZDOMan man = ZDOMan.instance;
            if (man == null) return null;

            ZDO best = null;
            float bestScore = float.MaxValue;
            HashSet<ZDOID> visited = new HashSet<ZDOID>();

            try
            {
                FieldInfo[] fields = typeof(ZDOMan).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                {
                    object value;
                    try { value = fields[i].GetValue(man); }
                    catch { continue; }
                    HarvestPersistentWorkerCandidates(value, 0, homeZdo, homePosition, visited, ref best, ref bestScore);
                    if (bestScore <= 0.001f) break;
                }
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Worker home persistent recovery scan failed: " + error.GetBaseException().Message);
            }

            return best;
        }

        private static void HarvestPersistentWorkerCandidates(object value, int depth, ZDO homeZdo, Vector3 homePosition, HashSet<ZDOID> visited, ref ZDO best, ref float bestScore)
        {
            if (value == null || depth > 2 || bestScore <= 0.001f) return;

            if (value is ZDO single)
            {
                ConsiderPersistentCandidate(single, homeZdo, homePosition, visited, ref best, ref bestScore);
                return;
            }

            if (value is IDictionary dictionary)
            {
                int guard = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (++guard > 250000) break;
                    if (entry.Value is ZDO zdo)
                        ConsiderPersistentCandidate(zdo, homeZdo, homePosition, visited, ref best, ref bestScore);
                    else if (depth < 2)
                        HarvestPersistentWorkerCandidates(entry.Value, depth + 1, homeZdo, homePosition, visited, ref best, ref bestScore);
                    if (bestScore <= 0.001f) break;
                }
                return;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                int guard = 0;
                foreach (object item in enumerable)
                {
                    if (++guard > 250000) break;
                    if (item is ZDO zdo)
                        ConsiderPersistentCandidate(zdo, homeZdo, homePosition, visited, ref best, ref bestScore);
                    else if (depth < 2)
                        HarvestPersistentWorkerCandidates(item, depth + 1, homeZdo, homePosition, visited, ref best, ref bestScore);
                    if (bestScore <= 0.001f) break;
                }
            }
        }

        private static void ConsiderPersistentCandidate(ZDO candidate, ZDO homeZdo, Vector3 homePosition, HashSet<ZDOID> visited, ref ZDO best, ref float bestScore)
        {
            if (candidate == null || !visited.Add(candidate.m_uid)) return;
            if (!TryScoreWorkerForHome(homeZdo, homePosition, candidate, null, out float score)) return;
            if (score >= bestScore) return;
            bestScore = score;
            best = candidate;
        }

        private static void RepairBidirectionalLink(ZDO homeZdo, ZDO workerZdo)
        {
            if (homeZdo == null || !WorkerZdo.IsWorker(workerZdo)) return;
            homeZdo.Set(WorkerHomeAnchorZdo.AssignedWorkerZdoId, workerZdo.m_uid);
            workerZdo.Set(WorkerZdo.HomeAnchorZdoId, homeZdo.m_uid);

            string homeKind = WorkerHomeAnchorZdo.ReadAnchorKind(homeZdo);
            if (!string.IsNullOrEmpty(homeKind) && string.IsNullOrEmpty(workerZdo.GetString(WorkerZdo.HomeAnchorType, string.Empty)))
                workerZdo.Set(WorkerZdo.HomeAnchorType, homeKind);
        }

        private static ZDO ResolveHome(ZDO workerZdo)
        {
            ZDOID homeId = WorkerZdo.ReadHomeAnchorZdoId(workerZdo);
            if (!homeId.Equals(ZDOID.None) && ZDOMan.instance != null)
            {
                ZDO direct = ZDOMan.instance.GetZDO(homeId);
                if (direct != null) return direct;
            }

            // Backwards fallback: when the old assignment has no stored ZDOID, inspect loaded network
            // objects around the exact persisted home position and select the matching HomeAnchor ZDO.
            Vector3 home = WorkerZdo.ReadHome(workerZdo, workerZdo.GetPosition());
            string kind = workerZdo.GetString(WorkerZdo.HomeAnchorType, string.Empty);
            long owner = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            Collider[] hits = Physics.OverlapSphere(home, 1.5f);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider hit = hits[i];
                if (hit == null) continue;
                ZNetView view = hit.GetComponentInParent<ZNetView>();
                ZDO candidate = view != null && view.IsValid() ? view.GetZDO() : null;
                if (candidate == null || !WorkerHomeAnchorZdo.IsAssigned(candidate)) continue;
                if (!string.Equals(WorkerHomeAnchorZdo.ReadAnchorKind(candidate), kind, StringComparison.Ordinal)) continue;
                if (owner != 0L && candidate.GetLong(WorkerHomeAnchorZdo.OwnerPlayerId, 0L) != owner) continue;
                candidate.Set(WorkerHomeAnchorZdo.AssignedWorkerZdoId, workerZdo.m_uid);
                workerZdo.Set(WorkerZdo.HomeAnchorZdoId, candidate.m_uid);
                return candidate;
            }
            return null;
        }
    }

    [HarmonyPatch(typeof(Character), "OnDeath")]
    internal static class WorkerDeathHomeReleasePatch
    {
        private static void Prefix(Character __instance)
        {
            try
            {
                if (__instance == null || __instance is Player) return;
                ZNetView view = __instance.GetComponent<ZNetView>();
                if (view == null || !view.IsValid() || !view.IsOwner()) return;
                ZDO workerZdo = view.GetZDO();
                if (!WorkerZdo.IsWorker(workerZdo)) return;

                bool released = WorkerHomeAssignmentRuntime.ReleaseForWorkerDeath(workerZdo);
                Plugin.Log?.LogInfo($"Worker death cleanup. Worker={SpeciesRoleRegistry.NormalizePrefabName(__instance.gameObject.name)}, homeReleased={released}.");
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Worker death HomeAnchor cleanup failed: " + error.GetBaseException().Message);
            }
        }
    }
}
