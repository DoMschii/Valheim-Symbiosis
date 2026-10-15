using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Purely visual hammer used by the Fuling Smith while repairing structures.
    /// No ItemData is created, nothing is added to inventory and no durability exists.
    /// </summary>
    internal static class RepairHammerVisualRuntime
    {
        private const string HammerPrefabName = "Hammer";
        private const float DesiredHammerWorldSize = 0.42f;
        private static readonly Dictionary<int, GameObject> Visuals = new Dictionary<int, GameObject>();

        private static readonly MethodInfo ZNetSceneGetPrefabMethod = typeof(ZNetScene).GetMethod(
            "GetPrefab",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string) },
            null);

        internal static void SyncForWorker(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null)
            {
                Hide(worker);
                return;
            }

            bool repairing = false;
            WorkerState state = (WorkerState)zdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (state == WorkerState.Work &&
                WorkerZdo.TryReadTarget(zdo, out _, out string kind, out _) &&
                string.Equals(kind, BaseRepairWorkRuntime.TargetKind, StringComparison.Ordinal))
            {
                repairing = true;
            }

            if (repairing) Show(worker);
            else Hide(worker);
        }

        internal static void Show(Character worker)
        {
            if (worker == null) return;
            int key = worker.GetInstanceID();
            if (Visuals.TryGetValue(key, out GameObject existing) && existing != null)
            {
                if (!existing.activeSelf) existing.SetActive(true);
                return;
            }

            Transform hand = ResolveRightHand(worker.transform);
            if (hand == null)
            {
                Plugin.Log?.LogDebug("Repair hammer visual: right-hand bone not found.");
                return;
            }

            GameObject hammerPrefab = ResolveHammerPrefab();
            if (hammerPrefab == null)
            {
                Plugin.Log?.LogDebug("Repair hammer visual: vanilla Hammer prefab not available.");
                return;
            }

            Transform visualSource = FindVisualSource(hammerPrefab.transform);
            GameObject visual = StaticVisualCloneRuntime.Create(
                visualSource != null ? visualSource.gameObject : hammerPrefab,
                hand,
                "Workers_RepairHammerVisual");
            if (visual == null) return;

            // The clone is decoration only. These values place the vanilla hammer naturally in a
            // humanoid right hand without touching Humanoid/VisEquipment or inventory state.
            visual.transform.localPosition = new Vector3(0.015f, -0.035f, 0.015f);
            visual.transform.localRotation = Quaternion.Euler(8f, 88f, 176f);
            NormalizeVisualScale(visual, hand);

            Visuals[key] = visual;
            Plugin.Log?.LogDebug("Repair hammer visual attached to " + hand.name + ".");
        }

        internal static void Hide(Character worker)
        {
            if (worker == null) return;
            int key = worker.GetInstanceID();
            if (Visuals.TryGetValue(key, out GameObject visual) && visual != null && visual.activeSelf)
                visual.SetActive(false);
        }

        internal static void Destroy(Character worker)
        {
            if (worker == null) return;
            int key = worker.GetInstanceID();
            if (!Visuals.TryGetValue(key, out GameObject visual)) return;
            Visuals.Remove(key);
            if (visual != null) UnityEngine.Object.Destroy(visual);
        }

        internal static void ResetAll()
        {
            foreach (GameObject visual in Visuals.Values)
                if (visual != null) UnityEngine.Object.Destroy(visual);
            Visuals.Clear();
        }


        private static void NormalizeVisualScale(GameObject visual, Transform hand)
        {
            if (visual == null || hand == null) return;

            // Worker skeletons can carry a non-1 lossy scale. A plain localScale=1 therefore makes
            // an item visual inherit the rig scale and, depending on the source mesh hierarchy,
            // can turn the Hammer into a several-metre prop. First cancel the parent scale, then
            // normalize by the actual rendered world bounds so the result is independent of both
            // the Fuling rig and the Hammer prefab's internal import scale.
            Vector3 parentScale = hand.lossyScale;
            visual.transform.localScale = new Vector3(
                SafeInverseScale(parentScale.x),
                SafeInverseScale(parentScale.y),
                SafeInverseScale(parentScale.z));

            Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
            bool haveBounds = false;
            Bounds bounds = default(Bounds);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                if (!haveBounds)
                {
                    bounds = renderer.bounds;
                    haveBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!haveBounds) return;
            float currentSize = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (currentSize <= 0.0001f) return;

            float factor = Mathf.Clamp(DesiredHammerWorldSize / currentSize, 0.01f, 100f);
            visual.transform.localScale = Vector3.Scale(visual.transform.localScale, new Vector3(factor, factor, factor));
            Plugin.Log?.LogDebug($"Repair hammer visual normalized: {currentSize:0.000}m -> {DesiredHammerWorldSize:0.00}m (factor {factor:0.000}).");
        }

        private static float SafeInverseScale(float value)
        {
            float magnitude = Mathf.Abs(value);
            return magnitude > 0.0001f ? 1f / magnitude : 1f;
        }

        private static GameObject ResolveHammerPrefab()
        {
            ObjectDB db = ObjectDB.instance;
            if (db != null)
            {
                try
                {
                    GameObject item = db.GetItemPrefab(HammerPrefabName);
                    if (item != null) return item;
                }
                catch { }
            }

            ZNetScene scene = ZNetScene.instance;
            if (scene != null && ZNetSceneGetPrefabMethod != null)
            {
                try { return ZNetSceneGetPrefabMethod.Invoke(scene, new object[] { HammerPrefabName }) as GameObject; }
                catch { }
            }
            return null;
        }

        private static Transform ResolveRightHand(Transform root)
        {
            if (root == null) return null;

            Transform best = null;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null) continue;
                string n = t.name ?? string.Empty;
                string normalized = n.Replace("_", string.Empty).Replace(".", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

                if (normalized == "righthand" || normalized == "handright" || normalized == "rhand" || normalized == "handr")
                    return t;

                if (normalized.Contains("righthand") || normalized.Contains("handright"))
                    best = t;
            }
            return best;
        }

        private static Transform FindVisualSource(Transform root)
        {
            if (root == null) return null;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null) continue;
                string n = (t.name ?? string.Empty).ToLowerInvariant();
                if (n == "attach" || n == "attach_skin" || n == "attachobj" || n.Contains("attach"))
                    return t;
            }
            return root;
        }
    }
}
