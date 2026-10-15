using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Physical transport for recruited Workers on real Valheim ships.
    ///
    /// Workers never pathfind onto a moving ship. The player first brings a following Worker close
    /// to the vessel, then the Worker is snapped to a generated/discovered passenger point and held
    /// relative to the real ship transform while normal Worker AI/navigation is paused. No ship
    /// resources, cargo or Worker assignments are changed.
    /// </summary>
    internal static class BoatPassengerRuntime
    {
        private const float PlayerShipSearchRadius = 11f;
        private const float WorkerBoardRadius = 9f;
        private const float SafeShoreRadius = 12f;
        private const float ProbeCacheSeconds = 0.35f;

        private sealed class PassengerSession
        {
            internal Character Worker;
            internal Transform Ship;
            internal int SeatIndex;
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
            internal Vector3 LastWorldPosition;
            internal Collider[] WorkerColliders;
            internal Collider[] ShipColliders;
            internal Rigidbody Body;
        }

        private sealed class ProbeCache
        {
            internal float Expires;
            internal Transform Ship;
        }

        private struct SeatPose
        {
            internal Vector3 Position;
            internal Quaternion Rotation;
        }

        private static readonly Collider[] ShipSearchBuffer = new Collider[128];
        private static readonly Dictionary<int, PassengerSession> Sessions = new Dictionary<int, PassengerSession>();
        private static readonly Dictionary<int, ProbeCache> ProbeByWorker = new Dictionary<int, ProbeCache>();
        private static readonly List<SeatPose> SeatScratch = new List<SeatPose>(12);
        private static readonly List<int> SessionKeyScratch = new List<int>(16);
        private static readonly HashSet<int> SeenShipRoots = new HashSet<int>();
        private static Action<string> _debugLog;

        internal static void Initialize(Action<string> debugLog)
        {
            _debugLog = debugLog;
            Sessions.Clear();
            ProbeByWorker.Clear();
            SessionKeyScratch.Clear();
        }

        internal static void Shutdown()
        {
            SessionKeyScratch.Clear();
            foreach (int key in Sessions.Keys)
                SessionKeyScratch.Add(key);

            for (int i = 0; i < SessionKeyScratch.Count; i++)
            {
                if (Sessions.TryGetValue(SessionKeyScratch[i], out PassengerSession session))
                    ReleaseCollisionIgnores(session);
            }

            Sessions.Clear();
            ProbeByWorker.Clear();
            SessionKeyScratch.Clear();
            _debugLog = null;
        }

        internal static bool IsTransported(Character worker)
        {
            return worker != null && Sessions.ContainsKey(worker.GetInstanceID());
        }

        internal static bool CanBoardNearby(Character worker, Player player)
        {
            if (worker == null || player == null || IsTransported(worker)) return false;
            if (WorkerExternalControlBridge.IsControlled(worker)) return false;
            return FindCandidateShip(worker, player, false) != null;
        }

        internal static bool TryBoardNearestBoat(Character worker, ZDO zdo, Player player, out string status)
        {
            status = string.Empty;
            if (worker == null || zdo == null || player == null) return false;
            if (IsTransported(worker))
            {
                status = "Arbeiter sitzt bereits auf einem Boot.";
                return true;
            }
            if (WorkerExternalControlBridge.IsControlled(worker))
            {
                status = "Arbeiter wird gerade extern gesteuert und kann nicht gleichzeitig als Bootspassagier gesetzt werden.";
                return true;
            }

            Transform ship = FindCandidateShip(worker, player, true);
            if (ship == null) return false;

            BuildSeatPoses(ship, SeatScratch);
            if (SeatScratch.Count == 0)
            {
                status = "Boot erkannt, aber kein sicherer Passagierplatz konnte bestimmt werden.";
                return true;
            }

            int seatIndex = FindFreeSeat(ship, SeatScratch.Count);
            if (seatIndex < 0)
            {
                status = "Auf diesem Boot ist aktuell kein Workers-Passagierplatz frei.";
                return true;
            }

            SeatPose seat = SeatScratch[seatIndex];
            PassengerSession session = new PassengerSession
            {
                Worker = worker,
                Ship = ship,
                SeatIndex = seatIndex,
                LocalPosition = seat.Position,
                LocalRotation = seat.Rotation,
                LastWorldPosition = ship.TransformPoint(seat.Position),
                WorkerColliders = worker.GetComponentsInChildren<Collider>(true),
                ShipColliders = ship.GetComponentsInChildren<Collider>(true),
                Body = worker.GetComponent<Rigidbody>()
            };

            ApplyCollisionIgnores(session, true);
            Sessions[worker.GetInstanceID()] = session;
            ProbeByWorker.Remove(worker.GetInstanceID());

            MonsterAI ai = worker.GetComponent<MonsterAI>();
            if (ai != null)
            {
                WorkerNavigationRuntime.SetNativeFollowTarget(ai, null);
                WorkerNavigationRuntime.StopMoving(ai);
            }
            worker.SetMoveDir(Vector3.zero);
            MaintainSession(session);

            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            string shipName = NormalizeObjectName(ship.gameObject.name);
            status = $"{species}: auf {shipName} gesetzt. E am Worker = am Ufer aussteigen.";
            _debugLog?.Invoke($"Boat passenger boarded: worker={species}, ship={shipName}, seat={seatIndex}.");
            return true;
        }

        internal static bool TryDisembark(Character worker, ZDO zdo, Player player, out string status)
        {
            status = string.Empty;
            if (worker == null || zdo == null || player == null) return false;
            if (!Sessions.TryGetValue(worker.GetInstanceID(), out PassengerSession session)) return false;

            Vector3 reference = session.Ship != null ? session.Ship.position : session.LastWorldPosition;
            if (!TryFindSafeShorePoint(player.transform.position, reference, out Vector3 landing))
            {
                status = "Noch kein sicherer Uferpunkt gefunden. Fahr naeher ans Land und versuch E erneut.";
                return true;
            }

            ReleaseSession(worker, session, landing, player, true);
            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            status = $"{species}: vom Boot abgesetzt.";
            _debugLog?.Invoke($"Boat passenger disembarked: worker={species}, landing={landing}.");
            return true;
        }

        internal static string GetHoverText(Character worker)
        {
            if (worker == null) return null;
            return IsTransported(worker)
                ? "[<color=yellow><b>E</b></color>] Vom Boot absetzen\n[<color=yellow><b>Shift+E</b></color>] Arbeiter-Inventar"
                : null;
        }

        /// <summary>Run in LateUpdate so passengers follow the final ship transform for the frame.</summary>
        internal static void Tick()
        {
            if (Sessions.Count == 0) return;

            // v0.5.458: this runs every LateUpdate while a Worker is on a ship.
            // Reuse one key scratch list instead of allocating int[Sessions.Count] every frame.
            SessionKeyScratch.Clear();
            foreach (int key in Sessions.Keys)
                SessionKeyScratch.Add(key);

            for (int i = 0; i < SessionKeyScratch.Count; i++)
            {
                int key = SessionKeyScratch[i];
                if (!Sessions.TryGetValue(key, out PassengerSession session)) continue;
                Character worker = session.Worker;
                if (worker == null || worker.IsDead())
                {
                    ReleaseCollisionIgnores(session);
                    Sessions.Remove(key);
                    continue;
                }

                if (session.Ship == null)
                {
                    Vector3 fallback = session.LastWorldPosition;
                    if (TryFindSafeShorePoint(fallback, fallback, out Vector3 safe)) fallback = safe;
                    ReleaseSession(worker, session, fallback, Player.m_localPlayer, false);
                    continue;
                }

                MaintainSession(session);
            }
        }

        private static void MaintainSession(PassengerSession session)
        {
            if (session?.Worker == null || session.Ship == null) return;

            Vector3 world = session.Ship.TransformPoint(session.LocalPosition);
            Quaternion rotation = session.Ship.rotation * session.LocalRotation;
            session.LastWorldPosition = world;

            Character worker = session.Worker;
            worker.SetMoveDir(Vector3.zero);
            worker.transform.SetPositionAndRotation(world, rotation);

            Rigidbody body = session.Body;
            if (body != null)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }

        private static void ReleaseSession(Character worker, PassengerSession session, Vector3 landing, Player player, bool restoreFollow)
        {
            ReleaseCollisionIgnores(session);
            Sessions.Remove(worker.GetInstanceID());
            ProbeByWorker.Remove(worker.GetInstanceID());

            worker.transform.position = landing;
            worker.SetMoveDir(Vector3.zero);
            Rigidbody body = worker.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            if (!restoreFollow) return;
            ZNetView nview = worker.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (!WorkerZdo.IsWorker(zdo)) return;

            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            MonsterAI ai = worker.GetComponent<MonsterAI>();
            if (state == WorkerState.Follow && ai != null && player != null)
                WorkerNavigationRuntime.SetNativeFollowTarget(ai, player.gameObject);
        }

        private static Transform FindCandidateShip(Character worker, Player player, bool forceRefresh)
        {
            int key = worker.GetInstanceID();
            if (!forceRefresh && ProbeByWorker.TryGetValue(key, out ProbeCache cached) && Time.unscaledTime <= cached.Expires)
            {
                if (cached.Ship != null && IsCandidateDistanceValid(worker, player, cached.Ship)) return cached.Ship;
                return null;
            }

            Transform best = null;
            float bestScore = float.MaxValue;
            SeenShipRoots.Clear();
            int count = Physics.OverlapSphereNonAlloc(player.transform.position, PlayerShipSearchRadius, ShipSearchBuffer,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
            for (int i = 0; i < count; i++)
            {
                Collider collider = ShipSearchBuffer[i];
                ShipSearchBuffer[i] = null;
                if (collider == null) continue;
                Transform ship = FindShipRoot(collider.transform);
                if (ship == null || !SeenShipRoots.Add(ship.GetInstanceID())) continue;
                if (!IsCandidateDistanceValid(worker, player, ship)) continue;

                float score = (worker.transform.position - ship.position).sqrMagnitude +
                              (player.transform.position - ship.position).sqrMagnitude * 0.25f;
                if (score >= bestScore) continue;
                bestScore = score;
                best = ship;
            }
            SeenShipRoots.Clear();

            ProbeByWorker[key] = new ProbeCache { Expires = Time.unscaledTime + ProbeCacheSeconds, Ship = best };
            return best;
        }

        private static bool IsCandidateDistanceValid(Character worker, Player player, Transform ship)
        {
            if (worker == null || player == null || ship == null) return false;
            if ((worker.transform.position - ship.position).sqrMagnitude > WorkerBoardRadius * WorkerBoardRadius) return false;
            if ((player.transform.position - ship.position).sqrMagnitude > PlayerShipSearchRadius * PlayerShipSearchRadius) return false;
            return true;
        }

        private static Transform FindShipRoot(Transform start)
        {
            Transform current = start;
            for (int depth = 0; current != null && depth < 12; depth++, current = current.parent)
            {
                Component[] components = current.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null) continue;
                    Type type = component.GetType();
                    if (type != null && string.Equals(type.Name, "Ship", StringComparison.Ordinal))
                        return current;
                }
            }
            return null;
        }

        private static int FindFreeSeat(Transform ship, int seatCount)
        {
            for (int seat = 0; seat < seatCount; seat++)
            {
                bool occupied = false;
                foreach (PassengerSession session in Sessions.Values)
                {
                    if (session.Ship == ship && session.SeatIndex == seat)
                    {
                        occupied = true;
                        break;
                    }
                }
                if (!occupied) return seat;
            }
            return -1;
        }

        private static void BuildSeatPoses(Transform ship, List<SeatPose> output)
        {
            output.Clear();

            // v0.5.117: Never guess a seat from a transform name. Vanilla ships contain several
            // attach/control/decorative transforms (mast/sail/rudder) whose names may look like
            // seats/attach points. That made Workers snap to the mast. Only real Valheim Chair
            // components are accepted as authoritative passenger anchors.
            Component[] components = ship.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length && output.Count < 10; i++)
            {
                Component component = components[i];
                if (component == null) continue;
                Type type = component.GetType();
                if (type == null || !string.Equals(type.Name, "Chair", StringComparison.Ordinal)) continue;

                try
                {
                    FieldInfo field = type.GetField("m_attachPoint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    Transform attach = field?.GetValue(component) as Transform;
                    if (attach == null || !attach.IsChildOf(ship)) continue;
                    if (IsForbiddenShipAnchor(attach)) continue;

                    Vector3 local = ship.InverseTransformPoint(attach.position);
                    Quaternion rotation = Quaternion.Inverse(ship.rotation) * attach.rotation;
                    if (!IsPlausibleSeatLocalPosition(ship, local)) continue;

                    output.Add(new SeatPose { Position = local, Rotation = rotation });
                }
                catch { }
            }

            // If the vessel exposes no actual Chair anchors (vanilla boats often do not expose
            // passenger benches as Chairs), use deterministic deck-bank positions derived from the
            // hull bounds. This is safer than hierarchy-name heuristics and works for modded ships.
            if (output.Count > 0) return;

            GetShipLocalBounds(ship, out Vector3 min, out Vector3 max);
            float width = Mathf.Max(1.8f, max.x - min.x);
            float length = Mathf.Max(3.0f, max.z - min.z);
            float height = Mathf.Max(0.8f, max.y - min.y);
            float deckY = min.y + height * 0.58f;
            float xOffset = Mathf.Clamp(width * 0.24f, 0.45f, 1.15f);

            string shipName = NormalizeObjectName(ship.gameObject.name).ToLowerInvariant();
            int capacity;
            if (shipName.Contains("raft")) capacity = 2;
            else if (shipName.Contains("karve")) capacity = 4;
            else if (shipName.Contains("viking") || shipName.Contains("long") || shipName.Contains("drakkar")) capacity = 8;
            else capacity = Mathf.Clamp(Mathf.FloorToInt(length / 1.7f) * 2, 2, 8);

            int rows = Mathf.Max(1, (capacity + 1) / 2);
            float zMin = min.z + length * 0.30f;
            float zMax = max.z - length * 0.30f;
            for (int row = 0; row < rows && output.Count < capacity; row++)
            {
                float t = rows <= 1 ? 0.5f : row / (float)(rows - 1);
                float z = Mathf.Lerp(zMin, zMax, t);

                output.Add(new SeatPose
                {
                    Position = new Vector3(-xOffset, deckY, z),
                    Rotation = Quaternion.Euler(0f, 90f, 0f)
                });
                if (output.Count >= capacity) break;
                output.Add(new SeatPose
                {
                    Position = new Vector3(xOffset, deckY, z),
                    Rotation = Quaternion.Euler(0f, -90f, 0f)
                });
            }
        }

        private static bool IsForbiddenShipAnchor(Transform anchor)
        {
            Transform current = anchor;
            for (int depth = 0; current != null && depth < 5; depth++, current = current.parent)
            {
                string n = (current.name ?? string.Empty).ToLowerInvariant();
                if (n.Contains("mast") || n.Contains("sail") || n.Contains("rudder") ||
                    n.Contains("steer") || n.Contains("control") || n.Contains("ladder"))
                    return true;
            }
            return false;
        }

        private static bool IsPlausibleSeatLocalPosition(Transform ship, Vector3 local)
        {
            GetShipLocalBounds(ship, out Vector3 min, out Vector3 max);
            float marginX = Mathf.Max(0.35f, (max.x - min.x) * 0.15f);
            float marginZ = Mathf.Max(0.50f, (max.z - min.z) * 0.10f);
            float lowY = min.y - 0.25f;
            float highY = min.y + (max.y - min.y) * 0.78f;
            return local.x >= min.x + marginX && local.x <= max.x - marginX &&
                   local.z >= min.z + marginZ && local.z <= max.z - marginZ &&
                   local.y >= lowY && local.y <= highY;
        }

        private static void GetShipLocalBounds(Transform ship, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool found = false;

            Collider[] colliders = ship.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || collider.isTrigger) continue;
                Bounds b = collider.bounds;
                Vector3 c = b.center;
                Vector3 e = b.extents;
                for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 local = ship.InverseTransformPoint(c + new Vector3(e.x * x, e.y * y, e.z * z));
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                    found = true;
                }
            }

            if (!found)
            {
                min = new Vector3(-1.5f, -0.5f, -3f);
                max = new Vector3(1.5f, 1.0f, 3f);
            }
        }

        private static bool TryFindSafeShorePoint(Vector3 playerPosition, Vector3 shipPosition, out Vector3 point)
        {
            point = default;
            ZoneSystem zone = ZoneSystem.instance;
            if (zone == null) return false;
            float water = zone.m_waterLevel;

            Vector3 origin = playerPosition;
            if (TryGroundCandidate(origin, water, zone, out point)) return true;

            Vector3 towardPlayer = playerPosition - shipPosition;
            towardPlayer.y = 0f;
            Vector3 baseDir = towardPlayer.sqrMagnitude > 0.01f ? towardPlayer.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, baseDir).normalized;

            float[] radii = { 1.5f, 3f, 5f, 7.5f, 10f, SafeShoreRadius };
            for (int r = 0; r < radii.Length; r++)
            {
                float radius = radii[r];
                Vector3[] dirs =
                {
                    baseDir, -baseDir, side, -side,
                    (baseDir + side).normalized, (baseDir - side).normalized,
                    (-baseDir + side).normalized, (-baseDir - side).normalized
                };
                for (int i = 0; i < dirs.Length; i++)
                {
                    Vector3 candidate = shipPosition + dirs[i] * radius;
                    if (TryGroundCandidate(candidate, water, zone, out point)) return true;
                }
            }
            return false;
        }

        private static bool TryGroundCandidate(Vector3 horizontal, float waterLevel, ZoneSystem zone, out Vector3 point)
        {
            point = default;
            float ground;
            try { ground = zone.GetGroundHeight(horizontal); }
            catch { return false; }
            if (ground <= waterLevel + 0.20f) return false;

            point = new Vector3(horizontal.x, ground + 0.35f, horizontal.z);
            return true;
        }

        private static void ApplyCollisionIgnores(PassengerSession session, bool ignore)
        {
            if (session?.WorkerColliders == null || session.ShipColliders == null) return;
            for (int i = 0; i < session.WorkerColliders.Length; i++)
            {
                Collider a = session.WorkerColliders[i];
                if (a == null) continue;
                for (int j = 0; j < session.ShipColliders.Length; j++)
                {
                    Collider b = session.ShipColliders[j];
                    if (b == null) continue;
                    try { Physics.IgnoreCollision(a, b, ignore); } catch { }
                }
            }
        }

        private static void ReleaseCollisionIgnores(PassengerSession session)
        {
            ApplyCollisionIgnores(session, false);
        }

        private static string NormalizeObjectName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Boot";
            int clone = name.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
            if (clone >= 0) name = name.Substring(0, clone);
            return string.IsNullOrWhiteSpace(name) ? "Boot" : name.Trim();
        }
    }
}
