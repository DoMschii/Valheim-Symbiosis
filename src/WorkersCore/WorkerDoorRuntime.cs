using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    internal static class WorkerDoorRuntime
    {
        private sealed class DoorState
        {
            internal Door Door;
            internal bool OpenedByWorker;
            internal float OpenedAt;
            internal float LastCloseRequestAt;
            internal Vector3 TravelDirection;
            internal Vector3 DoorPosition;
        }

        private static readonly Dictionary<int, DoorState> Active = new Dictionary<int, DoorState>();
        private static readonly Dictionary<int, float> NextScan = new Dictionary<int, float>();
        private static readonly List<Door> LoadedDoors = new List<Door>(128);
        private static bool _bootstrapped;
        private static float _nextCleanupAt;
        private const BindingFlags DoorMemberFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MethodInfo DoorInteractMethod =
            typeof(Door).GetMethod("Interact", DoorMemberFlags, null, new[] { typeof(Humanoid), typeof(bool), typeof(bool) }, null);
        private static readonly MethodInfo DoorGetStateMethod =
            typeof(Door).GetMethod("GetState", DoorMemberFlags, null, Type.EmptyTypes, null);
        private static readonly FieldInfo DoorStateField =
            typeof(Door).GetField("m_state", DoorMemberFlags) ?? typeof(Door).GetField("m_open", DoorMemberFlags);

        internal static void Register(Door door)
        {
            if (door == null || LoadedDoors.Contains(door)) return;
            LoadedDoors.Add(door);
        }

        /// <summary>
        /// Closes a door that this Worker opened once the Worker has actually cleared the doorway.
        /// This is deliberately callable before the normal role/state early-outs so Follow, combat,
        /// rest, work-area boundaries or a cleared target cannot strand an animal-pen gate open.
        /// Doors that were already open before the Worker arrived are never claimed and therefore
        /// never auto-closed by Workers.
        /// </summary>
        internal static void MaintainOpenedDoor(Character worker)
        {
            Humanoid humanoid = worker as Humanoid;
            if (humanoid == null) return;
            int key = worker.GetInstanceID();
            if (!Active.TryGetValue(key, out DoorState state)) return;

            Door door = state?.Door;
            if (door == null || door.gameObject == null)
            {
                Active.Remove(key);
                return;
            }

            if (!state.OpenedByWorker)
            {
                Active.Remove(key);
                return;
            }

            bool isOpen = IsOpen(door);
            if (!isOpen)
            {
                // Door.Interact is RPC-backed. Keep the lease briefly after opening so an async
                // network state update cannot make us forget the door before it ever reports open.
                if (state.LastCloseRequestAt > 0f || Time.time - state.OpenedAt > 0.75f)
                    Active.Remove(key);
                return;
            }

            Vector3 workerPos = worker.transform.position;
            Vector3 doorPos = door.transform.position;
            float distance = HorizontalDistance(workerPos, doorPos);

            // Primary close condition: the Worker has moved through the doorway in the direction
            // it was travelling when it opened the door. This closes pens much sooner than the old
            // fixed 3.1 m radius while still keeping the door open until the Worker body is clear.
            Vector3 fromDoor = workerPos - state.DoorPosition;
            fromDoor.y = 0f;
            bool passedThrough = state.TravelDirection.sqrMagnitude > 0.01f &&
                                 Vector3.Dot(fromDoor, state.TravelDirection) > 1.45f;

            // Fallback for a Worker that opens a door and then changes target/turns around. Do not
            // leave the pen open forever just because it never crossed the original door plane.
            bool safelyAway = distance > 2.15f;
            bool timeoutAway = Time.time - state.OpenedAt > 2.0f && distance > 1.65f;

            if (!passedThrough && !safelyAway && !timeoutAway) return;

            // Do not fire the RPC every AI tick while waiting for the replicated door state. If
            // one close request fails/stalls we retry, but we never drop the lease while it is
            // still visibly open. This is the important pen-safety guarantee.
            if (state.LastCloseRequestAt > 0f && Time.time - state.LastCloseRequestAt < 0.75f) return;
            Toggle(door, humanoid);
            state.LastCloseRequestAt = Time.time;
        }

        internal static void Tick(Character worker, ZDO zdo)
        {
            Humanoid humanoid = worker as Humanoid;
            if (humanoid == null || zdo == null) return;
            int key = worker.GetInstanceID();

            MaintainOpenedDoor(worker);

            if (Time.time < (NextScan.TryGetValue(key, out float next) ? next : 0f)) return;
            NextScan[key] = Time.time + 0.25f;
            if (Active.ContainsKey(key)) return;

            if (!WorkerZdo.TryReadTarget(zdo, out _, out _, out Vector3 target)) return;
            Vector3 travel = target - worker.transform.position;
            travel.y = 0f;
            if (travel.sqrMagnitude < 0.1f) return;
            travel.Normalize();

            // v0.5.91: door discovery is lifecycle-indexed. The old FindObjectsOfType<Door>()
            // allocated and traversed the complete loaded scene whenever a moving Worker checked
            // for a nearby door, which scaled very badly in large bases.
            EnsureBootstrapped();
            CleanupIfDue();

            Door best = null;
            float bestDistance = 2.3f;
            for (int i = 0; i < LoadedDoors.Count; i++)
            {
                Door door = LoadedDoors[i];
                if (door == null || door.gameObject == null || !door.gameObject.activeInHierarchy) continue;
                float distance = HorizontalDistance(worker.transform.position, door.transform.position);
                if (distance >= bestDistance) continue;
                Vector3 toDoor = door.transform.position - worker.transform.position;
                toDoor.y = 0f;
                if (toDoor.sqrMagnitude < 0.01f) continue;
                if (Vector3.Dot(travel, toDoor.normalized) < 0.20f) continue;
                if (!IsInsideAssignedArea(zdo, door.transform.position)) continue;
                best = door;
                bestDistance = distance;
            }

            if (best == null || IsOpen(best)) return;
            if (Toggle(best, humanoid))
            {
                Active[key] = new DoorState
                {
                    Door = best,
                    OpenedByWorker = true,
                    OpenedAt = Time.time,
                    TravelDirection = travel,
                    DoorPosition = best.transform.position
                };
            }
        }

        internal static void Clear()
        {
            Active.Clear();
            NextScan.Clear();
            LoadedDoors.Clear();
            _bootstrapped = false;
            _nextCleanupAt = 0f;
        }

        private static void EnsureBootstrapped()
        {
            if (_bootstrapped) return;
            _bootstrapped = true;
            try
            {
                Door[] loaded = UnityEngine.Object.FindObjectsOfType<Door>();
                if (loaded == null) return;
                for (int i = 0; i < loaded.Length; i++) Register(loaded[i]);
            }
            catch { }
        }

        private static void CleanupIfDue()
        {
            if (Time.unscaledTime < _nextCleanupAt) return;
            _nextCleanupAt = Time.unscaledTime + 60f;
            for (int i = LoadedDoors.Count - 1; i >= 0; i--)
            {
                Door door = LoadedDoors[i];
                if (door == null || door.gameObject == null)
                    LoadedDoors.RemoveAt(i);
            }
        }

        private static bool Toggle(Door door, Humanoid user)
        {
            try
            {
                if (DoorInteractMethod == null) return false;
                object result = DoorInteractMethod.Invoke(door, new object[] { user, false, false });
                return !(result is bool b) || b;
            }
            catch { return false; }
        }

        private static bool IsOpen(Door door)
        {
            if (door == null) return false;
            try
            {
                if (DoorGetStateMethod != null)
                {
                    object value = DoorGetStateMethod.Invoke(door, null);
                    if (value is int i) return i != 0;
                    if (value is bool b) return b;
                }
                if (DoorStateField != null)
                {
                    object value = DoorStateField.GetValue(door);
                    if (value is int i) return i != 0;
                    if (value is bool b) return b;
                }
            }
            catch { }
            return false;
        }

        private static bool IsInsideAssignedArea(ZDO zdo, Vector3 position)
        {
            if (!WorkerZdo.TryReadWorkArea(zdo, out _, out Vector3 center, out float radius)) return true;
            Vector3 d = position - center;
            d.y = 0f;
            float r = Mathf.Max(5f, radius) + 2f;
            return d.sqrMagnitude <= r * r;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }

    [HarmonyPatch(typeof(Door), "Awake")]
    internal static class WorkerDoorIndexPatch
    {
        private static void Postfix(Door __instance)
        {
            WorkerDoorRuntime.Register(__instance);
        }
    }
}
