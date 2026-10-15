using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal readonly struct WorkAssignmentOption
    {
        internal readonly WorkFlagInteractable Flag;
        internal readonly string Id;
        internal readonly string DisplayName;
        internal readonly WorkOrder Order;
        internal readonly Vector3 Center;
        internal readonly float Radius;
        internal readonly float Distance;

        internal WorkAssignmentOption(WorkFlagInteractable flag, string id, string displayName, WorkOrder order, Vector3 center, float radius, float distance)
        {
            Flag = flag;
            Id = id ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Order = order;
            Center = center;
            Radius = radius;
            Distance = distance;
        }
    }

    internal readonly struct WorkAreaSnapshot
    {
        internal readonly WorkFlagInteractable Flag;
        internal readonly string Id;
        internal readonly int OrdersMask;
        internal readonly string DisplayName;
        internal readonly WorkOrder Order;
        internal readonly bool Enabled;
        internal readonly Vector3 Center;
        internal readonly float Radius;
        internal readonly float Distance;

        internal WorkAreaSnapshot(WorkFlagInteractable flag, string id, string displayName, int ordersMask, WorkOrder order, bool enabled, Vector3 center, float radius, float distance)
        {
            Flag = flag;
            Id = id ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            OrdersMask = ordersMask;
            Order = order;
            Enabled = enabled;
            Center = center;
            Radius = radius;
            Distance = distance;
        }

        internal bool TryResolveOrder(WorkerRole role, out WorkOrder order)
        {
            return WorkOrderCatalog.TryResolveForRole(OrdersMask, role, out order);
        }
    }

    internal static class WorkFlagRegistry
    {
        private static readonly HashSet<WorkFlagInteractable> Flags = new HashSet<WorkFlagInteractable>();
        private static readonly List<WorkFlagInteractable> Cleanup = new List<WorkFlagInteractable>();
        // ZDO-backed registry survives unloaded scene objects and is used by background jobs.
        private static readonly Dictionary<string, ZDO> PersistentFlags = new Dictionary<string, ZDO>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ZDO> PersistentFlagWorldScan = new Dictionary<string, ZDO>(StringComparer.Ordinal);
        private static readonly HashSet<string> PersistentFlagWorldRejectedIds = new HashSet<string>(StringComparer.Ordinal);
        private static readonly MethodInfo ZNetSceneGetPrefabByHashMethod = typeof(ZNetScene).GetMethod(
            "GetPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
        private const float PersistentWorldIndexScanSeconds = 30f;
        private static float _nextPersistentWorldIndexScan;

        internal static int LastPersistentWorldIndexRejectedCount => PersistentFlagWorldRejectedIds.Count;

        internal static int LoadedCount => Flags.Count;

        internal static void RememberPersistent(ZDO zdo)
        {
            if (zdo == null) return;
            string id = WorkFlagZdo.ReadId(zdo);
            if (string.IsNullOrEmpty(id)) return;
            PersistentFlags[id] = zdo;
        }

        private static readonly Dictionary<string, float> NextPersistentLookup = new Dictionary<string, float>(StringComparer.Ordinal);
        private const float PersistentLookupRetrySeconds = 5f;

        internal static bool TryGetPersistentState(string id, out ZDO zdo)
        {
            zdo = null;
            if (string.IsNullOrEmpty(id)) return false;
            if (PersistentFlags.TryGetValue(id, out zdo) && zdo != null) return true;

            PersistentFlags.Remove(id);
            zdo = null;

            // A remote background worker can be discovered from its persistent Worker ZDO before
            // the corresponding WorkFlag GameObject has ever been loaded in this session. Older
            // builds only populated PersistentFlags from loaded flags, so the background Sorter
            // stopped at sorting:no-ledger-flag until the player visited Base. Recover the exact
            // flag by its durable vw_workflag_id directly from ZDOMan. This lookup is strongly
            // throttled per missing id and is therefore never a per-frame world scan.
            float now = Time.realtimeSinceStartup;
            if (NextPersistentLookup.TryGetValue(id, out float next) && now < next) return false;
            NextPersistentLookup[id] = now + PersistentLookupRetrySeconds;

            if (!TryRecoverPersistentFlag(id, out zdo) || zdo == null) return false;
            PersistentFlags[id] = zdo;
            NextPersistentLookup.Remove(id);
            BackgroundMonitorRuntime.Note($"Recovered persistent WorkFlag {id} for unloaded worker simulation.");
            return true;
        }

        private static bool TryRecoverPersistentFlag(string id, out ZDO found)
        {
            found = null;
            if (string.IsNullOrEmpty(id) || ZDOMan.instance == null) return false;

            try
            {
                FieldInfo[] fields = typeof(ZDOMan).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length && found == null; i++)
                {
                    object value;
                    try { value = fields[i].GetValue(ZDOMan.instance); }
                    catch { continue; }
                    TryFindFlagInObject(value, id, 0, ref found);
                }
            }
            catch
            {
                found = null;
            }

            return found != null;
        }

        private static void TryFindFlagInObject(object value, string id, int depth, ref ZDO found)
        {
            if (found != null || value == null || depth > 2) return;
            if (value is ZDO single)
            {
                if (string.Equals(WorkFlagZdo.ReadId(single), id, StringComparison.Ordinal)) found = single;
                return;
            }

            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (found != null) break;
                    if (entry.Value is ZDO zdo)
                    {
                        if (string.Equals(WorkFlagZdo.ReadId(zdo), id, StringComparison.Ordinal)) found = zdo;
                    }
                    else if (depth < 2) TryFindFlagInObject(entry.Value, id, depth + 1, ref found);
                }
                return;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                int guard = 0;
                foreach (object item in enumerable)
                {
                    if (found != null || ++guard > 200000) break;
                    if (item is ZDO zdo)
                    {
                        if (string.Equals(WorkFlagZdo.ReadId(zdo), id, StringComparison.Ordinal)) found = zdo;
                    }
                    else if (depth < 2) TryFindFlagInObject(item, id, depth + 1, ref found);
                }
            }
        }



        /// <summary>
        /// v0.5.364: resolve a position against the already-known persistent WorkFlag index without
        /// triggering a world/ZDO scan. Used by the economy logistics-flow tracker to attribute a
        /// completed transport to the Workzone that actually supplied the cargo.
        /// </summary>
        internal static bool TryFindPersistentContainingArea(Vector3 position, float defaultRadius, out string areaId)
        {
            areaId = string.Empty;
            float bestSqr = float.MaxValue;

            foreach (KeyValuePair<string, ZDO> pair in PersistentFlags)
            {
                ZDO zdo = pair.Value;
                if (zdo == null || string.IsNullOrWhiteSpace(pair.Key)) continue;
                if (!WorkFlagZdo.IsEnabled(zdo)) continue;

                Vector3 delta = zdo.GetPosition() - position;
                delta.y = 0f;
                float sqr = delta.sqrMagnitude;
                float radius = Mathf.Max(4f, WorkFlagZdo.ReadRadius(zdo, defaultRadius));
                float allowed = radius + 4f;
                if (sqr > allowed * allowed || sqr >= bestSqr) continue;

                bestSqr = sqr;
                areaId = pair.Key;
            }

            if (!string.IsNullOrWhiteSpace(areaId)) return true;

            // Loaded flags may be available before their persistent mirror is remembered.
            if (TryFindNearest(position, Mathf.Max(40f, defaultRadius * 2f), defaultRadius, out WorkAreaSnapshot loaded) &&
                !string.IsNullOrWhiteSpace(loaded.Id) && loaded.Distance <= loaded.Radius + 4f)
            {
                areaId = loaded.Id;
                return true;
            }

            return false;
        }

        internal static bool TryFindNearest(Vector3 position, float maxDistance, float defaultRadius, out WorkAreaSnapshot snapshot)
        {
            snapshot = default;
            float maxSqr = Mathf.Max(1f, maxDistance);
            maxSqr *= maxSqr;
            float bestSqr = float.MaxValue;
            WorkFlagInteractable best = null;
            ZDO bestZdo = null;
            string bestId = string.Empty;
            int bestMask = 0;
            float bestRadius = defaultRadius;

            Cleanup.Clear();
            foreach (WorkFlagInteractable flag in Flags)
            {
                if (flag == null || flag.gameObject == null || !flag.gameObject.activeInHierarchy)
                {
                    Cleanup.Add(flag);
                    continue;
                }

                ZNetView nview = flag.GetNetworkView();
                ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                if (zdo == null) continue;

                WorkFlagZdo.InitializeIfNeeded(zdo, defaultRadius);
                if (!WorkFlagZdo.IsEnabled(zdo)) continue;

                Vector3 delta = flag.transform.position - position;
                delta.y = 0f;
                float sqr = delta.sqrMagnitude;
                if (sqr > maxSqr || sqr >= bestSqr) continue;

                string id = WorkFlagZdo.ReadId(zdo);
                if (string.IsNullOrEmpty(id) && nview.IsOwner()) id = WorkFlagZdo.EnsureId(zdo);
                if (string.IsNullOrEmpty(id)) continue;

                bestSqr = sqr;
                best = flag;
                bestZdo = zdo;
                bestId = id;
                bestMask = WorkFlagZdo.ReadOrdersMask(zdo);
                bestRadius = WorkFlagZdo.ReadRadius(zdo, defaultRadius);
            }

            CleanupInvalid();
            if (best == null || bestZdo == null) return false;

            snapshot = new WorkAreaSnapshot(
                best,
                bestId,
                WorkFlagZdo.ReadDisplayName(bestZdo),
                bestMask,
                WorkOrderCatalog.FirstEnabled(bestMask),
                true,
                best.transform.position,
                bestRadius,
                Mathf.Sqrt(bestSqr));
            return true;
        }

        internal static void Register(WorkFlagInteractable flag)
        {
            if (flag != null) Flags.Add(flag);
        }

        internal static void Unregister(WorkFlagInteractable flag)
        {
            if (flag != null) Flags.Remove(flag);
        }

        internal static void Clear()
        {
            // v0.5.365: flush throttled rolling activity while the persistent WorkFlag index is
            // still resolvable. This is event-history persistence only; it performs no world scan.
            WorkzoneActivityFlowRuntime.Clear();
            Flags.Clear();
            Cleanup.Clear();
            PersistentFlags.Clear();
            PersistentFlagWorldScan.Clear();
            NextPersistentLookup.Clear();
            WorkzoneEconomyRuntime.Clear();
            WorkzoneEconomyBackgroundRuntime.Clear();
            WorkzoneLogisticsFlowRuntime.Clear();
            _nextPersistentWorldIndexScan = 0f;
        }

        /// <summary>
        /// User-triggered Dev Reload rebuild. Normal gameplay never performs this global loaded-object
        /// query; work flags otherwise stay lifecycle-indexed through OnEnable/OnDisable.
        /// </summary>
        internal static void RebuildLoadedState()
        {
            Flags.Clear();
            Cleanup.Clear();
            try
            {
                WorkFlagInteractable[] loaded = Resources.FindObjectsOfTypeAll<WorkFlagInteractable>();
                if (loaded == null) return;
                for (int i = 0; i < loaded.Length; i++)
                {
                    WorkFlagInteractable flag = loaded[i];
                    if (flag != null && flag.gameObject != null && flag.gameObject.activeInHierarchy)
                        Flags.Add(flag);
                }
            }
            catch
            {
                // Failing a manual cache refresh must never break existing persistent workzone data.
            }
        }

        internal static void CollectLoadedFlags(long ownerPlayerId, WorkOrder order, float defaultRadius, List<WorkFlagInteractable> results)
        {
            if (results == null) return;
            results.Clear();
            Cleanup.Clear();
            foreach (WorkFlagInteractable flag in Flags)
            {
                if (flag == null || !flag.gameObject.activeInHierarchy)
                {
                    Cleanup.Add(flag);
                    continue;
                }

                ZNetView view = flag.GetNetworkView();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null) continue;
                WorkFlagZdo.InitializeIfNeeded(zdo, defaultRadius);
                if (!WorkFlagZdo.IsEnabled(zdo) || !WorkFlagZdo.HasOrder(zdo, order)) continue;

                long flagOwner = WorkFlagZdo.ReadOwner(zdo);
                if (flagOwner != 0L && ownerPlayerId != 0L && flagOwner != ownerPlayerId) continue;
                results.Add(flag);
            }
            CleanupInvalid();
        }

        /// <summary>
        /// Finds the nearest enabled work area containing the position, regardless of which
        /// legacy order bits are enabled on the flag. Job permission now belongs to the worker/rest
        /// profile; the flag is the spatial/persistent work-area anchor only. This is important for
        /// player-planned Garden fields, which must not fail just because an old Garden bit is off.
        /// </summary>
        internal static bool TryFindContainingWorkAreaForPlanning(Vector3 position, float defaultRadius, out ZNetView nview, out ZDO zdo)
        {
            nview = null; zdo = null;
            float bestSqr = float.MaxValue;

            // Planning is a user-triggered action, so correctness matters more than relying solely
            // on the lifecycle cache. A WorkFlag can remain visible/usable while its Interactable
            // missed Register() (for example after hot reload / component recreation). In that case
            // the old code reported planned-no-workzone even though the player was visibly inside
            // the work area. First use the fast registry, then repair it from loaded scene objects.
            Cleanup.Clear();
            foreach (WorkFlagInteractable flag in Flags)
                ConsiderPlanningFlag(flag, position, defaultRadius, ref bestSqr, ref nview, ref zdo, true);
            CleanupInvalid();

            try
            {
                WorkFlagInteractable[] loaded = Resources.FindObjectsOfTypeAll<WorkFlagInteractable>();
                if (loaded != null)
                {
                    for (int i = 0; i < loaded.Length; i++)
                    {
                        WorkFlagInteractable flag = loaded[i];
                        if (flag == null || flag.gameObject == null || !flag.gameObject.activeInHierarchy) continue;
                        Flags.Add(flag); // heal the lifecycle registry while we are here
                        ConsiderPlanningFlag(flag, position, defaultRadius, ref bestSqr, ref nview, ref zdo, false);
                    }
                }
            }
            catch
            {
                // A failed repair probe must not invalidate a valid cached result.
            }

            // v0.5.311: some live WorkArea roots can temporarily have a valid ZNetView/ZDO but no
            // active WorkFlagInteractable component (hot reload, prefab child replacement, legacy save).
            // Planning is an explicit click, so perform one bounded loaded-view recovery pass and
            // identify WorkAreas by their durable WorkFlag ZDO id instead of by the UI component.
            if (nview == null || zdo == null)
            {
                try
                {
                    ZNetView[] views = Resources.FindObjectsOfTypeAll<ZNetView>();
                    if (views != null)
                    {
                        for (int i = 0; i < views.Length; i++)
                        {
                            ZNetView view = views[i];
                            if (view == null || view.gameObject == null || !view.gameObject.activeInHierarchy || !view.IsValid()) continue;
                            ZDO candidate = view.GetZDO();
                            if (candidate == null || string.IsNullOrEmpty(WorkFlagZdo.ReadId(candidate))) continue;
                            ConsiderPlanningZdo(view, candidate, position, defaultRadius, ref bestSqr, ref nview, ref zdo);
                        }
                    }
                }
                catch
                {
                    // Keep the normal fast-path result if Unity refuses an emergency loaded-view probe.
                }
            }

            return nview != null && zdo != null;
        }

        private static void ConsiderPlanningFlag(WorkFlagInteractable flag, Vector3 position, float defaultRadius,
            ref float bestSqr, ref ZNetView bestView, ref ZDO bestZdo, bool collectInvalid)
        {
            if (flag == null || flag.gameObject == null || !flag.gameObject.activeInHierarchy)
            {
                if (collectInvalid) Cleanup.Add(flag);
                return;
            }

            ZNetView view = flag.GetNetworkView();
            ZDO candidate = view != null && view.IsValid() ? view.GetZDO() : null;
            if (candidate == null) return;

            WorkFlagZdo.InitializeIfNeeded(candidate, defaultRadius);
            if (!WorkFlagZdo.IsEnabled(candidate)) return;

            float radius = WorkFlagZdo.ReadRadius(candidate, defaultRadius);
            // v0.5.370: the Workzone editor, assignment UI and normal loaded-zone lookup all
            // use the WorkFlagInteractable anchor. Planning must use the exact same visible center.
            // The ZDO can belong to a parent/root whose pivot is offset from that child anchor,
            // which previously made an obviously in-zone grid fail as outside-workzone.
            Vector3 center = flag.transform.position;
            Vector3 delta = center - position;
            delta.y = 0f;
            float sqr = delta.sqrMagnitude;
            if (sqr > radius * radius || sqr >= bestSqr) return;

            bestSqr = sqr;
            bestView = view;
            bestZdo = candidate;
            RememberPersistent(candidate);
        }


        private static void ConsiderPlanningZdo(ZNetView view, ZDO candidate, Vector3 position, float defaultRadius,
            ref float bestSqr, ref ZNetView bestView, ref ZDO bestZdo)
        {
            if (view == null || candidate == null) return;
            WorkFlagZdo.InitializeIfNeeded(candidate, defaultRadius);
            if (!WorkFlagZdo.IsEnabled(candidate)) return;

            float radius = WorkFlagZdo.ReadRadius(candidate, defaultRadius);
            Vector3 center = candidate.GetPosition();
            Vector3 delta = center - position;
            delta.y = 0f;
            float sqr = delta.sqrMagnitude;
            if (sqr > radius * radius || sqr >= bestSqr) return;

            bestSqr = sqr;
            bestView = view;
            bestZdo = candidate;
            RememberPersistent(candidate);
        }

        internal static bool TryFindContainingWorkArea(long ownerPlayerId, Vector3 position, float defaultRadius, out ZNetView nview, out ZDO zdo)
        {
            nview = null; zdo = null;
            float bestSqr = float.MaxValue;
            Cleanup.Clear();
            foreach (WorkFlagInteractable flag in Flags)
            {
                if (flag == null || !flag.gameObject.activeInHierarchy) { Cleanup.Add(flag); continue; }
                ZNetView view = flag.GetNetworkView();
                ZDO candidate = view != null && view.IsValid() ? view.GetZDO() : null;
                if (candidate == null) continue;
                WorkFlagZdo.InitializeIfNeeded(candidate, defaultRadius);
                if (!WorkFlagZdo.IsEnabled(candidate)) continue;
                long flagOwner = WorkFlagZdo.ReadOwner(candidate);
                if (flagOwner != 0L && ownerPlayerId != 0L && flagOwner != ownerPlayerId) continue;
                float radius = WorkFlagZdo.ReadRadius(candidate, defaultRadius);
                Vector3 delta = flag.transform.position - position;
                delta.y = 0f;
                float sqr = delta.sqrMagnitude;
                if (sqr > radius * radius || sqr >= bestSqr) continue;
                bestSqr = sqr; nview = view; zdo = candidate;
            }
            CleanupInvalid();
            return nview != null && zdo != null;
        }

        internal static bool TryFindContainingFlag(long ownerPlayerId, WorkOrder order, Vector3 position, float defaultRadius, out ZNetView nview, out ZDO zdo)
        {
            nview = null; zdo = null;
            float bestSqr = float.MaxValue;
            Cleanup.Clear();
            foreach (WorkFlagInteractable flag in Flags)
            {
                if (flag == null || !flag.gameObject.activeInHierarchy) { Cleanup.Add(flag); continue; }
                ZNetView view = flag.GetNetworkView();
                ZDO candidate = view != null && view.IsValid() ? view.GetZDO() : null;
                if (candidate == null) continue;
                WorkFlagZdo.InitializeIfNeeded(candidate, defaultRadius);
                if (!WorkFlagZdo.IsEnabled(candidate) || !WorkFlagZdo.HasOrder(candidate, order)) continue;
                long flagOwner = WorkFlagZdo.ReadOwner(candidate);
                if (flagOwner != 0L && ownerPlayerId != 0L && flagOwner != ownerPlayerId) continue;
                float radius = WorkFlagZdo.ReadRadius(candidate, defaultRadius);
                Vector3 delta = flag.transform.position - position;
                delta.y = 0f; // Workzones are horizontal map areas; terrain height must not shrink the usable radius.
                float sqr = delta.sqrMagnitude;
                if (sqr > radius * radius || sqr >= bestSqr) continue;
                bestSqr = sqr; nview = view; zdo = candidate;
            }
            CleanupInvalid();
            return nview != null && zdo != null;
        }

        /// <summary>
        /// Resolves one loaded WorkFlag that contains the ENTIRE planned field.
        /// This is intentionally evaluated only on an explicit planning click; it never runs in
        /// the worker/background loops. Resolving the whole field at once avoids overlap bugs where
        /// slot 1 selected a nearby/smaller Workzone although another Workzone contained all slots.
        /// </summary>
        internal static bool TryFindContainingFlagForField(long ownerPlayerId, WorkOrder order,
            IList<Vector3> positions, float defaultRadius, out ZNetView nview, out ZDO zdo)
        {
            nview = null;
            zdo = null;
            if (positions == null || positions.Count < 1) return false;

            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < positions.Count; i++) centroid += positions[i];
            centroid /= positions.Count;

            float bestSqr = float.MaxValue;
            Cleanup.Clear();
            foreach (WorkFlagInteractable flag in Flags)
            {
                if (flag == null || !flag.gameObject.activeInHierarchy)
                {
                    Cleanup.Add(flag);
                    continue;
                }

                ZNetView view = flag.GetNetworkView();
                ZDO candidate = view != null && view.IsValid() ? view.GetZDO() : null;
                if (candidate == null) continue;

                WorkFlagZdo.InitializeIfNeeded(candidate, defaultRadius);
                if (!WorkFlagZdo.IsEnabled(candidate)) continue;
                if (order != WorkOrder.None && !WorkFlagZdo.HasOrder(candidate, order)) continue;

                long flagOwner = WorkFlagZdo.ReadOwner(candidate);
                if (flagOwner != 0L && ownerPlayerId != 0L && flagOwner != ownerPlayerId) continue;

                float radius = WorkFlagZdo.ReadRadius(candidate, defaultRadius);
                float radiusSqr = radius * radius;
                Vector3 center = flag.transform.position;

                bool containsAll = true;
                for (int i = 0; i < positions.Count; i++)
                {
                    Vector3 delta = positions[i] - center;
                    delta.y = 0f;
                    if (delta.sqrMagnitude > radiusSqr + 0.01f)
                    {
                        containsAll = false;
                        break;
                    }
                }
                if (!containsAll) continue;

                Vector3 centerDelta = centroid - center;
                centerDelta.y = 0f;
                float sqr = centerDelta.sqrMagnitude;
                if (sqr >= bestSqr) continue;

                bestSqr = sqr;
                nview = view;
                zdo = candidate;
                RememberPersistent(candidate);
            }

            CleanupInvalid();
            return nview != null && zdo != null;
        }

        internal static bool TryFindContainingAnyFlag(long ownerPlayerId, Vector3 position,
            out string id, out ZDO zdo, out Vector3 center, out float radius)
        {
            id = string.Empty;
            zdo = null;
            center = default;
            radius = 0f;
            float bestSqr = float.MaxValue;

            Cleanup.Clear();
            foreach (WorkFlagInteractable flag in Flags)
            {
                if (flag == null || !flag.gameObject.activeInHierarchy)
                {
                    Cleanup.Add(flag);
                    continue;
                }

                ZNetView view = flag.GetNetworkView();
                ZDO candidate = view != null && view.IsValid() ? view.GetZDO() : null;
                if (candidate == null) continue;
                long flagOwner = WorkFlagZdo.ReadOwner(candidate);
                if (flagOwner != 0L && ownerPlayerId != 0L && flagOwner != ownerPlayerId) continue;

                float candidateRadius = WorkFlagZdo.ReadRadius(candidate, 40f);
                Vector3 delta = flag.transform.position - position;
                delta.y = 0f;
                float sqr = delta.sqrMagnitude;
                if (sqr > candidateRadius * candidateRadius || sqr >= bestSqr) continue;

                string candidateId = WorkFlagZdo.ReadId(candidate);
                if (string.IsNullOrEmpty(candidateId) && view != null && view.IsOwner())
                    candidateId = WorkFlagZdo.EnsureId(candidate);
                if (string.IsNullOrEmpty(candidateId)) continue;

                bestSqr = sqr;
                id = candidateId;
                zdo = candidate;
                center = flag.transform.position;
                radius = candidateRadius;
                PersistentFlags[candidateId] = candidate;
            }
            CleanupInvalid();

            // Background AirFreight queries may run while the WorkFlag GameObject is unloaded.
            // Reuse the already known persistent flag cache; do not trigger a world-wide ZDO scan.
            foreach (KeyValuePair<string, ZDO> pair in PersistentFlags)
            {
                ZDO candidate = pair.Value;
                if (candidate == null || string.IsNullOrEmpty(pair.Key)) continue;
                long flagOwner = WorkFlagZdo.ReadOwner(candidate);
                if (flagOwner != 0L && ownerPlayerId != 0L && flagOwner != ownerPlayerId) continue;

                float candidateRadius = WorkFlagZdo.ReadRadius(candidate, 40f);
                Vector3 candidateCenter = candidate.GetPosition();
                Vector3 delta = candidateCenter - position;
                delta.y = 0f;
                float sqr = delta.sqrMagnitude;
                if (sqr > candidateRadius * candidateRadius || sqr >= bestSqr) continue;

                bestSqr = sqr;
                id = pair.Key;
                zdo = candidate;
                center = candidateCenter;
                radius = candidateRadius;
            }

            return zdo != null && !string.IsNullOrEmpty(id);
        }

        internal static bool TryGetNetworkState(string id, float defaultRadius, out ZNetView nview, out ZDO zdo)
        {
            nview = null; zdo = null;
            if (!TryFindById(id, defaultRadius, out WorkAreaSnapshot snapshot) || snapshot.Flag == null) return false;
            nview = snapshot.Flag.GetNetworkView();
            if (nview == null || !nview.IsValid()) return false;
            zdo = nview.GetZDO();
            return zdo != null;
        }

        /// <summary>
        /// v0.5.370: returns the same center players see in the loaded Workzone editor and that
        /// worker assignment uses. For unloaded flags there is no Interactable transform, so the
        /// persistent ZDO position remains the correct fallback. This is registry-only and never
        /// performs a world scan.
        /// </summary>
        internal static Vector3 ResolveRuntimeCenter(ZDO zdo, float defaultRadius)
        {
            if (zdo == null) return Vector3.zero;
            string id = WorkFlagZdo.ReadId(zdo);
            if (!string.IsNullOrEmpty(id) && TryFindById(id, defaultRadius, out WorkAreaSnapshot loaded) && loaded.Flag != null)
                return loaded.Center;
            return zdo.GetPosition();
        }

        internal static bool TryFindById(string id, float defaultRadius, out WorkAreaSnapshot snapshot)
        {
            snapshot = default;
            if (string.IsNullOrEmpty(id)) return false;

            bool found = false;
            Cleanup.Clear();
            foreach (WorkFlagInteractable flag in Flags)
            {
                if (flag == null || !flag.gameObject.activeInHierarchy)
                {
                    Cleanup.Add(flag);
                    continue;
                }

                ZNetView nview = flag.GetNetworkView();
                ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                if (zdo == null) continue;

                WorkFlagZdo.InitializeIfNeeded(zdo, defaultRadius);
                string flagId = WorkFlagZdo.ReadId(zdo);
                if (string.IsNullOrEmpty(flagId) && nview.IsOwner())
                    flagId = WorkFlagZdo.EnsureId(zdo);
                if (!string.Equals(flagId, id, StringComparison.Ordinal)) continue;

                int ordersMask = WorkFlagZdo.ReadOrdersMask(zdo);
                snapshot = new WorkAreaSnapshot(
                    flag,
                    flagId,
                    WorkFlagZdo.ReadDisplayName(zdo),
                    ordersMask,
                    WorkOrderCatalog.FirstEnabled(ordersMask),
                    WorkFlagZdo.IsEnabled(zdo),
                    flag.transform.position,
                    WorkFlagZdo.ReadRadius(zdo, defaultRadius),
                    0f);
                found = true;
                break;
            }

            CleanupInvalid();
            return found;
        }

        internal static bool TryFindBestForOrder(Character worker, ZDO workerZdo, WorkerRole role, WorkOrder requiredOrder, float maxDispatchDistance, float defaultRadius, out WorkAreaSnapshot snapshot)
        {
            snapshot = default;
            if (worker == null || workerZdo == null || role == WorkerRole.None || requiredOrder == WorkOrder.None) return false;
            if (!WorkerJobProfileRuntime.SupportsOrder(workerZdo, role, requiredOrder)) return false;

            long ownerPlayerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            float maxSqr = Mathf.Max(20f, maxDispatchDistance);
            maxSqr *= maxSqr;
            float bestSqr = float.MaxValue;
            WorkFlagInteractable best = null;
            string bestId = string.Empty;
            int bestMask = 0;
            float bestRadius = defaultRadius;

            Cleanup.Clear();
            foreach (WorkFlagInteractable flag in Flags)
            {
                if (flag == null || !flag.gameObject.activeInHierarchy)
                {
                    Cleanup.Add(flag);
                    continue;
                }

                ZNetView nview = flag.GetNetworkView();
                ZDO flagZdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                if (flagZdo == null) continue;
                WorkFlagZdo.InitializeIfNeeded(flagZdo, defaultRadius);
                if (!WorkFlagZdo.IsEnabled(flagZdo)) continue;

                int mask = WorkFlagZdo.ReadOrdersMask(flagZdo);
                if (!WorkOrderCatalog.Contains(mask, requiredOrder)) continue;

                long flagOwner = WorkFlagZdo.ReadOwner(flagZdo);
                if (flagOwner != 0L && ownerPlayerId != 0L && flagOwner != ownerPlayerId) continue;

                float candidateRadius = WorkFlagZdo.ReadRadius(flagZdo, defaultRadius);
                if (!WorkerWorkAreaRules.TryValidateHomeForArea(workerZdo, flag.transform.position, candidateRadius, out _)) continue;

                float sqr = (flag.transform.position - worker.transform.position).sqrMagnitude;
                if (sqr > maxSqr || sqr >= bestSqr) continue;

                string flagId = WorkFlagZdo.ReadId(flagZdo);
                if (string.IsNullOrEmpty(flagId) && nview.IsOwner())
                    flagId = WorkFlagZdo.EnsureId(flagZdo);

                bestSqr = sqr;
                best = flag;
                bestId = flagId;
                bestMask = mask;
                bestRadius = candidateRadius;
            }

            CleanupInvalid();
            if (best == null) return false;
            ZNetView bestView = best.GetNetworkView();
            ZDO bestZdo = bestView != null && bestView.IsValid() ? bestView.GetZDO() : null;
            snapshot = new WorkAreaSnapshot(best, bestId, WorkFlagZdo.ReadDisplayName(bestZdo), bestMask, requiredOrder, true, best.transform.position, bestRadius, Mathf.Sqrt(bestSqr));
            return true;
        }

        internal static bool TryFindBestForWorker(Character worker, ZDO workerZdo, WorkerRole role, float maxDispatchDistance, float defaultRadius, out WorkAreaSnapshot snapshot)
        {
            snapshot = default;
            if (worker == null || workerZdo == null || role == WorkerRole.None) return false;

            long ownerPlayerId = workerZdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
            float maxSqr = Mathf.Max(20f, maxDispatchDistance);
            maxSqr *= maxSqr;
            float bestSqr = float.MaxValue;
            WorkFlagInteractable best = null;
            string bestId = string.Empty;
            int bestMask = 0;
            WorkOrder bestOrder = WorkOrder.None;
            float bestRadius = defaultRadius;

            Cleanup.Clear();
            foreach (WorkFlagInteractable flag in Flags)
            {
                if (flag == null || !flag.gameObject.activeInHierarchy)
                {
                    Cleanup.Add(flag);
                    continue;
                }

                ZNetView nview = flag.GetNetworkView();
                ZDO flagZdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                if (flagZdo == null) continue;
                WorkFlagZdo.InitializeIfNeeded(flagZdo, defaultRadius);
                if (!WorkFlagZdo.IsEnabled(flagZdo)) continue;
                int flagMask = WorkFlagZdo.ReadOrdersMask(flagZdo);
                WorkOrder order = WorkerJobProfileRuntime.ResolveAssignmentOrder(workerZdo, role);
                if (order == WorkOrder.None) continue;

                long flagOwner = WorkFlagZdo.ReadOwner(flagZdo);
                if (flagOwner != 0L && ownerPlayerId != 0L && flagOwner != ownerPlayerId) continue;

                float candidateRadius = WorkFlagZdo.ReadRadius(flagZdo, defaultRadius);
                if (!WorkerWorkAreaRules.TryValidateHomeForArea(workerZdo, flag.transform.position, candidateRadius, out _)) continue;

                float sqr = (flag.transform.position - worker.transform.position).sqrMagnitude;
                if (sqr > maxSqr || sqr >= bestSqr) continue;

                string flagId = WorkFlagZdo.ReadId(flagZdo);
                if (string.IsNullOrEmpty(flagId) && nview.IsOwner())
                    flagId = WorkFlagZdo.EnsureId(flagZdo);

                bestSqr = sqr;
                best = flag;
                bestId = flagId;
                bestMask = WorkFlagZdo.ReadOrdersMask(flagZdo);
                bestOrder = order;
                bestRadius = candidateRadius;
            }

            CleanupInvalid();

            if (best == null) return false;
            ZNetView bestView = best.GetNetworkView();
            ZDO bestZdo = bestView != null && bestView.IsValid() ? bestView.GetZDO() : null;
            snapshot = new WorkAreaSnapshot(best, bestId, WorkFlagZdo.ReadDisplayName(bestZdo), bestMask, bestOrder, true, best.transform.position, bestRadius, Mathf.Sqrt(bestSqr));
            return true;
        }


        internal static void CollectCompatibleAssignments(long ownerPlayerId, WorkerRole role, ZDO workerZdo, Vector3 workerPosition, float defaultRadius, List<WorkAssignmentOption> results)
        {
            if (results == null) return;
            results.Clear();
            Cleanup.Clear();

            foreach (WorkFlagInteractable flag in Flags)
            {
                if (flag == null || !flag.gameObject.activeInHierarchy)
                {
                    Cleanup.Add(flag);
                    continue;
                }

                ZNetView view = flag.GetNetworkView();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null) continue;
                WorkFlagZdo.InitializeIfNeeded(zdo, defaultRadius);
                if (!WorkFlagZdo.IsEnabled(zdo)) continue;

                long flagOwner = WorkFlagZdo.ReadOwner(zdo);
                if (flagOwner != 0L && ownerPlayerId != 0L && flagOwner != ownerPlayerId) continue;

                string id = WorkFlagZdo.ReadId(zdo);
                if (string.IsNullOrEmpty(id) && view.IsOwner())
                    id = WorkFlagZdo.EnsureId(zdo);

                string name = WorkFlagZdo.ReadDisplayName(zdo);
                float radius = WorkFlagZdo.ReadRadius(zdo, defaultRadius);
                if (!WorkerWorkAreaRules.TryValidateHomeForArea(workerZdo, flag.transform.position, radius, out _)) continue;
                float distance = Vector3.Distance(flag.transform.position, workerPosition);
                WorkOrder order = WorkerJobProfileRuntime.ResolveAssignmentOrder(workerZdo, role);
                if (order == WorkOrder.None) continue;
                results.Add(new WorkAssignmentOption(flag, id, name, order, flag.transform.position, radius, distance));
            }

            CleanupInvalid();
            results.Sort((a, b) =>
            {
                int byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                if (byName != 0) return byName;
                int byOrder = ((int)a.Order).CompareTo((int)b.Order);
                if (byOrder != 0) return byOrder;
                return a.Distance.CompareTo(b.Distance);
            });
        }

        /// <summary>
        /// World-wide outpost index for explicit Cartography-table UI use. Unlike the normal
        /// runtime registry this may perform a throttled full ZDOMan scan, because the player
        /// explicitly opened/refreshed the overview. Normal Worker scheduling never calls this.
        /// </summary>
        internal static void CollectOwnedPersistentSnapshots(long ownerPlayerId, float defaultRadius, Vector3 origin, List<WorkAreaSnapshot> results, bool forceWorldScan = false)
        {
            if (results == null) return;
            results.Clear();

            // Always seed/update the durable cache from currently loaded flags first.
            Cleanup.Clear();
            foreach (WorkFlagInteractable flag in Flags)
            {
                if (flag == null || !flag.gameObject.activeInHierarchy)
                {
                    Cleanup.Add(flag);
                    continue;
                }

                ZNetView view = flag.GetNetworkView();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null) continue;
                string id = WorkFlagZdo.ReadId(zdo);
                if (string.IsNullOrEmpty(id) && view.IsOwner()) id = WorkFlagZdo.EnsureId(zdo);
                if (!string.IsNullOrEmpty(id)) PersistentFlags[id] = zdo;
            }
            CleanupInvalid();

            RefreshPersistentWorldIndexIfDue(forceWorldScan);

            foreach (KeyValuePair<string, ZDO> pair in PersistentFlags)
            {
                string id = pair.Key;
                ZDO zdo = pair.Value;
                if (zdo == null || string.IsNullOrEmpty(id)) continue;

                long flagOwner = WorkFlagZdo.ReadOwner(zdo);
                if (flagOwner != 0L && ownerPlayerId != 0L && flagOwner != ownerPlayerId) continue;

                WorkFlagInteractable loadedFlag = null;
                Vector3 center = zdo.GetPosition();
                if (TryFindById(id, defaultRadius, out WorkAreaSnapshot loaded) && loaded.Flag != null)
                {
                    loadedFlag = loaded.Flag;
                    center = loaded.Center;
                }

                string name = WorkFlagZdo.ReadDisplayName(zdo);
                int mask = WorkFlagZdo.ReadOrdersMask(zdo);
                float radius = WorkFlagZdo.ReadRadius(zdo, defaultRadius);
                float distance = Vector3.Distance(origin, center);
                results.Add(new WorkAreaSnapshot(
                    loadedFlag,
                    id,
                    name,
                    mask,
                    WorkOrderCatalog.FirstEnabled(mask),
                    WorkFlagZdo.IsEnabled(zdo),
                    center,
                    radius,
                    distance));
            }

            results.Sort((a, b) =>
            {
                int byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                if (byName != 0) return byName;
                return a.Distance.CompareTo(b.Distance);
            });
        }

        private static void RefreshPersistentWorldIndexIfDue(bool force)
        {
            if (!force && Time.unscaledTime < _nextPersistentWorldIndexScan) return;
            _nextPersistentWorldIndexScan = Time.unscaledTime + PersistentWorldIndexScanSeconds;

            ZDOMan man = ZDOMan.instance;
            if (man == null) return;

            PersistentFlagWorldScan.Clear();
            PersistentFlagWorldRejectedIds.Clear();
            bool scanCompleted = false;
            try
            {
                FieldInfo[] fields = typeof(ZDOMan).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                {
                    object value;
                    try { value = fields[i].GetValue(man); }
                    catch { continue; }
                    HarvestPersistentFlags(value, 0);
                }
                scanCompleted = true;
            }
            catch
            {
                scanCompleted = false;
            }

            if (!scanCompleted) return;
            if (PersistentFlagWorldScan.Count == 0 && PersistentFlags.Count > 0) return;

            // Replace the cache from the authoritative world scan. Avoid replacing a known-good
            // cache with an empty startup scan while ZDOMan is still rebuilding its collections.
            PersistentFlags.Clear();
            foreach (KeyValuePair<string, ZDO> pair in PersistentFlagWorldScan)
                PersistentFlags[pair.Key] = pair.Value;
        }

        private static void HarvestPersistentFlags(object value, int depth)
        {
            if (value == null || depth > 2) return;
            if (value is ZDO single)
            {
                RememberWorldFlag(single);
                return;
            }

            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is ZDO zdo) RememberWorldFlag(zdo);
                    else if (depth < 2) HarvestPersistentFlags(entry.Value, depth + 1);
                }
                return;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                int guard = 0;
                foreach (object item in enumerable)
                {
                    if (++guard > 200000) break;
                    if (item is ZDO zdo) RememberWorldFlag(zdo);
                    else if (depth < 2) HarvestPersistentFlags(item, depth + 1);
                }
            }
        }

        private static void RememberWorldFlag(ZDO zdo)
        {
            if (zdo == null) return;
            string id = WorkFlagZdo.ReadId(zdo);
            if (string.IsNullOrEmpty(id)) return;

            if (!IsLiveWorldFlagZdo(zdo))
            {
                PersistentFlagWorldRejectedIds.Add(id);
                return;
            }

            PersistentFlagWorldScan[id] = zdo;
        }

        /// <summary>
        /// Cartography scans walk several private ZDOMan collections, including implementation
        /// details that may still contain a destroyed/retired ZDO for a short time. A durable
        /// workflag id alone therefore is not proof that the flag still exists. Keep only ZDOs
        /// that are still canonical in ZDOMan and, whenever the prefab can be resolved, really
        /// belong to the Workers work-flag prefab. This validation is used only by the explicit
        /// Cartography world scan; normal Worker scheduling remains untouched.
        /// </summary>
        private static bool IsLiveWorldFlagZdo(ZDO zdo)
        {
            if (zdo == null || string.IsNullOrEmpty(WorkFlagZdo.ReadId(zdo))) return false;

            try
            {
                if (ZDOMan.instance != null)
                {
                    ZDO canonical = ZDOMan.instance.GetZDO(zdo.m_uid);
                    if (canonical == null) return false;
                }
            }
            catch
            {
                // If a Valheim update changes this lookup, do not hide a potentially valid flag.
            }

            try
            {
                if (ZNetScene.instance != null && ZNetSceneGetPrefabByHashMethod != null)
                {
                    GameObject prefab = ZNetSceneGetPrefabByHashMethod.Invoke(
                        ZNetScene.instance, new object[] { zdo.GetPrefab() }) as GameObject;
                    if (prefab != null)
                    {
                        string prefabName = (prefab.name ?? string.Empty).Replace("(Clone)", string.Empty).Trim();
                        if (!string.Equals(prefabName, WorkFlagPieceRuntime.PrefabName, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                }
            }
            catch
            {
                // Prefab resolution is an additional stale-ZDO guard, not a hard dependency.
            }

            return true;
        }

        /// <summary>
        /// Explicit Cartography-table save helper. On the authoritative server/host, update the
        /// denormalized workzone metadata stored on every persistent Worker ZDO assigned to the
        /// edited flag. Background runtimes read this cached radius/name even while the scene is
        /// unloaded, so keeping it synchronized is required for true remote outpost editing.
        /// This is a user-triggered one-shot world scan, never part of the normal AI tick.
        /// </summary>
        internal static int UpdatePersistentAssignedWorkerAreaMetadata(string flagId, string displayName, Vector3 center, float radius)
        {
            if (string.IsNullOrEmpty(flagId) || ZDOMan.instance == null) return 0;

            int updated = 0;
            HashSet<ZDO> visited = new HashSet<ZDO>();
            try
            {
                FieldInfo[] fields = typeof(ZDOMan).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                {
                    object value;
                    try { value = fields[i].GetValue(ZDOMan.instance); }
                    catch { continue; }
                    UpdateAssignedWorkersInObject(value, flagId, displayName, center, radius, 0, visited, ref updated);
                }
            }
            catch
            {
                // A failed explicit metadata refresh must not invalidate the flag edit itself.
            }
            return updated;
        }

        private static void UpdateAssignedWorkersInObject(object value, string flagId, string displayName, Vector3 center, float radius, int depth, HashSet<ZDO> visited, ref int updated)
        {
            if (value == null || depth > 2) return;
            if (value is ZDO single)
            {
                UpdateAssignedWorkerZdo(single, flagId, displayName, center, radius, visited, ref updated);
                return;
            }

            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is ZDO zdo) UpdateAssignedWorkerZdo(zdo, flagId, displayName, center, radius, visited, ref updated);
                    else if (depth < 2) UpdateAssignedWorkersInObject(entry.Value, flagId, displayName, center, radius, depth + 1, visited, ref updated);
                }
                return;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                int guard = 0;
                foreach (object item in enumerable)
                {
                    if (++guard > 200000) break;
                    if (item is ZDO zdo) UpdateAssignedWorkerZdo(zdo, flagId, displayName, center, radius, visited, ref updated);
                    else if (depth < 2) UpdateAssignedWorkersInObject(item, flagId, displayName, center, radius, depth + 1, visited, ref updated);
                }
            }
        }

        private static void UpdateAssignedWorkerZdo(ZDO zdo, string flagId, string displayName, Vector3 center, float radius, HashSet<ZDO> visited, ref int updated)
        {
            if (zdo == null || !visited.Add(zdo) || !WorkerZdo.IsWorker(zdo)) return;
            if (!string.Equals(WorkerZdo.ReadWorkAreaId(zdo), flagId, StringComparison.Ordinal)) return;

            zdo.Set(WorkerZdo.WorkAreaName, displayName ?? string.Empty);
            zdo.Set(WorkerZdo.WorkAreaX, center.x);
            zdo.Set(WorkerZdo.WorkAreaY, center.y);
            zdo.Set(WorkerZdo.WorkAreaZ, center.z);
            zdo.Set(WorkerZdo.WorkAreaRadius, Mathf.Max(5f, radius));
            updated++;
        }

        internal static void CollectOwnedSnapshots(long ownerPlayerId, float defaultRadius, Vector3 origin, List<WorkAreaSnapshot> results)
        {
            if (results == null) return;
            results.Clear();
            Cleanup.Clear();

            foreach (WorkFlagInteractable flag in Flags)
            {
                if (flag == null || !flag.gameObject.activeInHierarchy)
                {
                    Cleanup.Add(flag);
                    continue;
                }

                ZNetView view = flag.GetNetworkView();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null) continue;
                WorkFlagZdo.InitializeIfNeeded(zdo, defaultRadius);

                long flagOwner = WorkFlagZdo.ReadOwner(zdo);
                if (flagOwner != 0L && ownerPlayerId != 0L && flagOwner != ownerPlayerId) continue;

                string id = WorkFlagZdo.ReadId(zdo);
                if (string.IsNullOrEmpty(id) && view.IsOwner()) id = WorkFlagZdo.EnsureId(zdo);
                string name = WorkFlagZdo.ReadDisplayName(zdo);
                int mask = WorkFlagZdo.ReadOrdersMask(zdo);
                float radius = WorkFlagZdo.ReadRadius(zdo, defaultRadius);
                float distance = Vector3.Distance(origin, flag.transform.position);
                results.Add(new WorkAreaSnapshot(flag, id, name, mask, WorkOrderCatalog.FirstEnabled(mask), WorkFlagZdo.IsEnabled(zdo), flag.transform.position, radius, distance));
            }

            CleanupInvalid();
            results.Sort((a, b) =>
            {
                int byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                if (byName != 0) return byName;
                return a.Distance.CompareTo(b.Distance);
            });
        }

        internal static int GetNextDefaultIndex(long ownerPlayerId)
        {
            int max = 0;
            Cleanup.Clear();
            foreach (WorkFlagInteractable flag in Flags)
            {
                if (flag == null || !flag.gameObject.activeInHierarchy) { Cleanup.Add(flag); continue; }
                ZNetView view = flag.GetNetworkView();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null) continue;
                long owner = WorkFlagZdo.ReadOwner(zdo);
                if (ownerPlayerId != 0L && owner != 0L && owner != ownerPlayerId) continue;
                string name = WorkFlagZdo.ReadDisplayName(zdo);
                if (name.StartsWith("Workzone", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(name.Substring("Workzone".Length), out int index))
                    max = Mathf.Max(max, index);
            }
            CleanupInvalid();
            return max + 1;
        }

        internal static string TryGetDisplayName(string id, string fallback = "")
        {
            if (string.IsNullOrEmpty(id)) return fallback ?? string.Empty;
            if (TryFindById(id, 30f, out WorkAreaSnapshot snapshot) && !string.IsNullOrWhiteSpace(snapshot.DisplayName))
                return snapshot.DisplayName;
            return fallback ?? string.Empty;
        }

        private static void CleanupInvalid()
        {
            for (int i = 0; i < Cleanup.Count; i++)
                Flags.Remove(Cleanup[i]);
            Cleanup.Clear();
        }
    }
}
