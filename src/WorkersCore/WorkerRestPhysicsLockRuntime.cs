using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Final render-frame lock for Workers that are already resting on an authored RestPoint.
    /// This intentionally does not use a Harmony Character.FixedUpdate patch: current Valheim builds
    /// do not expose that method on Character, which caused PatchAll() to fail during plugin startup.
    /// WorkerNavigationRuntime still owns all travel/pathfinding; this only stabilizes the final pose
    /// after Valheim has completed normal movement/physics for the frame.
    /// </summary>
    internal static class WorkerRestPhysicsLockRuntime
    {
        private const float ActiveLockRefreshSeconds = 0.25f;

        private struct RestLockEntry
        {
            internal Character Worker;
            internal ZNetView View;
            internal ZDO Zdo;
            internal Transform RestPoint;
            internal Rigidbody Body;
            internal bool IsTroll;
        }

        private static readonly List<Character> KnownWorkers = new List<Character>(32);
        private static readonly List<RestLockEntry> ActiveLocks = new List<RestLockEntry>(16);
        private static float _nextActiveLockRefresh;

        // Trolls use their native standing idle at the Lehnplatz. Character/MonsterAI still updates
        // the locomotion blend tree even when Workers owns the final transform. Tiny residual
        // forward/side/turn values are enough to make only the legs visibly tremble while the body
        // itself stays perfectly locked. Cache the native locomotion float parameters once and pin
        // only those values to zero while the Troll is in a real home/rest state.
        private sealed class TrollAnimatorBinding
        {
            internal Animator Animator;
            internal Component ZSync;
            internal string[] LocomotionFloats = Array.Empty<string>();
            internal MethodInfo ZSyncSetFloat;
            internal readonly object[] SetFloatArgs = new object[2];
        }

        private static readonly object BoxedZeroFloat = 0f;
        private static readonly Dictionary<int, TrollAnimatorBinding> TrollAnimatorBindings = new Dictionary<int, TrollAnimatorBinding>();

        internal static void TickLate()
        {
            float now = Time.unscaledTime;
            if (now >= _nextActiveLockRefresh)
            {
                _nextActiveLockRefresh = now + ActiveLockRefreshSeconds;
                RefreshActiveLocks();
            }

            // v0.5.458: final-frame pose locking still runs every rendered frame, but only for
            // Workers that were already classified as resting. The old implementation copied and
            // inspected every loaded Worker, resolved its rest station and fetched its Rigidbody
            // on every LateUpdate.
            for (int i = 0; i < ActiveLocks.Count; i++)
            {
                RestLockEntry entry = ActiveLocks[i];
                Character worker = entry.Worker;
                ZNetView nview = entry.View;
                ZDO zdo = entry.Zdo;
                Transform rest = entry.RestPoint;

                if (worker == null || worker.IsDead() || nview == null || !nview.IsValid() ||
                    !nview.IsOwner() || zdo == null || rest == null)
                    continue;

                // State is intentionally checked every frame so a Worker leaving rest is released
                // immediately; only the expensive discovery/classification work is quarter-second.
                WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
                if (!IsRestLockState(state)) continue;

                Vector3 targetPosition = rest.position;
                Vector3 forward = Vector3.ProjectOnPlane(rest.forward, Vector3.up);
                bool hasForward = forward.sqrMagnitude > 0.0001f;
                Quaternion targetRotation = hasForward
                    ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                    : worker.transform.rotation;

                Rigidbody body = entry.Body;
                if (body != null)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    body.position = targetPosition;
                    if (hasForward) body.rotation = targetRotation;
                }

                worker.transform.position = targetPosition;
                if (hasForward)
                {
                    worker.transform.rotation = targetRotation;
                    try { worker.SetLookDir(forward.normalized, 0f); }
                    catch { }
                }

                worker.SetMoveDir(Vector3.zero);
                worker.SetRun(false);

                if (entry.IsTroll)
                    StabilizeTrollIdleAnimator(worker);
            }
        }

        private static void RefreshActiveLocks()
        {
            ActiveLocks.Clear();
            WorkerFastLookupRuntime.CopyKnownWorkers(KnownWorkers);

            float maxHomeDistance = WorkerRestRuntime.HomeRestDistance + 2.5f;
            float maxHomeDistanceSqr = maxHomeDistance * maxHomeDistance;

            for (int i = 0; i < KnownWorkers.Count; i++)
            {
                Character worker = KnownWorkers[i];
                if (worker == null || worker is Player || worker.IsDead()) continue;

                if (!WorkerFastLookupRuntime.TryGetWorker(worker, true, out ZNetView nview, out ZDO zdo))
                    continue;
                if (nview == null || zdo == null) continue;

                WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
                if (!IsRestLockState(state)) continue;

                // Never pull a travelling Worker to furniture. Navigation must first bring the
                // creature into the existing home/rest radius.
                Vector3 home = WorkerZdo.ReadHome(zdo, worker.transform.position);
                if ((worker.transform.position - home).sqrMagnitude > maxHomeDistanceSqr)
                    continue;

                WorkerRestStation station = WorkerRestStation.ResolveLoadedForWorker(zdo);
                Transform rest = station != null ? station.RestPoint : null;
                if (rest == null) continue;

                string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
                ActiveLocks.Add(new RestLockEntry
                {
                    Worker = worker,
                    View = nview,
                    Zdo = zdo,
                    RestPoint = rest,
                    Body = worker.GetComponent<Rigidbody>(),
                    IsTroll = string.Equals(species, "Troll", StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        private static bool IsRestLockState(WorkerState state)
        {
            return state == WorkerState.Resting ||
                   state == WorkerState.Recover ||
                   state == WorkerState.WaitAtHome;
        }

        internal static void Clear()
        {
            KnownWorkers.Clear();
            ActiveLocks.Clear();
            TrollAnimatorBindings.Clear();
            _nextActiveLockRefresh = 0f;
        }

        private static void StabilizeTrollIdleAnimator(Character worker)
        {
            if (worker == null) return;
            int key = worker.GetInstanceID();
            if (!TrollAnimatorBindings.TryGetValue(key, out TrollAnimatorBinding binding) ||
                binding == null || binding.Animator == null)
            {
                binding = DiscoverTrollAnimator(worker);
                TrollAnimatorBindings[key] = binding;
            }
            if (binding == null || binding.Animator == null || binding.LocomotionFloats == null) return;

            for (int i = 0; i < binding.LocomotionFloats.Length; i++)
            {
                string parameter = binding.LocomotionFloats[i];
                if (string.IsNullOrEmpty(parameter)) continue;

                // Write through ZSyncAnimation first so the synchronized/native animation layer sees
                // the same stationary value, then mirror directly to Animator for this rendered frame.
                try
                {
                    if (binding.ZSync != null && binding.ZSyncSetFloat != null)
                    {
                        binding.SetFloatArgs[0] = parameter;
                        binding.SetFloatArgs[1] = BoxedZeroFloat;
                        binding.ZSyncSetFloat.Invoke(binding.ZSync, binding.SetFloatArgs);
                    }
                }
                catch { }

                try { binding.Animator.SetFloat(parameter, 0f); }
                catch { }
            }
        }

        private static TrollAnimatorBinding DiscoverTrollAnimator(Character worker)
        {
            Animator animator = null;
            try { animator = worker.GetComponentInChildren<Animator>(true); }
            catch { }
            if (animator == null) return null;

            Component zsync = null;
            try
            {
                Component[] all = worker.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    Component candidate = all[i];
                    if (candidate != null && string.Equals(candidate.GetType().Name, "ZSyncAnimation", StringComparison.Ordinal))
                    {
                        zsync = candidate;
                        break;
                    }
                }
            }
            catch { }

            MethodInfo setFloat = null;
            try
            {
                setFloat = zsync?.GetType().GetMethod(
                    "SetFloat",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(string), typeof(float) },
                    null);
            }
            catch { }

            List<string> locomotion = new List<string>(4);
            try
            {
                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    AnimatorControllerParameter p = parameters[i];
                    if (p.type != AnimatorControllerParameterType.Float) continue;
                    string name = p.name ?? string.Empty;
                    string lower = name.ToLowerInvariant();

                    // Valheim's standard creature controller uses forward_speed, sideway_speed and
                    // turn_speed. Accept small naming variations but deliberately reject combat,
                    // attack and generic timing/speed multipliers.
                    bool isLocomotion =
                        lower == "forward_speed" || lower == "forwardspeed" ||
                        lower == "sideway_speed" || lower == "sideways_speed" || lower == "sidewayspeed" ||
                        lower == "turn_speed" || lower == "turnspeed" ||
                        lower == "move_speed" || lower == "movespeed" ||
                        lower == "movement_speed" || lower == "movementspeed";
                    if (isLocomotion) locomotion.Add(name);
                }
            }
            catch { }

            return new TrollAnimatorBinding
            {
                Animator = animator,
                ZSync = zsync,
                ZSyncSetFloat = setFloat,
                LocomotionFloats = locomotion.ToArray()
            };
        }
    }
}
