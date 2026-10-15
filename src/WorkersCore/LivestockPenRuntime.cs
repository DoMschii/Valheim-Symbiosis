using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Pen/enclosure support for the Butcher/Livestock Keeper.
    /// The SmartStorage feeding trough is the primary pen marker; its ZDO stores a configurable radius. Legacy Tierpflege signs remain readable.
    /// Escaped tamed livestock are escorted back physically via vanilla MonsterAI follow behaviour.
    /// A local-owner rescue teleport is only used after a prolonged escort stall.
    /// </summary>
    internal static class LivestockPenRuntime
    {
        internal const string AnimalTargetKind = "LivestockPenAnimal";
        internal const string HomeTargetKind = "LivestockPenHome";
        internal const string PenRadiusZdoKey = "vw_livestock_pen_radius";
        internal const float DefaultPenRadius = 12f;
        internal const float MinPenRadius = 4f;
        internal const float MaxPenRadius = 40f;

        private const float EscapeMargin = 3f;
        private const float CatchDistance = 3.2f;
        private const float HomeArrivalDistance = 2.0f;
        private const float EscortTimeout = 18f;
        private const float ScanInterval = 2.5f;

        private sealed class EscortJob
        {
            internal Character Animal;
            internal LivestockFeedSignMarker Sign;
            internal float PenRadius;
            internal bool Following;
            internal float StartedAt;
            internal float LastProgressAt;
            internal float BestAnimalDistance;
        }

        private static readonly Dictionary<int, EscortJob> Jobs = new Dictionary<int, EscortJob>();
        private static readonly Dictionary<int, float> NextScanAt = new Dictionary<int, float>();
        private static readonly MethodInfo ClaimOwnershipMethod = typeof(ZNetView).GetMethod(
            "ClaimOwnership", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

        internal static bool IsTargetKind(string kind) =>
            string.Equals(kind, AnimalTargetKind, StringComparison.Ordinal) ||
            string.Equals(kind, HomeTargetKind, StringComparison.Ordinal);

        internal static float GetArrivalRadius(string kind)
        {
            return string.Equals(kind, AnimalTargetKind, StringComparison.Ordinal) ? CatchDistance : HomeArrivalDistance;
        }

        internal static float GetPenRadius(LivestockFeedSignMarker sign)
        {
            ZDO zdo = GetSignZdo(sign);
            if (zdo == null) return DefaultPenRadius;
            return Mathf.Clamp(zdo.GetFloat(PenRadiusZdoKey, DefaultPenRadius), MinPenRadius, MaxPenRadius);
        }

        internal static bool SetPenRadius(LivestockFeedSignMarker sign, float radius, out string error)
        {
            error = string.Empty;
            ZNetView view = sign != null ? sign.GetNetworkView() : null;
            if (view == null || !view.IsValid()) { error = "Futtertrog ist nicht netzwerkbereit."; return false; }
            if (!view.IsOwner()) { error = "Gehege-Radius kann nur vom Netzwerkbesitzer geaendert werden."; return false; }
            ZDO zdo = view.GetZDO();
            if (zdo == null) { error = "Futtertrog hat kein ZDO."; return false; }
            zdo.Set(PenRadiusZdoKey, Mathf.Clamp(radius, MinPenRadius, MaxPenRadius));
            return true;
        }

        internal static bool TryAssignEscapedAnimal(Character worker, ZDO workerZdo, Vector3 workCenter, float workRadius, out Character animal, out string reason)
        {
            animal = null;
            reason = "Gehege: alle Tiere innerhalb";
            if (worker == null || workerZdo == null) return false;

            int workerKey = worker.GetInstanceID();
            if (Jobs.TryGetValue(workerKey, out EscortJob existing) && IsJobValid(existing, workCenter, workRadius))
            {
                animal = existing.Animal;
                reason = existing.Following ? "Tier wird ins Gehege zurueckgebracht" : "Ausgebuextes Tier einfangen";
                return animal != null;
            }
            ClearJob(workerKey, true);

            float now = Time.realtimeSinceStartup;
            if (NextScanAt.TryGetValue(workerKey, out float next) && next > now) return false;
            NextScanAt[workerKey] = now + ScanInterval;

            if (!LivestockFeedSignRegistry.TryFindNearest(workCenter, workRadius, out LivestockFeedSignMarker sign) || sign == null)
            {
                reason = "Gehege: kein Futtertrog in der Workzone";
                return false;
            }

            float penRadius = GetPenRadius(sign);
            float escapedSqr = (penRadius + EscapeMargin) * (penRadius + EscapeMargin);
            float rescueRadius = Mathf.Max(workRadius, penRadius + 18f);
            float rescueSqr = rescueRadius * rescueRadius;
            Vector3 penCenter = sign.transform.position;

            Character best = null;
            float bestWorkerSqr = float.MaxValue;
            List<Character> characters = WorkerCharacterIndexRuntime.GetTamedNonWorkers();
            if (characters.Count == 0) return false;

            for (int i = 0; i < characters.Count; i++)
            {
                Character candidate = characters[i];
                if (!IsTamedLivestock(candidate)) continue;

                Vector3 fromPen = candidate.transform.position - penCenter;
                fromPen.y = 0f;
                float penSqr = fromPen.sqrMagnitude;
                if (penSqr <= escapedSqr || penSqr > rescueSqr) continue;

                // Do not steal livestock from another nearby enclosure: the candidate must be
                // geometrically closest to this caretaker's pen marker among loaded Tierpflege signs.
                if (!IsOwnedBySign(candidate.transform.position, sign, Mathf.Sqrt(penSqr))) continue;

                Vector3 fromWorker = candidate.transform.position - worker.transform.position;
                fromWorker.y = 0f;
                float workerSqr = fromWorker.sqrMagnitude;
                if (workerSqr < bestWorkerSqr)
                {
                    best = candidate;
                    bestWorkerSqr = workerSqr;
                }
            }

            if (best == null) return false;

            Jobs[workerKey] = new EscortJob
            {
                Animal = best,
                Sign = sign,
                PenRadius = penRadius,
                Following = false,
                StartedAt = Time.realtimeSinceStartup,
                LastProgressAt = Time.realtimeSinceStartup,
                BestAnimalDistance = HorizontalDistance(best.transform.position, penCenter)
            };
            animal = best;
            reason = $"Ausgebuext: {SpeciesRoleRegistry.NormalizePrefabName(best.gameObject.name)} -> Gehege";
            return true;
        }

        internal static bool TryWork(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null) return false;
            if (!WorkerZdo.TryReadTarget(workerZdo, out _, out string kind, out _ ) || !IsTargetKind(kind)) return false;

            int workerKey = worker.GetInstanceID();
            if (!Jobs.TryGetValue(workerKey, out EscortJob job) || job == null || job.Animal == null || job.Sign == null)
            {
                Finish(workerKey, workerZdo, false);
                return true;
            }

            Vector3 penCenter = job.Sign.transform.position;
            float animalPenDistance = HorizontalDistance(job.Animal.transform.position, penCenter);
            if (animalPenDistance <= Mathf.Max(MinPenRadius, job.PenRadius - 0.8f))
            {
                ClearAnimalFollow(job.Animal);
                Finish(workerKey, workerZdo, true);
                return true;
            }

            if (!job.Following)
            {
                float workerAnimalDistance = HorizontalDistance(worker.transform.position, job.Animal.transform.position);
                if (workerAnimalDistance > CatchDistance + 0.4f)
                {
                    WorkerZdo.WriteTarget(workerZdo, SpeciesRoleRegistry.NormalizePrefabName(job.Animal.gameObject.name), job.Animal.transform.position, AnimalTargetKind);
                    workerZdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
                    return true;
                }

                MonsterAI animalAi = null;
                try { animalAi = job.Animal.GetComponent<MonsterAI>() ?? job.Animal.GetComponentInChildren<MonsterAI>(true); } catch { }
                if (animalAi != null)
                {
                    try { animalAi.SetFollowTarget(worker.gameObject); } catch { }
                }
                job.Following = true;
                job.StartedAt = Time.realtimeSinceStartup;
                job.LastProgressAt = job.StartedAt;
                job.BestAnimalDistance = animalPenDistance;
                WorkerZdo.WriteTarget(workerZdo, "Gehege", penCenter, HomeTargetKind);
                workerZdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
                return true;
            }

            float now = Time.realtimeSinceStartup;
            if (animalPenDistance + 0.4f < job.BestAnimalDistance)
            {
                job.BestAnimalDistance = animalPenDistance;
                job.LastProgressAt = now;
            }

            // Worker already reached the marker; give the following animal time to enter the pen.
            if (now - job.LastProgressAt < EscortTimeout)
                return true;

            // Safety fallback for doors, boats and pathing traps. Only the local network owner may
            // reposition the animal, so multiplayer never fights over transforms.
            if (TryOwnerRescue(job.Animal, penCenter, job.PenRadius))
            {
                ClearAnimalFollow(job.Animal);
                Finish(workerKey, workerZdo, true);
                return true;
            }

            // No authority: release follow and retry later rather than looping forever.
            ClearAnimalFollow(job.Animal);
            Finish(workerKey, workerZdo, false);
            return true;
        }

        internal static void ClearCaches()
        {
            foreach (EscortJob job in Jobs.Values)
                if (job != null && job.Animal != null) ClearAnimalFollow(job.Animal);
            Jobs.Clear();
            NextScanAt.Clear();
        }

        internal static string GetDebug(ZDO workerZdo)
        {
            if (workerZdo == null || !WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 center, out float radius))
                return "Gehege: keine Workzone";
            if (!LivestockFeedSignRegistry.TryFindNearest(center, radius, out LivestockFeedSignMarker sign) || sign == null)
                return "Gehege: kein Futtertrog";
            return $"Gehege-Radius: {GetPenRadius(sign):0} m | Rueckhol-Puffer: {EscapeMargin:0} m";
        }

        private static bool IsJobValid(EscortJob job, Vector3 workCenter, float workRadius)
        {
            if (job == null || job.Animal == null || job.Sign == null || job.Animal.IsDead()) return false;
            Vector3 signDelta = job.Sign.transform.position - workCenter;
            signDelta.y = 0f;
            return signDelta.sqrMagnitude <= Mathf.Max(5f, workRadius) * Mathf.Max(5f, workRadius);
        }

        private static bool IsTamedLivestock(Character c)
        {
            if (c == null || c is Player || c.IsDead() || c.gameObject == null || !c.gameObject.activeInHierarchy) return false;
            ZNetView view = null;
            ZDO zdo = null;
            try { view = c.GetComponent<ZNetView>(); zdo = view != null && view.IsValid() ? view.GetZDO() : null; } catch { }
            if (WorkerZdo.IsWorker(zdo)) return false;

            bool livestockShape = false;
            try { livestockShape = c.GetComponent<Tameable>() != null || c.GetComponentInChildren<Tameable>(true) != null || c.GetComponent<Growup>() != null || c.GetComponentInChildren<Growup>(true) != null; } catch { }
            if (!livestockShape) return false;
            try { if (c.IsTamed()) return true; } catch { }
            try { return zdo != null && zdo.GetBool("tamed", false); } catch { return false; }
        }

        private static bool IsOwnedBySign(Vector3 animalPosition, LivestockFeedSignMarker sign, float currentDistance)
        {
            // Registry does not expose its collection by design. Probe nearest sign around the animal;
            // if another sign is closer than this one, leave the animal to that enclosure.
            if (!LivestockFeedSignRegistry.TryFindNearest(animalPosition, Mathf.Max(5f, currentDistance + 0.5f), out LivestockFeedSignMarker nearest))
                return true;
            return nearest == sign;
        }

        private static void Finish(int workerKey, ZDO workerZdo, bool success)
        {
            if (Jobs.TryGetValue(workerKey, out EscortJob job) && job != null && job.Animal != null)
                ClearAnimalFollow(job.Animal);
            Jobs.Remove(workerKey);
            WorkerZdo.ClearTarget(workerZdo);
            workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            NextScanAt[workerKey] = Time.realtimeSinceStartup + (success ? 2f : 5f);
        }

        private static void ClearJob(int workerKey, bool clearFollow)
        {
            if (Jobs.TryGetValue(workerKey, out EscortJob job) && clearFollow && job != null && job.Animal != null)
                ClearAnimalFollow(job.Animal);
            Jobs.Remove(workerKey);
        }

        private static void ClearAnimalFollow(Character animal)
        {
            if (animal == null) return;
            try
            {
                MonsterAI ai = animal.GetComponent<MonsterAI>() ?? animal.GetComponentInChildren<MonsterAI>(true);
                if (ai != null) ai.SetFollowTarget(null);
            }
            catch { }
        }

        private static bool TryOwnerRescue(Character animal, Vector3 penCenter, float penRadius)
        {
            if (animal == null) return false;
            ZNetView view = null;
            try { view = animal.GetComponent<ZNetView>(); } catch { }
            if (view == null || !view.IsValid()) return false;
            try
            {
                if (!view.IsOwner()) ClaimOwnershipMethod?.Invoke(view, null);
                if (!view.IsOwner()) return false;
            }
            catch { return false; }

            Vector3 target = penCenter + Vector3.up * 0.15f;
            // Slight deterministic offset avoids stacking multiple rescued animals exactly together.
            int hash = Mathf.Abs(animal.GetInstanceID());
            float angle = (hash % 360) * Mathf.Deg2Rad;
            float offset = Mathf.Min(2.0f, Mathf.Max(0.8f, penRadius * 0.2f));
            target += new Vector3(Mathf.Cos(angle) * offset, 0f, Mathf.Sin(angle) * offset);
            try
            {
                animal.transform.position = target;
                Rigidbody body = animal.GetComponent<Rigidbody>();
                if (body != null) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; body.position = target; }
                return true;
            }
            catch { return false; }
        }

        private static ZDO GetSignZdo(LivestockFeedSignMarker sign)
        {
            ZNetView view = sign != null ? sign.GetNetworkView() : null;
            try { return view != null && view.IsValid() ? view.GetZDO() : null; } catch { return null; }
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            Vector3 delta = a - b;
            delta.y = 0f;
            return delta.magnitude;
        }
    }
}
