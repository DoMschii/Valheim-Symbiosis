using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Upgraded FieldDepot. It keeps the complete normal FieldDepot behaviour and additionally
    /// advertises itself as a valid AirTransport endpoint. Ground workers can keep using it.
    /// </summary>
    internal sealed class AdvancedFieldDepotRuntime
    {
        internal const string PrefabName = "ValheimWorkers_AdvancedFieldDepot";
        internal const string BasePrefabName = "piece_chest_wood";

        // Air transport must not be constrained by the normal ground-logistics radius.
        // Every loaded Advanced FieldDepot registers here once its real Container is ready.
        // The AirHauler can then discover remote loaded endpoints without a world scan.
        private static readonly List<Container> LoadedAirDepots = new List<Container>(32);
        private sealed class PersistentDepotRecord
        {
            internal ZDO Zdo;
            internal Vector3 Position;
            internal long Owner;
            internal AirDepotMode Mode;
        }
        private static readonly Dictionary<ZDOID, PersistentDepotRecord> PersistentAirDepots = new Dictionary<ZDOID, PersistentDepotRecord>();
        private const string PendingBackgroundManifest = "vw_bg_pending_delivery";

        internal static void RegisterLoadedDepot(Container container)
        {
            if (container == null) return;
            PruneLoadedDepots();
            if (!LoadedAirDepots.Contains(container)) LoadedAirDepots.Add(container);
            ZNetView view = WorkerContainerRoleRuntime.GetNetworkView(container);
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo != null)
            {
                PersistentAirDepots[zdo.m_uid] = new PersistentDepotRecord
                {
                    Zdo = zdo,
                    Position = container.transform.position,
                    Owner = zdo.GetLong(WorkerContainerRoleRuntime.OwnerKey, 0L),
                    Mode = WorkerContainerRoleRuntime.GetAirDepotMode(container)
                };
            }
        }

        internal static void UnregisterLoadedDepot(Container container)
        {
            if (container == null) return;
            LoadedAirDepots.Remove(container);
        }

        internal static void ForEachLoadedDepot(Action<Container> visitor)
        {
            if (visitor == null) return;
            PruneLoadedDepots();
            for (int i = 0; i < LoadedAirDepots.Count; i++)
            {
                Container container = LoadedAirDepots[i];
                if (container != null) visitor(container);
            }
        }

        internal static int GetLoadedDepotCount()
        {
            PruneLoadedDepots();
            return LoadedAirDepots.Count;
        }

        private static void PruneLoadedDepots()
        {
            for (int i = LoadedAirDepots.Count - 1; i >= 0; i--)
            {
                if (LoadedAirDepots[i] == null) LoadedAirDepots.RemoveAt(i);
            }
        }

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private float _nextAttempt;
        private float _attemptDeadline;
        private bool _registered;
        private bool _gaveUp;
        private string _status = "waiting for Jotunn PieceManager";
        internal string Status => _registered ? "registered" : _status;

        internal static bool TryFindPersistentDepot(Vector3 near, long ownerId, AirDepotMode mode, out ZDO depotZdo, out Vector3 position)
        {
            depotZdo = null; position = default;
            float best = float.MaxValue;
            foreach (KeyValuePair<ZDOID, PersistentDepotRecord> pair in PersistentAirDepots)
            {
                PersistentDepotRecord record = pair.Value;
                if (record == null || record.Zdo == null || record.Mode != mode) continue;
                if (record.Owner != 0L && ownerId != 0L && record.Owner != ownerId) continue;
                float sqr = (record.Position - near).sqrMagnitude;
                if (sqr >= best) continue;
                best = sqr; depotZdo = record.Zdo; position = record.Position;
            }
            return depotZdo != null;
        }

        internal static int GetPendingBackgroundDelivery(ZDO depotZdo, string prefab)
        {
            if (depotZdo == null || string.IsNullOrEmpty(prefab)) return 0;
            Dictionary<string, int> manifest = ParseManifest(depotZdo.GetString(PendingBackgroundManifest, string.Empty));
            return manifest.TryGetValue(prefab, out int amount) ? Mathf.Max(0, amount) : 0;
        }

        internal static bool HasPendingBackgroundDelivery(ZDO depotZdo)
        {
            return depotZdo != null && !string.IsNullOrEmpty(depotZdo.GetString(PendingBackgroundManifest, string.Empty));
        }

        internal static void CollectPendingBackgroundPrefabs(ZDO depotZdo, List<string> target)
        {
            if (target == null) return;
            target.Clear();
            if (depotZdo == null) return;
            Dictionary<string, int> manifest = ParseManifest(depotZdo.GetString(PendingBackgroundManifest, string.Empty));
            foreach (KeyValuePair<string, int> pair in manifest)
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0) target.Add(pair.Key);
        }

        internal static void QueueBackgroundDelivery(ZDO depotZdo, string prefab, int amount)
        {
            if (depotZdo == null || string.IsNullOrEmpty(prefab) || amount <= 0) return;
            Dictionary<string, int> manifest = ParseManifest(depotZdo.GetString(PendingBackgroundManifest, string.Empty));
            manifest.TryGetValue(prefab, out int current);
            manifest[prefab] = current + amount;
            depotZdo.Set(PendingBackgroundManifest, WriteManifest(manifest));
            // v0.5.356: wake only the loaded destination container instead of relying on a
            // synchronized three-second poll on every container in the base.
            WorkerBackgroundDeliveryApplier.NotifyPending(depotZdo);
        }

        internal static bool TryTakePendingBackgroundDelivery(ZDO depotZdo, string prefab, int requested, out int taken)
        {
            taken = 0;
            if (depotZdo == null || string.IsNullOrEmpty(prefab) || requested <= 0) return false;
            Dictionary<string, int> manifest = ParseManifest(depotZdo.GetString(PendingBackgroundManifest, string.Empty));
            if (!manifest.TryGetValue(prefab, out int current) || current <= 0) return false;
            taken = Mathf.Min(requested, current);
            current -= taken;
            if (current <= 0) manifest.Remove(prefab); else manifest[prefab] = current;
            depotZdo.Set(PendingBackgroundManifest, WriteManifest(manifest));
            return taken > 0;
        }

        internal static void ApplyPendingBackgroundDeliveries(Container container, ZDO zdo)
        {
            if (container == null || zdo == null) return;
            string raw = zdo.GetString(PendingBackgroundManifest, string.Empty);
            if (string.IsNullOrEmpty(raw)) return;
            Dictionary<string, int> manifest = ParseManifest(raw);
            if (manifest.Count == 0) { zdo.Set(PendingBackgroundManifest, string.Empty); return; }
            Inventory inv = container.GetInventory();
            if (inv == null) return;
            Dictionary<string, int> remaining = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> pair in manifest)
            {
                int requested = pair.Value;
                int toApply = requested;
                if (WorkerContainerRoleRuntime.GetRole(container) == WorkerContainerRole.Import)
                {
                    int physical = WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, pair.Key);
                    int accepted = ContainerStockLimitRuntime.GetAcceptCapacity(container, pair.Key, physical);
                    toApply = Mathf.Min(requested, accepted);
                }

                int leftFromAttempt = toApply > 0 ? AddPrefabToInventory(inv, pair.Key, toApply) : toApply;
                int actuallyApplied = Mathf.Max(0, toApply - leftFromAttempt);
                int left = Mathf.Max(0, requested - actuallyApplied);
                if (left > 0) remaining[pair.Key] = left;
            }
            WorkerStorageRuntime.PersistContainer(container, inv);
            zdo.Set(PendingBackgroundManifest, WriteManifest(remaining));
        }

        private static int AddPrefabToInventory(Inventory inv, string prefabName, int amount)
        {
            if (inv == null || string.IsNullOrEmpty(prefabName) || amount <= 0) return amount;
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            if (prefab == null && ZNetScene.instance != null) prefab = ZNetScene.instance.GetPrefab(prefabName);
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop?.m_itemData == null) return amount;
            int left = amount;
            int stackMax = Mathf.Max(1, drop.m_itemData.m_shared?.m_maxStackSize ?? 1);
            while (left > 0)
            {
                int chunk = Mathf.Min(left, stackMax);
                ItemDrop.ItemData data = drop.m_itemData.Clone();
                data.m_dropPrefab = prefab;
                data.m_stack = chunk;
                if (!WorkerStorageRuntime.TryAddItem(inv, data)) break;
                left -= chunk;
            }
            return left;
        }

        private static Dictionary<string, int> ParseManifest(string raw)
        {
            Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw)) return result;
            string[] entries = raw.Split(';');
            for (int i = 0; i < entries.Length; i++)
            {
                int eq = entries[i].IndexOf('=');
                if (eq <= 0) continue;
                string key = entries[i].Substring(0, eq);
                if (!int.TryParse(entries[i].Substring(eq + 1), out int value) || value <= 0) continue;
                result[key] = value;
            }
            return result;
        }

        private static string WriteManifest(Dictionary<string, int> manifest)
        {
            if (manifest == null || manifest.Count == 0) return string.Empty;
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, int> pair in manifest)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value <= 0) continue;
                if (b.Length > 0) b.Append(';');
                b.Append(pair.Key).Append('=').Append(pair.Value);
            }
            return b.ToString();
        }

        internal AdvancedFieldDepotRuntime(Action<string> debugLog, Action<string> infoLog)
        {
            _debugLog = debugLog;
            _infoLog = infoLog;
        }

        internal void Begin()
        {
            // Custom networked pieces must exist in Jotunn before ZNetScene.Awake loads world ZDOs.
            // Register exactly when vanilla prefabs become cloneable; late Update retries are only a fallback.
            _attemptDeadline = float.PositiveInfinity;
            if (JotunnPrefabLifecycleRuntime.Subscribe(TryRegister, _debugLog))
            {
                if (!_registered) _status = "waiting for Jotunn vanilla prefabs";
                return;
            }

            _attemptDeadline = Time.unscaledTime + 15f;
            TryRegister();
        }

        internal void Tick()
        {
            if (_registered || _gaveUp) return;
            if (JotunnPrefabLifecycleRuntime.WaitingForVanillaPrefabs) return;
            if (float.IsPositiveInfinity(_attemptDeadline))
                _attemptDeadline = Time.unscaledTime + 15f;
            if (Time.unscaledTime > _attemptDeadline)
            {
                _gaveUp = true;
                _status = "Jotunn registration unavailable";
                return;
            }
            if (Time.unscaledTime < _nextAttempt) return;
            _nextAttempt = Time.unscaledTime + 1.5f;
            TryRegister();
        }

        private void TryRegister()
        {
            try
            {
                Assembly jotunn = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Jotunn", StringComparison.OrdinalIgnoreCase));
                if (jotunn == null) { _status = "Jotunn not loaded"; return; }

                Type pieceConfigType = jotunn.GetType("Jotunn.Configs.PieceConfig", false);
                Type customPieceType = jotunn.GetType("Jotunn.Entities.CustomPiece", false);
                Type pieceManagerType = jotunn.GetType("Jotunn.Managers.PieceManager", false);
                if (pieceConfigType == null || customPieceType == null || pieceManagerType == null)
                {
                    _status = "Jotunn custom-piece API types missing";
                    return;
                }

                object manager = pieceManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                if (manager == null) { _status = "PieceManager not ready"; return; }

                MethodInfo getPiece = pieceManagerType.GetMethod("GetPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (getPiece != null && getPiece.Invoke(manager, new object[] { PrefabName }) != null)
                {
                    _registered = true;
                    _status = "already registered";
                    return;
                }

                object config = Activator.CreateInstance(pieceConfigType);
                SetProperty(config, "Name", "Erweitertes Felddepot");
                SetProperty(config, "Description", "FieldDepot-Upgrade mit Luftfracht-Unterstuetzung. Per Shift+E als Aussenposten oder Base-Luftfracht-Hub markieren. Lokale Worker und Bodentraeger koennen es weiterhin normal benutzen.");
                SetProperty(config, "PieceTable", "Hammer");
                SetProperty(config, "Category", "Furniture");
                SetProperty(config, "CraftingStation", "piece_workbench");
                SetProperty(config, "AllowedInDungeons", false);
                SetProperty(config, "Enabled", true);

                MethodInfo addReq = pieceConfigType.GetMethod("AddRequirement", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(int), typeof(bool) }, null);
                if (addReq == null) { _status = "AddRequirement unavailable"; return; }
                addReq.Invoke(config, new object[] { "Wood", 10, true });
                addReq.Invoke(config, new object[] { "FineWood", 8, true });
                addReq.Invoke(config, new object[] { "Silver", 2, true });

                ConstructorInfo ctor = customPieceType.GetConstructor(new[] { typeof(string), typeof(string), pieceConfigType });
                if (ctor == null) { _status = "CustomPiece ctor unavailable"; return; }
                object customPiece = ctor.Invoke(new object[] { PrefabName, BasePrefabName, config });
                GameObject prefab = customPieceType.GetProperty("PiecePrefab", BindingFlags.Instance | BindingFlags.Public)?.GetValue(customPiece) as GameObject;
                if (prefab == null) { _status = "no PiecePrefab"; return; }

                if (prefab.GetComponent<AdvancedFieldDepotMarker>() == null)
                    prefab.AddComponent<AdvancedFieldDepotMarker>();

                ApplyAirFreightFunnelVisual(prefab, jotunn);

                MethodInfo addPiece = pieceManagerType.GetMethod("AddPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { customPieceType }, null);
                if (addPiece == null) { _status = "AddPiece unavailable"; return; }
                addPiece.Invoke(manager, new[] { customPiece });

                _registered = true;
                _status = "registered";
                _infoLog?.Invoke("[Storage] Registered Erweitertes Felddepot (AirTransport)");
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _status = $"Advanced FieldDepot registration pending: {root.GetType().Name}: {root.Message}";
                _debugLog?.Invoke(_status);
            }
        }

        private void ApplyAirFreightFunnelVisual(GameObject prefab, Assembly jotunn)
        {
            if (prefab == null) return;
            try
            {
                // Visual-only rebuild copied from the player's actual Vanilla-piece construction.
                // Structure: open black-metal chest + 4 vertical posts + 4 straight 45° thatch roofs
                // + 4 INNER 45° roof corners. The eight roof pieces form one continuous cargo funnel.
                // Gameplay components/colliders remain those of the registered container prefab.
                Renderer[] originalRenderers = prefab.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < originalRenderers.Length; i++)
                    if (originalRenderers[i] != null) originalRenderers[i].enabled = false;

                Transform old = prefab.transform.Find("ValheimWorkers_AirFreightFunnelVisual");
                if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);

                GameObject visualRoot = new GameObject("ValheimWorkers_AirFreightFunnelVisual");
                visualRoot.transform.SetParent(prefab.transform, false);

                GameObject chest = ResolvePrefab(jotunn, "piece_chest_blackmetal") ?? ResolvePrefab(jotunn, "piece_chest_wood");
                GameObject pole = ResolvePrefab(jotunn, "wood_pole2") ?? ResolvePrefab(jotunn, "wood_pole");
                GameObject roof45 = ResolvePrefab(jotunn, "wood_roof_45");
                GameObject inner45 = ResolvePrefab(jotunn, "wood_roof_icorner_45");

                // Chest sits centered under the narrow end of the funnel. Keep only its static visuals and hide the lid.
                GameObject crate = CreateStaticVisual(chest, visualRoot.transform, "OpenAirFreightChest",
                    new Vector3(0f, 0.08f, 0f), Vector3.one, Vector3.zero);
                DisableNamedVisualParts(crate, "lid", "cover", "top");
                TintMetalAccentsSilver(crate);

                // Four 2 m posts, matching the four-corner support frame in the reference build.
                const float post = 1.10f;
                Vector3[] postPositions =
                {
                    new Vector3(-post, 1.0f, -post), new Vector3(post, 1.0f, -post),
                    new Vector3(-post, 1.0f,  post), new Vector3(post, 1.0f,  post)
                };
                for (int i = 0; i < postPositions.Length; i++)
                    CreateStaticVisual(pole, visualRoot.transform, "FunnelPost" + i, postPositions[i], Vector3.one, Vector3.zero);

                // The player's construction is effectively a 3x3 roof grid around an open center:
                // straight roof pieces on N/S/E/W and inner-corner pieces on all four diagonals.
                // Keep Vanilla scale 1:1 so the snap geometry/visual proportions remain intact.
                // One last geometry pass based on the player's hand-built reference:
                // the LOW edges point inward toward the chest while the HIGH edges sit on the outside frame.
                // v0.2.66 had this direction reversed, which made the assembly read like a little roof/house.
                const float cell = 1.15f;
                const float y = 1.28f;

                // Four straight 45° roof slopes, yawed 180° from the previous version so they fall INWARD.
                CreateStaticVisual(roof45, visualRoot.transform, "FunnelRoofNorth", new Vector3(0f, y,  cell), Vector3.one, new Vector3(0f, 180f, 0f));
                CreateStaticVisual(roof45, visualRoot.transform, "FunnelRoofEast",  new Vector3( cell, y, 0f), Vector3.one, new Vector3(0f, 270f, 0f));
                CreateStaticVisual(roof45, visualRoot.transform, "FunnelRoofSouth", new Vector3(0f, y, -cell), Vector3.one, new Vector3(0f,   0f, 0f));
                CreateStaticVisual(roof45, visualRoot.transform, "FunnelRoofWest",  new Vector3(-cell, y, 0f), Vector3.one, new Vector3(0f,  90f, 0f));

                // Four INNER 45° corners complete the same inward-facing funnel.
                CreateStaticVisual(inner45, visualRoot.transform, "FunnelCornerNE", new Vector3( cell, y,  cell), Vector3.one, new Vector3(0f, 180f, 0f));
                CreateStaticVisual(inner45, visualRoot.transform, "FunnelCornerSE", new Vector3( cell, y, -cell), Vector3.one, new Vector3(0f, 270f, 0f));
                CreateStaticVisual(inner45, visualRoot.transform, "FunnelCornerSW", new Vector3(-cell, y, -cell), Vector3.one, new Vector3(0f,   0f, 0f));
                CreateStaticVisual(inner45, visualRoot.transform, "FunnelCornerNW", new Vector3(-cell, y,  cell), Vector3.one, new Vector3(0f,  90f, 0f));

                _debugLog?.Invoke("Advanced FieldDepot visual: player-reference funnel registered (4x roof45 + 4x inner-corner45 + 4 posts + open black-metal chest).");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Advanced FieldDepot funnel visual fallback: {ex.GetType().Name}: {ex.Message}");
                Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++) if (renderers[i] != null) renderers[i].enabled = true;
            }
        }

        private static GameObject CreateStaticVisual(GameObject source, Transform parent, string name, Vector3 localPosition, Vector3 localScale, Vector3 localEuler)
        {
            if (source == null || parent == null) return null;
            GameObject clone = StaticVisualCloneRuntime.Create(source, parent, name);
            if (clone == null) return null;
            clone.transform.localPosition = localPosition;
            clone.transform.localRotation = Quaternion.Euler(localEuler);
            clone.transform.localScale = localScale;
            clone.SetActive(true);
            return clone;
        }

        private static void DisableNamedVisualParts(GameObject root, params string[] tokens)
        {
            if (root == null || tokens == null) return;
            Transform[] nodes = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < nodes.Length; i++)
            {
                Transform node = nodes[i];
                if (node == null || node == root.transform) continue;
                string n = node.name ?? string.Empty;
                for (int t = 0; t < tokens.Length; t++)
                {
                    if (string.IsNullOrEmpty(tokens[t]) || n.IndexOf(tokens[t], StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Renderer[] rs = node.GetComponentsInChildren<Renderer>(true);
                    for (int r = 0; r < rs.Length; r++) if (rs[r] != null) rs[r].enabled = false;
                    break;
                }
            }
        }

        private static void TintMetalAccentsSilver(GameObject root)
        {
            if (root == null) return;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null) continue;
                Material[] materials = renderer.materials;
                bool changed = false;
                for (int m = 0; m < materials.Length; m++)
                {
                    Material material = materials[m];
                    if (material == null) continue;
                    string materialName = material.name ?? string.Empty;
                    if (materialName.IndexOf("metal", StringComparison.OrdinalIgnoreCase) < 0 &&
                        materialName.IndexOf("iron", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    Material local = new Material(material);
                    local.name = material.name + "_ValheimWorkersSilver";
                    Color silver = new Color(0.72f, 0.76f, 0.80f, 1f);
                    if (local.HasProperty("_Color")) local.SetColor("_Color", silver);
                    else local.color = silver;
                    materials[m] = local;
                    changed = true;
                }
                if (changed) renderer.materials = materials;
            }
        }

        private static GameObject ResolvePrefab(Assembly jotunn, string prefabName)
        {
            if (jotunn == null || string.IsNullOrEmpty(prefabName)) return null;
            try
            {
                Type prefabManagerType = jotunn.GetType("Jotunn.Managers.PrefabManager", false);
                object manager = prefabManagerType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                MethodInfo getPrefab = prefabManagerType?.GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                return manager != null ? getPrefab?.Invoke(manager, new object[] { prefabName }) as GameObject : null;
            }
            catch { return null; }
        }

        private static void SetProperty(object target, string name, object value)
        {
            PropertyInfo p = target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p != null && p.CanWrite) p.SetValue(target, value);
        }
    }

    internal sealed class AdvancedFieldDepotMarker : MonoBehaviour
    {
        private bool _initialized;

        private void OnEnable()
        {
            if (_initialized) return;
            CancelInvoke(nameof(TryInitialize));
            Invoke(nameof(TryInitialize), 0.05f);
        }

        private void TryInitialize()
        {
            if (_initialized || !isActiveAndEnabled) return;

            Container container = GetComponent<Container>() ?? GetComponentInChildren<Container>(true);
            if (container == null)
            {
                Invoke(nameof(TryInitialize), 0.75f);
                return;
            }

            AdvancedFieldDepotRuntime.RegisterLoadedDepot(container);
            ZNetView view = WorkerContainerRoleRuntime.GetNetworkView(container);
            if (view == null || !view.IsValid() || !view.IsOwner())
            {
                Invoke(nameof(TryInitialize), 0.75f);
                return;
            }

            ZDO zdo = view.GetZDO();
            if (zdo == null)
            {
                Invoke(nameof(TryInitialize), 0.75f);
                return;
            }

            // Reconcile background deliveries exactly once when the physical depot becomes ready.
            // No permanent per-frame Update is needed after this one-time handoff.
            AdvancedFieldDepotRuntime.ApplyPendingBackgroundDeliveries(container, zdo);
            zdo.Set(WorkerContainerRoleRuntime.AirTransportKey, true);
            if (WorkerContainerRoleRuntime.GetRole(container) == WorkerContainerRole.Normal)
            {
                long owner = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
                WorkerContainerRoleRuntime.SetRole(container, WorkerContainerRole.FieldDepot, owner);
            }
            FieldDepotWorkzoneRuntime.TryAutoAssignNearestWorkArea(container, out _, out _);
            _initialized = true;
            CancelInvoke(nameof(TryInitialize));
        }

        private void OnDisable()
        {
            CancelInvoke(nameof(TryInitialize));
        }

        private void OnDestroy()
        {
            CancelInvoke(nameof(TryInitialize));
            Container container = GetComponent<Container>() ?? GetComponentInChildren<Container>(true);
            if (container != null) AdvancedFieldDepotRuntime.UnregisterLoadedDepot(container);
        }
    }
}
