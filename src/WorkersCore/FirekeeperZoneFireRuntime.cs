using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Unassigned Firekeeper mode: patrol real Fireplace components inside the Workzone and restore
    /// their vanilla fuel one unit at a time. No Wood/Resin/Coal item is consumed or created.
    /// </summary>
    internal static class FirekeeperZoneFireRuntime
    {
        internal const string ZoneFireTargetKind = "firekeeper_zone_fire";

        private static readonly Collider[] ScanBuffer = new Collider[1024];
        private static readonly HashSet<Fireplace> Seen = new HashSet<Fireplace>();
        private static readonly List<Fireplace> LoadedFires = new List<Fireplace>(256);
        private static bool _fireIndexBootstrapped;
        private static float _nextFireIndexCleanupAt;
        private static readonly MethodInfo SetFuelMethod = typeof(Fireplace).GetMethod("SetFuel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(float) }, null);
        private static readonly FieldInfo MaxFuelField = typeof(Fireplace).GetField("m_maxFuel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private sealed class TendState
        {
            internal float NextActionAt;
            internal bool AimPrimed;
            internal float AimReadyAt;
            internal Vector3 AimPoint;
        }

        private static readonly Dictionary<int, TendState> States = new Dictionary<int, TendState>();
        private static string _lastDebug = "not scanned";
        internal static string GetDebugStatus() => _lastDebug;

        internal static bool TryFindNearestNeedingFuel(
            Vector3 center,
            float radius,
            Vector3 from,
            out Fireplace fireplace,
            out Vector3 position,
            out string displayName)
        {
            fireplace = null;
            position = Vector3.zero;
            displayName = "Feuer";
            Seen.Clear();

            int fires = 0;
            int needingFuel = 0;
            float bestSqr = float.MaxValue;

            // Lifecycle-indexed discovery: avoid Resources.FindObjectsOfTypeAll<Fireplace>() on
            // every Firekeeper decision. The index is bootstrapped once and then maintained from
            // Fireplace.Awake, which keeps patrol scans proportional to actual fires instead of
            // the entire loaded Unity object graph.
            EnsureFireIndex();
            CleanupFireIndexIfDue();

            for (int i = 0; i < LoadedFires.Count; i++)
            {
                Fireplace candidate = LoadedFires[i];
                if (candidate == null || !Seen.Add(candidate)) continue;
                GameObject candidateObject = candidate.gameObject;
                if (candidateObject == null || !candidateObject.scene.IsValid() || !candidateObject.activeInHierarchy) continue;

                Vector3 p = candidate.transform.position;
                Vector3 flat = p - center;
                flat.y = 0f;
                if (flat.sqrMagnitude > radius * radius) continue;
                fires++;

                // The Surtling HomeAnchor is already its permanent/infinite home fire and must never
                // become a roaming patrol target. Otherwise the Firekeeper can keep selecting its
                // own campfire and starve real work such as torches/hearths/the stone oven.
                if (IsSurtlingHomeFire(candidate)) continue;

                // A fire explicitly bound to a Firekeeper is exclusive to that worker and already
                // receives infinite fuel through FirekeeperAssignedFireRuntime.
                if (FirekeeperAssignedFireRuntime.IsManaged(candidate)) continue;
                if (!TryReadFuel(candidate, out float fuel) || !TryReadMaxFuel(candidate, out float maxFuel)) continue;
                if (maxFuel <= 0.01f || fuel + 0.01f >= maxFuel) continue;
                needingFuel++;

                float sqr = (p - from).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                fireplace = candidate;
                position = p;
                displayName = GetDisplayName(candidate);
            }

            _lastDebug = $"fires={fires}, needingFuel={needingFuel}, selected={(fireplace != null ? displayName : "none")}";
            return fireplace != null;
        }

        internal static bool TryTend(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null) return false;
            if (!WorkerZdo.TryReadTarget(workerZdo, out _, out string kind, out Vector3 targetPosition) ||
                !string.Equals(kind, ZoneFireTargetKind, StringComparison.Ordinal)) return false;

            Fireplace fire = FindLoadedFire(targetPosition);
            if (fire == null || IsSurtlingHomeFire(fire) || FirekeeperAssignedFireRuntime.IsManaged(fire))
            {
                WorkerZdo.ClearTarget(workerZdo);
                workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            bool localFallback = LocalWorkFallbackRuntime.IsActive(workerZdo,
                SpeciesRoleRegistry.NormalizePrefabName(fire.gameObject.name), kind);
            if (!WorkerInteractionRuntime.IsInsideAura(worker.transform.position, fire.transform.position) && !localFallback) return false;
            if (!TryReadFuel(fire, out float fuel) || !TryReadMaxFuel(fire, out float maxFuel) || maxFuel <= 0.01f)
            {
                WorkerZdo.ClearTarget(workerZdo);
                workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            if (fuel + 0.01f >= maxFuel)
            {
                WorkerZdo.ClearTarget(workerZdo);
                workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            int key = worker.GetInstanceID();
            if (!States.TryGetValue(key, out TendState state))
            {
                state = new TendState();
                States[key] = state;
            }
            Vector3 exactAimPoint = GetFireAimPoint(fire);
            FaceFire(worker, exactAimPoint);

            if (Time.time < state.NextActionAt) return true;

            // Prime the exact target briefly before starting the visual cast. This prevents the
            // Surtling animator from consuming a stale combat/look direction from a previous target.
            if (!state.AimPrimed || (state.AimPoint - exactAimPoint).sqrMagnitude > 0.04f)
            {
                state.AimPrimed = true;
                state.AimPoint = exactAimPoint;
                state.AimReadyAt = Time.time + 0.22f;
                return true;
            }

            if (Time.time < state.AimReadyAt) return true;

            FaceFire(worker, exactAimPoint);

            // Arm the projectile redirect before starting the native Surtling attack. The actual
            // fireball is created slightly later by an animation event; FirekeeperProjectileAimRuntime
            // catches its Projectile.Setup and replaces only the trajectory with spawn->selected fire.
            FirekeeperProjectileAimRuntime.Arm(worker, exactAimPoint);

            bool fired = worker.StartAttack(null, false);
            if (!fired)
            {
                state.AimPrimed = false;
                state.NextActionAt = Time.time + 0.65f;
                _lastDebug = $"fireball attack not ready for {GetDisplayName(fire)}; retrying";
                return true;
            }

            state.AimPrimed = false;

            ZNetView view = fire.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || !view.IsOwner())
            {
                state.NextActionAt = Time.time + 0.75f;
                return true;
            }

            float next = Mathf.Min(maxFuel, fuel + 1f);
            if (!TryWriteFuel(fire, next))
            {
                state.NextActionAt = Time.time + 0.75f;
                return true;
            }

            // Roughly one visible fire-cast per restored fuel unit. Re-scan after every unit so the
            // nearest/most urgent work can change naturally and a maxed fire is immediately released.
            state.NextActionAt = Time.time + 1.55f;
            WorkerZdo.ClearTarget(workerZdo);
            workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            _lastDebug = $"tended {GetDisplayName(fire)}: {fuel:0.#}->{next:0.#}/{maxFuel:0.#}";
            return true;
        }


        private static bool IsSurtlingHomeFire(Fireplace fireplace)
        {
            if (fireplace == null) return false;
            try
            {
                Transform current = fireplace.transform;
                while (current != null)
                {
                    string prefab = SpeciesRoleRegistry.NormalizePrefabName(current.gameObject.name);
                    if (string.Equals(prefab, SurtlingHomePieceRuntime.PrefabName, StringComparison.OrdinalIgnoreCase))
                        return true;
                    current = current.parent;
                }
            }
            catch { }
            return false;
        }

        private static Fireplace FindLoadedFire(Vector3 position)
        {
            EnsureFireIndex();
            CleanupFireIndexIfDue();
            if (LoadedFires.Count == 0) return null;

            Fireplace best = null;
            float bestSqr = 9f; // same 3 m tolerance as the old overlap search
            for (int i = 0; i < LoadedFires.Count; i++)
            {
                Fireplace fire = LoadedFires[i];
                if (fire == null) continue;
                GameObject fireObject = fire.gameObject;
                if (fireObject == null || !fireObject.scene.IsValid() || !fireObject.activeInHierarchy) continue;
                float sqr = (fire.transform.position - position).sqrMagnitude;
                if (sqr > bestSqr) continue;
                bestSqr = sqr;
                best = fire;
            }
            return best;
        }

        private static bool TryReadFuel(Fireplace fireplace, out float fuel)
        {
            fuel = 0f;
            if (fireplace == null) return false;

            // Fireplace fuel is persisted in the ZDO under the vanilla "fuel" key. Reading the
            // ZDO directly is more reliable for torches than reflecting GetFuel(), which can report
            // a stale/full value depending on ownership/update timing.
            try
            {
                ZNetView view = fireplace.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo != null)
                {
                    fuel = zdo.GetFloat("fuel", 0f);
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool TryReadMaxFuel(Fireplace fireplace, out float maxFuel)
        {
            maxFuel = 0f;
            if (fireplace == null || MaxFuelField == null) return false;
            try
            {
                maxFuel = Convert.ToSingle(MaxFuelField.GetValue(fireplace));
                return true;
            }
            catch { return false; }
        }

        private static bool TryWriteFuel(Fireplace fireplace, float fuel)
        {
            if (fireplace == null) return false;

            // Write the same vanilla ZDO key used by Fireplace itself. This is the authoritative
            // persisted value for campfires and torches and avoids SetFuel ownership/version quirks.
            try
            {
                ZNetView view = fireplace.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo != null && view.IsOwner())
                {
                    zdo.Set("fuel", fuel);
                    return true;
                }
            }
            catch { }

            if (SetFuelMethod == null) return false;
            try
            {
                SetFuelMethod.Invoke(fireplace, new object[] { fuel });
                return true;
            }
            catch { return false; }
        }

        private static Vector3 GetFireAimPoint(Fireplace fire)
        {
            if (fire == null) return Vector3.zero;

            // For torches, renderer bounds can include flame/smoke particles and therefore point
            // well above or beside the actual hitable torch. Prefer the physical collider body.
            try
            {
                Collider[] colliders = fire.GetComponentsInChildren<Collider>(true);
                Collider best = null;
                float bestVolume = 0f;
                if (colliders != null)
                {
                    for (int i = 0; i < colliders.Length; i++)
                    {
                        Collider c = colliders[i];
                        if (c == null || !c.enabled || c.isTrigger) continue;
                        Vector3 size = c.bounds.size;
                        float volume = Mathf.Max(0.001f, size.x * size.y * size.z);
                        if (volume <= bestVolume) continue;
                        bestVolume = volume;
                        best = c;
                    }
                }
                if (best != null) return best.bounds.center;
            }
            catch { }

            // Fallback to non-particle renderer bounds if the piece has no useful collider.
            try
            {
                Renderer[] renderers = fire.GetComponentsInChildren<Renderer>(true);
                if (renderers != null && renderers.Length > 0)
                {
                    bool hasBounds = false;
                    Bounds bounds = default;
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        Renderer renderer = renderers[i];
                        if (renderer == null || !renderer.enabled) continue;
                        // Avoid a hard reference to UnityEngine.ParticleSystemModule. Some Valheim
                        // setups do not reference that module in this project, so identify particle
                        // renderers by runtime type name instead.
                        if (string.Equals(renderer.GetType().Name, "ParticleSystemRenderer", StringComparison.Ordinal)) continue;
                        if (!hasBounds) { bounds = renderer.bounds; hasBounds = true; }
                        else bounds.Encapsulate(renderer.bounds);
                    }
                    if (hasBounds) return bounds.center;
                }
            }
            catch { }

            return fire.transform.position + Vector3.up * 0.35f;
        }

        private static void FaceFire(Character worker, Vector3 exactAimPoint)
        {
            if (worker == null) return;
            Vector3 flat = exactAimPoint - worker.transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.01f)
                worker.transform.rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);

            Vector3 eye = worker.transform.position + Vector3.up * 1.05f;
            Vector3 aim = exactAimPoint - eye;
            if (aim.sqrMagnitude > 0.01f) worker.SetLookDir(aim.normalized, 0f);

            // Never allow an old hostile AI target to steer the work animation away from the
            // fireplace that is currently selected by the Firekeeper job.
            WorkerCombatStanceRuntime.ClearWorkAttackTargets(worker.GetComponent<MonsterAI>());
        }



        internal static void RegisterLoadedFire(Fireplace fire)
        {
            if (fire == null || LoadedFires.Contains(fire)) return;
            LoadedFires.Add(fire);
        }

        internal static void ClearIndex()
        {
            LoadedFires.Clear();
            Seen.Clear();
            _fireIndexBootstrapped = false;
            _nextFireIndexCleanupAt = 0f;
        }

        private static void EnsureFireIndex()
        {
            if (_fireIndexBootstrapped) return;
            _fireIndexBootstrapped = true;
            try
            {
                Fireplace[] loaded = Resources.FindObjectsOfTypeAll<Fireplace>();
                if (loaded == null) return;
                for (int i = 0; i < loaded.Length; i++)
                {
                    Fireplace fire = loaded[i];
                    if (fire == null || fire.gameObject == null || !fire.gameObject.scene.IsValid() || !fire.gameObject.activeInHierarchy) continue;
                    RegisterLoadedFire(fire);
                }
            }
            catch { }
        }

        private static void CleanupFireIndexIfDue()
        {
            if (Time.unscaledTime < _nextFireIndexCleanupAt) return;
            _nextFireIndexCleanupAt = Time.unscaledTime + 60f;
            for (int i = LoadedFires.Count - 1; i >= 0; i--)
            {
                Fireplace fire = LoadedFires[i];
                if (fire == null || fire.gameObject == null)
                    LoadedFires.RemoveAt(i);
            }
        }

        private static string GetDisplayName(Fireplace fire)
        {
            if (fire == null) return "Feuer";
            string prefab = SpeciesRoleRegistry.NormalizePrefabName(fire.gameObject.name);
            return string.IsNullOrEmpty(prefab) ? "Feuer" : prefab;
        }
    }

    [HarmonyPatch(typeof(Fireplace), "Awake")]
    internal static class FirekeeperFireplaceIndexPatch
    {
        private static void Postfix(Fireplace __instance)
        {
            FirekeeperZoneFireRuntime.RegisterLoadedFire(__instance);
        }
    }
}
