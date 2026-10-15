using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Loaded-scene base repair job for the existing Fuling Smith.
    ///
    /// v0.5.120 deliberately uses Valheim's own loaded Piece registry instead of a broad Physics
    /// overlap. Dense bases can contain thousands of terrain/trigger/prop colliders and the old
    /// fixed collider buffer could therefore fill before a damaged building piece was ever seen.
    ///
    /// Design rules:
    /// - Never scans every frame. A per-worker cooldown gates Piece.GetAllPiecesInRadius.
    /// - Only real loaded Piece + WearNTear objects are considered.
    /// - Walking remains presentation: the stable Worker navigator moves to a concrete target point.
    /// - The actual repair happens only after a visible work swing at the structure.
    /// - Repair uses WearNTear.Repair() directly, i.e. Valheim's native network-aware repair RPC.
    /// - No resources are created/consumed; this mirrors Valheim's normal no-material repair action.
    /// </summary>
    internal static class BaseRepairWorkRuntime
    {
        internal const string TargetKind = "BaseRepair";

        private const float ScanCooldownSeconds = 3.5f;
        private const float RepairApplyDelay = 0.42f;
        private const float RepairSwingInterval = 1.45f;
        private const float HealthyThreshold = 0.999f;
        private const float ResolveRadius = 8f;
        private const float FallbackMaxRepairDistance = 18f;

        // Valheim is single-threaded here. Reusing these lists avoids a new collection on every scan.
        private static readonly List<Piece> PieceBuffer = new List<Piece>(512);
        private static readonly List<Piece> ResolvePieceBuffer = new List<Piece>(64);
        private static readonly List<WearNTear> WearBuffer = new List<WearNTear>(512);
        private static readonly HashSet<int> Seen = new HashSet<int>();
        private static readonly Dictionary<int, float> NextScanAt = new Dictionary<int, float>();
        private static readonly Dictionary<int, RepairCycle> Cycles = new Dictionary<int, RepairCycle>();
        // Keep the exact loaded WearNTear chosen by FindJob bound to this Worker.
        // Resolving a long beam/roof piece again from its saved collider-surface point can fail
        // because the prefab transform/origin is several metres away from that surface.
        private static readonly Dictionary<int, WearNTear> BoundTargets = new Dictionary<int, WearNTear>();

        private sealed class RepairCycle
        {
            internal int TargetId;
            internal float ApplyAt;
            internal float NextSwingAt;
            internal bool Pending;
        }

        private sealed class BackgroundRepairCandidate
        {
            internal WearNTear Target;
            internal float Health;
            internal float Distance;
        }

        private static readonly List<BackgroundRepairCandidate> BackgroundCandidates = new List<BackgroundRepairCandidate>(256);

        internal static bool TryFindTarget(
            Character worker,
            Vector3 workCenter,
            float workRadius,
            out WearNTear target,
            out Vector3 targetPoint,
            out string targetName,
            out string status)
        {
            target = null;
            targetPoint = workCenter;
            targetName = string.Empty;
            status = "keine beschaedigte Struktur gefunden";
            if (worker == null) return false;

            int workerKey = worker.GetInstanceID();
            float now = Time.time;
            if (NextScanAt.TryGetValue(workerKey, out float next) && now < next)
            {
                status = "Reparatur-Scan wartet";
                return false;
            }
            NextScanAt[workerKey] = now + ScanCooldownSeconds;

            float safeRadius = Mathf.Clamp(workRadius, 4f, 200f);
            PieceBuffer.Clear();
            try
            {
                Piece.GetAllPiecesInRadius(workCenter, safeRadius, PieceBuffer);
            }
            catch (Exception ex)
            {
                status = "Reparatur-Scan fehlgeschlagen: " + ex.GetType().Name;
                return false;
            }

            Seen.Clear();
            float bestScore = float.MaxValue;
            float bestHealth = 1f;
            int damaged = 0;

            for (int i = 0; i < PieceBuffer.Count; i++)
            {
                Piece piece = PieceBuffer[i];
                if (piece == null || piece.gameObject == null || !piece.gameObject.activeInHierarchy) continue;
                WearNTear candidate = ResolveWearNTear(piece);
                ConsiderCandidate(candidate, worker.transform.position, workCenter, safeRadius,
                    ref target, ref targetPoint, ref bestScore, ref bestHealth, ref damaged);
            }

            // v0.5.191: Some current-Valheim building pieces are missing from Piece.GetAllPiecesInRadius
            // until the local Piece registry has been touched by vanilla interaction. The repairer must
            // still work immediately after loading a base, so when the native registry yields no damaged
            // target we perform one throttled loaded-scene WearNTear fallback pass. This is NOT a world
            // scan: only active, currently loaded WearNTear components are inspected, and only candidates
            // inside this worker's Workzone are accepted. The normal 3.5 s per-worker cooldown still gates it.
            int fallbackWearCount = 0;
            if (target == null)
            {
                fallbackWearCount = WorkerWearNTearIndexRuntime.CopyInRadius(workCenter, safeRadius, WearBuffer);
                for (int i = 0; i < WearBuffer.Count; i++)
                {
                    ConsiderCandidate(WearBuffer[i], worker.transform.position, workCenter, safeRadius,
                        ref target, ref targetPoint, ref bestScore, ref bestHealth, ref damaged);
                }
                WearBuffer.Clear();
            }

            if (target == null)
            {
                status = $"keine beschaedigte Struktur in der Workzone ({PieceBuffer.Count} Piece-Registry, {fallbackWearCount} geladene WearNTear geprueft)";
                return false;
            }

            Piece targetPiece = target.GetComponent<Piece>() ?? target.GetComponentInParent<Piece>();
            targetName = targetPiece != null
                ? SpeciesRoleRegistry.NormalizePrefabName(targetPiece.gameObject.name)
                : SpeciesRoleRegistry.NormalizePrefabName(target.gameObject.name);
            if (string.IsNullOrEmpty(targetName)) targetName = "Bauteil";

            // Bind the exact loaded component before the controller persists only name/point.
            // This prevents large/offset building pieces from disappearing between FindJob and Work.
            BoundTargets[workerKey] = target;
            status = $"{targetName} bei {bestHealth * 100f:0}% ({damaged} Reparaturziel(e), {PieceBuffer.Count} Pieces im Scan)";
            Plugin.Log?.LogInfo($"[BaseRepair] Ziel gebunden: {targetName}, id={target.GetInstanceID()}, HP={bestHealth * 100f:0.0}%, Distanz={WorkerInteractionRuntime.HorizontalDistance(worker.transform.position, targetPoint):0.0}m.");
            return true;
        }

        internal static bool TryTickRepair(Character worker, ZDO zdo, out string status)
        {
            status = "kein Reparaturziel";
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out string targetName, out string targetKind, out Vector3 targetPosition) ||
                !string.Equals(targetKind, TargetKind, StringComparison.Ordinal))
                return false;

            int workerKey = worker.GetInstanceID();
            WearNTear target = null;
            float health = 1f;
            Vector3 actualPoint = targetPosition;

            // Fast/authoritative loaded-scene path: use the exact component selected by FindJob.
            // The saved targetPosition is an interaction point on the nearest collider surface, not
            // necessarily near the prefab transform origin (long beams are the common failure case).
            if (BoundTargets.TryGetValue(workerKey, out WearNTear bound) && IsRepairable(bound, out health))
            {
                target = bound;
                actualPoint = GetInteractionPoint(bound, worker.transform.position);
            }
            else
            {
                BoundTargets.Remove(workerKey);
                if (!TryResolveTarget(targetPosition, targetName, worker.transform.position, out target, out health, out actualPoint))
                {
                    RepairHammerVisualRuntime.Hide(worker);
                    LocalWorkFallbackRuntime.Clear(zdo);
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    status = "Reparaturziel verschwunden oder bereits repariert";
                    Plugin.Log?.LogWarning($"[BaseRepair] Zielauflösung verloren: worker={workerKey}, name={targetName}, savedPoint={targetPosition}.");
                    return true;
                }
                BoundTargets[workerKey] = target;
            }

            float distance = WorkerInteractionRuntime.HorizontalDistance(worker.transform.position, actualPoint);
            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo, targetName, TargetKind);
            if (localFallback && distance > FallbackMaxRepairDistance)
            {
                // A fallback is allowed to bridge an actually unreachable wall/roof/ledge, but it
                // must never become a base-wide remote repair beam. If the worker is still very far
                // away after the navigation watchdog fired, drop this target and let the next scan
                // choose something sensible.
                RepairHammerVisualRuntime.Hide(worker);
                LocalWorkFallbackRuntime.Clear(zdo);
                BoundTargets.Remove(workerKey);
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                status = $"Reparatur-Fallback verworfen: {targetName} ist noch {distance:0.0} m entfernt";
                return true;
            }

            if (distance > WorkerInteractionRuntime.AccessRadius + 0.75f && !localFallback)
            {
                RepairHammerVisualRuntime.Hide(worker);
                WorkerZdo.WriteTarget(zdo, targetName, actualPoint, TargetKind);
                zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
                status = $"Reparaturziel wieder annaehern ({distance:0.0} m)";
                return true;
            }

            // v0.5.122: Navigation remains the preferred presentation. If the shared local-work
            // watchdog has proven that this exact repair target is unreachable/stalled, keep the
            // worker where it is and finish the real WearNTear repair from there instead of
            // bouncing forever between MoveToTarget and Work.
            FaceTarget(worker, actualPoint);

            int key = workerKey;
            if (!Cycles.TryGetValue(key, out RepairCycle cycle))
            {
                cycle = new RepairCycle();
                Cycles[key] = cycle;
            }

            int targetId = target.GetInstanceID();
            if (cycle.TargetId != targetId)
            {
                cycle.TargetId = targetId;
                cycle.Pending = false;
                cycle.ApplyAt = 0f;
                cycle.NextSwingAt = 0f;
            }

            float now = Time.time;
            if (cycle.Pending && now >= cycle.ApplyAt)
            {
                cycle.Pending = false;
                float before = GetHealthPercentage(target);
                bool repaired = TryRepair(target);
                float after = GetHealthPercentage(target);

                if (repaired)
                {
                    // WearNTear.Repair() is network-aware and may update the health ZDO a frame later
                    // when another peer owns the piece. A successful native repair request therefore
                    // completes this work cycle immediately; the next throttled scan verifies the
                    // world state naturally instead of hammering the same RPC repeatedly.
                    RepairHammerVisualRuntime.Hide(worker);
                    LocalWorkFallbackRuntime.Clear(zdo);
                    BoundTargets.Remove(workerKey);
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    status = $"{targetName} repariert ({before * 100f:0}% -> {after * 100f:0}%)";
                    Plugin.Log?.LogInfo($"[BaseRepair] Repair() erfolgreich: {targetName}, vorher={before * 100f:0.0}%, direkt danach={after * 100f:0.0}%.");
                    return true;
                }

                if (!IsRepairable(target, out after))
                {
                    RepairHammerVisualRuntime.Hide(worker);
                    LocalWorkFallbackRuntime.Clear(zdo);
                    BoundTargets.Remove(workerKey);
                    WorkerZdo.ClearTarget(zdo);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                    status = $"{targetName} ist nicht mehr reparaturbeduerftig";
                    return true;
                }

                Plugin.Log?.LogWarning($"[BaseRepair] WearNTear.Repair() lieferte false: {targetName}, HP={after * 100f:0.0}%.");
                status = $"Reparatur von {targetName} wurde von Valheim abgelehnt";
                cycle.NextSwingAt = now + RepairSwingInterval;
                return true;
            }

            if (cycle.Pending)
            {
                status = localFallback
                    ? $"Fallback-Reparaturschlag auf {targetName} laeuft ({distance:0.0} m)"
                    : $"Reparaturschlag auf {targetName} laeuft";
                return true;
            }

            if (now < cycle.NextSwingAt)
            {
                status = localFallback
                    ? $"Fallback-Reparatur an {targetName} aus {distance:0.0} m ({health * 100f:0}%)"
                    : $"Reparateur arbeitet an {targetName} ({health * 100f:0}%)";
                return true;
            }

            // The Hammer is presentation-only: no ItemData, inventory slot or durability exists.
            // The actual repair still happens through Valheim's native WearNTear.Repair().
            RepairHammerVisualRuntime.Show(worker);
            bool animated = FirekeeperAnimationRuntime.TryPlayCast(worker);
            cycle.Pending = true;
            cycle.ApplyAt = now + RepairApplyDelay;
            cycle.NextSwingAt = now + RepairSwingInterval;
            status = localFallback
                ? $"Fallback-Reparatur gestartet: {targetName} aus {distance:0.0} m ({health * 100f:0}%)"
                : animated
                    ? $"Reparaturschlag gestartet: {targetName} ({health * 100f:0}%)"
                    : $"Reparatur ohne sichtbare Spezialanimation: {targetName} ({health * 100f:0}%)";
            return true;
        }

        /// <summary>
        /// v0.5.379 handoff-only backlog snapshot used by the shared Smith work budget.
        /// It uses Valheim's loaded Piece registry once when the Workzone is still physical; no
        /// recurring background scan is installed.
        /// </summary>
        internal static int CountBackgroundRepairBacklog(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null) return 0;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 workCenter, out float workRadius)) return 0;
            float safeRadius = Mathf.Clamp(workRadius, 4f, 200f);
            PieceBuffer.Clear();
            Seen.Clear();
            int count = 0;
            try
            {
                Piece.GetAllPiecesInRadius(workCenter, safeRadius, PieceBuffer);
                for (int i = 0; i < PieceBuffer.Count; i++)
                {
                    Piece piece = PieceBuffer[i];
                    if (piece == null) continue;
                    WearNTear candidate = ResolveWearNTear(piece);
                    if (!IsRepairable(candidate, out _)) continue;
                    int id = candidate.GetInstanceID();
                    if (!Seen.Add(id)) continue;
                    Vector3 delta = candidate.transform.position - workCenter;
                    delta.y = 0f;
                    if (delta.sqrMagnitude <= safeRadius * safeRadius) count++;
                }
            }
            catch { return 0; }
            finally
            {
                PieceBuffer.Clear();
                Seen.Clear();
            }
            return count;
        }

        /// <summary>
        /// v0.5.378 background repair materialisation. Elapsed unloaded time is converted to a
        /// finite repair quota elsewhere; this method performs ONE loaded Workzone pass and uses
        /// that quota on the real damaged WearNTear pieces. It never runs while the zone is
        /// unloaded and never installs a periodic world scanner.
        /// </summary>
        internal static int RunBackgroundCatchup(Character worker, ZDO workerZdo, int maxPieces, out int damagedSeen, out string status)
        {
            damagedSeen = 0;
            status = "Reparatur-Catchup: kein Auftrag";
            if (worker == null || workerZdo == null || maxPieces <= 0) return 0;
            if (!WorkerZdo.TryReadWorkArea(workerZdo, out _, out Vector3 workCenter, out float workRadius))
            {
                status = "Reparatur-Catchup: keine Workzone";
                return 0;
            }

            float safeRadius = Mathf.Clamp(workRadius, 4f, 200f);
            BackgroundCandidates.Clear();
            PieceBuffer.Clear();
            Seen.Clear();

            try
            {
                Piece.GetAllPiecesInRadius(workCenter, safeRadius, PieceBuffer);
            }
            catch (Exception ex)
            {
                status = "Reparatur-Catchup Scan fehlgeschlagen: " + ex.GetType().Name;
                return 0;
            }

            for (int i = 0; i < PieceBuffer.Count; i++)
                AddBackgroundCandidate(ResolveWearNTear(PieceBuffer[i]), worker.transform.position, workCenter, safeRadius);

            // Same compatibility fallback as the visible repair job, but only if the normal Piece
            // registry did not expose a single damaged target. Because this catch-up runs once at
            // a load boundary, it cannot turn into the recurring FindObjectsOfType cost that the
            // performance architecture intentionally removed.
            if (BackgroundCandidates.Count == 0)
            {
                WorkerWearNTearIndexRuntime.CopyInRadius(workCenter, safeRadius, WearBuffer);
                for (int i = 0; i < WearBuffer.Count; i++)
                    AddBackgroundCandidate(WearBuffer[i], worker.transform.position, workCenter, safeRadius);
                WearBuffer.Clear();
            }

            damagedSeen = BackgroundCandidates.Count;
            if (damagedSeen <= 0)
            {
                status = "Reparatur-Catchup: keine beschaedigten Bauteile";
                return 0;
            }

            BackgroundCandidates.Sort((a, b) =>
            {
                int healthCompare = a.Health.CompareTo(b.Health);
                return healthCompare != 0 ? healthCompare : a.Distance.CompareTo(b.Distance);
            });

            int requested = Mathf.Clamp(maxPieces, 0, 1000);
            int repaired = 0;
            for (int i = 0; i < BackgroundCandidates.Count && repaired < requested; i++)
            {
                WearNTear target = BackgroundCandidates[i].Target;
                if (!IsRepairable(target, out _)) continue;
                if (TryRepair(target)) repaired++;
            }

            BackgroundCandidates.Clear();
            status = repaired > 0
                ? $"Reparatur-Catchup: {repaired}/{Mathf.Min(requested, damagedSeen)} Bauteile repariert"
                : $"Reparatur-Catchup: {damagedSeen} Ziele gefunden, Netzwerk-Reparatur noch ausstehend";
            return repaired;
        }

        private static void AddBackgroundCandidate(WearNTear candidate, Vector3 workerPosition, Vector3 workCenter, float workRadius)
        {
            if (!IsRepairable(candidate, out float health)) return;
            int id = candidate.GetInstanceID();
            if (!Seen.Add(id)) return;

            Vector3 delta = candidate.transform.position - workCenter;
            delta.y = 0f;
            if (delta.sqrMagnitude > workRadius * workRadius) return;

            Vector3 point = GetInteractionPoint(candidate, workerPosition);
            BackgroundCandidates.Add(new BackgroundRepairCandidate
            {
                Target = candidate,
                Health = health,
                Distance = WorkerInteractionRuntime.HorizontalDistance(workerPosition, point)
            });
        }

        internal static void Reset(Character worker)
        {
            if (worker == null) return;
            int key = worker.GetInstanceID();
            Cycles.Remove(key);
            NextScanAt.Remove(key);
            BoundTargets.Remove(key);
            RepairHammerVisualRuntime.Hide(worker);
        }

        internal static void ResetAll()
        {
            Cycles.Clear();
            NextScanAt.Clear();
            BoundTargets.Clear();
            Seen.Clear();
            PieceBuffer.Clear();
            ResolvePieceBuffer.Clear();
            WearBuffer.Clear();
            RepairHammerVisualRuntime.ResetAll();
        }

        private static bool TryResolveTarget(Vector3 targetPosition, string expectedTargetName, Vector3 workerPosition, out WearNTear target, out float health, out Vector3 interactionPoint)
        {
            target = null;
            health = 1f;
            interactionPoint = targetPosition;

            // First use Valheim's normal Piece registry. This is the cheap path and remains the
            // preferred resolver for ordinary loaded structures.
            ResolvePieceBuffer.Clear();
            try
            {
                Piece.GetAllPiecesInRadius(targetPosition, ResolveRadius, ResolvePieceBuffer);
            }
            catch { }

            float best = float.MaxValue;
            bool bestNameMatch = false;
            Seen.Clear();
            for (int i = 0; i < ResolvePieceBuffer.Count; i++)
            {
                Piece piece = ResolvePieceBuffer[i];
                if (piece == null) continue;

                WearNTear candidate = ResolveWearNTear(piece);
                ConsiderResolvedCandidate(candidate, targetPosition, expectedTargetName, workerPosition,
                    ref target, ref health, ref interactionPoint, ref best, ref bestNameMatch);
            }

            if (target != null) return true;

            // v0.5.192: v0.5.191 could DISCOVER a damaged piece through the loaded WearNTear
            // fallback and then immediately lose it again here because repair execution only
            // resolved through Piece.GetAllPiecesInRadius. The result was exactly:
            //   AI decision = "Basis reparieren ..."
            //   State/target on the next frame = FindJob / none
            // Keep discovery and execution symmetrical: only when the cheap Piece resolver fails,
            // inspect currently loaded WearNTear components around the saved target position.
            // This is not a world scan and only runs while an already-assigned repair target needs
            // resolving. Prefer an exact prefab-name match so dense bases do not jump to a neighbour.
            WorkerWearNTearIndexRuntime.CopyInRadius(targetPosition, ResolveRadius, WearBuffer);
            for (int i = 0; i < WearBuffer.Count; i++)
            {
                WearNTear candidate = WearBuffer[i];
                if (candidate == null) continue;
                ConsiderResolvedCandidate(candidate, targetPosition, expectedTargetName, workerPosition,
                    ref target, ref health, ref interactionPoint, ref best, ref bestNameMatch);
            }
            WearBuffer.Clear();

            return target != null;
        }

        private static void ConsiderResolvedCandidate(
            WearNTear candidate,
            Vector3 targetPosition,
            string expectedTargetName,
            Vector3 workerPosition,
            ref WearNTear bestTarget,
            ref float bestHealth,
            ref Vector3 bestInteractionPoint,
            ref float bestDistance,
            ref bool bestNameMatch)
        {
            if (!IsRepairable(candidate, out float candidateHealth)) return;

            int id = candidate.GetInstanceID();
            if (!Seen.Add(id)) return;

            Piece piece = null;
            try { piece = candidate.GetComponent<Piece>() ?? candidate.GetComponentInParent<Piece>(); }
            catch { }
            string candidateName = piece != null
                ? SpeciesRoleRegistry.NormalizePrefabName(piece.gameObject.name)
                : SpeciesRoleRegistry.NormalizePrefabName(candidate.gameObject.name);
            bool nameMatch = !string.IsNullOrEmpty(expectedTargetName) &&
                string.Equals(candidateName, expectedTargetName, StringComparison.Ordinal);

            float distance = (candidate.transform.position - targetPosition).sqrMagnitude;
            if (bestTarget != null)
            {
                if (bestNameMatch && !nameMatch) return;
                if (bestNameMatch == nameMatch && distance >= bestDistance) return;
            }

            bestDistance = distance;
            bestNameMatch = nameMatch;
            bestTarget = candidate;
            bestHealth = candidateHealth;
            bestInteractionPoint = GetInteractionPoint(candidate, workerPosition);
        }


        private static void ConsiderCandidate(
            WearNTear candidate,
            Vector3 workerPosition,
            Vector3 workCenter,
            float workRadius,
            ref WearNTear bestTarget,
            ref Vector3 bestPoint,
            ref float bestScore,
            ref float bestHealth,
            ref int damaged)
        {
            if (!IsRepairable(candidate, out float health)) return;

            int id = candidate.GetInstanceID();
            if (!Seen.Add(id)) return;

            Vector3 candidatePosition = candidate.transform.position;
            Vector3 zoneDelta = candidatePosition - workCenter;
            zoneDelta.y = 0f;
            if (zoneDelta.sqrMagnitude > workRadius * workRadius) return;

            damaged++;
            Vector3 point = GetInteractionPoint(candidate, workerPosition);
            float distance = WorkerInteractionRuntime.HorizontalDistance(workerPosition, point);
            float score = health * 100f + distance;
            if (score >= bestScore) return;

            bestScore = score;
            bestHealth = health;
            bestTarget = candidate;
            bestPoint = point;
        }

        private static WearNTear ResolveWearNTear(Piece piece)
        {
            if (piece == null) return null;
            WearNTear wear = null;
            try { wear = piece.GetComponent<WearNTear>(); }
            catch { }
            if (wear != null) return wear;
            try { return piece.GetComponentInChildren<WearNTear>(); }
            catch { return null; }
        }

        private static bool IsRepairable(WearNTear target, out float health)
        {
            health = 1f;
            if (target == null || target.gameObject == null || !target.gameObject.activeInHierarchy) return false;

            Piece piece = null;
            try { piece = target.GetComponent<Piece>() ?? target.GetComponentInParent<Piece>(); }
            catch { }
            if (piece == null) return false;

            health = GetHealthPercentage(target);
            return health >= 0f && health < HealthyThreshold;
        }

        private static float GetHealthPercentage(WearNTear target)
        {
            if (target == null) return 1f;

            // Current Valheim exposes this API directly. Use it first; unlike the old reflection-only
            // path a missing reflection binding can no longer silently make every piece look healthy.
            try
            {
                float direct = target.GetHealthPercentage();
                if (!float.IsNaN(direct) && !float.IsInfinity(direct)) return Mathf.Clamp01(direct);
            }
            catch { }

            // Fail-soft fallback for modded WearNTear implementations: the ZDO stores current health
            // under the vanilla "health" key while m_health is the piece's maximum health.
            try
            {
                ZNetView view = target.GetComponent<ZNetView>() ?? target.GetComponentInParent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                float max = target.m_health;
                if (zdo != null && max > 0f)
                    return Mathf.Clamp01(zdo.GetFloat("health", max) / max);
            }
            catch { }

            return 1f;
        }

        private static bool TryRepair(WearNTear target)
        {
            if (target == null) return false;
            try
            {
                // Do not force ownership. WearNTear.Repair() already routes the repair to the
                // network object's owner, which is the same path used by vanilla hammer repair.
                return target.Repair();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Base repair failed for {target.gameObject.name}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static Vector3 GetInteractionPoint(WearNTear target, Vector3 from)
        {
            if (target == null) return from;

            // Building prefabs frequently contain several colliders. GetComponent<Collider>() can
            // return an internal/roof/trigger collider that puts the navigator on the wrong side of
            // the structure. Pick the closest real collider surface to the worker instead.
            Collider[] colliders = null;
            try { colliders = target.GetComponentsInChildren<Collider>(true); }
            catch { }

            Vector3 bestPoint = target.transform.position;
            float bestDistance = float.MaxValue;
            if (colliders != null)
            {
                for (int i = 0; i < colliders.Length; i++)
                {
                    Collider collider = colliders[i];
                    if (collider == null || !collider.enabled || collider.isTrigger) continue;
                    try
                    {
                        Vector3 point = ColliderGeometryRuntime.ClosestPointSafe(collider, from);
                        if (float.IsNaN(point.x) || float.IsInfinity(point.x) ||
                            float.IsNaN(point.y) || float.IsInfinity(point.y) ||
                            float.IsNaN(point.z) || float.IsInfinity(point.z)) continue;
                        Vector3 delta = point - from;
                        delta.y = 0f;
                        float sqr = delta.sqrMagnitude;
                        if (sqr >= bestDistance) continue;
                        bestDistance = sqr;
                        bestPoint = point;
                    }
                    catch { }
                }
            }
            return bestPoint;
        }

        private static void FaceTarget(Character worker, Vector3 target)
        {
            if (worker == null) return;
            Vector3 direction = target - worker.transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f) return;
            try { worker.transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up); }
            catch { }
        }
    }
}
