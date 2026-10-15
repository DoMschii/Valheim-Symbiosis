using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Dedicated Surtling recruitment structure. This is deliberately NOT a Worker home.
    /// It remains a normal wood-fuelled vanilla bonfire and only hosts the FireHole ritual.
    /// </summary>
    internal sealed class SurtlingSignalFirePieceRuntime
    {
        internal const string PrefabName = "ValheimWorkers_SurtlingSignalFire";
        internal const string BasePrefabName = "bonfire";

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private float _nextAttempt;
        private float _attemptDeadline;
        private bool _registered;
        private bool _gaveUp;
        private string _status = "waiting for Jotunn PieceManager";

        internal bool IsRegistered => _registered;
        internal string Status => _registered ? "registered" : _status;

        internal SurtlingSignalFirePieceRuntime(Action<string> debugLog, Action<string> infoLog)
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
            if (float.IsPositiveInfinity(_attemptDeadline))
                _attemptDeadline = Time.unscaledTime + 15f;
            if (Time.unscaledTime > _attemptDeadline)
            {
                _gaveUp = true;
                _status = "Jotunn reflection registration unavailable; other Workers features still active";
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
                GameObject existingPiece = getPiece?.Invoke(manager, new object[] { PrefabName }) as GameObject;
                if (existingPiece != null)
                {
                    _registered = true;
                    _status = "already registered via Jotunn; Workbench required";
                    return;
                }

                object config = Activator.CreateInstance(pieceConfigType);
                SetProperty(config, "Name", "Prächtiges Signalfeuer");
                SetProperty(config, "Description", "Ein großes Ritualfeuer zur Surtling-Rekrutierung. Innerhalb von 30 m eines natürlichen Feuergeysirs unter einem Dach errichten und 3 Valheim-Tage mit normalem Holz am Brennen halten. Nach vollendetem Ritual können per Shift+E jeweils 10 Surtling-Kerne geopfert werden, um weitere Feuerwärter zu rufen.");
                SetProperty(config, "PieceTable", "Hammer");
                SetProperty(config, "Category", "Furniture");
                SetProperty(config, "CraftingStation", "piece_workbench");
                SetProperty(config, "AllowedInDungeons", false);
                SetProperty(config, "Enabled", true);

                MethodInfo addRequirement = pieceConfigType.GetMethod("AddRequirement", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(int), typeof(bool) }, null);
                if (addRequirement == null) { _status = "Jotunn PieceConfig.AddRequirement(string,int,bool) not found"; return; }
                addRequirement.Invoke(config, new object[] { "Stone", 20, true });
                addRequirement.Invoke(config, new object[] { "FineWood", 10, true });
                addRequirement.Invoke(config, new object[] { "Bronze", 2, true });
                addRequirement.Invoke(config, new object[] { "SurtlingCore", 1, true });

                ConstructorInfo ctor = customPieceType.GetConstructor(new[] { typeof(string), typeof(string), pieceConfigType });
                if (ctor == null) { _status = "Jotunn CustomPiece(name,baseName,PieceConfig) constructor not found"; return; }
                object customPiece = ctor.Invoke(new object[] { PrefabName, BasePrefabName, config });
                GameObject prefab = customPieceType.GetProperty("PiecePrefab", BindingFlags.Instance | BindingFlags.Public)?.GetValue(customPiece) as GameObject;
                if (prefab == null) { _status = "Jotunn created no PiecePrefab"; return; }

                // The signal fire must be actively maintained for several Valheim days. Vanilla bonfires
                // burn one Wood for a very long time, so this dedicated ritual fire gets a smaller fuel
                // buffer and a five-minute burn time per Wood. That keeps normal Wood as the fuel while
                // requiring the player to return and tend the fire during the multi-day ritual.
                ConfigureRitualFireplace(prefab);

                if (prefab.GetComponent<SurtlingSignalFireInteractable>() == null)
                    prefab.AddComponent<SurtlingSignalFireInteractable>();
                if (prefab.GetComponent<SurtlingSignalFireRecruitmentRuntime>() == null)
                    prefab.AddComponent<SurtlingSignalFireRecruitmentRuntime>();

                MethodInfo addPiece = pieceManagerType.GetMethod("AddPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { customPieceType }, null);
                if (addPiece == null) { _status = "Jotunn PieceManager.AddPiece(CustomPiece) not found"; return; }
                object result = addPiece.Invoke(manager, new[] { customPiece });
                if (result is bool ok && !ok)
                {
                    existingPiece = getPiece?.Invoke(manager, new object[] { PrefabName }) as GameObject;
                    if (existingPiece != null)
                    {
                        _registered = true;
                        _status = "already registered via Jotunn; Workbench required";
                        return;
                    }
                    _status = "Jotunn rejected custom piece (duplicate or invalid)";
                    return;
                }

                _registered = true;
                _status = "registered via optional Jotunn reflection; Workbench required";
                _infoLog?.Invoke("[Recruitment] Registered Prächtiges Signalfeuer with Workbench requirement");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                _status = $"Jotunn Surtling signal-fire registration pending: {inner.GetType().Name}: {inner.Message}";
                _debugLog?.Invoke(_status);
            }
            catch (Exception ex)
            {
                _status = $"Surtling signal-fire registration pending: {ex.GetType().Name}: {ex.Message}";
                _debugLog?.Invoke(_status);
            }
        }

        private static void ConfigureRitualFireplace(GameObject prefab)
        {
            if (prefab == null) return;
            Fireplace fireplace = prefab.GetComponent<Fireplace>() ?? prefab.GetComponentInChildren<Fireplace>(true);
            if (fireplace == null) return;

            SetField(fireplace, "m_startFuel", 1f);
            SetField(fireplace, "m_maxFuel", 8f);
            SetField(fireplace, "m_secPerFuel", 300f);
            SetField(fireplace, "m_infiniteFuel", false);
            SetField(fireplace, "m_canRefill", true);
        }

        private static void SetField(object target, string name, object value)
        {
            if (target == null) return;
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null) return;
            try { field.SetValue(target, Convert.ChangeType(value, field.FieldType)); }
            catch
            {
                try { field.SetValue(target, value); } catch { }
            }
        }

        private static void SetProperty(object target, string name, object value)
        {
            if (target == null) return;
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite) property.SetValue(target, value);
        }
    }
}
