using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Workers-owned persistent wild flora. This deliberately does not depend on SmartReplant.
    /// We clone the vanilla persistent Pickable prefab through Jotunn before ZNetScene starts,
    /// add a Cultivator Piece + real resource requirement, and then use the normal Workers
    /// planning grid/field logic for preview, rotation, sizing and saved slots.
    /// </summary>
    internal sealed class WorkersWildFloraRuntime
    {
        internal const string Prefix = "VW_Flora_";

        internal sealed class Definition
        {
            internal readonly string Id;
            internal readonly string PrefabName;
            internal readonly string SourcePrefab;
            internal readonly string ResourcePrefab;
            internal readonly string DisplayName;
            internal readonly int Cost;
            internal readonly float Spacing;

            internal Definition(string id, string sourcePrefab, string resourcePrefab, string displayName, int cost, float spacing)
            {
                Id = id;
                PrefabName = Prefix + id;
                SourcePrefab = sourcePrefab;
                ResourcePrefab = resourcePrefab;
                DisplayName = displayName;
                Cost = Math.Max(1, cost);
                Spacing = Mathf.Max(0.5f, spacing);
            }
        }

        private static readonly Definition[] Definitions =
        {
            new Definition("Raspberry",      "RaspberryBush",             "Raspberry",      "Himbeerbusch",      5, 3.05f),
            new Definition("Blueberries",    "BlueberryBush",             "Blueberries",    "Blaubeerbusch",    5, 3.05f),
            new Definition("Cloudberry",     "CloudberryBush",            "Cloudberry",     "Moltebeere",       5, 1.04f),
            new Definition("Mushroom",       "Pickable_Mushroom",         "Mushroom",       "Pilz",             3, 1.01f),
            new Definition("MushroomYellow", "Pickable_Mushroom_yellow",  "MushroomYellow", "Gelber Pilz",      3, 0.63f),
            new Definition("MushroomBlue",   "Pickable_Mushroom_blue",    "MushroomBlue",   "Blauer Pilz",      3, 0.63f),
            new Definition("Thistle",        "Pickable_Thistle",          "Thistle",        "Distel",           3, 0.56f),
            new Definition("Dandelion",      "Pickable_Dandelion",        "Dandelion",      "Loewenzahn",       3, 0.79f),
        };

        private static readonly Dictionary<string, Definition> ByPrefab =
            Definitions.ToDictionary(x => x.PrefabName, StringComparer.OrdinalIgnoreCase);

        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo ZNetViewPersistentField = typeof(ZNetView).GetField("m_persistent", InstanceFlags);
        private static readonly MethodInfo ZdoSetPersistentMethod = typeof(ZDO).GetMethod("SetPersistent", InstanceFlags, null, new[] { typeof(bool) }, null);
        private static readonly FieldInfo DestroyWhenPickedField = typeof(Pickable).GetField("m_destroyWhenPicked", InstanceFlags);
        private static readonly MethodInfo ClaimOwnershipMethod = typeof(ZNetView).GetMethod(
            "ClaimOwnership", InstanceFlags, null, Type.EmptyTypes, null);
        private static readonly MethodInfo ZNetSceneDestroyGameObjectMethod = typeof(ZNetScene)
            .GetMethods(InstanceFlags)
            .FirstOrDefault(m => string.Equals(m.Name, "Destroy", StringComparison.Ordinal) &&
                                 m.GetParameters().Length == 1 &&
                                 m.GetParameters()[0].ParameterType == typeof(GameObject));

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private bool _registered;
        private bool _gaveUp;
        private float _nextAttempt;
        private string _status = "waiting for Jotunn vanilla prefabs";

        internal WorkersWildFloraRuntime(Action<string> debugLog, Action<string> infoLog)
        {
            _debugLog = debugLog;
            _infoLog = infoLog;
        }

        internal bool Registered => _registered;
        internal string Status => _status;

        internal void Begin()
        {
            if (JotunnPrefabLifecycleRuntime.Subscribe(TryRegister, _debugLog)) return;
            _status = "Jotunn lifecycle unavailable; waiting for late fallback";
        }

        internal void Tick()
        {
            if (_registered || _gaveUp) return;
            if (JotunnPrefabLifecycleRuntime.WaitingForVanillaPrefabs) return;
            if (Time.unscaledTime < _nextAttempt) return;
            _nextAttempt = Time.unscaledTime + 2f;
            TryRegister();
        }

        internal static bool IsWorkersWildFloraName(string raw)
        {
            string n = GardenWorkRuntime.NormalizePrefabName(raw ?? string.Empty);
            return n.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryGetDefinition(string rawPrefabName, out Definition definition)
        {
            string n = GardenWorkRuntime.NormalizePrefabName(rawPrefabName ?? string.Empty);
            return ByPrefab.TryGetValue(n, out definition);
        }

        internal static bool TryGetDefinition(GameObject go, out Definition definition) =>
            TryGetDefinition(go != null ? go.name : string.Empty, out definition);

        /// <summary>
        /// Returns the original vanilla world prefab used only as the planning/ghost visual.
        /// Gameplay still plants the Workers-owned persistent clone so naturally spawned flora is
        /// never modified. This keeps the UI simple: a planned mushroom looks like a mushroom.
        /// </summary>
        internal static bool TryResolvePlanningVisual(string rawIdentity, out GameObject sourcePrefab)
        {
            sourcePrefab = null;
            if (!TryCanonicalizeLegacyIdentity(rawIdentity, out Definition def) || def == null) return false;
            if (ZNetScene.instance != null) sourcePrefab = ZNetScene.instance.GetPrefab(def.SourcePrefab);
            if (sourcePrefab == null && ObjectDB.instance != null) sourcePrefab = ObjectDB.instance.GetItemPrefab(def.SourcePrefab);
            return sourcePrefab != null;
        }

        internal static bool TryResolvePlanningVisual(Piece piece, out GameObject sourcePrefab)
        {
            sourcePrefab = null;
            if (piece == null || !TryGetDefinition(piece.gameObject, out Definition def) || def == null) return false;
            if (ZNetScene.instance != null) sourcePrefab = ZNetScene.instance.GetPrefab(def.SourcePrefab);
            if (sourcePrefab == null && ObjectDB.instance != null) sourcePrefab = ObjectDB.instance.GetItemPrefab(def.SourcePrefab);
            return sourcePrefab != null;
        }

        internal static bool TryGetDefinitionByResource(string resourcePrefab, out Definition definition)
        {
            definition = null;
            string normalized = WorkerStorageRuntime.NormalizeObjectName(resourcePrefab ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalized)) return false;
            for (int i = 0; i < Definitions.Length; i++)
            {
                Definition candidate = Definitions[i];
                if (candidate != null && string.Equals(candidate.ResourcePrefab, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    definition = candidate;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Expected passive yield of one completed Workers WildGrowth plant per Valheim day.
        /// This is intentionally an estimate for display/ledger purposes only: it uses the
        /// vanilla Pickable base amount and respawn minutes and never changes gameplay timing.
        /// </summary>
        internal static bool TryGetExpectedYieldPerValheimDay(string resourcePrefab, out double perPlantPerDay, out int yieldPerCycle, out double respawnMinutes)
        {
            perPlantPerDay = 0.0;
            yieldPerCycle = 0;
            respawnMinutes = 0.0;
            if (!TryGetDefinitionByResource(resourcePrefab, out Definition def)) return false;

            Pickable pickable = null;
            try
            {
                GameObject source = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(def.SourcePrefab) : null;
                if (source == null && ObjectDB.instance != null) source = ObjectDB.instance.GetItemPrefab(def.SourcePrefab);
                if (source != null) pickable = source.GetComponent<Pickable>();
            }
            catch { }

            yieldPerCycle = pickable != null ? Math.Max(1, pickable.m_amount) : 1;
            if (pickable != null)
            {
                try
                {
                    FieldInfo field = typeof(Pickable).GetField("m_respawnTimeMinutes", InstanceFlags);
                    object raw = field != null ? field.GetValue(pickable) : null;
                    if (raw != null) respawnMinutes = Convert.ToDouble(raw);
                }
                catch { respawnMinutes = 0.0; }
            }

            // Vanilla berries respawn after five real hours (= 10 Valheim days); the smaller
            // pickables use four hours. Keep these only as fail-safe values when reflection is
            // unavailable so the sign still gives a useful approximate balance.
            if (respawnMinutes <= 0.01)
            {
                bool berry = string.Equals(def.Id, "Raspberry", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(def.Id, "Blueberries", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(def.Id, "Cloudberry", StringComparison.OrdinalIgnoreCase);
                respawnMinutes = berry ? 300.0 : 240.0;
            }

            double valheimDayMinutes = Math.Max(1.0, WorkerSustenanceRuntime.MealInterval.TotalMinutes);
            perPlantPerDay = yieldPerCycle * (valheimDayMinutes / respawnMinutes);
            return perPlantPerDay >= 0.0;
        }

        /// <summary>
        /// Converts every legacy wild-flora identity used by older Workers/SmartReplant planning
        /// builds to the Workers-owned VW_Flora_* identity. This is intentionally name based so
        /// existing world ZDO slots migrate without requiring SmartReplant to be installed.
        /// </summary>
        internal static bool TryCanonicalizeLegacyIdentity(string raw, out Definition definition)
        {
            definition = null;
            string n = GardenWorkRuntime.NormalizePrefabName(raw ?? string.Empty);
            if (string.IsNullOrWhiteSpace(n)) return false;
            if (ByPrefab.TryGetValue(n, out definition)) return true;

            string id = null;
            if (n.StartsWith("SmartReplant_Flora_", StringComparison.OrdinalIgnoreCase))
                id = n.Substring("SmartReplant_Flora_".Length);
            else if (string.Equals(n, "RaspberryBush", StringComparison.OrdinalIgnoreCase) ||
                     n.IndexOf("Raspberry", StringComparison.OrdinalIgnoreCase) >= 0) id = "Raspberry";
            else if (string.Equals(n, "BlueberryBush", StringComparison.OrdinalIgnoreCase) ||
                     n.IndexOf("Blueberr", StringComparison.OrdinalIgnoreCase) >= 0) id = "Blueberries";
            else if (string.Equals(n, "CloudberryBush", StringComparison.OrdinalIgnoreCase) ||
                     n.IndexOf("Cloudberr", StringComparison.OrdinalIgnoreCase) >= 0) id = "Cloudberry";
            else if (string.Equals(n, "Pickable_Thistle", StringComparison.OrdinalIgnoreCase) ||
                     n.IndexOf("Thistle", StringComparison.OrdinalIgnoreCase) >= 0) id = "Thistle";
            else if (string.Equals(n, "Pickable_Dandelion", StringComparison.OrdinalIgnoreCase) ||
                     n.IndexOf("Dandelion", StringComparison.OrdinalIgnoreCase) >= 0) id = "Dandelion";
            else if (string.Equals(n, "Pickable_Mushroom_blue", StringComparison.OrdinalIgnoreCase) ||
                     n.IndexOf("MushroomBlue", StringComparison.OrdinalIgnoreCase) >= 0) id = "MushroomBlue";
            else if (string.Equals(n, "Pickable_Mushroom_yellow", StringComparison.OrdinalIgnoreCase) ||
                     n.IndexOf("MushroomYellow", StringComparison.OrdinalIgnoreCase) >= 0) id = "MushroomYellow";
            else if (string.Equals(n, "Pickable_Mushroom", StringComparison.OrdinalIgnoreCase) ||
                     n.IndexOf("Mushroom", StringComparison.OrdinalIgnoreCase) >= 0) id = "Mushroom";

            if (string.IsNullOrEmpty(id)) return false;
            return ByPrefab.TryGetValue(Prefix + id, out definition);
        }

        internal static bool TryGetPlanningData(Piece piece, out string harvestPrefab, out string plantPrefab,
            out string resourcePrefab, out int resourceAmount, out float spacing)
        {
            harvestPrefab = string.Empty;
            plantPrefab = string.Empty;
            resourcePrefab = string.Empty;
            resourceAmount = 0;
            spacing = 0f;
            if (piece == null || !TryGetDefinition(piece.gameObject, out Definition def)) return false;

            // VW_Flora_* is both the durable planning identity and the physical planted prefab.
            // v0.5.325 deliberately returns to the Workers clone because it can carry explicit
            // persistence + visual guards without modifying every natural vanilla berry bush.
            harvestPrefab = def.PrefabName;
            plantPrefab = def.PrefabName;
            resourcePrefab = def.ResourcePrefab;
            resourceAmount = def.Cost;
            spacing = def.Spacing;
            return true;
        }


        private static void RebindPickableHierarchyReferences(GameObject sourceRoot, GameObject cloneRoot)
        {
            if (sourceRoot == null || cloneRoot == null || ReferenceEquals(sourceRoot, cloneRoot)) return;
            Pickable source = sourceRoot.GetComponent<Pickable>();
            Pickable clone = cloneRoot.GetComponent<Pickable>();
            if (source == null || clone == null) return;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo[] fields = typeof(Pickable).GetFields(flags);
            int rebound = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                try
                {
                    object value = field.GetValue(source);
                    if (TryRemapHierarchyValue(sourceRoot.transform, cloneRoot.transform, field.FieldType, value, out object mapped))
                    {
                        field.SetValue(clone, mapped);
                        rebound++;
                    }
                }
                catch { }
            }

        }

        private static bool TryRemapHierarchyValue(Transform sourceRoot, Transform cloneRoot, Type fieldType, object value, out object mapped)
        {
            mapped = value;
            if (value == null || sourceRoot == null || cloneRoot == null) return false;

            if (typeof(UnityEngine.Object).IsAssignableFrom(fieldType))
            {
                if (!(value is UnityEngine.Object obj) || !TryGetTransform(obj, out Transform sourceTransform)) return false;
                if (!IsUnder(sourceRoot, sourceTransform)) return false;
                Transform target = FindByRelativePath(sourceRoot, cloneRoot, sourceTransform);
                if (target == null) return false;
                mapped = ConvertTransformToRequestedObject(target, fieldType);
                return mapped != null;
            }

            if (fieldType.IsArray && typeof(UnityEngine.Object).IsAssignableFrom(fieldType.GetElementType()))
            {
                Array original = value as Array;
                if (original == null) return false;
                Type elementType = fieldType.GetElementType();
                Array copy = Array.CreateInstance(elementType, original.Length);
                bool changed = false;
                for (int i = 0; i < original.Length; i++)
                {
                    object item = original.GetValue(i);
                    object outItem = item;
                    if (item is UnityEngine.Object itemObj && TryGetTransform(itemObj, out Transform sourceTransform) && IsUnder(sourceRoot, sourceTransform))
                    {
                        Transform target = FindByRelativePath(sourceRoot, cloneRoot, sourceTransform);
                        object replacement = target != null ? ConvertTransformToRequestedObject(target, elementType) : null;
                        if (replacement != null) { outItem = replacement; changed = true; }
                    }
                    copy.SetValue(outItem, i);
                }
                if (!changed) return false;
                mapped = copy;
                return true;
            }

            return false;
        }

        private static bool TryGetTransform(UnityEngine.Object obj, out Transform transform)
        {
            transform = null;
            if (obj is GameObject go) transform = go.transform;
            else if (obj is Transform t) transform = t;
            else if (obj is Component c) transform = c.transform;
            return transform != null;
        }

        private static bool IsUnder(Transform root, Transform node)
        {
            for (Transform t = node; t != null; t = t.parent)
                if (t == root) return true;
            return false;
        }

        private readonly struct PathStep
        {
            internal readonly string Name;
            internal readonly int SameNameIndex;
            internal PathStep(string name, int sameNameIndex) { Name = name ?? string.Empty; SameNameIndex = sameNameIndex; }
        }

        private static Transform FindByRelativePath(Transform sourceRoot, Transform cloneRoot, Transform sourceNode)
        {
            if (sourceNode == sourceRoot) return cloneRoot;

            // Do not use raw sibling indices here. Jotunn/Unity may insert helper children into a
            // cloned network prefab, making the same numeric sibling index point at a completely
            // different visual. That is fatal for Pickable.m_hideWhenPicked: if it is rebound from
            // model/Berrys to model (or another sibling), harvesting hides the entire bush. Resolve
            // the path by child name plus duplicate-name ordinal instead. This also handles the
            // repeated Cloudberry "berry" children deterministically.
            var path = new Stack<PathStep>();
            Transform current = sourceNode;
            while (current != null && current != sourceRoot)
            {
                Transform parent = current.parent;
                if (parent == null) return null;
                int sameNameIndex = 0;
                for (int i = 0; i < current.GetSiblingIndex(); i++)
                    if (string.Equals(parent.GetChild(i).name, current.name, StringComparison.Ordinal)) sameNameIndex++;
                path.Push(new PathStep(current.name, sameNameIndex));
                current = parent;
            }
            if (current != sourceRoot) return null;

            Transform target = cloneRoot;
            while (path.Count > 0)
            {
                PathStep step = path.Pop();
                int seen = 0;
                Transform match = null;
                for (int i = 0; i < target.childCount; i++)
                {
                    Transform child = target.GetChild(i);
                    if (!string.Equals(child.name, step.Name, StringComparison.Ordinal)) continue;
                    if (seen++ == step.SameNameIndex) { match = child; break; }
                }
                if (match == null) return null;
                target = match;
            }
            return target;
        }

        internal static void EnsurePersistentBushVisual(Pickable pickable)
        {
            if (pickable == null || pickable.gameObject == null) return;
            string identity = GardenHarvestTargetFinder.GetIdentityName(pickable);
            if (!TryCanonicalizeLegacyIdentity(identity, out Definition def)) return;
            if (!string.Equals(def.Id, "Raspberry", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(def.Id, "Blueberries", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(def.Id, "Cloudberry", StringComparison.OrdinalIgnoreCase)) return;

            ZNetView nview = pickable.GetComponent<ZNetView>() ?? pickable.GetComponentInParent<ZNetView>();
            Transform root = nview != null ? nview.transform : pickable.transform;
            if (root == null) return;
            root.gameObject.SetActive(true);

            // Berry meshes are allowed to disappear while picked; the structural bush body must
            // remain visible. Resolve from the network root, not from the Pickable child: cloned
            // and vanilla berry prefabs do not always put Pickable on the same hierarchy level.
            string[] keepVisible = string.Equals(def.Id, "Cloudberry", StringComparison.OrdinalIgnoreCase)
                ? new[] { "high" }
                : new[] { "model", "model/low" };
            for (int i = 0; i < keepVisible.Length; i++)
            {
                Transform t = root.Find(keepVisible[i]);
                if (t == null) continue;
                t.gameObject.SetActive(true);
                Renderer[] renderers = t.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    Renderer renderer = renderers[r];
                    if (renderer == null) continue;
                    // Do not reactivate the berry child itself; only structural bush visuals.
                    string rendererName = renderer.transform != null ? renderer.transform.name : string.Empty;
                    if (rendererName.IndexOf("Berr", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    renderer.enabled = true;
                    renderer.forceRenderingOff = false;
                    if (!renderer.gameObject.activeSelf) renderer.gameObject.SetActive(true);
                }
            }
        }

        internal static void PrepareSpawnedInstance(GameObject instance)
        {
            if (instance == null || !TryCanonicalizeLegacyIdentity(instance.name, out _)) return;
            if (!instance.activeSelf) instance.SetActive(true);

            ZNetView nview = instance.GetComponent<ZNetView>() ?? instance.GetComponentInChildren<ZNetView>(true);
            ForcePersistent(nview);

            Pickable[] pickables = instance.GetComponentsInChildren<Pickable>(true);
            for (int i = 0; i < pickables.Length; i++)
            {
                Pickable pickable = pickables[i];
                if (pickable == null) continue;
                ForceNonDestructivePick(pickable);
                EnsurePersistentBushVisual(pickable);
            }
        }

        private static void ConfigurePersistentNetworkPrefab(GameObject prefab)
        {
            if (prefab == null) return;
            if (prefab.GetComponent<WorkersWildFloraVisualGuard>() == null)
                prefab.AddComponent<WorkersWildFloraVisualGuard>();
            ZNetView nview = prefab.GetComponent<ZNetView>() ?? prefab.GetComponentInChildren<ZNetView>(true);
            try
            {
                if (nview != null && ZNetViewPersistentField != null && ZNetViewPersistentField.FieldType == typeof(bool))
                    ZNetViewPersistentField.SetValue(nview, true);
            }
            catch { }

            Pickable[] pickables = prefab.GetComponentsInChildren<Pickable>(true);
            for (int i = 0; i < pickables.Length; i++) ForceNonDestructivePick(pickables[i]);
        }

        private static void ForcePersistent(ZNetView nview)
        {
            if (nview == null) return;
            try
            {
                if (ZNetViewPersistentField != null && ZNetViewPersistentField.FieldType == typeof(bool))
                    ZNetViewPersistentField.SetValue(nview, true);
            }
            catch { }

            try
            {
                ZDO zdo = nview.GetZDO();
                if (zdo != null && ZdoSetPersistentMethod != null)
                    ZdoSetPersistentMethod.Invoke(zdo, new object[] { true });
            }
            catch { }
        }

        private static void ForceNonDestructivePick(Pickable pickable)
        {
            if (pickable == null || DestroyWhenPickedField == null || DestroyWhenPickedField.FieldType != typeof(bool)) return;
            try { DestroyWhenPickedField.SetValue(pickable, false); } catch { }
        }

        private static void ReconcileManagedWildGrowthSlot(Pickable pickable)
        {
            if (pickable == null) return;
            ZNetView nview = pickable.GetComponent<ZNetView>() ?? pickable.GetComponentInParent<ZNetView>();
            if (!GardenTargetFinder.TryReadWildGrowthMarker(nview, out int slotId, out string workAreaId)) return;

            // v0.5.386: old race conditions can leave two physical VW_Flora objects with the same
            // persistent Workzone+slot identity. Resolve this only on spawn/load events (the visual
            // guard already schedules these checks); there is no recurring world scan. The lower
            // stable ZDO id wins deterministically so both peers reach the same decision.
            if (TryRemoveDuplicateManagedWildGrowth(pickable, nview, slotId, workAreaId)) return;

            GardenReplantPlanRuntime.ConfirmPersistentWildGrowthSlot(workAreaId, slotId);
        }

        private static bool TryRemoveDuplicateManagedWildGrowth(Pickable pickable, ZNetView nview, int slotId, string workAreaId)
        {
            if (pickable == null || nview == null || !nview.IsValid() || slotId <= 0 || string.IsNullOrEmpty(workAreaId)) return false;
            ZDO currentZdo = null;
            try { currentZdo = nview.GetZDO(); } catch { }
            if (currentZdo == null) return false;

            string currentId = currentZdo.m_uid.ToString();
            string winningId = currentId;
            ZNetView winningView = nview;
            float searchRadius = 3f;
            Vector3 center = pickable.transform.position;

            WorkerWorldIndex.ForEachPickable(center, searchRadius, other =>
            {
                if (other == null || other == pickable) return;
                ZNetView otherView = other.GetComponent<ZNetView>() ?? other.GetComponentInParent<ZNetView>();
                if (!GardenTargetFinder.TryReadWildGrowthMarker(otherView, out int otherSlot, out string otherArea)) return;
                if (otherSlot != slotId || !string.Equals(otherArea, workAreaId, StringComparison.Ordinal)) return;
                ZDO otherZdo = null;
                try { otherZdo = otherView.GetZDO(); } catch { }
                if (otherZdo == null) return;
                string otherId = otherZdo.m_uid.ToString();
                if (string.CompareOrdinal(otherId, winningId) >= 0) return;
                winningId = otherId;
                winningView = otherView;
            });

            if (winningView == nview) return false;
            bool removed = TryDestroyNetworkedWildGrowthDuplicate(nview);
            if (removed)
                BackgroundMonitorRuntime.Note($"WildGrowth duplicate removed: area={workAreaId}, slot={slotId}, duplicate={currentId}, kept={winningId}", false);
            return removed;
        }

        private static bool TryDestroyNetworkedWildGrowthDuplicate(ZNetView nview)
        {
            if (nview == null || !nview.IsValid() || nview.gameObject == null) return false;
            try
            {
                if (!nview.IsOwner()) ClaimOwnershipMethod?.Invoke(nview, null);
            }
            catch { }

            // Never perform a local-only delete when ownership could not be acquired; that would
            // merely hide the duplicate on one client and let it return on the next load.
            try { if (!nview.IsOwner()) return false; } catch { return false; }

            try
            {
                if (ZNetScene.instance != null && ZNetSceneDestroyGameObjectMethod != null)
                {
                    ZNetSceneDestroyGameObjectMethod.Invoke(ZNetScene.instance, new object[] { nview.gameObject });
                    return true;
                }
            }
            catch { }

            return false;
        }

        internal static IEnumerator RestorePersistentBushVisualDelayed(Pickable pickable)
        {
            yield return null;
            if (pickable != null)
            {
                EnsurePersistentBushVisual(pickable);
                ReconcileManagedWildGrowthSlot(pickable);
            }
            yield return new WaitForSeconds(0.15f);
            if (pickable != null)
            {
                EnsurePersistentBushVisual(pickable);
                ReconcileManagedWildGrowthSlot(pickable);
            }
            yield return new WaitForSeconds(0.6f);
            if (pickable != null)
            {
                EnsurePersistentBushVisual(pickable);
                ReconcileManagedWildGrowthSlot(pickable);
            }
        }

        private static object ConvertTransformToRequestedObject(Transform target, Type requested)
        {
            if (target == null) return null;
            if (requested == typeof(GameObject)) return target.gameObject;
            if (requested == typeof(Transform) || requested.IsAssignableFrom(typeof(Transform))) return target;
            if (typeof(Component).IsAssignableFrom(requested)) return target.GetComponent(requested);
            return null;
        }

        private void TryRegister()
        {
            try
            {
                Assembly jotunn = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Jotunn", StringComparison.OrdinalIgnoreCase));
                Type prefabManagerType = jotunn?.GetType("Jotunn.Managers.PrefabManager", false);
                object prefabManager = prefabManagerType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                MethodInfo getPrefab = prefabManagerType?.GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                MethodInfo clonePrefab = prefabManagerType?.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "CreateClonedPrefab" && m.GetParameters().Length == 2 &&
                                         m.GetParameters()[0].ParameterType == typeof(string) &&
                                         m.GetParameters()[1].ParameterType == typeof(string));
                MethodInfo addPrefab = prefabManagerType?.GetMethod("AddPrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(GameObject) }, null);
                if (prefabManager == null || getPrefab == null || clonePrefab == null || addPrefab == null)
                {
                    _status = "Jotunn PrefabManager clone/register API unavailable";
                    return;
                }

                GameObject cultivator = getPrefab.Invoke(prefabManager, new object[] { "Cultivator" }) as GameObject;
                PieceTable table = cultivator != null
                    ? cultivator.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces
                    : null;
                if (table?.m_pieces == null)
                {
                    _status = "Cultivator PieceTable not ready";
                    return;
                }

                int ready = 0;
                for (int i = 0; i < Definitions.Length; i++)
                {
                    Definition def = Definitions[i];
                    GameObject prefab = getPrefab.Invoke(prefabManager, new object[] { def.PrefabName }) as GameObject;
                    bool newlyCreated = false;
                    if (prefab == null)
                    {
                        GameObject source = getPrefab.Invoke(prefabManager, new object[] { def.SourcePrefab }) as GameObject;
                        if (source == null)
                        {
                            _debugLog?.Invoke($"[WildFlora] source prefab missing: {def.SourcePrefab}");
                            continue;
                        }
                        prefab = clonePrefab.Invoke(prefabManager, new object[] { def.PrefabName, def.SourcePrefab }) as GameObject;
                        newlyCreated = prefab != null;
                    }
                    if (prefab == null) continue;

                    // Berry bushes are persistent Pickables whose visual state is controlled by
                    // hierarchy references such as Pickable.m_hideWhenPicked. Jotunn clones the
                    // GameObject hierarchy, but Unity object fields can still reference children
                    // of the vanilla source prefab. That makes the placed VW_Flora_* bush exist
                    // network-wise while its own berry/branch visuals never become authoritative.
                    // Rebind every Pickable hierarchy reference to the corresponding child on the
                    // Workers clone before the prefab is registered/used by the Shaman.
                    GameObject vanillaSource = getPrefab.Invoke(prefabManager, new object[] { def.SourcePrefab }) as GameObject;
                    RebindPickableHierarchyReferences(vanillaSource, prefab);
                    ConfigurePersistentNetworkPrefab(prefab);

                    Piece piece = prefab.GetComponent<Piece>();
                    if (piece == null) piece = prefab.AddComponent<Piece>();
                    piece.m_name = def.DisplayName;
                    piece.m_description = "Mit P als Feld planen. Beim Planen wird nichts verbraucht; der Schamane holt spaeter das benoetigte Material und setzt die Pflanze. Die Vorschau nutzt direkt das Vanilla-Pflanzenmodell.";

                    GameObject item = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(def.ResourcePrefab) : null;
                    if (item == null) item = getPrefab.Invoke(prefabManager, new object[] { def.ResourcePrefab }) as GameObject;
                    ItemDrop itemDrop = item != null ? item.GetComponent<ItemDrop>() : null;
                    if (itemDrop == null)
                    {
                        _debugLog?.Invoke($"[WildFlora] resource ItemDrop missing: {def.ResourcePrefab} for {def.PrefabName}");
                        continue;
                    }

                    piece.m_resources = new[]
                    {
                        new Piece.Requirement
                        {
                            m_resItem = itemDrop,
                            m_amount = def.Cost,
                            m_recover = true
                        }
                    };

                    // Use the harvested item icon for the Cultivator entry. This avoids any custom
                    // assets and makes the Workers-owned variant immediately recognizable.
                    try
                    {
                        Sprite[] icons = itemDrop.m_itemData?.m_shared?.m_icons;
                        if (icons != null && icons.Length > 0) piece.m_icon = icons[0];
                    }
                    catch { }

                    // CreateClonedPrefab only creates the clone. It does NOT register the prefab
                    // with Jotunn's PrefabManager/ZNetScene. Without AddPrefab, saved VW_Flora_*
                    // ZDOs are reported as "Missing prefab hash" on the next world load and Valheim
                    // permanently destroys those objects. Register every newly created network prefab
                    // before ZNetScene.Awake; existing registered prefabs need no second registration.
                    if (newlyCreated)
                        addPrefab.Invoke(prefabManager, new object[] { prefab });

                    // Keep only one Cultivator entry even across F12/plugin hot reloads.
                    bool exists = table.m_pieces.Any(x => x != null &&
                        string.Equals(GardenWorkRuntime.NormalizePrefabName(x.name), def.PrefabName, StringComparison.OrdinalIgnoreCase));
                    if (!exists) table.m_pieces.Add(prefab);
                    ready++;
                }

                if (ready == Definitions.Length)
                {
                    _registered = true;
                    _status = $"registered {ready}/{Definitions.Length}";
                    _infoLog?.Invoke($"[WildFlora] Workers-native Wildwuchs registered: {ready} Cultivator flora pieces; SmartReplant not used by this path.");
                    GardenWorkRuntime.InvalidateRecipeCache();
                }
                else
                {
                    _status = $"partial {ready}/{Definitions.Length}; retrying";
                }
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _status = $"registration pending: {root.GetType().Name}: {root.Message}";
                _debugLog?.Invoke("[WildFlora] " + _status);
            }
        }
    }

    /// <summary>
    /// One-shot load/spawn guard for Workers berry bushes. No Update/scan loop: Awake schedules a
    /// few delayed structural-visual repairs on the always-active plugin runner so loaded picked
    /// bushes remain visible even when Valheim applies their picked state after the prefab awakes.
    /// </summary>
    internal sealed class WorkersWildFloraVisualGuard : MonoBehaviour
    {
        private void Awake()
        {
            Pickable pickable = GetComponent<Pickable>() ?? GetComponentInChildren<Pickable>(true);
            if (pickable == null) return;
            WorkersWildFloraRuntime.EnsurePersistentBushVisual(pickable);
            if (Plugin.Instance != null)
                Plugin.Instance.StartCoroutine(WorkersWildFloraRuntime.RestorePersistentBushVisualDelayed(pickable));
        }
    }

}
