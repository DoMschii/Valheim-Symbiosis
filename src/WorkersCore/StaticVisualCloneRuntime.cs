using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Builds a renderer-only copy of a prefab without instantiating gameplay components such as
    /// ItemDrop, ZNetView, WearNTear, ParticleSystem or colliders. This avoids Awake side effects
    /// when item/world prefabs are reused purely as decoration on Worker rest stations.
    /// </summary>
    internal static class StaticVisualCloneRuntime
    {
        internal static GameObject Create(GameObject source, Transform parent, string name)
        {
            if (source == null || parent == null) return null;
            GameObject root = new GameObject(name);
            root.transform.SetParent(parent, false);
            CopyChildren(source.transform, root.transform, true);
            return root;
        }

        private static void CopyChildren(Transform source, Transform destination, bool isRoot)
        {
            if (source == null || destination == null) return;

            if (!isRoot)
            {
                destination.localPosition = source.localPosition;
                destination.localRotation = source.localRotation;
                destination.localScale = source.localScale;
                destination.gameObject.SetActive(source.gameObject.activeSelf);
            }

            MeshFilter sourceFilter = source.GetComponent<MeshFilter>();
            MeshRenderer sourceRenderer = source.GetComponent<MeshRenderer>();
            if (sourceFilter != null && sourceRenderer != null)
            {
                MeshFilter filter = destination.gameObject.AddComponent<MeshFilter>();
                filter.sharedMesh = sourceFilter.sharedMesh;
                MeshRenderer renderer = destination.gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = sourceRenderer.sharedMaterials;
                renderer.enabled = sourceRenderer.enabled;
                renderer.shadowCastingMode = sourceRenderer.shadowCastingMode;
                renderer.receiveShadows = sourceRenderer.receiveShadows;
            }

            // Most item/build-piece decorations use MeshRenderer. Skinned visuals are intentionally
            // skipped here because copying them safely requires their full bone hierarchy and can
            // reintroduce gameplay/animation side effects.
            for (int i = 0; i < source.childCount; i++)
            {
                Transform child = source.GetChild(i);
                if (child == null || !child.gameObject.activeSelf || !HasStaticMesh(child)) continue;
                GameObject childCopy = new GameObject(child.name);
                childCopy.transform.SetParent(destination, false);
                CopyChildren(child, childCopy.transform, false);
            }
        }

        private static bool HasStaticMesh(Transform node)
        {
            if (node == null) return false;
            if (node.GetComponent<MeshFilter>() != null && node.GetComponent<MeshRenderer>() != null) return true;
            for (int i = 0; i < node.childCount; i++)
                if (HasStaticMesh(node.GetChild(i))) return true;
            return false;
        }
    }
}
