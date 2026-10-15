using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Separate recruitment nest for Drake eggs. It intentionally does not act as a HomeAnchor;
    /// a hatched Drake follows the player and later needs the existing Drake landing pad.
    /// </summary>
    internal sealed class DrakeEggNestPieceRuntime
    {
        internal const string PrefabName = "ValheimWorkers_DrakeEggNest";
        internal const string BasePrefabName = "wood_floor";
        internal const string EggVisualName = "Workers_DrakeNest_EggVisual";

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private float _nextAttempt;
        private float _attemptDeadline;
        private bool _registered;
        private bool _gaveUp;
        private string _status = "waiting for Jotunn PieceManager";

        internal string Status => _registered ? "registered" : _status;

        internal DrakeEggNestPieceRuntime(Action<string> debugLog, Action<string> infoLog)
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
                _status = "Jotunn reflection registration unavailable; Drake egg nest disabled";
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
                SetProperty(config, "Name", "Drachenbrutnest");
                SetProperty(config, "Description", "Rekrutierungsnest fuer einen Drake. Testphase: 1 Drachenei einlegen; nach 20 Sek. bewegt es sich, nach 40 Sek. entstehen Risse und nach 60 Sek. schlüpft der Drake automatisch. Nach dem Test wird die Brutdauer wieder auf 2 Valheim-Tage gesetzt.");
                SetProperty(config, "PieceTable", "Hammer");
                SetProperty(config, "Category", "Furniture");
                SetProperty(config, "CraftingStation", "piece_workbench");
                SetProperty(config, "AllowedInDungeons", false);
                SetProperty(config, "Enabled", true);

                MethodInfo addReq = pieceConfigType.GetMethod("AddRequirement", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(int), typeof(bool) }, null);
                if (addReq == null) { _status = "AddRequirement unavailable"; return; }
                addReq.Invoke(config, new object[] { "Wood", 12, true });
                addReq.Invoke(config, new object[] { "WolfPelt", 4, true });
                addReq.Invoke(config, new object[] { "Crystal", 2, true });

                ConstructorInfo ctor = customPieceType.GetConstructor(new[] { typeof(string), typeof(string), pieceConfigType });
                if (ctor == null) { _status = "CustomPiece ctor unavailable"; return; }
                object customPiece = ctor.Invoke(new object[] { PrefabName, BasePrefabName, config });
                GameObject prefab = customPieceType.GetProperty("PiecePrefab", BindingFlags.Instance | BindingFlags.Public)?.GetValue(customPiece) as GameObject;
                if (prefab == null) { _status = "no PiecePrefab"; return; }

                AddNestVisuals(prefab, jotunn);
                WorkerRestStationFactory.EnsurePlacementCollider(prefab, new Vector3(0f, 0.18f, 0f), new Vector3(2.4f, 0.45f, 2.4f));
                if (prefab.GetComponent<DrakeEggNestInteractable>() == null) prefab.AddComponent<DrakeEggNestInteractable>();
                if (prefab.GetComponent<DrakeEggRecruitmentRuntime>() == null) prefab.AddComponent<DrakeEggRecruitmentRuntime>();

                MethodInfo addPiece = pieceManagerType.GetMethod("AddPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { customPieceType }, null);
                if (addPiece == null) { _status = "AddPiece unavailable"; return; }
                addPiece.Invoke(manager, new[] { customPiece });

                _registered = true;
                _status = "registered";
                _infoLog?.Invoke("[Recruitment] Registered Drachenbrutnest with visible straw/log nest + egg state");
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _status = $"Drake egg nest registration pending: {root.GetType().Name}: {root.Message}";
                _debugLog?.Invoke(_status);
            }
        }

        private void AddNestVisuals(GameObject prefab, Assembly jotunn)
        {
            if (prefab == null) return;

            try
            {
                // Keep the cloned wood_floor Piece/ZNetView/collider as infrastructure, but replace
                // its flat visible platform with a proper incubation nest built only from renderer
                // clones. This has no gameplay components or extra network objects.
                Renderer[] baseRenderers = prefab.GetComponentsInChildren<Renderer>(true);
                List<GameObject> created = new List<GameObject>();

                GameObject straw = ResolvePrefab(jotunn, "goblin_bed");
                if (straw != null)
                {
                    GameObject bedA = StaticVisualCloneRuntime.Create(straw, prefab.transform, "Workers_DrakeNest_Straw_A");
                    if (bedA != null)
                    {
                        bedA.transform.localPosition = new Vector3(0f, 0.09f, 0f);
                        bedA.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                        bedA.transform.localScale = new Vector3(0.88f, 0.48f, 0.78f);
                        created.Add(bedA);
                    }

                    GameObject bedB = StaticVisualCloneRuntime.Create(straw, prefab.transform, "Workers_DrakeNest_Straw_B");
                    if (bedB != null)
                    {
                        bedB.transform.localPosition = new Vector3(0f, 0.12f, 0.02f);
                        bedB.transform.localRotation = Quaternion.Euler(0f, 0f, 0f);
                        bedB.transform.localScale = new Vector3(0.72f, 0.44f, 0.72f);
                        created.Add(bedB);
                    }
                }

                GameObject pole = ResolvePrefab(jotunn, "wood_pole") ?? ResolveScenePrefab("wood_pole");
                if (pole != null)
                {
                    const int ringCount = 10;
                    for (int i = 0; i < ringCount; i++)
                    {
                        float angle = i * (360f / ringCount) + ((i & 1) == 0 ? 7f : -5f);
                        float rad = angle * Mathf.Deg2Rad;
                        float radius = 0.88f + ((i % 3) - 1) * 0.06f;
                        Vector3 pos = new Vector3(Mathf.Cos(rad) * radius, 0.18f + (i % 2) * 0.035f, Mathf.Sin(rad) * radius);

                        GameObject stick = StaticVisualCloneRuntime.Create(pole, prefab.transform, "Workers_DrakeNest_Ring_" + i);
                        if (stick == null) continue;
                        stick.transform.localPosition = pos;
                        stick.transform.localRotation = Quaternion.Euler(74f + (i % 3) * 4f, -angle + 90f, (i % 2 == 0 ? 11f : -9f));
                        stick.transform.localScale = new Vector3(0.72f, 0.52f + (i % 2) * 0.05f, 0.72f);
                        created.Add(stick);
                    }
                }

                GameObject dragonEgg = ResolvePrefab(jotunn, DrakeEggRecruitmentRuntime.EggPrefab) ?? ResolveScenePrefab(DrakeEggRecruitmentRuntime.EggPrefab);
                if (dragonEgg != null)
                {
                    GameObject eggVisual = StaticVisualCloneRuntime.Create(dragonEgg, prefab.transform, EggVisualName);
                    if (eggVisual != null)
                    {
                        eggVisual.transform.localPosition = new Vector3(0f, 0.50f, 0f);
                        eggVisual.transform.localRotation = Quaternion.Euler(0f, 22f, -4f);
                        eggVisual.transform.localScale = Vector3.one * 0.92f;
                        eggVisual.SetActive(false);
                        created.Add(eggVisual);
                    }
                }

                bool hasCustomRenderer = false;
                for (int i = 0; i < created.Count && !hasCustomRenderer; i++)
                    hasCustomRenderer = created[i] != null && created[i].GetComponentsInChildren<Renderer>(true).Length > 0;

                // Do not show the old wood-floor platform once the replacement nest rendered
                // successfully. The base object still supplies Piece/WearNTear/network behaviour.
                if (hasCustomRenderer)
                {
                    for (int i = 0; i < baseRenderers.Length; i++)
                        if (baseRenderers[i] != null) baseRenderers[i].enabled = false;
                }
                else
                {
                    _debugLog?.Invoke("[Recruitment] Drachenbrutnest custom visuals unavailable; keeping wood-floor fallback visible.");
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"[Recruitment] Drachenbrutnest visual fallback: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static GameObject ResolveScenePrefab(string prefabName)
        {
            try { return ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null; }
            catch { return null; }
        }

        private static GameObject ResolvePrefab(Assembly jotunn, string prefabName)
        {
            try
            {
                Type type = jotunn?.GetType("Jotunn.Managers.PrefabManager", false);
                object manager = type?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                MethodInfo method = type?.GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                return method?.Invoke(manager, new object[] { prefabName }) as GameObject;
            }
            catch { return null; }
        }

        private static void SetProperty(object target, string name, object value)
        {
            PropertyInfo p = target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p != null && p.CanWrite) p.SetValue(target, value);
        }
    }
}
