using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Optional oversized wooden rest chair for the Fuling family.  It shares the same durable
    /// FulingCamp HomeAnchor contract as the authentic village bed, so players can choose the
    /// visual that suits the worker.  In particular the wider chair gives a Fuling Berserker a
    /// believable place to rest without forcing every Fuling onto the small goblin_bed.
    /// </summary>
    internal sealed class FulingRestChairPieceRuntime
    {
        internal const string PrefabName = "ValheimWorkers_FulingRestChair";
        internal const string BasePrefabName = "bed";
        private const string ChairVisualPrefabName = "piece_chair02";
        private const float ChairVisualScale = 1.65f;

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private float _nextAttempt;
        private float _attemptDeadline;
        private bool _registered;
        private bool _gaveUp;
        private string _status = "waiting for Jotunn PieceManager";

        internal string Status => _registered ? "registered" : _status;

        internal FulingRestChairPieceRuntime(Action<string> debugLog, Action<string> infoLog)
        {
            _debugLog = debugLog;
            _infoLog = infoLog;
        }

        internal void Begin()
        {
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
            if (float.IsPositiveInfinity(_attemptDeadline)) _attemptDeadline = Time.unscaledTime + 15f;
            if (Time.unscaledTime > _attemptDeadline)
            {
                _gaveUp = true;
                _status = "Jotunn reflection registration unavailable; Fuling-Bett remains active";
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
                    _status = "Jotunn custom-piece API types not found";
                    return;
                }

                object manager = pieceManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                if (manager == null) { _status = "Jotunn PieceManager.Instance not ready"; return; }

                MethodInfo getPiece = pieceManagerType.GetMethod("GetPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (getPiece != null && getPiece.Invoke(manager, new object[] { PrefabName }) != null)
                {
                    _registered = true;
                    _status = "already registered via Jotunn";
                    return;
                }

                object config = Activator.CreateInstance(pieceConfigType);
                SetProperty(config, "Name", "Fuling-Ruhesitz");
                SetProperty(config, "Description", "Großer, einfacher Holzstuhl als alternative Ruhestätte für Fulings. Passt besonders gut zum Fuling-Berserker.");
                SetProperty(config, "PieceTable", "Hammer");
                SetProperty(config, "Category", "Furniture");
                SetProperty(config, "CraftingStation", "piece_workbench");
                SetProperty(config, "AllowedInDungeons", false);
                SetProperty(config, "Enabled", true);

                MethodInfo addRequirement = pieceConfigType.GetMethod("AddRequirement", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(int), typeof(bool) }, null);
                if (addRequirement == null) { _status = "Jotunn PieceConfig.AddRequirement(string,int,bool) not found"; return; }
                addRequirement.Invoke(config, new object[] { "Wood", 6, true });

                ConstructorInfo ctor = customPieceType.GetConstructor(new[] { typeof(string), typeof(string), pieceConfigType });
                if (ctor == null) { _status = "Jotunn CustomPiece(name,baseName,PieceConfig) constructor not found"; return; }

                object customPiece = ctor.Invoke(new object[] { PrefabName, BasePrefabName, config });
                GameObject prefab = customPieceType.GetProperty("PiecePrefab", BindingFlags.Instance | BindingFlags.Public)?.GetValue(customPiece) as GameObject;
                if (prefab == null) { _status = "Jotunn created no PiecePrefab"; return; }

                StripFurnitureInteraction(prefab);
                Vector3 restPosition = new Vector3(0f, 0.83f, 0.04f);
                Vector3 restEuler = new Vector3(0f, 180f, 0f);
                ApplyChairVisual(prefab, jotunn, ref restPosition, ref restEuler);

                FulingHomeInteractable interactable = prefab.GetComponent<FulingHomeInteractable>();
                if (interactable == null) interactable = prefab.AddComponent<FulingHomeInteractable>();
                interactable.ConfigureDisplayName("Fuling-Ruhesitz");

                WorkerRestStationFactory.Attach(
                    prefab,
                    RestStationType.Fuling,
                    new Vector3(0f, 0f, -2.2f),
                    restPosition,
                    new Vector3(1.65f, 0f, -1.75f),
                    restEuler);
                WorkerRestStationFactory.EnsurePlacementCollider(prefab, new Vector3(0f, 0.85f, 0f), new Vector3(2.15f, 1.75f, 2.15f));
                WorkerRestStationFactory.EnsureRestSupportCollider(prefab, new Vector3(0f, 0.04f, 0f), new Vector3(1.35f, 0.08f, 1.35f));

                MethodInfo addPiece = pieceManagerType.GetMethod("AddPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { customPieceType }, null);
                if (addPiece == null) { _status = "Jotunn PieceManager.AddPiece(CustomPiece) not found"; return; }
                object result = addPiece.Invoke(manager, new[] { customPiece });
                if (result is bool ok && !ok)
                {
                    if (getPiece != null && getPiece.Invoke(manager, new object[] { PrefabName }) != null)
                    {
                        _registered = true;
                        _status = "already registered via Jotunn";
                        return;
                    }
                    _status = "Jotunn rejected custom piece (duplicate or invalid)";
                    return;
                }

                _registered = true;
                _status = "registered via optional Jotunn reflection";
                _infoLog?.Invoke("[RestStation] Registered Fuling-Ruhesitz");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                _status = $"Fuling rest-chair registration pending: {inner.GetType().Name}: {inner.Message}";
                _debugLog?.Invoke(_status);
            }
            catch (Exception ex)
            {
                _status = $"Fuling rest-chair registration pending: {ex.GetType().Name}: {ex.Message}";
                _debugLog?.Invoke(_status);
            }
        }

        private void ApplyChairVisual(GameObject prefab, Assembly jotunn, ref Vector3 restPosition, ref Vector3 restEuler)
        {
            try
            {
                GameObject chairSource = ResolvePrefab(jotunn, ChairVisualPrefabName);
                if (chairSource == null)
                {
                    _debugLog?.Invoke("[RestStation] Fuling-Ruhesitz visual source piece_chair02 missing; keeping hidden-bed fail-safe geometry.");
                    return;
                }

                // Read the vanilla chair's authored attach point before cloning only its static mesh.
                // This gives the Worker rest point the same orientation/seat location the player chair uses.
                try
                {
                    Chair chair = chairSource.GetComponentInChildren<Chair>(true);
                    if (chair != null && chair.m_attachPoint != null)
                    {
                        Vector3 attachLocal = chairSource.transform.InverseTransformPoint(chair.m_attachPoint.position);
                        Quaternion attachLocalRot = Quaternion.Inverse(chairSource.transform.rotation) * chair.m_attachPoint.rotation;
                        restPosition = Vector3.Scale(attachLocal, new Vector3(ChairVisualScale, ChairVisualScale, ChairVisualScale));
                        restEuler = attachLocalRot.eulerAngles;
                    }
                }
                catch { }

                Renderer[] original = prefab.GetComponentsInChildren<Renderer>(true);
                GameObject visual = StaticVisualCloneRuntime.Create(chairSource, prefab.transform, "Workers_FulingRestChairVisual_v1");
                if (visual == null || visual.GetComponentsInChildren<Renderer>(true).Length == 0)
                {
                    if (visual != null) UnityEngine.Object.DestroyImmediate(visual);
                    _debugLog?.Invoke("[RestStation] Fuling-Ruhesitz chair clone had no renderers; keeping base visual fail-safe.");
                    return;
                }

                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one * ChairVisualScale;
                visual.SetActive(true);

                Renderer[] copied = visual.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < copied.Length; i++)
                {
                    if (copied[i] == null) continue;
                    copied[i].gameObject.SetActive(true);
                    copied[i].enabled = true;
                }

                for (int i = 0; i < original.Length; i++)
                    if (original[i] != null) original[i].enabled = false;

                _infoLog?.Invoke($"[RestStation] Fuling-Ruhesitz uses scaled vanilla wood chair visual ({ChairVisualScale:0.00}x).");
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Fuling-Ruhesitz visual fallback: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void StripFurnitureInteraction(GameObject prefab)
        {
            Component[] components = prefab.GetComponentsInChildren<Component>(true);
            for (int i = components.Length - 1; i >= 0; i--)
            {
                Component component = components[i];
                if (component == null) continue;
                string typeName = component.GetType().Name;
                if (string.Equals(typeName, "Bed", StringComparison.Ordinal) || string.Equals(typeName, "Chair", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(component);
            }
        }

        private static GameObject ResolvePrefab(Assembly jotunn, string prefabName)
        {
            try
            {
                Type type = jotunn.GetType("Jotunn.Managers.PrefabManager", false);
                object manager = type?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                MethodInfo method = type?.GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                return method?.Invoke(manager, new object[] { prefabName }) as GameObject;
            }
            catch { return null; }
        }

        private static void SetProperty(object target, string name, object value)
        {
            PropertyInfo property = target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite) property.SetValue(target, value);
        }
    }
}
