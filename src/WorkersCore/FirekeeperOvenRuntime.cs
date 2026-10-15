using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Firekeeper support for Valheim's stone oven. The oven is technically a Smelter rather than
    /// a Fireplace, so it needs a tiny adapter. Other production machines remain excluded.
    /// Fuel is restored magically, matching the existing Firekeeper behavior for fireplaces/torches.
    /// The Surtling uses its native fireball as the visible tending action and consumes no item.
    /// </summary>
    internal static class FirekeeperOvenRuntime
    {
        internal const string TargetKind = "firekeeper_oven_fire";

        private static readonly MethodInfo GetFuelMethod = AccessTools.Method(typeof(Smelter), "GetFuel");
        private static readonly Collider[] ScanBuffer = new Collider[256];
        private static readonly HashSet<Smelter> Seen = new HashSet<Smelter>();

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
            out Smelter oven,
            out Vector3 position,
            out string displayName)
        {
            oven = null;
            position = Vector3.zero;
            displayName = "Steinofen";

            Seen.Clear();
            int hits = Physics.OverlapSphereNonAlloc(center, Mathf.Max(3f, radius), ScanBuffer);
            int ovens = 0;
            int needingFuel = 0;
            float bestSqr = float.MaxValue;

            for (int i = 0; i < hits; i++)
            {
                Collider hit = ScanBuffer[i];
                if (hit == null) continue;
                Smelter candidate = hit.GetComponentInParent<Smelter>();
                if (candidate == null || !Seen.Add(candidate) || !IsSupportedOven(candidate)) continue;

                Vector3 p = candidate.transform.position;
                Vector3 flat = p - center;
                flat.y = 0f;
                if (flat.sqrMagnitude > radius * radius) continue;
                ovens++;

                if (!TryReadFuel(candidate, out float fuel)) continue;
                float maxFuel = candidate.m_maxFuel;
                if (maxFuel <= 0.01f || fuel + 0.01f >= maxFuel) continue;
                needingFuel++;

                float sqr = (p - from).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                oven = candidate;
                position = p;
                displayName = GetDisplayName(candidate);
            }

            _lastDebug = $"ovens={ovens}, needingFuel={needingFuel}, selected={(oven != null ? displayName : "none")}";
            return oven != null;
        }

        internal static bool IsFuelUsedByAnyOven(Vector3 center, float radius, string fuelPrefab)
        {
            if (string.IsNullOrEmpty(fuelPrefab)) return false;
            Seen.Clear();
            float scanRadius = Mathf.Max(3f, radius);
            int hits = Physics.OverlapSphereNonAlloc(center, scanRadius, ScanBuffer);
            float radiusSqr = scanRadius * scanRadius;
            for (int i = 0; i < hits; i++)
            {
                Collider hit = ScanBuffer[i];
                if (hit == null) continue;
                Smelter candidate = hit.GetComponentInParent<Smelter>();
                if (candidate == null || !Seen.Add(candidate) || !IsSupportedOven(candidate)) continue;
                Vector3 delta = candidate.transform.position - center; delta.y = 0f;
                if (delta.sqrMagnitude > radiusSqr) continue;
                if (!FirekeeperWorkRuntime.TryGetFuelPrefab(candidate, out string candidateFuel)) continue;
                if (string.Equals(candidateFuel, fuelPrefab, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        internal static bool TryTend(Character worker, ZDO workerZdo)
        {
            if (worker == null || workerZdo == null) return false;
            if (!WorkerZdo.TryReadTarget(workerZdo, out _, out string kind, out Vector3 targetPosition) ||
                !string.Equals(kind, TargetKind, StringComparison.Ordinal)) return false;

            Smelter oven = FindLoadedOven(targetPosition);
            if (oven == null)
            {
                WorkerZdo.ClearTarget(workerZdo);
                workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            bool localFallback = LocalWorkFallbackRuntime.IsActive(workerZdo,
                SpeciesRoleRegistry.NormalizePrefabName(oven.gameObject.name), kind);
            if (!WorkerInteractionRuntime.IsInsideAura(worker.transform.position, oven.transform.position) && !localFallback) return false;
            if (!TryReadFuel(oven, out float fuel) || oven.m_maxFuel <= 0.01f)
            {
                WorkerZdo.ClearTarget(workerZdo);
                workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            float maxFuel = oven.m_maxFuel;
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

            Vector3 aimPoint = GetAimPoint(oven.gameObject);
            FaceTarget(worker, aimPoint);
            if (Time.time < state.NextActionAt) return true;

            if (!state.AimPrimed || (state.AimPoint - aimPoint).sqrMagnitude > 0.04f)
            {
                state.AimPrimed = true;
                state.AimPoint = aimPoint;
                state.AimReadyAt = Time.time + 0.22f;
                return true;
            }

            if (Time.time < state.AimReadyAt) return true;
            FaceTarget(worker, aimPoint);
            FirekeeperProjectileAimRuntime.Arm(worker, aimPoint);

            bool fired = worker.StartAttack(null, false);
            if (!fired)
            {
                state.AimPrimed = false;
                state.NextActionAt = Time.time + 0.65f;
                _lastDebug = $"fireball attack not ready for {GetDisplayName(oven)}; retrying";
                return true;
            }

            state.AimPrimed = false;
            ZNetView view = oven.GetComponent<ZNetView>();
            if (view == null || !view.IsValid())
            {
                state.NextActionAt = Time.time + 0.75f;
                return true;
            }

            float next = Mathf.Min(maxFuel, fuel + 1f);

            // Smelter-derived pieces (including the vanilla stone oven) use their network RPC for fuel.
            // Do not require local ZDO ownership here: InvokeRPC routes the request to the network owner.
            // Firekeeper tending is intentionally magical/resource-free, matching campfires and torches.
            try
            {
                view.InvokeRPC("RPC_AddFuel", Array.Empty<object>());
            }
            catch (Exception ex)
            {
                state.NextActionAt = Time.time + 0.75f;
                _lastDebug = $"stone oven RPC_AddFuel failed: {ex.GetBaseException().Message}";
                return true;
            }

            state.NextActionAt = Time.time + 1.55f;
            WorkerZdo.ClearTarget(workerZdo);
            workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            _lastDebug = $"magically refueled {GetDisplayName(oven)}: {fuel:0.#}->{next:0.#}/{maxFuel:0.#}";
            return true;
        }

        private static bool IsSupportedOven(Smelter smelter)
        {
            if (smelter == null || smelter.gameObject == null || !smelter.gameObject.scene.IsValid() || !smelter.gameObject.activeInHierarchy)
                return false;

            string prefab = SpeciesRoleRegistry.NormalizePrefabName(smelter.gameObject.name);
            if (string.IsNullOrEmpty(prefab)) return false;
            string lower = prefab.ToLowerInvariant();

            // Vanilla stone oven is piece_oven. Name matching intentionally stays narrow so this
            // never turns the Surtling back into a smelter/blast-furnace/charcoal worker.
            return lower == "piece_oven" || lower.Contains("stoneoven") || lower.Contains("stone_oven");
        }

        private static Smelter FindLoadedOven(Vector3 position)
        {
            Seen.Clear();
            int hits = Physics.OverlapSphereNonAlloc(position, 3.5f, ScanBuffer);
            Smelter best = null;
            float bestSqr = 12.25f;
            for (int i = 0; i < hits; i++)
            {
                Collider hit = ScanBuffer[i];
                if (hit == null) continue;
                Smelter oven = hit.GetComponentInParent<Smelter>();
                if (oven == null || !Seen.Add(oven) || !IsSupportedOven(oven)) continue;
                float sqr = (oven.transform.position - position).sqrMagnitude;
                if (sqr > bestSqr) continue;
                bestSqr = sqr;
                best = oven;
            }
            return best;
        }

        private static bool TryReadFuel(Smelter smelter, out float fuel)
        {
            fuel = 0f;
            if (smelter == null) return false;
            try
            {
                ZNetView view = smelter.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo != null)
                {
                    fuel = zdo.GetFloat("fuel", 0f);
                    return true;
                }
            }
            catch { }

            if (GetFuelMethod == null) return false;
            try
            {
                object value = GetFuelMethod.Invoke(smelter, null);
                fuel = Convert.ToSingle(value);
                return true;
            }
            catch { return false; }
        }

        private static Vector3 GetAimPoint(GameObject target)
        {
            if (target == null) return Vector3.zero;
            Bounds? combined = null;

            try
            {
                Collider[] colliders = target.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                {
                    Collider c = colliders[i];
                    if (c == null || !c.enabled) continue;
                    if (!combined.HasValue) combined = c.bounds;
                    else
                    {
                        Bounds b = combined.Value;
                        b.Encapsulate(c.bounds);
                        combined = b;
                    }
                }
            }
            catch { }

            if (combined.HasValue)
            {
                Bounds b = combined.Value;
                return new Vector3(b.center.x, b.center.y + b.extents.y * 0.35f, b.center.z);
            }

            return target.transform.position + Vector3.up * 0.8f;
        }

        private static void FaceTarget(Character worker, Vector3 target)
        {
            if (worker == null) return;
            Vector3 dir = target - worker.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f) return;
            Quaternion rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            worker.transform.rotation = rotation;
        }

        private static string GetDisplayName(Smelter oven)
        {
            if (oven == null) return "Steinofen";
            string prefab = SpeciesRoleRegistry.NormalizePrefabName(oven.gameObject.name);
            return string.IsNullOrEmpty(prefab) ? "Steinofen" : prefab;
        }
    }
}
