using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal readonly struct SmartReplantGardenPlan
    {
        internal readonly string ResourcePrefab;
        internal readonly string PlantPrefab;
        internal readonly int ResourceAmount;
        internal readonly float Spacing;
        internal readonly bool SeedProduction;
        internal readonly string SeedResourcePrefab;
        internal readonly int SeedStock;
        internal readonly int SeedReserve;

        internal SmartReplantGardenPlan(string resourcePrefab, string plantPrefab, int resourceAmount, float spacing, bool seedProduction, string seedResourcePrefab, int seedStock, int seedReserve)
        {
            ResourcePrefab = resourcePrefab ?? string.Empty; PlantPrefab = plantPrefab ?? string.Empty; ResourceAmount = resourceAmount;
            Spacing = spacing; SeedProduction = seedProduction; SeedResourcePrefab = seedResourcePrefab ?? string.Empty; SeedStock = seedStock; SeedReserve = seedReserve;
        }
    }

    /// <summary>
    /// Optional reflection-only bridge to SmartReplant. Workers stays loadable without SmartReplant.
    /// v0.1.51 additionally recognizes SmartReplant's own planted flora and mature crop registry so
    /// Gatherers can harvest those objects without copying SmartReplant internals into Workers.
    /// </summary>
    internal static class SmartReplantBridge
    {
        private static readonly string[] ExpectedTypes =
        {
            "SmartReplant.PlantRegistry",
            "SmartReplant.CropRegistry",
            "SmartReplant.PlantRelationships",
            "SmartReplant.SmartSpacingService",
            "SmartReplant.FieldPlantingService",
            "SmartReplant.StorageService",
            "SmartReplant.FloraRegistry",
            "SmartReplant.SmartReplantPlacedFlora"
        };

        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static Action<string> _debugLog;
        private static Assembly _assembly;
        private static object _pluginInstance;
        private static object _registry;
        private static IDictionary _harvests;
        private static readonly HashSet<string> MaturePrefabNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static object _floraRegistry;
        private static MethodInfo _floraFind;
        private static Type _placedFloraType;
        private static bool _initialized;
        private static bool _reported;
        private static float _nextProbeAt;
        private static string _ensureReadyState = "not-called";

        internal static bool IsInstalled { get; private set; }
        internal static bool HasPlantRegistry => HasType("SmartReplant.PlantRegistry") || HasType("SmartReplant.CropRegistry");
        internal static bool HasPlantRelationships => HasType("SmartReplant.PlantRelationships");
        internal static bool HasSpacingService => HasType("SmartReplant.SmartSpacingService");
        internal static bool HasFieldPlantingService => HasType("SmartReplant.FieldPlantingService");
        internal static bool HasStorageService => HasType("SmartReplant.StorageService");
        internal static bool HasHarvestRegistry => _harvests != null;

        internal static bool PlantingRulesReady => IsInstalled && HasPlantRegistry;

        internal static void Initialize(Action<string> debugLog)
        {
            _debugLog = debugLog;
            Refresh();
        }

        internal static void Refresh()
        {
            Types.Clear();
            _assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "SmartReplant", StringComparison.OrdinalIgnoreCase));
            IsInstalled = _assembly != null;
            _initialized = true;
            _pluginInstance = null;
            _registry = null;
            _harvests = null;
            MaturePrefabNames.Clear();
            _floraRegistry = null;
            _floraFind = null;
            _placedFloraType = null;
            _ensureReadyState = "not-called";

            if (_assembly != null)
            {
                for (int i = 0; i < ExpectedTypes.Length; i++)
                {
                    string name = ExpectedTypes[i];
                    Type type = _assembly.GetType(name, false);
                    if (type != null) Types[name] = type;
                }

                TryBindRuntimeObjects();
            }

            if (_reported) return;
            _reported = true;

            if (!IsInstalled)
            {
                _debugLog?.Invoke("SmartReplant bridge: SmartReplant not loaded; Workers remains standalone.");
                return;
            }

            _debugLog?.Invoke(
                "SmartReplant bridge detected: " +
                $"PlantRegistry={HasPlantRegistry}, HarvestRegistry={HasHarvestRegistry}, FloraRegistry={_floraRegistry != null}, " +
                $"SmartSpacingService={HasSpacingService}, FieldPlantingService={HasFieldPlantingService}, StorageService={HasStorageService}. " +
                "Workers will recognize SmartReplant-owned harvestables without a hard assembly reference.");
        }

        internal static bool IsSmartReplantHarvestable(Pickable pickable)
        {
            if (pickable == null || pickable.gameObject == null) return false;

            string prefab = GetPrefabNameSafe(pickable.gameObject);
            if (string.IsNullOrEmpty(prefab)) prefab = NormalizePrefabName(pickable.gameObject.name);

            // Current SmartReplant persistent flora are self-identifying network prefabs. This is
            // safe even if plugin load order means the reflection bridge has not bound yet.
            if (prefab.StartsWith("SmartReplant_Flora_", StringComparison.OrdinalIgnoreCase))
                return true;

            EnsureInstalledBound();
            if (!IsInstalled) return false;
            if (_pluginInstance == null) TryBindRuntimeObjects();

            // Prefer SmartReplant's own mature prefab identities. Harvests maps mature prefab
            // hashes to CropDefinition values; each definition exposes the real planting Piece(s),
            // whose Plant.m_grownPrefabs are the authoritative harvest prefab objects. This avoids
            // relying on hash implementations or ZDO layout differences between SmartReplant builds.
            EnsureMaturePrefabNames();
            if (MaturePrefabNames.Contains(prefab)) return true;

            // Compatibility fallback for older/newer SmartReplant builds where the definition
            // shape could not be reflected. The actual network hash remains useful when available.
            if (_harvests != null)
            {
                try
                {
                    ZNetView view = FindNetworkView(pickable);
                    ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                    if (zdo != null && _harvests.Contains(zdo.GetPrefab())) return true;
                }
                catch { }
            }

            // Current/legacy planted flora carry a SmartReplant marker component. We deliberately
            // require that marker (or the custom prefab prefix above) so natural wild flora are not
            // harvested merely because SmartReplant knows about the same Pickable type.
            if (_placedFloraType != null)
            {
                try
                {
                    if (pickable.gameObject.GetComponent(_placedFloraType) != null ||
                        pickable.GetComponentInParent(_placedFloraType) != null)
                        return true;
                }
                catch { }
            }

            // FloraRegistry.Find(Pickable) is useful as a compatibility signal, but only together
            // with an explicit SmartReplant-prefab/marker; Find alone would also match natural flora.
            return false;
        }


        internal static bool IsRegisteredHarvestView(ZNetView view)
        {
            if (view == null || !view || !view.IsValid()) return false;
            EnsureInstalledBound();
            if (!IsInstalled) return false;
            if (_pluginInstance == null) TryBindRuntimeObjects();
            EnsureMaturePrefabNames();
            if (_harvests == null || _harvests.Count == 0) return false;
            try
            {
                ZDO zdo = view.GetZDO();
                return zdo != null && _harvests.Contains(zdo.GetPrefab());
            }
            catch { return false; }
        }

        internal static ZNetView FindNetworkView(Pickable pickable)
        {
            if (pickable == null) return null;
            ZNetView view = pickable.GetComponent<ZNetView>();
            if (view != null) return view;
            return pickable.GetComponentInParent<ZNetView>();
        }

        internal static bool TryGetSeedReserveInfo(string maturePrefab, out string seedResourcePrefab, out int reserve)
        {
            seedResourcePrefab = string.Empty;
            reserve = 0;
            if (!TryGetHarvestDefinition(maturePrefab, out object definition)) return false;
            try
            {
                object seedResource = GetMember(definition, "SeedResource");
                seedResourcePrefab = ComponentPrefabName(seedResource);

                object config = GetMember(definition, "Config");
                object reserveEntry = GetMember(config, "Reserve");
                reserve = ReadIntValue(reserveEntry);

                // Some generations omit SeedResource but still expose the normal planting Piece.
                if (string.IsNullOrEmpty(seedResourcePrefab))
                {
                    Piece normal = AsPiece(GetMemberAny(definition, "Normal", "NormalCropPiece", "Piece"));
                    if (TryReadSingleRequirement(normal, out string resource, out _)) seedResourcePrefab = resource;
                }
                return !string.IsNullOrEmpty(seedResourcePrefab) || reserve <= 0;
            }
            catch { return false; }
        }

        internal static bool TryBuildGardenPlan(string maturePrefab, int seedStock, out SmartReplantGardenPlan plan)
        {
            return TryBuildGardenPlanInternal(maturePrefab, seedStock, null, -1, out plan);
        }

        /// <summary>
        /// Builds the same SmartReplant-backed plan, but lets Workers supply the seed-production
        /// state from the FieldDepot hysteresis policy instead of SmartReplant's single reserve
        /// threshold. This keeps crop relationships data-driven for every supported vegetable.
        /// </summary>
        internal static bool TryBuildGardenPlanWithMode(string maturePrefab, int seedStock, bool seedProduction, int seedReserve, out SmartReplantGardenPlan plan)
        {
            return TryBuildGardenPlanInternal(maturePrefab, seedStock, seedProduction, Mathf.Max(0, seedReserve), out plan);
        }

        internal static bool SupportsSeedProductionResource(string seedResourcePrefab)
        {
            string wanted = NormalizePrefabName(seedResourcePrefab);
            if (string.IsNullOrEmpty(wanted)) return false;
            EnsureInstalledBound();
            if (!IsInstalled) return false;
            if (_pluginInstance == null) TryBindRuntimeObjects();
            if (_harvests == null || _harvests.Count == 0) return false;

            try
            {
                foreach (DictionaryEntry entry in _harvests)
                {
                    object definition = entry.Value;
                    if (definition == null) continue;
                    Piece seedPiece = AsPiece(GetMemberAny(definition, "SeedProduction", "SeedProductionPiece", "SeedPiece"));
                    Piece normal = AsPiece(GetMemberAny(definition, "Normal", "NormalCropPiece", "Piece"));
                    if (seedPiece == null || normal == null) continue;

                    string seedResource = ComponentPrefabName(GetMember(definition, "SeedResource"));
                    if (string.IsNullOrEmpty(seedResource) && TryReadSingleRequirement(normal, out string fallbackSeed, out _))
                        seedResource = fallbackSeed;
                    if (string.Equals(NormalizePrefabName(seedResource), wanted, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        internal static void CollectSeedProductionResources(List<string> output)
        {
            if (output == null) return;
            output.Clear();
            EnsureInstalledBound();
            if (!IsInstalled) return;
            if (_pluginInstance == null) TryBindRuntimeObjects();
            if (_harvests == null || _harvests.Count == 0) return;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (DictionaryEntry entry in _harvests)
                {
                    object definition = entry.Value;
                    if (definition == null) continue;
                    Piece seedPiece = AsPiece(GetMemberAny(definition, "SeedProduction", "SeedProductionPiece", "SeedPiece"));
                    Piece normal = AsPiece(GetMemberAny(definition, "Normal", "NormalCropPiece", "Piece"));
                    if (seedPiece == null || normal == null) continue;
                    string seedResource = ComponentPrefabName(GetMember(definition, "SeedResource"));
                    if (string.IsNullOrEmpty(seedResource) && TryReadSingleRequirement(normal, out string fallbackSeed, out _))
                        seedResource = fallbackSeed;
                    seedResource = NormalizePrefabName(seedResource);
                    if (!string.IsNullOrEmpty(seedResource) && seen.Add(seedResource)) output.Add(seedResource);
                }
            }
            catch { }
            output.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private static bool TryBuildGardenPlanInternal(string maturePrefab, int seedStock, bool? seedModeOverride, int reserveOverride, out SmartReplantGardenPlan plan)
        {
            plan = default;
            if (!TryGetHarvestDefinition(maturePrefab, out object definition)) return false;
            try
            {
                Piece normal = AsPiece(GetMemberAny(definition, "Normal", "NormalCropPiece", "Piece"));
                Piece seedPiece = AsPiece(GetMemberAny(definition, "SeedProduction", "SeedProductionPiece", "SeedPiece"));
                if (normal == null) return false;

                string seedResourcePrefab = string.Empty;
                object seedResource = GetMember(definition, "SeedResource");
                seedResourcePrefab = ComponentPrefabName(seedResource);
                if (string.IsNullOrEmpty(seedResourcePrefab) && TryReadSingleRequirement(normal, out string fallbackSeed, out _))
                    seedResourcePrefab = fallbackSeed;

                object config = GetMember(definition, "Config");
                int legacyReserve = ReadIntValue(GetMember(config, "Reserve"));
                int reserve = reserveOverride >= 0 ? reserveOverride : legacyReserve;
                bool seedMode = seedModeOverride ?? (legacyReserve > 0 && seedStock < legacyReserve && seedPiece != null);
                if (seedMode && seedPiece == null) seedMode = false;
                Piece chosen = seedMode ? seedPiece : normal;
                if (!TryReadSingleRequirement(chosen, out string resourcePrefab, out int amount)) return false;

                Plant plant = chosen.GetComponent<Plant>();
                float growRadius = plant != null ? Mathf.Max(0.35f, plant.m_growRadius) : 0.7f;
                float spacing = Mathf.Max(0.7f, growRadius * 2f * GardenWorkRuntime.DefaultSpacingMultiplier);
                string plantPrefab = NormalizePrefabName(chosen.gameObject.name);
                if (string.IsNullOrEmpty(plantPrefab)) return false;

                plan = new SmartReplantGardenPlan(resourcePrefab, plantPrefab, amount, spacing, seedMode, seedResourcePrefab, Mathf.Max(0, seedStock), Mathf.Max(0, reserve));
                return true;
            }
            catch (Exception error)
            {
                _debugLog?.Invoke("SmartReplant garden-plan reflection failed safely: " + error.GetType().Name + ": " + error.Message);
                return false;
            }
        }

        private static bool TryGetHarvestDefinition(string maturePrefab, out object definition)
        {
            definition = null;
            if (string.IsNullOrEmpty(maturePrefab)) return false;
            EnsureInstalledBound();
            if (!IsInstalled) return false;
            if (_pluginInstance == null) TryBindRuntimeObjects();
            if (_harvests == null || _harvests.Count == 0) return false;
            string wanted = NormalizePrefabName(maturePrefab);
            try
            {
                foreach (DictionaryEntry entry in _harvests)
                {
                    if (!(entry.Key is int hash)) continue;
                    GameObject prefab = ResolveScenePrefabByHash(hash);
                    if (prefab == null || !string.Equals(NormalizePrefabName(prefab.name), wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    definition = entry.Value;
                    return definition != null;
                }
            }
            catch { }
            return false;
        }

        private static object GetMemberAny(object target, params string[] names)
        {
            if (target == null || names == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                object value = GetMember(target, names[i]);
                if (value != null) return value;
            }
            return null;
        }

        private static object GetMember(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name)) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = target.GetType();
            try
            {
                FieldInfo field = type.GetField(name, flags);
                if (field != null) return field.GetValue(target);
                PropertyInfo property = type.GetProperty(name, flags);
                return property?.GetValue(target, null);
            }
            catch { return null; }
        }

        private static Piece AsPiece(object value)
        {
            if (value is Piece piece) return piece;
            if (value is GameObject go) return go.GetComponent<Piece>();
            if (value is Component component) return component.GetComponent<Piece>();
            return null;
        }

        private static bool TryReadSingleRequirement(Piece piece, out string resourcePrefab, out int amount)
        {
            resourcePrefab = string.Empty; amount = 0;
            if (piece == null || piece.m_resources == null) return false;
            Piece.Requirement found = null;
            for (int i = 0; i < piece.m_resources.Length; i++)
            {
                Piece.Requirement req = piece.m_resources[i];
                if (req == null || req.m_resItem == null || req.GetAmount(0) <= 0) continue;
                if (found != null) return false;
                found = req;
            }
            if (found == null) return false;
            resourcePrefab = NormalizePrefabName(found.m_resItem.gameObject.name);
            amount = found.GetAmount(0);
            return !string.IsNullOrEmpty(resourcePrefab) && amount > 0;
        }

        private static string ComponentPrefabName(object value)
        {
            if (value is GameObject go) return NormalizePrefabName(go.name);
            if (value is Component component) return NormalizePrefabName(component.gameObject.name);
            return string.Empty;
        }

        private static int ReadIntValue(object value)
        {
            if (value == null) return 0;
            if (value is int direct) return direct;
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo p = value.GetType().GetProperty("Value", flags);
                object raw = p?.GetValue(value, null);
                if (raw is int i) return i;
                if (raw != null && int.TryParse(raw.ToString(), out i)) return i;
            }
            catch { }
            return 0;
        }

        internal static bool TryResolveFloraDefinition(Piece selectedPiece, out GameObject targetPrefab, out float spacing)
        {
            targetPrefab = null;
            spacing = 0f;
            if (selectedPiece == null || selectedPiece.gameObject == null) return false;
            EnsureInstalledBound();
            if (!IsInstalled) return false;
            if (_pluginInstance == null) TryBindRuntimeObjects();
            if (_floraRegistry == null) return false;

            string wanted = NormalizePrefabName(selectedPiece.gameObject.name);
            if (string.IsNullOrEmpty(wanted)) return false;
            try
            {
                object rawDefinitions = GetMember(_floraRegistry, "Definitions");
                if (!(rawDefinitions is IEnumerable definitions)) return false;
                foreach (object definition in definitions)
                {
                    if (definition == null) continue;
                    Piece piece = AsPiece(GetMember(definition, "Piece"));
                    string pieceName = piece != null && piece.gameObject != null ? NormalizePrefabName(piece.gameObject.name) : string.Empty;
                    if (!string.Equals(pieceName, wanted, StringComparison.OrdinalIgnoreCase)) continue;

                    object target = GetMember(definition, "Target");
                    if (target is GameObject go) targetPrefab = go;
                    else if (target is Component component) targetPrefab = component.gameObject;

                    object spacingValue = GetMember(definition, "Spacing");
                    if (spacingValue is float f) spacing = f;
                    else if (spacingValue is double d) spacing = (float)d;
                    else if (spacingValue != null) float.TryParse(spacingValue.ToString(), out spacing);

                    if (targetPrefab != null)
                    {
                        _debugLog?.Invoke($"SmartReplant flora definition resolved: piece={wanted}, target={NormalizePrefabName(targetPrefab.name)}, spacing={spacing:0.00}.");
                        return true;
                    }
                    return false;
                }
            }
            catch (Exception error)
            {
                _debugLog?.Invoke("SmartReplant flora-definition reflection failed safely: " + error.GetType().Name + ": " + error.Message);
            }
            return false;
        }

        internal static bool IsSmartReplantPlantingPiece(GameObject prefab)
        {
            if (prefab == null) return false;
            string name = NormalizePrefabName(prefab.name);
            return name.StartsWith("SmartReplant_Flora_", StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetStatusText()
        {
            if (!_initialized) return "SmartReplant: noch nicht geprueft";
            if (!IsInstalled) return "SmartReplant: nicht geladen (Workers bleibt eigenstaendig)";
            if (HasHarvestRegistry) return "SmartReplant: Pflanzen-/Ernteregistry erkannt";
            if (HasPlantRegistry) return "SmartReplant: Pflanzenregistry erkannt";
            return "SmartReplant: geladen, aber Registry nur teilweise erkannt";
        }

        internal static string GetDebugSummary()
        {
            if (!IsInstalled) return "smartreplant=off";
            int harvestCount = -1;
            try { if (_harvests != null) harvestCount = _harvests.Count; } catch { }
            EnsureMaturePrefabNames();
            string sample = MaturePrefabNames.Count == 0 ? "none" : string.Join(",", MaturePrefabNames.Take(5).ToArray());
            return $"smartreplant=on; ensureReady={_ensureReadyState}; harvestRegistry={harvestCount}; maturePrefabs={MaturePrefabNames.Count} [{sample}]; floraRegistry={(_floraRegistry != null ? "yes" : "no")}; marker={(_placedFloraType != null ? "yes" : "no")}";
        }

        internal static void Shutdown()
        {
            Types.Clear();
            _debugLog = null;
            _assembly = null;
            _pluginInstance = null;
            _registry = null;
            _harvests = null;
            MaturePrefabNames.Clear();
            _floraRegistry = null;
            _floraFind = null;
            _placedFloraType = null;
            _initialized = false;
            _reported = false;
            _nextProbeAt = 0f;
            IsInstalled = false;
        }

        private static void EnsureInstalledBound()
        {
            if (IsInstalled)
            {
                // SmartReplant can be present before its runtime harvest registry is populated.
                // A one-time early bind therefore used to stay empty for the entire session until
                // Workers F12 rebuilt the bridge. Retry only while the runtime binding is incomplete.
                bool incomplete = _pluginInstance == null || _registry == null || _harvests == null || _harvests.Count == 0;
                if (!incomplete || Time.time < _nextProbeAt) return;
                _nextProbeAt = Time.time + 5f;
                TryBindRuntimeObjects();
                return;
            }
            if (Time.time < _nextProbeAt) return;
            _nextProbeAt = Time.time + 5f;
            Refresh();
        }

        private static void TryBindRuntimeObjects()
        {
            if (_assembly == null) return;
            try
            {
                Type pluginType = _assembly.GetType("SmartReplant.SmartReplantPlugin", false);
                if (pluginType == null) return;
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo instanceField = pluginType.GetField("Instance", flags);
                _pluginInstance = instanceField?.GetValue(null);
                if (_pluginInstance == null) return;

                FieldInfo registryField = pluginType.GetField("Registry", flags);
                _registry = registryField?.GetValue(_pluginInstance);
                if (_registry != null)
                {
                    // SmartReplant builds its crop/harvest mapping lazily through Registry.EnsureReady(...).
                    // Reading Harvests before that returns an empty dictionary even though crops are present.
                    // Ask SmartReplant itself to initialize its registry instead of duplicating discovery logic.
                    TryEnsureRegistryReady(pluginType, flags);

                    FieldInfo harvestsField = _registry.GetType().GetField("Harvests", flags);
                    _harvests = harvestsField?.GetValue(_registry) as IDictionary;
                    RebuildMaturePrefabNames();
                }

                FieldInfo floraField = pluginType.GetField("Flora", flags);
                _floraRegistry = floraField?.GetValue(_pluginInstance);
                if (_floraRegistry != null)
                    _floraFind = _floraRegistry.GetType().GetMethod("Find", flags, null, new[] { typeof(Pickable) }, null);

                _placedFloraType = _assembly.GetType("SmartReplant.SmartReplantPlacedFlora", false);
            }
            catch (Exception error)
            {
                _debugLog?.Invoke("SmartReplant bridge runtime binding failed safely: " + error.GetType().Name + ": " + error.Message);
            }
        }

        private static void TryEnsureRegistryReady(Type pluginType, BindingFlags flags)
        {
            if (_registry == null || _pluginInstance == null) return;
            try
            {
                MethodInfo ensure = _registry.GetType().GetMethods(flags)
                    .FirstOrDefault(m => string.Equals(m.Name, "EnsureReady", StringComparison.Ordinal));
                if (ensure == null)
                {
                    _ensureReadyState = "method-missing";
                    return;
                }

                ParameterInfo[] parameters = ensure.GetParameters();
                object result;
                if (parameters.Length == 0)
                {
                    result = ensure.Invoke(_registry, null);
                }
                else if (parameters.Length == 1)
                {
                    FieldInfo settingsField = pluginType.GetField("Settings", flags);
                    object settings = settingsField?.GetValue(_pluginInstance);
                    if (settings == null || !parameters[0].ParameterType.IsInstanceOfType(settings))
                    {
                        _ensureReadyState = "settings-mismatch";
                        return;
                    }
                    result = ensure.Invoke(_registry, new[] { settings });
                }
                else
                {
                    _ensureReadyState = "unsupported-arity:" + parameters.Length;
                    return;
                }

                _ensureReadyState = result is bool ready ? (ready ? "ready" : "returned-false") : "invoked";
            }
            catch (TargetInvocationException error)
            {
                Exception inner = error.InnerException ?? error;
                _ensureReadyState = "error:" + inner.GetType().Name;
                _debugLog?.Invoke("SmartReplant EnsureReady failed safely: " + inner.GetType().Name + ": " + inner.Message);
            }
            catch (Exception error)
            {
                _ensureReadyState = "error:" + error.GetType().Name;
                _debugLog?.Invoke("SmartReplant EnsureReady binding failed safely: " + error.GetType().Name + ": " + error.Message);
            }
        }

        private static void EnsureMaturePrefabNames()
        {
            if (MaturePrefabNames.Count > 0 || _harvests == null || _harvests.Count == 0) return;
            RebuildMaturePrefabNames();
        }

        private static void RebuildMaturePrefabNames()
        {
            MaturePrefabNames.Clear();
            if (_harvests == null || _harvests.Count == 0) return;

            try
            {
                // SmartReplant's Harvests dictionary is already keyed by the stable hash of each
                // mature harvest prefab. Resolve those authoritative keys back through ZNetScene
                // instead of depending on the internal CropDefinition/PlantDefinition field layout,
                // which changed between SmartReplant generations.
                foreach (DictionaryEntry entry in _harvests)
                {
                    if (!(entry.Key is int hash)) continue;
                    GameObject prefab = ResolveScenePrefabByHash(hash);
                    if (prefab == null) continue;
                    string name = NormalizePrefabName(prefab.name);
                    if (!string.IsNullOrEmpty(name)) MaturePrefabNames.Add(name);
                }

                // Compatibility fallback: older SmartReplant builds exposed CropDefinition with
                // Piece fields. Keep this secondary path only if key resolution produced nothing.
                if (MaturePrefabNames.Count == 0)
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    foreach (DictionaryEntry entry in _harvests)
                    {
                        object definition = entry.Value;
                        if (definition == null) continue;
                        Type type = definition.GetType();
                        string[] names = { "Normal", "NormalCropPiece", "SeedProduction", "SeedProductionPiece", "Piece" };
                        for (int i = 0; i < names.Length; i++)
                        {
                            object value = type.GetField(names[i], flags)?.GetValue(definition)
                                ?? type.GetProperty(names[i], flags)?.GetValue(definition, null);
                            if (value is Component component) AddGrownPrefabNames(component);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                _debugLog?.Invoke("SmartReplant mature-prefab resolution failed safely: " + error.GetType().Name + ": " + error.Message);
            }
        }

        private static GameObject ResolveScenePrefabByHash(int hash)
        {
            if (ZNetScene.instance == null) return null;
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo method = typeof(ZNetScene).GetMethod("GetPrefab", flags, null, new[] { typeof(int) }, null);
                return method?.Invoke(ZNetScene.instance, new object[] { hash }) as GameObject;
            }
            catch { return null; }
        }

        private static void AddGrownPrefabNames(Component pieceComponent)
        {
            if (pieceComponent == null) return;
            Plant plant = pieceComponent.GetComponent<Plant>();
            if (plant == null || plant.m_grownPrefabs == null) return;
            for (int i = 0; i < plant.m_grownPrefabs.Length; i++)
            {
                GameObject grown = plant.m_grownPrefabs[i];
                if (grown == null) continue;
                string name = NormalizePrefabName(grown.name);
                if (!string.IsNullOrEmpty(name)) MaturePrefabNames.Add(name);
            }
        }

        private static bool HasType(string name) => Types.ContainsKey(name);

        private static string GetPrefabNameSafe(GameObject gameObject)
        {
            if (gameObject == null) return string.Empty;

            // Do not depend on Valheim's Utils helper here. Runtime instances normally use
            // "PrefabName(Clone)", and SmartReplant keys its mature registry by the
            // original prefab name. Normalizing the GameObject name therefore gives the
            // same identity without an additional assembly/namespace dependency.
            return NormalizePrefabName(gameObject.name);
        }

        private static string NormalizePrefabName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return string.Empty;
            int clone = objectName.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone >= 0) objectName = objectName.Substring(0, clone);
            return objectName.Trim();
        }
    }
}
