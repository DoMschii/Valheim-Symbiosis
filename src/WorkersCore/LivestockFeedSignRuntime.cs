using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class LivestockFeedSignMarker : MonoBehaviour
    {
        private void OnEnable() => LivestockFeedSignRegistry.Register(this);
        private void OnDisable() => LivestockFeedSignRegistry.Unregister(this);
        private void OnDestroy() => LivestockFeedSignRegistry.Unregister(this);

        internal ZNetView GetNetworkView()
        {
            ZNetView view = GetComponent<ZNetView>();
            if (view == null) view = GetComponentInParent<ZNetView>();
            return view;
        }

        internal Container GetContainer()
        {
            Container container = GetComponent<Container>();
            if (container == null) container = GetComponentInParent<Container>();
            if (container == null) container = GetComponentInChildren<Container>(true);
            return container;
        }

        internal bool IsFeedingTrough => LivestockFeedingRuntime.IsFeedingTrough(GetContainer());
    }

    internal static class LivestockFeedSignRegistry
    {
        private static readonly HashSet<LivestockFeedSignMarker> Loaded = new HashSet<LivestockFeedSignMarker>();

        internal static int LoadedCount => Loaded.Count;
        internal static void Register(LivestockFeedSignMarker marker) { if (marker != null) Loaded.Add(marker); }
        internal static void Unregister(LivestockFeedSignMarker marker) { if (marker != null) Loaded.Remove(marker); }

        internal static bool TryFindNearest(Vector3 center, float radius, out LivestockFeedSignMarker marker)
        {
            marker = null;
            float safe = Mathf.Max(5f, radius);
            float maxSqr = safe * safe;
            float best = float.MaxValue;
            foreach (LivestockFeedSignMarker candidate in Loaded)
            {
                if (candidate == null || !candidate.gameObject.activeInHierarchy) continue;
                Vector3 delta = candidate.transform.position - center;
                delta.y = 0f;
                float sqr = delta.sqrMagnitude;
                if (sqr > maxSqr || sqr >= best) continue;
                best = sqr;
                marker = candidate;
            }
            return marker != null;
        }

        internal static int CountInside(Vector3 center, float radius)
        {
            int count = 0;
            float safe = Mathf.Max(5f, radius);
            float maxSqr = safe * safe;
            foreach (LivestockFeedSignMarker candidate in Loaded)
            {
                if (candidate == null || !candidate.gameObject.activeInHierarchy) continue;
                Vector3 delta = candidate.transform.position - center;
                delta.y = 0f;
                if (delta.sqrMagnitude <= maxSqr) count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Buildable vanilla-sign clone used as the livestock feeding configuration/drop point.
    /// Shift+E opens the Workers feeding menu. Plain E intentionally remains the vanilla Sign editor.
    /// </summary>
    internal sealed class LivestockFeedSignPieceRuntime
    {
        internal const string PrefabName = "ValheimWorkers_LivestockFeedSign";
        internal const string BasePrefabName = "sign";

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private float _nextAttempt;
        private float _attemptDeadline;
        private bool _registered;
        private bool _gaveUp;
        private string _status = "waiting for Jotunn PieceManager";

        internal string Status => _registered ? "registered" : _status;

        internal LivestockFeedSignPieceRuntime(Action<string> debugLog, Action<string> infoLog)
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
                    _status = "Jotunn custom-piece API missing";
                    return;
                }

                object manager = pieceManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                if (manager == null) { _status = "PieceManager not ready"; return; }

                MethodInfo getPiece = pieceManagerType.GetMethod("GetPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                GameObject existing = getPiece?.Invoke(manager, new object[] { PrefabName }) as GameObject;
                if (existing != null)
                {
                    if (existing.GetComponent<LivestockFeedSignMarker>() == null) existing.AddComponent<LivestockFeedSignMarker>();
                    _registered = true;
                    _status = "already registered";
                    return;
                }

                object config = Activator.CreateInstance(pieceConfigType);
                SetProperty(config, "Name", "Tierpflege-Schild (Legacy)");
                SetProperty(config, "Description", "Legacy-Gehege-Marker. Neue Gehege verwenden den SmartStorage-Futtertrog direkt als Marker und Futterlager.");
                SetProperty(config, "PieceTable", "Hammer");
                SetProperty(config, "Category", "Workers");
                SetProperty(config, "CraftingStation", "piece_workbench");
                SetProperty(config, "AllowedInDungeons", false);
                SetProperty(config, "Enabled", false);

                MethodInfo addRequirement = pieceConfigType.GetMethod("AddRequirement", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(int), typeof(bool) }, null);
                if (addRequirement == null) { _status = "AddRequirement unavailable"; return; }
                addRequirement.Invoke(config, new object[] { "Wood", 2, true });

                ConstructorInfo ctor = customPieceType.GetConstructor(new[] { typeof(string), typeof(string), pieceConfigType });
                if (ctor == null) { _status = "CustomPiece ctor unavailable"; return; }
                object customPiece = ctor.Invoke(new object[] { PrefabName, BasePrefabName, config });
                GameObject prefab = customPieceType.GetProperty("PiecePrefab", BindingFlags.Instance | BindingFlags.Public)?.GetValue(customPiece) as GameObject;
                if (prefab == null) { _status = "no PiecePrefab"; return; }
                if (prefab.GetComponent<LivestockFeedSignMarker>() == null) prefab.AddComponent<LivestockFeedSignMarker>();

                MethodInfo addPiece = pieceManagerType.GetMethod("AddPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { customPieceType }, null);
                if (addPiece == null) { _status = "AddPiece unavailable"; return; }
                addPiece.Invoke(manager, new[] { customPiece });

                _registered = true;
                _status = "registered";
                _infoLog?.Invoke("Workers legacy Tierpflege-Schild registered but hidden; SmartStorage feeding trough is the primary pen marker.");
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _status = $"Tierpflege-Schild registration pending: {root.GetType().Name}: {root.Message}";
                _debugLog?.Invoke(_status);
            }
        }

        private static void SetProperty(object target, string name, object value)
        {
            if (target == null) return;
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                try { property.SetValue(target, value); } catch { }
                return;
            }
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { field.SetValue(target, value); } catch { }
            }
        }
    }
}
