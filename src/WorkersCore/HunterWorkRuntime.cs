using System;
using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Tier-2 ranged Draugr hunting runtime. The job deliberately lets Valheim's native MonsterAI
    /// own movement/aiming/archery once a real configured prey target has been selected. Workers only
    /// chooses the prey, bounds it to the assigned Workzone and handles the real world drops after
    /// the kill. No meat/hide is ever synthesized.
    /// </summary>
    internal static class HunterWorkRuntime
    {
        internal const string LootTargetKind = "HunterLoot";
        internal const string VantageTargetKind = "HunterVantage";
        internal const float LootSearchRadius = 6f;
        internal const float LootWindowSeconds = 120f;
        private static float _shootRange = 30f;
        private static float _shootRangeHysteresis = 34f;
        private const float LineOfSightProbeInterval = 0.30f;
        private const float RepositionReuseSeconds = 1.75f;
        private const float RepositionArriveDistance = 3.25f;

        internal static float ShootRange => _shootRange;
        internal static float ShootRangeHysteresis => _shootRangeHysteresis;

        internal static void ConfigureShootRange(float meters)
        {
            _shootRange = Mathf.Clamp(meters, 15f, 50f);
            _shootRangeHysteresis = Mathf.Min(54f, _shootRange + 4f);
        }

        private sealed class HuntSession
        {
            internal Character Prey;
            internal Vector3 LastKnownPosition;
            internal float LootUntil;
            internal bool LootPhase;
            internal float NextLineOfSightProbeAt;
            internal bool LastLineOfSightClear = true;
            internal bool HasRepositionPoint;
            internal Vector3 RepositionPoint;
            internal float RepositionValidUntil;
        }

        private static readonly Dictionary<int, HuntSession> Sessions = new Dictionary<int, HuntSession>();
        private static readonly Dictionary<int, float> NextShotAt = new Dictionary<int, float>();
        private static Action<string> _debugLog;

        internal static void Initialize(Action<string> debugLog) => _debugLog = debugLog;
        internal static void Shutdown()
        {
            Sessions.Clear();
            NextShotAt.Clear();
            _debugLog = null;
        }

        internal static bool IsAssignedPrey(Character worker, Character target, ZDO zdo)
        {
            if (worker == null || target == null || zdo == null || target.IsDead()) return false;
            if ((WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None) != WorkerRole.Hunter) return false;
            if ((WorkOrder)zdo.GetInt(WorkerZdo.WorkOrderKey, (int)WorkOrder.None) != WorkOrder.Hunt) return false;
            if (!Sessions.TryGetValue(worker.GetInstanceID(), out HuntSession session) || session == null || session.LootPhase) return false;
            if (session.Prey != target) return false;

            // Never let a quarry drag the Hunter indefinitely outside its assigned outpost.
            if (WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 center, out float radius))
            {
                float safe = Mathf.Max(5f, radius) + 4f;
                Vector3 delta = target.transform.position - center;
                delta.y = 0f;
                if (delta.sqrMagnitude > safe * safe) return false;
            }

            session.LastKnownPosition = target.transform.position;
            return true;
        }

        internal static void NotePreyPosition(Character worker, Character target)
        {
            if (worker == null || target == null) return;
            if (!Sessions.TryGetValue(worker.GetInstanceID(), out HuntSession session) || session == null) return;
            if (session.Prey == target) session.LastKnownPosition = target.transform.position;
        }

        internal static bool HasAvailablePrey(ZDO zdo, Vector3 workCenter, float workRadius, Vector3 fromPosition)
        {
            if (zdo == null) return false;
            return FindNearestPrey(zdo, workCenter, workRadius, fromPosition) != null;
        }

        internal static bool TryEnsureHuntTarget(Character worker, ZDO zdo, Vector3 workCenter, float workRadius, out Character prey, out string status)
        {
            prey = null;
            status = string.Empty;
            if (worker == null || zdo == null) return false;

            int key = worker.GetInstanceID();
            if (Sessions.TryGetValue(key, out HuntSession existing) && existing != null)
            {
                if (!existing.LootPhase && existing.Prey != null && !existing.Prey.IsDead())
                {
                    if (IsEligiblePrey(existing.Prey, zdo, workCenter, workRadius, false))
                    {
                        existing.LastKnownPosition = existing.Prey.transform.position;
                        prey = existing.Prey;
                        status = $"jagt {SpeciesRoleRegistry.NormalizePrefabName(prey.gameObject.name)}";
                        return true;
                    }

                    // The quarry is still alive but no longer valid (for example because the
                    // player disabled this prey category). Do not enter loot mode for an animal
                    // that was never killed; simply forget it and choose a new allowed target.
                    Sessions.Remove(key);
                    existing = null;
                }

                if (existing != null && !existing.LootPhase)
                {
                    existing.LootPhase = true;
                    existing.LootUntil = Time.time + LootWindowSeconds;
                    existing.Prey = null;
                    status = "Beute erlegt; suche echte Drops";
                }
            }

            if (Sessions.TryGetValue(key, out HuntSession lootSession) && lootSession != null && lootSession.LootPhase)
            {
                if (Time.time <= lootSession.LootUntil && TryFindNearestLoot(worker.transform.position, lootSession.LastKnownPosition, out _, out _))
                {
                    status = "Jagdbeute liegt bereit";
                    return false;
                }

                if (Time.time <= lootSession.LootUntil && Vector3.Distance(worker.transform.position, lootSession.LastKnownPosition) > LootSearchRadius + 2f)
                {
                    status = "kehrt zur erlegten Beute zurueck";
                    return false;
                }

                Sessions.Remove(key);
            }

            Character best = FindNearestPrey(zdo, workCenter, workRadius, worker.transform.position);
            if (best == null)
            {
                status = $"kein erlaubtes Jagdziel in der Workzone ({HunterPreyFilterRuntime.DescribeAllowed(zdo)})";
                return false;
            }

            Sessions[key] = new HuntSession
            {
                Prey = best,
                LastKnownPosition = best.transform.position,
                LootUntil = 0f,
                LootPhase = false
            };
            prey = best;
            status = $"neues Jagdziel: {SpeciesRoleRegistry.NormalizePrefabName(best.gameObject.name)}";
            return true;
        }


        /// <summary>
        /// Hunters do not treat range as permission to shoot. A cheap, rate-limited line-of-sight
        /// probe is authoritative once the prey is in bow range. If a tree/rock/building blocks the
        /// shot, choose a nearby lateral firing point and let the existing Navigator move there.
        /// No world scan is performed; at most six deterministic candidate rays are tested when a
        /// blocked shot actually needs a new position.
        /// </summary>
        internal static bool TryGetBlockedShotReposition(Character worker, ZDO zdo, Character prey, Vector3 workCenter, float workRadius, out Vector3 repositionPoint, out string status)
        {
            repositionPoint = default;
            status = string.Empty;
            if (worker == null || zdo == null || prey == null || prey.IsDead()) return false;

            int key = worker.GetInstanceID();
            if (!Sessions.TryGetValue(key, out HuntSession session) || session == null || session.Prey != prey || session.LootPhase)
                return false;

            float horizontal = Mathf.Sqrt(HorizontalSqr(worker.transform.position, prey.transform.position));
            if (horizontal > ShootRangeHysteresis)
            {
                session.HasRepositionPoint = false;
                return false;
            }

            if (HasClearShotCached(worker, prey, session))
            {
                session.HasRepositionPoint = false;
                return false;
            }

            if (session.HasRepositionPoint && Time.time <= session.RepositionValidUntil)
            {
                float remaining = Mathf.Sqrt(HorizontalSqr(worker.transform.position, session.RepositionPoint));
                if (remaining > RepositionArriveDistance)
                {
                    repositionPoint = session.RepositionPoint;
                    status = $"Sicht blockiert; wechselt Schussposition ({remaining:0.0} m)";
                    return true;
                }
            }

            if (TryFindRepositionPoint(worker, prey, workCenter, workRadius, out repositionPoint))
            {
                session.HasRepositionPoint = true;
                session.RepositionPoint = repositionPoint;
                session.RepositionValidUntil = Time.time + RepositionReuseSeconds;
                status = "Sicht blockiert; sucht freie Schusslinie";
                return true;
            }

            // Even if none of the six probes already has a verified clear ray, do not stand in
            // front of the same tree forever. Take one deterministic side-step and re-evaluate on
            // arrival. This is still bounded, local work and does not touch Navigator v5 itself.
            Vector3 toPrey = prey.transform.position - worker.transform.position;
            toPrey.y = 0f;
            if (toPrey.sqrMagnitude > 0.001f)
            {
                Vector3 forward = toPrey.normalized;
                Vector3 side = new Vector3(-forward.z, 0f, forward.x);
                float sign = (key & 1) == 0 ? 1f : -1f;
                Vector3 candidate = worker.transform.position + side * (6f * sign);
                if (TryGroundCandidate(candidate, workCenter, workRadius, prey.transform.position, out candidate))
                {
                    session.HasRepositionPoint = true;
                    session.RepositionPoint = candidate;
                    session.RepositionValidUntil = Time.time + RepositionReuseSeconds;
                    repositionPoint = candidate;
                    status = "Sicht blockiert; macht Seitenschritt";
                    return true;
                }
            }

            status = "Sicht blockiert; keine freie Schussposition gefunden";
            return false;
        }

        internal static bool HasClearShot(Character worker, Character prey)
        {
            if (worker == null || prey == null || prey.IsDead()) return false;
            int key = worker.GetInstanceID();
            if (Sessions.TryGetValue(key, out HuntSession session) && session != null && session.Prey == prey)
                return HasClearShotCached(worker, prey, session);
            return HasClearShotFrom(worker.transform.position, prey);
        }

        private static bool HasClearShotCached(Character worker, Character prey, HuntSession session)
        {
            if (Time.time < session.NextLineOfSightProbeAt) return session.LastLineOfSightClear;
            session.NextLineOfSightProbeAt = Time.time + LineOfSightProbeInterval;
            session.LastLineOfSightClear = HasClearShotFrom(worker.transform.position, prey);
            return session.LastLineOfSightClear;
        }

        private static bool HasClearShotFrom(Vector3 shooterPosition, Character prey)
        {
            if (prey == null || prey.IsDead()) return false;
            Vector3 target = prey.transform.position + Vector3.up * 0.8f;
            Vector3 origin = shooterPosition + Vector3.up * 1.35f;
            Vector3 ray = target - origin;
            float distance = ray.magnitude;
            if (distance <= 0.25f) return true;
            Vector3 direction = ray / distance;
            origin += direction * 0.30f; // start outside the Hunter's own body collider
            distance = Mathf.Max(0.05f, distance - 0.30f);

            if (!Physics.Raycast(origin, direction, out RaycastHit hit, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return true;

            Collider collider = hit.collider;
            if (collider == null) return true;
            Character hitCharacter = collider.GetComponentInParent<Character>();
            return hitCharacter == prey;
        }

        private static bool TryFindRepositionPoint(Character worker, Character prey, Vector3 workCenter, float workRadius, out Vector3 point)
        {
            point = default;
            Vector3 toPrey = prey.transform.position - worker.transform.position;
            toPrey.y = 0f;
            if (toPrey.sqrMagnitude < 0.001f) return false;

            Vector3 forward = toPrey.normalized;
            Vector3 side = new Vector3(-forward.z, 0f, forward.x);
            int parity = (worker.GetInstanceID() & 1) == 0 ? 1 : -1;
            float[] offsets = { 4f, -4f, 8f, -8f, 12f, -12f };

            float bestSqr = float.MaxValue;
            bool found = false;
            for (int i = 0; i < offsets.Length; i++)
            {
                float signed = offsets[(i + (parity > 0 ? 0 : 1)) % offsets.Length];
                Vector3 candidate = worker.transform.position + side * signed;
                if (!TryGroundCandidate(candidate, workCenter, workRadius, prey.transform.position, out candidate)) continue;
                if (!HasClearShotFrom(candidate, prey)) continue;

                float sqr = HorizontalSqr(candidate, worker.transform.position);
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                point = candidate;
                found = true;
            }
            return found;
        }

        private static bool TryGroundCandidate(Vector3 candidate, Vector3 workCenter, float workRadius, Vector3 preyPosition, out Vector3 grounded)
        {
            grounded = candidate;
            float safeRadius = Mathf.Max(5f, workRadius);
            Vector3 fromCenter = candidate - workCenter;
            fromCenter.y = 0f;
            if (fromCenter.sqrMagnitude > safeRadius * safeRadius) return false;

            Vector3 toPrey = preyPosition - candidate;
            toPrey.y = 0f;
            if (toPrey.sqrMagnitude > ShootRangeHysteresis * ShootRangeHysteresis) return false;

            try
            {
                if (ZoneSystem.instance != null)
                    grounded.y = ZoneSystem.instance.GetGroundHeight(candidate) + 0.05f;
            }
            catch
            {
                grounded.y = candidate.y;
            }
            return true;
        }


        internal static bool TryTickGuardRangedAttack(Character worker, Character target, out string status)
        {
            status = string.Empty;
            if (worker == null || target == null || target.IsDead()) return false;

            Vector3 toTarget = target.transform.position - worker.transform.position;
            float horizontal = new Vector2(toTarget.x, toTarget.z).magnitude;
            if (horizontal > ShootRange)
            {
                status = $"Guard-Ziel ausser Reichweite ({horizontal:0.0}/{ShootRange:0} m)";
                return false;
            }

            // v0.5.377: stationary ranged guards must obey the same real line-of-sight rule as
            // Hunters. A wall/tree/rock in front of the guard is not a valid firing solution.
            // Stationary guards simply hold fire; Deploy guards can reposition through their
            // normal patrol/combat movement instead of shooting into geometry.
            if (!HasClearShotFrom(worker.transform.position, target))
            {
                status = $"Guard-Ziel verdeckt ({horizontal:0.0} m)";
                return false;
            }

            Vector3 aim = (target.transform.position + Vector3.up * 0.8f) - (worker.transform.position + Vector3.up * 1.2f);
            if (aim.sqrMagnitude > 0.001f)
                worker.SetLookDir(aim.normalized, 0f);

            int key = worker.GetInstanceID();
            if (Time.time < (NextShotAt.TryGetValue(key, out float next) ? next : 0f))
            {
                status = $"Guard zielt ({horizontal:0.0} m)";
                return true;
            }

            string weaponStatus = EnsureRangedWeapon(worker);
            bool started = worker.StartAttack(target, false);
            if (started)
            {
                NextShotAt[key] = Time.time + 1.55f;
                status = $"Guard schiesst ({horizontal:0.0} m; {weaponStatus})";
                return true;
            }

            NextShotAt[key] = Time.time + 0.35f;
            status = $"Guard-Fernkampfattacke noch nicht bereit ({weaponStatus})";
            return true;
        }

        internal static bool TryTickRangedAttack(Character worker, ZDO zdo, out string status)
        {
            status = string.Empty;
            if (worker == null || zdo == null) return false;

            int key = worker.GetInstanceID();
            if (!Sessions.TryGetValue(key, out HuntSession session) || session == null || session.LootPhase || session.Prey == null)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                status = "kein aktives Jagdziel";
                return false;
            }

            Character prey = session.Prey;
            if (prey.IsDead())
            {
                session.LastKnownPosition = prey.transform.position;
                session.Prey = null;
                session.LootPhase = true;
                session.LootUntil = Time.time + LootWindowSeconds;
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                status = "Beute erlegt; suche echte Drops";
                return true;
            }

            session.LastKnownPosition = prey.transform.position;
            Vector3 toPrey = prey.transform.position - worker.transform.position;
            float horizontal = new Vector2(toPrey.x, toPrey.z).magnitude;
            string preyName = SpeciesRoleRegistry.NormalizePrefabName(prey.gameObject.name);
            bool localFallback = LocalWorkFallbackRuntime.IsActive(zdo, preyName, "HunterPrey");

            if (horizontal <= ShootRangeHysteresis && !HasClearShotCached(worker, prey, session))
            {
                if (WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 losCenter, out float losRadius) &&
                    TryGetBlockedShotReposition(worker, zdo, prey, losCenter, losRadius, out Vector3 reposition, out string repositionStatus))
                {
                    WorkerZdo.WriteTarget(zdo, preyName, reposition, VantageTargetKind);
                    zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
                    status = repositionStatus;
                    return true;
                }

                NextShotAt[key] = Time.time + 0.25f;
                status = $"keine freie Schusslinie auf {preyName}; wartet auf neue Position";
                return true;
            }

            if (horizontal > ShootRange)
            {
                // A local navigation fallback may relax presentation/proximity for some jobs, but
                // it must never grant a Hunter supernatural bow range. Clear the stale fallback and
                // keep trying to reach a real firing distance.
                if (localFallback) LocalWorkFallbackRuntime.Clear(zdo);
                WorkerZdo.WriteTarget(zdo, preyName, prey.transform.position, "HunterPrey");
                zdo.Set(WorkerZdo.State, (int)WorkerState.MoveToTarget);
                status = $"naehert sich Jagdziel ({horizontal:0.0} m)";
                return true;
            }

            Vector3 aim = (prey.transform.position + Vector3.up * 0.8f) - (worker.transform.position + Vector3.up * 1.2f);
            if (aim.sqrMagnitude > 0.001f)
                worker.SetLookDir(aim.normalized, 0f);

            if (Time.time < (NextShotAt.TryGetValue(key, out float next) ? next : 0f))
            {
                status = $"zielt auf {SpeciesRoleRegistry.NormalizePrefabName(prey.gameObject.name)} ({horizontal:0.0} m)";
                return true;
            }

            string weaponStatus = EnsureRangedWeapon(worker);
            // v0.5.377: never synthesize direct damage for a stuck Hunter. Even under a local
            // fallback the shot must use Valheim's native ranged attack, be inside real bow range
            // and have passed the LOS gate above. This removes the old through-tree/wall escape hatch.
            bool started = worker.StartAttack(prey, false);
            if (started)
            {
                NextShotAt[key] = Time.time + 1.55f;
                status = $"schiesst auf {SpeciesRoleRegistry.NormalizePrefabName(prey.gameObject.name)} ({horizontal:0.0} m; {weaponStatus})";
                return true;
            }

            NextShotAt[key] = Time.time + 0.35f;
            status = $"Fernkampfattacke noch nicht bereit ({weaponStatus})";
            return true;
        }

        private static string EnsureRangedWeapon(Character worker)
        {
            Humanoid humanoid = worker as Humanoid;
            if (humanoid == null) return "kein Humanoid";

            // Preserve the creature's native ranged loadout. This is important for the Tier-3
            // Dvergr rogue: its crossbow must never be replaced by the Draugr hunter bow.
            ItemDrop.ItemData current = humanoid.GetCurrentWeapon();
            if (IsSupportedRangedWeapon(current)) return DescribeWeapon(current);

            List<ItemDrop.ItemData> items = humanoid.GetInventory()?.GetAllItems();
            if (items == null) return "kein Inventar";

            ItemDrop.ItemData best = null;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (!IsSupportedRangedWeapon(item)) continue;
                best = item;

                // Prefer a native Dvergr crossbow/arbalest when present, otherwise the first
                // valid bow keeps the existing Draugr hunter behaviour unchanged.
                string n = GetWeaponPrefabName(item);
                if (ContainsRangedToken(n, "crossbow") || ContainsRangedToken(n, "arbalest"))
                    break;
            }

            if (best == null)
                return current != null ? $"aktuell {DescribeWeapon(current)}; keine Fernkampfwaffe gefunden" : "keine Fernkampfwaffe gefunden";

            bool equipped = humanoid.EquipItem(best, false);
            ItemDrop.ItemData after = humanoid.GetCurrentWeapon();
            return IsSupportedRangedWeapon(after)
                ? $"Fernkampfwaffe {DescribeWeapon(after)}"
                : (equipped ? "Fernkampfwaffe wird ausgeruestet" : "Fernkampfwaffe konnte nicht ausgeruestet werden");
        }

        private static bool IsSupportedRangedWeapon(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null || !item.HavePrimaryAttack()) return false;
            string name = GetWeaponPrefabName(item);
            return ContainsRangedToken(name, "bow") ||
                   ContainsRangedToken(name, "crossbow") ||
                   ContainsRangedToken(name, "arbalest");
        }

        private static bool ContainsRangedToken(string name, string token)
        {
            return !string.IsNullOrEmpty(name) &&
                   !string.IsNullOrEmpty(token) &&
                   name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetWeaponPrefabName(ItemDrop.ItemData item)
        {
            if (item == null) return string.Empty;
            if (item.m_dropPrefab != null) return SpeciesRoleRegistry.NormalizePrefabName(item.m_dropPrefab.name);
            return item.m_shared != null ? item.m_shared.m_name ?? string.Empty : string.Empty;
        }

        private static string DescribeWeapon(ItemDrop.ItemData item)
        {
            string name = GetWeaponPrefabName(item);
            return string.IsNullOrEmpty(name) ? "unbekannt" : name;
        }

        internal static bool TryGetLootTarget(Character worker, out ItemDrop drop, out string prefabName, out Vector3 position)
        {
            drop = null;
            prefabName = string.Empty;
            position = default;
            if (worker == null) return false;
            if (!Sessions.TryGetValue(worker.GetInstanceID(), out HuntSession session) || session == null || !session.LootPhase) return false;
            if (Time.time > session.LootUntil) return false;
            return TryFindNearestLoot(worker.transform.position, session.LastKnownPosition, out drop, out prefabName, out position);
        }

        internal static bool TryGetLootAnchor(Character worker, out Vector3 position)
        {
            position = default;
            if (worker == null) return false;
            if (!Sessions.TryGetValue(worker.GetInstanceID(), out HuntSession session) || session == null || !session.LootPhase) return false;
            position = session.LastKnownPosition;
            return Time.time <= session.LootUntil;
        }

        internal static bool TryCollectLoot(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out string desiredPrefab, out string kind, out _) ||
                !string.Equals(kind, LootTargetKind, StringComparison.Ordinal) || string.IsNullOrEmpty(desiredPrefab))
            {
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            if (!TryFindNearbyMatchingLoot(worker, desiredPrefab, out ItemDrop drop))
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            ZNetView nview = drop.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }
            if (!nview.IsOwner())
            {
                drop.RequestOwn();
                return true;
            }

            int available = Mathf.Max(0, drop.m_itemData.m_stack);
            int wanted = WorkerCargoRuntime.GetMaxAddAmount(worker, zdo, desiredPrefab, available);
            if (wanted <= 0)
            {
                zdo.Set(WorkerZdo.State, (int)WorkerState.Deliver);
                return true;
            }
            int removed = 0;
            for (int i = 0; i < wanted; i++)
            {
                if (drop == null || !drop.RemoveOne()) break;
                removed++;
            }
            if (removed <= 0) return true;

            WorkerZdo.AddCargoAmount(zdo, desiredPrefab, removed);
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, WorkerCargoRuntime.IsFull(worker, zdo) ? (int)WorkerState.Deliver : (int)WorkerState.FindJob);
            _debugLog?.Invoke($"Hunter collected real loot: {desiredPrefab} x{removed}; cargo={WorkerCargoRuntime.GetSummary(worker, zdo)}");
            return true;
        }

        internal static void Clear(Character worker)
        {
            if (worker != null)
            {
                int key = worker.GetInstanceID();
                Sessions.Remove(key);
                NextShotAt.Remove(key);
            }
        }

        private static Character FindNearestPrey(ZDO zdo, Vector3 center, float radius, Vector3 from)
        {
            float safeRadius = Mathf.Max(5f, radius);
            float bestSqr = float.MaxValue;
            Character best = null;
            List<Character> candidates = WorkerCharacterIndexRuntime.GetUntamedNonPlayers();
            for (int i = 0; i < candidates.Count; i++)
            {
                Character candidate = candidates[i];
                if (!IsEligiblePrey(candidate, zdo, center, safeRadius)) continue;
                float sqr = HorizontalSqr(candidate.transform.position, from);
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = candidate;
            }
            return best;
        }

        private static bool IsEligiblePrey(Character candidate, ZDO zdo, Vector3 center, float radius, bool checkDemand = true)
        {
            if (candidate == null || candidate is Player || candidate.IsDead() || candidate.IsTamed()) return false;
            if (!HunterPreyFilterRuntime.IsPreyAllowed(zdo, candidate)) return false;
            if (checkDemand && !ProductionDemandRuntime.AllowsHunterPrey(zdo, candidate.gameObject != null ? candidate.gameObject.name : string.Empty)) return false;
            float safe = Mathf.Max(5f, radius);
            return HorizontalSqr(candidate.transform.position, center) <= safe * safe;
        }

        private static bool TryFindNearestLoot(Vector3 from, Vector3 killPosition, out ItemDrop drop, out string prefabName, out Vector3 position)
        {
            ItemDrop foundDrop = null;
            string foundPrefab = string.Empty;
            Vector3 foundPosition = default;
            float bestSqr = float.MaxValue;
            WorkerWorldIndex.ForEachItemDrop(killPosition, LootSearchRadius, candidate =>
            {
                if (!IsLootDrop(candidate)) return;
                float sqr = (candidate.transform.position - from).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                foundDrop = candidate;
                foundPrefab = GathererTargetFinder.GetCargoPrefabName(candidate);
                foundPosition = candidate.transform.position;
            });
            drop = foundDrop;
            prefabName = foundPrefab;
            position = foundPosition;
            return foundDrop != null && !string.IsNullOrEmpty(foundPrefab);
        }

        private static bool TryFindNearestLoot(Vector3 from, Vector3 killPosition, out ItemDrop drop, out string prefabName)
        {
            return TryFindNearestLoot(from, killPosition, out drop, out prefabName, out _);
        }

        private static bool TryFindNearbyMatchingLoot(Character worker, string wantedPrefab, out ItemDrop best)
        {
            best = null;
            if (worker == null) return false;
            ItemDrop found = null;
            float bestSqr = float.MaxValue;
            WorkerWorldIndex.ForEachItemDrop(worker.transform.position, GathererWorkRuntime.CollectRadius, candidate =>
            {
                if (!IsLootDrop(candidate)) return;
                string prefab = GathererTargetFinder.GetCargoPrefabName(candidate);
                if (!string.Equals(prefab, wantedPrefab, StringComparison.OrdinalIgnoreCase)) return;
                float sqr = (candidate.transform.position - worker.transform.position).sqrMagnitude;
                if (sqr >= bestSqr) return;
                bestSqr = sqr;
                found = candidate;
            });
            best = found;
            return found != null;
        }

        private static bool IsLootDrop(ItemDrop drop)
        {
            if (drop == null || drop.gameObject == null || !drop.gameObject.activeInHierarchy || drop.m_itemData == null || drop.m_itemData.m_shared == null) return false;
            if (drop.m_itemData.m_stack <= 0 || drop.m_itemData.m_shared.m_questItem) return false;
            return true;
        }

        private static float HorizontalSqr(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return x * x + z * z;
        }
    }
}
