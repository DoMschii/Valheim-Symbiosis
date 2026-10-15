using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Valheim_Workers
{
    internal static class ProductionMetricsRuntime
    {
        private sealed class Metric { internal int Produced; internal int Consumed; internal long DayBucket; }
        private static readonly Dictionary<string, Dictionary<string, Metric>> ByArea = new Dictionary<string, Dictionary<string, Metric>>(StringComparer.Ordinal);
        // v0.5.336: Display text is static for a prefab during a session. Avoid repeated ObjectDB
        // lookup + reflection-based Localization calls every 15 s for every production sign row.
        private static readonly Dictionary<string, string> DisplayNameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static void RecordProduced(ZDO workerZdo, string prefab, int amount)
        {
            Record(workerZdo, prefab, amount, true);
        }

        internal static void RecordProduced(string areaId, string prefab, int amount)
        {
            RecordArea(areaId, prefab, amount, true);
        }

        internal static void RecordConsumed(ZDO workerZdo, string prefab, int amount)
        {
            Record(workerZdo, prefab, amount, false);
        }

        internal static void RecordConsumed(string areaId, string prefab, int amount)
        {
            RecordArea(areaId, prefab, amount, false);
        }

        private static void Record(ZDO workerZdo, string prefab, int amount, bool produced)
        {
            if (workerZdo == null) return;
            string area = WorkerZdo.ReadWorkAreaId(workerZdo);
            RecordArea(area, prefab, amount, produced);
        }

        private static void RecordArea(string areaId, string prefab, int amount, bool produced)
        {
            if (amount <= 0 || string.IsNullOrWhiteSpace(prefab)) return;
            string area = string.IsNullOrWhiteSpace(areaId) ? "base" : areaId;
            prefab = WorkerStorageRuntime.NormalizeObjectName(prefab);
            if (!ByArea.TryGetValue(area, out Dictionary<string, Metric> items)) ByArea[area] = items = new Dictionary<string, Metric>(StringComparer.OrdinalIgnoreCase);
            if (!items.TryGetValue(prefab, out Metric m)) items[prefab] = m = new Metric();
            EnsureCurrentDay(m);
            if (produced) m.Produced += amount; else m.Consumed += amount;

            // v0.5.365: the central Workzone economy keeps a rolling event-driven throughput
            // history. This is deliberately fed from the metrics event itself so no producer,
            // container or worker scan is introduced.
            try { WorkzoneActivityFlowRuntime.Record(area, prefab, amount, produced); }
            catch { }
        }

        internal static void GetMetric(string areaId, string prefab, out int produced, out int consumed)
        {
            produced = consumed = 0;
            if (string.IsNullOrWhiteSpace(prefab)) return;
            string area = string.IsNullOrWhiteSpace(areaId) ? "base" : areaId;
            if (ByArea.TryGetValue(area, out Dictionary<string, Metric> items) && items.TryGetValue(prefab, out Metric m))
            {
                EnsureCurrentDay(m);
                produced = m.Produced;
                consumed = m.Consumed;
            }
        }

        private static long CurrentDayBucket()
        {
            long interval = Math.Max(TimeSpan.FromMinutes(30).Ticks, WorkerSustenanceRuntime.MealInterval.Ticks);
            return DateTime.UtcNow.Ticks / interval;
        }

        private static void EnsureCurrentDay(Metric metric)
        {
            if (metric == null) return;
            long bucket = CurrentDayBucket();
            if (metric.DayBucket == bucket) return;
            metric.DayBucket = bucket;
            metric.Produced = 0;
            metric.Consumed = 0;
        }

        internal static int GetStock(string areaId, Vector3 center, float radius, string prefab)
        {
            return SharedStorageStockIndexRuntime.GetStock(areaId, center, radius, prefab);
        }

        internal static List<string> GetItems(string areaId, Vector3 center, float radius)
        {
            string area = string.IsNullOrWhiteSpace(areaId) ? "base" : areaId;
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SharedStorageStockIndexRuntime.CollectItems(area, center, radius, set);
            if (ByArea.TryGetValue(area, out Dictionary<string, Metric> items)) foreach (string k in items.Keys) set.Add(k);
            List<string> result = set.ToList();
            result.Sort((a,b) => string.Compare(DisplayName(a), DisplayName(b), StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        private static string LocalizeRuntimeSafe(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            try
            {
                Type localizationType = AccessTools.TypeByName("Localization");
                if (localizationType == null) return raw;

                object instance = null;
                FieldInfo instanceField = localizationType.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (instanceField != null) instance = instanceField.GetValue(null);
                if (instance == null)
                {
                    PropertyInfo instanceProperty = localizationType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (instanceProperty != null) instance = instanceProperty.GetValue(null, null);
                }
                if (instance == null) return raw;

                MethodInfo localize = AccessTools.Method(localizationType, "Localize", new[] { typeof(string) });
                if (localize == null) return raw;
                object value = localize.Invoke(instance, new object[] { raw });
                return value as string ?? raw;
            }
            catch { return raw; }
        }

        internal static string DisplayName(string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return "-";
            string key = WorkerStorageRuntime.NormalizeObjectName(prefab);
            if (DisplayNameCache.TryGetValue(key, out string cached)) return cached;

            string resolved = key.Replace("_", " ");
            try
            {
                GameObject go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(key) : null;
                ItemDrop d = go != null ? go.GetComponent<ItemDrop>() : null;
                string raw = d?.m_itemData?.m_shared?.m_name;
                if (!string.IsNullOrWhiteSpace(raw)) resolved = LocalizeRuntimeSafe(raw);
            }
            catch { }

            if (DisplayNameCache.Count > 512) DisplayNameCache.Clear();
            DisplayNameCache[key] = resolved;
            return resolved;
        }
    }

    internal readonly struct ProductionDailyEstimate
    {
        internal readonly bool HasProductionModel;
        internal readonly int ProducerCount;
        internal readonly int ConsumerCount;
        internal readonly double ProductionPerDay;
        internal readonly double ConsumptionPerDay;
        internal readonly double BalancePerDay;
        internal readonly double StockCoverageDays;

        internal ProductionDailyEstimate(bool hasProductionModel, int producerCount, int consumerCount,
            double productionPerDay, double consumptionPerDay, double stockCoverageDays)
        {
            HasProductionModel = hasProductionModel;
            ProducerCount = Math.Max(0, producerCount);
            ConsumerCount = Math.Max(0, consumerCount);
            ProductionPerDay = Math.Max(0.0, productionPerDay);
            ConsumptionPerDay = Math.Max(0.0, consumptionPerDay);
            BalancePerDay = ProductionPerDay - ConsumptionPerDay;
            StockCoverageDays = Math.Max(0.0, stockCoverageDays);
        }
    }

    /// <summary>
    /// Cheap predictive economy model for production signs. It intentionally uses only durable
    /// Workers state: completed WildGrowth plan slots plus the registered Worker sustenance list.
    /// No Physics/world scan is performed by a sign refresh. One Worker consumes one provision
    /// per Valheim day; one bush contributes its vanilla Pickable yield divided by respawn days.
    /// </summary>
    internal static class ProductionBalanceRuntime
    {
        /// <summary>
        /// v0.5.362 compatibility facade. Forecast ownership moved to WorkzoneEconomyRuntime so
        /// production signs, WorkFlag UI and the outpost manager consume the same cached model.
        /// </summary>
        internal static ProductionDailyEstimate Estimate(string areaId, Vector3 center, float radius, string prefab, int currentStock)
        {
            return WorkzoneEconomyRuntime.EstimateResource(areaId, center, radius, prefab, currentStock);
        }
    }

    internal sealed class ProductionDisplayMarker : MonoBehaviour
    {
        internal bool Landscape;
        private sealed class DisplayFace
        {
            internal GameObject Root;
            internal Component Text;
            internal readonly List<Image> Icons = new List<Image>();
        }

        private const float RefreshSeconds = 15f;
        private const float InitialStaggerWindowSeconds = 8f;

        private DisplayFace _frontFace;
        private DisplayFace _backFace;
        private bool _refreshSchedulerStarted;
        private string _lastRenderedText;
        private string _selectedPrefab = string.Empty;
        private readonly List<string> _trackedPrefabs = new List<string>();
        private string _areaId = "base";
        private Vector3 _center;
        private float _radius = 50f;
        private int _selectionRevision = int.MinValue;
        private bool _selectionInitialized;

        private void Awake()
        {
            // The component can be added to a prefab before the registration code has had a
            // chance to assign Landscape. Infer the intended orientation from the prefab name
            // first so portrait and landscape can never be built with the same geometry.
            string objectName = WorkerStorageRuntime.NormalizeObjectName(gameObject.name ?? string.Empty);
            if (objectName.IndexOf("ProductionDisplayLandscape", StringComparison.OrdinalIgnoreCase) >= 0) Landscape = true;
            else if (objectName.IndexOf("ProductionDisplayPortrait", StringComparison.OrdinalIgnoreCase) >= 0) Landscape = false;

            BuildVisual();
            ResolveArea();
            LoadSelection(true);
            RefreshText();

            // v0.5.451: no permanent Update() dispatcher is necessary for a 15-second sign.
            // The scheduled refresh starts in Start(), after Awake has built the board and text.
        }

        private void Start()
        {
            _refreshSchedulerStarted = true;
            ScheduleRefresh(RefreshSeconds + ComputeInitialStagger());
        }

        private void OnEnable()
        {
            if (_refreshSchedulerStarted)
                ScheduleRefresh(RefreshSeconds + ComputeInitialStagger());
        }

        private void OnDisable()
        {
            CancelInvoke(nameof(ScheduledRefresh));
        }

        private void ScheduleRefresh(float delay)
        {
            if (!isActiveAndEnabled) return;
            CancelInvoke(nameof(ScheduledRefresh));
            Invoke(nameof(ScheduledRefresh), Mathf.Max(0.1f, delay));
        }

        private void ScheduledRefresh()
        {
            if (!isActiveAndEnabled) return;
            ResolveArea();
            RefreshText();
            ScheduleRefresh(RefreshSeconds);
        }

        internal void ConfigureLandscape(bool landscape)
        {
            bool changed = Landscape != landscape;
            Landscape = landscape;
            if (changed || transform.Find("WorkersProductionPanel") == null)
                BuildVisual();
            RefreshText();
        }

        private float ComputeInitialStagger()
        {
            int id = 0;
            try { id = GetInstanceID() & 0x7fffffff; } catch { }
            return (id % 1000) / 1000f * InitialStaggerWindowSeconds;
        }

        internal void AssignFromHotbar(ItemDrop.ItemData item)
        {
            if (item == null) return;
            string prefab = ResolveItemPrefab(item);
            if (string.IsNullOrWhiteSpace(prefab)) return;
            LoadSelection(false);
            if (!Landscape) _selectedPrefab = prefab;
            else
            {
                int existing = _trackedPrefabs.FindIndex(p => string.Equals(p, prefab, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) _trackedPrefabs.RemoveAt(existing);
                else { if (_trackedPrefabs.Count >= 8) _trackedPrefabs.RemoveAt(0); _trackedPrefabs.Add(prefab); }
            }
            SaveSelection();
            RefreshText();
        }

        internal void ClearSelection()
        {
            _selectedPrefab = string.Empty;
            _trackedPrefabs.Clear();
            SaveSelection();
            RefreshText();
        }

        private static string ResolveItemPrefab(ItemDrop.ItemData item)
        {
            if (item == null) return string.Empty;
            string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared?.m_name;
            return WorkerStorageRuntime.NormalizeObjectName(prefab ?? string.Empty);
        }

        private ZDO ResolveZdo()
        {
            try
            {
                ZNetView view = GetComponent<ZNetView>() ?? GetComponentInParent<ZNetView>() ?? GetComponentInChildren<ZNetView>(true);
                return view != null && view.IsValid() ? view.GetZDO() : null;
            }
            catch { return null; }
        }

        private void LoadSelection(bool force = false)
        {
            ZDO zdo = ResolveZdo();
            if (zdo == null)
            {
                // A freshly placed/local display can briefly exist before its ZDO is ready.
                // Never wipe a hotbar selection just because persistence is not available yet.
                _selectionInitialized = true;
                return;
            }

            int revision = zdo.GetInt("workers_prod_display_rev", 0);
            if (!force && _selectionInitialized && revision == _selectionRevision) return;

            if (!Landscape)
            {
                string stored = zdo.GetString("workers_prod_display_item", string.Empty);
                // During the same frame as a hotbar assignment the ZDO write can lag behind the
                // local component. Only an explicit/newer network revision may overwrite it.
                if (force || revision != _selectionRevision || string.IsNullOrWhiteSpace(_selectedPrefab))
                    _selectedPrefab = WorkerStorageRuntime.NormalizeObjectName(stored ?? string.Empty);
            }
            else
            {
                string packed = zdo.GetString("workers_prod_display_items", string.Empty);
                if (force || revision != _selectionRevision || _trackedPrefabs.Count == 0)
                {
                    _trackedPrefabs.Clear();
                    if (!string.IsNullOrWhiteSpace(packed))
                    {
                        string[] split = packed.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < split.Length && _trackedPrefabs.Count < 8; i++)
                        {
                            string prefab = WorkerStorageRuntime.NormalizeObjectName(split[i]);
                            if (!string.IsNullOrWhiteSpace(prefab) && !_trackedPrefabs.Any(p => string.Equals(p, prefab, StringComparison.OrdinalIgnoreCase)))
                                _trackedPrefabs.Add(prefab);
                        }
                    }
                }
            }

            _selectionRevision = revision;
            _selectionInitialized = true;
        }

        private void SaveSelection()
        {
            // Local state is authoritative immediately. Persistence/network replication follows.
            _selectionInitialized = true;
            ZDO zdo = ResolveZdo();
            if (zdo == null) return;
            try
            {
                if (!Landscape) zdo.Set("workers_prod_display_item", _selectedPrefab ?? string.Empty);
                else zdo.Set("workers_prod_display_items", string.Join(";", _trackedPrefabs.ToArray()));

                int nextRevision = Math.Max(_selectionRevision, zdo.GetInt("workers_prod_display_rev", 0)) + 1;
                zdo.Set("workers_prod_display_rev", nextRevision);
                _selectionRevision = nextRevision;
            }
            catch { }
        }

        private void ResolveArea()
        {
            if (WorkFlagRegistry.TryFindNearest(transform.position, 180f, 30f, out WorkAreaSnapshot area))
            {
                _areaId = string.IsNullOrWhiteSpace(area.Id) ? "base" : area.Id;
                _center = area.Center; _radius = area.Radius;
            }
            else { _areaId = "base"; _center = transform.position; _radius = 50f; }
        }

        private static int SafeGetStock(string areaId, Vector3 center, float radius, string prefab)
        {
            try { return ProductionMetricsRuntime.GetStock(areaId, center, radius, prefab); }
            catch { return 0; }
        }

        private static void SafeGetMetric(string areaId, string prefab, out int produced, out int consumed)
        {
            produced = consumed = 0;
            try { ProductionMetricsRuntime.GetMetric(areaId, prefab, out produced, out consumed); }
            catch { produced = consumed = 0; }
        }

        private static ProductionDailyEstimate SafeGetDailyEstimate(string areaId, Vector3 center, float radius, string prefab, int stock)
        {
            try { return ProductionBalanceRuntime.Estimate(areaId, center, radius, prefab, stock); }
            catch { return default; }
        }

        private static string FormatSigned(double value)
        {
            string sign = value >= -0.0001 ? "+" : string.Empty;
            return sign + value.ToString("0.0");
        }

        private static string FormatPlanPerDay(ProductionDailyEstimate estimate)
        {
            return estimate.HasProductionModel ? "~" + FormatSigned(estimate.BalancePerDay) : "-";
        }

        private void RefreshText()
        {
            if (_frontFace == null && _backFace == null) return;
            LoadSelection(false);
            ClearIcons();

            if (!Landscape)
            {
                if (string.IsNullOrWhiteSpace(_selectedPrefab))
                {
                    SetText("PRODUKTION\n\n[1-8] Item aus Hotbar\nzum Anzeigen waehlen");
                    return;
                }

                string p = _selectedPrefab;
                int stock = SafeGetStock(_areaId, _center, _radius, p);
                SafeGetMetric(_areaId, p, out int prod, out int cons);
                int bal = prod-cons;
                ProductionDailyEstimate estimate = SafeGetDailyEstimate(_areaId, _center, _radius, p, stock);

                System.Text.StringBuilder portrait = new System.Text.StringBuilder();
                portrait.Append(ProductionMetricsRuntime.DisplayName(p)).Append("\n\n")
                    .Append("Bestand      ").Append(stock).Append('\n')
                    .Append("Heute       +").Append(prod).Append('\n')
                    .Append("Verbrauch   -").Append(cons).Append('\n')
                    .Append("Ist-Bilanz   ").Append(bal >= 0 ? "+" : string.Empty).Append(bal).Append('\n');

                if (estimate.HasProductionModel)
                {
                    portrait.Append("Buesche       ").Append(estimate.ProducerCount).Append('\n')
                        .Append("Arbeiter      ").Append(estimate.ConsumerCount).Append('\n')
                        .Append("Ertrag/Tag   ~").Append(estimate.ProductionPerDay.ToString("0.0")).Append('\n')
                        .Append("Bedarf/Tag   ~").Append(estimate.ConsumptionPerDay.ToString("0.0")).Append('\n')
                        .Append("Bilanz/Tag   ~").Append(FormatSigned(estimate.BalancePerDay));
                    if (estimate.ConsumerCount > 0)
                        portrait.Append("\nReichweite   ~").Append(estimate.StockCoverageDays.ToString("0.0")).Append(" T");
                }
                else if (estimate.ConsumerCount > 0)
                {
                    portrait.Append("Arbeiter      ").Append(estimate.ConsumerCount).Append('\n')
                        .Append("Bedarf/Tag   ~").Append(estimate.ConsumptionPerDay.ToString("0.0"));
                }

                SetText(portrait.ToString());
                SetPortraitIcon(p);
                return;
            }

            if (_trackedPrefabs.Count == 0)
            {
                SetText("PRODUKTION\n\n[1-8] Items aus Hotbar\nhinzufuegen/entfernen");
                return;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder("PRODUKTION / BEDARF\nRessource       Best.  Ist+  Ist-  Bil.  Plan/T\n");
            for (int i = 0; i < _trackedPrefabs.Count && i < 8; i++)
            {
                string p = _trackedPrefabs[i];
                int stock = SafeGetStock(_areaId, _center, _radius, p);
                SafeGetMetric(_areaId, p, out int prod, out int cons);
                int bal = prod-cons;
                ProductionDailyEstimate estimate = SafeGetDailyEstimate(_areaId, _center, _radius, p, stock);
                string n = ProductionMetricsRuntime.DisplayName(p); if (n.Length > 12) n = n.Substring(0,12);
                string actualBalance = (bal >= 0 ? "+" : string.Empty) + bal.ToString();
                string planBalance = FormatPlanPerDay(estimate);
                sb.Append("    ").Append(n.PadRight(12))
                  .Append(stock.ToString().PadLeft(5)).Append("  +").Append(prod.ToString().PadLeft(3))
                  .Append("  -").Append(cons.ToString().PadLeft(3)).Append("  ")
                  .Append(actualBalance.PadLeft(5)).Append("  ").Append(planBalance.PadLeft(7)).Append('\n');
                SetLandscapeIcon(i, p);
            }
            SetText(sb.ToString());
        }

        private void SetText(string value)
        {
            string text = value ?? string.Empty;
            if (string.Equals(_lastRenderedText, text, StringComparison.Ordinal))
                return;

            _lastRenderedText = text;
            SetComponentText(_frontFace?.Text, text);
            SetComponentText(_backFace?.Text, text);
        }

        private static void SetComponentText(Component component, string value)
        {
            if (component == null) return;
            try
            {
                GameObject go = component.gameObject;
                if (go != null && !go.activeSelf) go.SetActive(true);
                if (component is Behaviour behaviour && !behaviour.enabled) behaviour.enabled = true;

                Type t = component.GetType();
                string textValue = value ?? string.Empty;

                // TMP can keep stale geometry after a cloned Sign text is changed at runtime.
                // Prefer its own SetText API and then force a mesh/layout rebuild.
                MethodInfo setText = t.GetMethod("SetText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (setText != null) setText.Invoke(component, new object[] { textValue });
                else
                {
                    PropertyInfo property = t.GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (property != null && property.CanWrite) property.SetValue(component, textValue, null);
                    else
                    {
                        FieldInfo field = t.GetField("m_text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (field != null) field.SetValue(component, textValue);
                    }
                }

                SetIntPropertyIfPresent(component, "firstVisibleCharacter", 0);
                SetIntPropertyIfPresent(component, "maxVisibleCharacters", 99999);
                SetIntPropertyIfPresent(component, "maxVisibleWords", 99999);
                SetIntPropertyIfPresent(component, "maxVisibleLines", 99999);
                SetIntPropertyIfPresent(component, "pageToDisplay", 1);

                MethodInfo force = t.GetMethod("ForceMeshUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool), typeof(bool) }, null);
                if (force != null) force.Invoke(component, new object[] { true, true });
                else
                {
                    force = t.GetMethod("ForceMeshUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    force?.Invoke(component, null);
                }

                t.GetMethod("SetVerticesDirty", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)?.Invoke(component, null);
                t.GetMethod("SetLayoutDirty", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)?.Invoke(component, null);
            }
            catch { }
        }

        private static void SetIntPropertyIfPresent(Component component, string name, int value)
        {
            if (component == null) return;
            try
            {
                PropertyInfo property = component.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.CanWrite && property.PropertyType == typeof(int)) property.SetValue(component, value, null);
            }
            catch { }
        }

        private static Component _smartStorageTextTemplate;

        /// <summary>
        /// v0.5.340: The yellow illuminated lettering the user refers to is not the native
        /// Valheim sign at all; it comes from the existing SmartStorage display family.  Reuse
        /// one of those live prefab text components as our template so font asset, SDF material,
        /// face/outline/glow settings and any future SmartStorage typography changes are copied
        /// exactly instead of approximated with hard-coded colours.
        /// </summary>
        private static Component FindSmartStorageTextTemplate()
        {
            try
            {
                if (_smartStorageTextTemplate != null) return _smartStorageTextTemplate;
                if (ZNetScene.instance == null) return null;

                string[] prefabs =
                {
                    "SmartStorage_StorageDisplayMedium",
                    "SmartStorage_StorageDisplayLarge",
                    "SmartStorage_StorageDisplaySmall"
                };

                Component best = null;
                float bestScore = float.MinValue;
                for (int p = 0; p < prefabs.Length; p++)
                {
                    GameObject prefab = null;
                    try { prefab = ZNetScene.instance.GetPrefab(prefabs[p]); } catch { }
                    if (prefab == null) continue;

                    Component[] components = prefab.GetComponentsInChildren<Component>(true);
                    for (int i = 0; i < components.Length; i++)
                    {
                        Component c = components[i];
                        if (c == null) continue;
                        string typeName = c.GetType().Name;
                        if (typeName.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) < 0 &&
                            typeName.IndexOf("TMP_Text", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        float score = ScoreSmartStorageTextTemplate(c);
                        if (best == null || score > bestScore)
                        {
                            best = c;
                            bestScore = score;
                        }
                    }
                }

                if (best != null) _smartStorageTextTemplate = best;
                return best;
            }
            catch { return null; }
        }

        private static float ScoreSmartStorageTextTemplate(Component component)
        {
            if (component == null) return float.MinValue;
            float score = 0f;
            try
            {
                string n = component.gameObject != null ? component.gameObject.name ?? string.Empty : string.Empty;
                if (n.IndexOf("count", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("amount", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("value", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 4f;

                Type t = component.GetType();
                PropertyInfo colorProperty = t.GetProperty("color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object rawColor = colorProperty != null && colorProperty.CanRead ? colorProperty.GetValue(component, null) : null;
                if (rawColor is Color color)
                {
                    // Prefer the familiar warm/yellow display text over labels/background text.
                    score += color.r * 3f + color.g * 2f - color.b * 2f + color.a;
                    if (color.r > 0.55f && color.g > 0.45f && color.b < 0.55f) score += 12f;
                }

                Material material = null;
                PropertyInfo sharedMaterial = t.GetProperty("fontSharedMaterial", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (sharedMaterial != null && sharedMaterial.CanRead) material = sharedMaterial.GetValue(component, null) as Material;
                if (material == null)
                {
                    PropertyInfo fontMaterial = t.GetProperty("fontMaterial", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fontMaterial != null && fontMaterial.CanRead) material = fontMaterial.GetValue(component, null) as Material;
                }
                if (material != null)
                {
                    if (material.HasProperty("_GlowPower")) score += 8f;
                    if (material.HasProperty("_GlowColor")) score += 4f;
                    if (material.HasProperty("_FaceColor"))
                    {
                        Color face = material.GetColor("_FaceColor");
                        score += face.r * 2f + face.g * 1.5f - face.b;
                        if (face.r > 0.55f && face.g > 0.45f && face.b < 0.55f) score += 8f;
                    }
                }
            }
            catch { }
            return score;
        }

        private static Component FindProductionTextTemplate(GameObject fallbackRoot)
        {
            Component smartStorage = FindSmartStorageTextTemplate();
            return smartStorage != null ? smartStorage : FindValheimTextTemplate(fallbackRoot);
        }

        private static Component FindValheimTextTemplate(GameObject root)
        {
            if (root == null) return null;
            try
            {
                Component[] components = root.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    Component c = components[i];
                    if (c == null || c.gameObject.name.StartsWith("WorkersProduction", StringComparison.Ordinal)) continue;
                    string typeName = c.GetType().Name;
                    if (typeName.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        typeName.IndexOf("TMP_Text", StringComparison.OrdinalIgnoreCase) >= 0)
                        return c;
                }
            }
            catch { }
            return null;
        }

        private static Sprite ResolveItemIcon(string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab)) return null;
            try
            {
                GameObject go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
                ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
                ItemDrop.ItemData data = drop?.m_itemData;
                if (data == null) return null;
                MethodInfo method = data.GetType().GetMethod("GetIcon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                return method?.Invoke(data, null) as Sprite;
            }
            catch { return null; }
        }

        private static void ConfigureClonedText(Component text, bool landscape)
        {
            if (text == null) return;
            try
            {
                Type t = text.GetType();
                PropertyInfo rich = t.GetProperty("richText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (rich != null && rich.CanWrite) rich.SetValue(text, false, null);
                PropertyInfo raycast = t.GetProperty("raycastTarget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (raycast != null && raycast.CanWrite) raycast.SetValue(text, false, null);
                PropertyInfo auto = t.GetProperty("enableAutoSizing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (auto != null && auto.CanWrite) auto.SetValue(text, false, null);

                // v0.5.340: The template normally comes straight from a SmartStorage display.
                // Keep its font, SDF material, face/outline/glow colours, style and weight fully
                // untouched. Only size/layout are adapted to the larger production board.
                PropertyInfo size = t.GetProperty("fontSize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (size != null && size.CanWrite) size.SetValue(text, landscape ? 38f : 42f, null);

                PropertyInfo lineSpacing = t.GetProperty("lineSpacing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (lineSpacing != null && lineSpacing.CanWrite) lineSpacing.SetValue(text, landscape ? -4f : 1f, null);

                PropertyInfo wrapping = t.GetProperty("enableWordWrapping", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (wrapping != null && wrapping.CanWrite) wrapping.SetValue(text, false, null);

                PropertyInfo overflow = t.GetProperty("overflowMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (overflow != null && overflow.CanWrite && overflow.PropertyType.IsEnum)
                {
                    try { overflow.SetValue(text, Enum.Parse(overflow.PropertyType, "Overflow"), null); } catch { }
                }

                PropertyInfo alignment = t.GetProperty("alignment", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (alignment != null && alignment.CanWrite && alignment.PropertyType.IsEnum)
                {
                    try { alignment.SetValue(text, Enum.Parse(alignment.PropertyType, landscape ? "TopLeft" : "TopLeft"), null); } catch { }
                }

                SetIntPropertyIfPresent(text, "firstVisibleCharacter", 0);
                SetIntPropertyIfPresent(text, "maxVisibleCharacters", 99999);
                SetIntPropertyIfPresent(text, "maxVisibleWords", 99999);
                SetIntPropertyIfPresent(text, "maxVisibleLines", 99999);
                SetIntPropertyIfPresent(text, "pageToDisplay", 1);
            }
            catch { }
        }

        private DisplayFace CreateValheimFace(Transform panel, Component template, string name, float z, float yRotation)
        {
            DisplayFace face = new DisplayFace();

            GameObject canvasObject = new GameObject(name + "Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            // Keep the world-space UI outside the scaled cube. Parenting the canvas to the
            // panel multiplied its layout by the board's non-uniform scale, which is why item
            // icons could float far above the sign. Position it in the display root instead.
            canvasObject.transform.SetParent(transform, false);
            Vector3 boardPos = panel.localPosition;
            canvasObject.transform.localPosition = new Vector3(boardPos.x, boardPos.y, boardPos.z + z);
            canvasObject.transform.localRotation = Quaternion.Euler(0f, yRotation, 0f);
            canvasObject.transform.localScale = Landscape ? new Vector3(0.00222f, 0.00222f, 1f) : new Vector3(0.00248f, 0.00238f, 1f);

            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 60;

            RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = Landscape ? new Vector2(980f, 360f) : new Vector2(360f, 660f);

            Component clonedText = null;
            if (template != null)
            {
                GameObject clone = Instantiate(template.gameObject, canvasObject.transform, false);
                clone.name = name + "Text";
                clone.SetActive(true);
                Component[] comps = clone.GetComponents<Component>();
                for (int i = 0; i < comps.Length; i++)
                {
                    Component c = comps[i];
                    if (c == null) continue;
                    string tn = c.GetType().Name;
                    if (tn.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("TMP_Text", StringComparison.OrdinalIgnoreCase) >= 0)
                    { clonedText = c; break; }
                }
                if (clonedText == null)
                {
                    Component[] children = clone.GetComponentsInChildren<Component>(true);
                    for (int i = 0; i < children.Length; i++)
                    {
                        Component c = children[i]; if (c == null) continue;
                        string tn = c.GetType().Name;
                        if (tn.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("TMP_Text", StringComparison.OrdinalIgnoreCase) >= 0)
                        { clonedText = c; break; }
                    }
                }
                RectTransform rt = clone.transform as RectTransform;
                if (rt != null)
                {
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.offsetMin = Landscape ? new Vector2(60f, 24f) : new Vector2(28f, 34f);
                    rt.offsetMax = Landscape ? new Vector2(-24f, -24f) : new Vector2(-24f, -108f);
                    rt.localScale = Vector3.one;
                    rt.localRotation = Quaternion.identity;
                    rt.localPosition = Vector3.zero;
                }
                ConfigureClonedText(clonedText, Landscape);
            }

            face.Root = canvasObject;
            face.Text = clonedText;

            int iconCount = Landscape ? 8 : 1;
            for (int i = 0; i < iconCount; i++)
            {
                GameObject iconGo = new GameObject(name + "Icon" + i, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                RectTransform rt = iconGo.GetComponent<RectTransform>();
                rt.SetParent(canvasObject.transform, false);
                Image image = iconGo.GetComponent<Image>();
                image.raycastTarget = false;
                image.preserveAspect = true;
                image.enabled = false;

                if (!Landscape)
                {
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.anchoredPosition = new Vector2(0f, -26f);
                    rt.sizeDelta = new Vector2(64f, 64f);
                }
                else
                {
                    rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
                    rt.pivot = new Vector2(0f, 1f);
                    rt.anchoredPosition = new Vector2(16f, -91f - i * 35f);
                    rt.sizeDelta = new Vector2(26f, 26f);
                }
                face.Icons.Add(image);
            }
            return face;
        }

        private void ClearIcons()
        {
            ClearFaceIcons(_frontFace);
            ClearFaceIcons(_backFace);
        }

        private static void ClearFaceIcons(DisplayFace face)
        {
            if (face == null) return;
            for (int i = 0; i < face.Icons.Count; i++)
            {
                Image image = face.Icons[i];
                if (image == null) continue;
                image.sprite = null;
                image.enabled = false;
            }
        }

        private void SetPortraitIcon(string prefab)
        {
            Sprite sprite = ResolveItemIcon(prefab);
            SetFaceIcon(_frontFace, 0, sprite);
            SetFaceIcon(_backFace, 0, sprite);
        }

        private void SetLandscapeIcon(int row, string prefab)
        {
            Sprite sprite = ResolveItemIcon(prefab);
            SetFaceIcon(_frontFace, row, sprite);
            SetFaceIcon(_backFace, row, sprite);
        }

        private static void SetFaceIcon(DisplayFace face, int index, Sprite sprite)
        {
            if (face == null || index < 0 || index >= face.Icons.Count) return;
            Image image = face.Icons[index];
            if (image == null) return;
            image.sprite = sprite;
            image.enabled = sprite != null;
        }

        private void BuildVisual()
        {
            _lastRenderedText = null;

            // Prefer the exact yellow/glowing text component used by the existing SmartStorage
            // displays. If SmartStorage has not registered its prefabs yet, fall back to the
            // inherited native sign text rather than failing to build the production display.
            Component valheimTextTemplate = FindProductionTextTemplate(gameObject);

            // IMPORTANT: the text/icon canvases are parented to the display root (not to the
            // scaled board). Older builds only destroyed WorkersProductionPanel. Every rebuild
            // therefore left the previous Front/Back canvases alive, so the original helper
            // text stayed visible on top of the freshly updated data. Remove every generated
            // visual root before rebuilding the current pair.
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child == null) continue;
                string childName = child.gameObject.name ?? string.Empty;
                if (!childName.StartsWith("WorkersProduction", StringComparison.Ordinal)) continue;
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }
            _frontFace = null;
            _backFace = null;

            Renderer sourceRenderer = null;
            try
            {
                Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer candidate = renderers[i];
                    if (candidate == null || candidate.sharedMaterial == null) continue;
                    if (candidate.gameObject.name.StartsWith("WorkersProduction", StringComparison.Ordinal)) continue;
                    sourceRenderer = candidate;
                    break;
                }
            }
            catch { }

            GameObject panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.name = "WorkersProductionPanel";
            panel.transform.SetParent(transform, false);
            // The vanilla sign prefab pivot sits slightly to one side.  Center the
            // wider landscape board visually on its post instead of letting it hang
            // far to the right.
            panel.transform.localPosition = Landscape ? new Vector3(-0.38f, 0.92f, 0f) : new Vector3(0f, 0.98f, 0f);
            panel.transform.localScale = Landscape ? new Vector3(2.05f, 0.82f, 0.08f) : new Vector3(0.95f, 1.45f, 0.08f);
            Collider col = panel.GetComponent<Collider>();
            if (col != null)
            {
                // Keep a thin trigger on the actual production board so hotbar selection/hover
                // works on the visible display instead of only on the hidden vanilla sign below.
                col.isTrigger = true;
            }
            Renderer r = panel.GetComponent<Renderer>();
            if (r != null && sourceRenderer != null && sourceRenderer.sharedMaterial != null)
                r.sharedMaterial = sourceRenderer.sharedMaterial;

            // World-space Valheim sign text is single-sided. The old orientation exposed its
            // back side, which made every glyph appear horizontally mirrored. Face each canvas
            // outward from its corresponding board side instead.
            _frontFace = CreateValheimFace(panel.transform, valheimTextTemplate, "WorkersProductionFront", -0.055f, 0f);
            _backFace = CreateValheimFace(panel.transform, valheimTextTemplate, "WorkersProductionBack", 0.055f, 180f);

            // The prefab is cloned from vanilla `sign`, so its original little wooden sign and
            // native text still exist underneath our larger board. Hide only those inherited
            // visuals; keep Piece/Sign/ZNetView and colliders alive for interaction/networking.
            HideInheritedSignVisuals(panel.transform);
        }

        private void HideInheritedSignVisuals(Transform customPanel)
        {
            try
            {
                Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null) continue;
                    if (customPanel != null && renderer.transform.IsChildOf(customPanel)) continue;
                    if (renderer.gameObject.name.StartsWith("WorkersProduction", StringComparison.Ordinal)) continue;
                    if (renderer.transform.parent != null && renderer.transform.parent.gameObject.name.StartsWith("WorkersProduction", StringComparison.Ordinal)) continue;
                    renderer.enabled = false;
                }

                Component[] components = GetComponentsInChildren<Component>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null) continue;
                    if (customPanel != null && component.transform.IsChildOf(customPanel)) continue;
                    if (component.gameObject.name.StartsWith("WorkersProduction", StringComparison.Ordinal)) continue;
                    if (component.transform.parent != null && component.transform.parent.gameObject.name.StartsWith("WorkersProduction", StringComparison.Ordinal)) continue;
                    string typeName = component.GetType().Name;
                    if (typeName.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) < 0 &&
                        typeName.IndexOf("TMP_Text", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (component is Behaviour behaviour) behaviour.enabled = false;
                }
            }
            catch { }
        }

    }

    [HarmonyPatch(typeof(Sign), "Interact", typeof(Humanoid), typeof(bool), typeof(bool))]
    internal static class ProductionDisplayInteractPatch
    {
        [HarmonyPriority(Priority.First + 300)]
        private static bool Prefix(Sign __instance, Humanoid __0, bool __1, bool __2, ref bool __result)
        {
            if (__instance == null) return true;
            ProductionDisplayMarker marker = __instance.GetComponent<ProductionDisplayMarker>() ?? __instance.GetComponentInParent<ProductionDisplayMarker>();
            if (marker == null) return true;
            __result = true; if (__1) return false;
            if (__0 == Player.m_localPlayer && __2) marker.ClearSelection();
            return false;
        }
    }

    [HarmonyPatch(typeof(Sign), "GetHoverText")]
    internal static class ProductionDisplayHoverPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Sign __instance, ref string __result)
        {
            ProductionDisplayMarker marker = __instance != null ? (__instance.GetComponent<ProductionDisplayMarker>() ?? __instance.GetComponentInParent<ProductionDisplayMarker>()) : null;
            if (marker == null) return;
            __result = marker.Landscape ? "Produktionsanzeige (Quer)\n[<color=yellow><b>1-8</b></color>] Hotbar-Item hinzufuegen/entfernen\n[<color=yellow><b>Alt+E</b></color>] Auswahl leeren" : "Produktionsanzeige (Hoch)\n[<color=yellow><b>1-8</b></color>] Hotbar-Item anzeigen\n[<color=yellow><b>Alt+E</b></color>] Auswahl leeren";
        }
    }

    [HarmonyPatch(typeof(Sign), "UseItem")]
    internal static class ProductionDisplayUseItemPatch
    {
        // Let vanilla route the hotbar action to the aimed Sign, then consume that action here.
        // This is much more reliable than trying to reproduce Valheim's hover/raycast + hotbar
        // indexing in Player.UseHotbarItem, and matches the behaviour of the existing display signs.
        [HarmonyPriority(Priority.First + 500)]
        private static bool Prefix(Sign __instance, object[] __args, ref bool __result)
        {
            if (__instance == null) return true;
            ProductionDisplayMarker marker = __instance.GetComponent<ProductionDisplayMarker>() ?? __instance.GetComponentInParent<ProductionDisplayMarker>();
            if (marker == null) return true;

            ItemDrop.ItemData item = null;
            Humanoid user = null;
            if (__args != null)
            {
                for (int i = 0; i < __args.Length; i++)
                {
                    if (item == null && __args[i] is ItemDrop.ItemData data) item = data;
                    if (user == null && __args[i] is Humanoid humanoid) user = humanoid;
                }
            }

            // Only own the local player's actual item-use.  Returning true for anything else
            // keeps remote/network behaviour and non-item interactions untouched.
            if (user != null && user != Player.m_localPlayer) return true;
            if (item == null)
            {
                __result = true;
                return false;
            }

            marker.AssignFromHotbar(item);
            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Produktionsanzeige aktualisiert");

            __result = true;
            return false;
        }
    }

    internal sealed class ProductionDisplayPieceRuntime
    {
        internal const string PortraitPrefab = "ValheimWorkers_ProductionDisplayPortrait";
        internal const string LandscapePrefab = "ValheimWorkers_ProductionDisplayLandscape";
        private readonly Action<string> _debug;
        private readonly Action<string> _info;
        private bool _registered;
        private float _nextAttempt;

        internal ProductionDisplayPieceRuntime(Action<string> debug, Action<string> info)
        {
            _debug = debug;
            _info = info;
        }

        internal void Begin()
        {
            JotunnPrefabLifecycleRuntime.Subscribe(OnVanillaPrefabsAvailable, _debug);
            TryRegister();
        }

        internal void Tick()
        {
            if (_registered || JotunnPrefabLifecycleRuntime.WaitingForVanillaPrefabs) return;
            if (Time.unscaledTime < _nextAttempt) return;
            _nextAttempt = Time.unscaledTime + 1.5f;
            TryRegister();
        }

        private void OnVanillaPrefabsAvailable()
        {
            _registered = false;
            _nextAttempt = 0f;
            TryRegister();
        }

        private void TryRegister()
        {
            try
            {
                Assembly jotunn = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "Jotunn", StringComparison.OrdinalIgnoreCase));
                if (jotunn == null) return;

                Type pieceConfigType = jotunn.GetType("Jotunn.Configs.PieceConfig", false);
                Type customPieceType = jotunn.GetType("Jotunn.Entities.CustomPiece", false);
                Type pieceManagerType = jotunn.GetType("Jotunn.Managers.PieceManager", false);
                if (pieceConfigType == null || customPieceType == null || pieceManagerType == null) return;

                object manager = pieceManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                if (manager == null) return;

                MethodInfo getPiece = pieceManagerType.GetMethod("GetPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                MethodInfo getPieceTable = pieceManagerType.GetMethod("GetPieceTable", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
                MethodInfo registerInTable = pieceManagerType.GetMethod("RegisterPieceInPieceTable", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(GameObject), typeof(string), typeof(string) }, null);

                // Jotunn's PieceManager table lookup is not guaranteed to be available at the same
                // moment as the live Hammer item. Resolve the actual PieceTable from the vanilla
                // Hammer as a fallback, otherwise registration can silently retry forever.
                PieceTable hammer = null;
                try { hammer = getPieceTable?.Invoke(manager, new object[] { "Hammer" }) as PieceTable; } catch { }
                if (hammer == null || hammer.m_pieces == null)
                    hammer = ResolveLiveHammerTable();
                if (hammer == null || hammer.m_pieces == null)
                {
                    _debug?.Invoke("Production display registration waiting: live Hammer PieceTable unavailable.");
                    return;
                }

                bool portraitReady = RegisterOne(manager, pieceManagerType, pieceConfigType, customPieceType, getPiece, registerInTable, hammer,
                    PortraitPrefab, "Produktionsanzeige - Hoch", "Detailansicht einer Ressource. Hotbar 1-8 waehlt das angezeigte Item.", false);
                bool landscapeReady = RegisterOne(manager, pieceManagerType, pieceConfigType, customPieceType, getPiece, registerInTable, hammer,
                    LandscapePrefab, "Produktionsanzeige - Quer", "Uebersicht mehrerer Ressourcen. Hotbar 1-8 fuegt Items hinzu oder entfernt sie.", true);

                _registered = portraitReady && landscapeReady;
                if (_registered)
                    _info?.Invoke("Workers production displays ready in Hammer/Furniture: portrait + landscape.");
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _debug?.Invoke("Production display registration pending: " + root.GetType().Name + ": " + root.Message);
            }
        }

        private static bool RegisterOne(
            object manager,
            Type pieceManagerType,
            Type pieceConfigType,
            Type customPieceType,
            MethodInfo getPiece,
            MethodInfo registerInTable,
            PieceTable hammer,
            string prefabName,
            string title,
            string description,
            bool landscape)
        {
            GameObject prefab = getPiece?.Invoke(manager, new object[] { prefabName }) as GameObject;

            if (prefab == null)
            {
                object config = Activator.CreateInstance(pieceConfigType);
                Set(config, "Name", title);
                Set(config, "Description", description);
                Set(config, "PieceTable", "Hammer");
                // Use a vanilla category so the displays can never disappear because a custom
                // category was not registered yet. They are signs/furniture and fit here naturally.
                Set(config, "Category", "Furniture");
                Set(config, "CraftingStation", "piece_workbench");
                Set(config, "AllowedInDungeons", false);
                Set(config, "Enabled", true);

                MethodInfo addRequirement = pieceConfigType.GetMethod("AddRequirement", BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(string), typeof(int), typeof(bool) }, null);
                addRequirement?.Invoke(config, new object[] { "FineWood", 2, true });

                ConstructorInfo ctor = customPieceType.GetConstructor(new[] { typeof(string), typeof(string), pieceConfigType });
                if (ctor == null) return false;

                object customPiece = ctor.Invoke(new object[] { prefabName, "sign", config });
                prefab = customPieceType.GetProperty("PiecePrefab", BindingFlags.Instance | BindingFlags.Public)?.GetValue(customPiece) as GameObject;
                if (prefab == null) return false;

                ProductionDisplayMarker marker = prefab.GetComponent<ProductionDisplayMarker>() ?? prefab.AddComponent<ProductionDisplayMarker>();
                marker.ConfigureLandscape(landscape);

                MethodInfo addPiece = pieceManagerType.GetMethod("AddPiece", BindingFlags.Instance | BindingFlags.Public, null, new[] { customPieceType }, null);
                addPiece?.Invoke(manager, new[] { customPiece });

                // Jotunn may already know the CustomPiece while the live Hammer table still misses
                // it (late registration). Re-resolve the manager prefab after AddPiece if possible.
                GameObject registeredPrefab = getPiece?.Invoke(manager, new object[] { prefabName }) as GameObject;
                if (registeredPrefab != null) prefab = registeredPrefab;
            }

            ProductionDisplayMarker existingMarker = prefab.GetComponent<ProductionDisplayMarker>() ?? prefab.AddComponent<ProductionDisplayMarker>();
            existingMarker.ConfigureLandscape(landscape);
            Piece piece = prefab.GetComponent<Piece>() ?? prefab.GetComponentInChildren<Piece>(true);
            if (piece != null)
            {
                // Do not rely solely on PieceConfig being applied to a late-created sign clone.
                // Force the user-visible Piece metadata/category on the actual prefab too.
                try { piece.m_enabled = true; } catch { }
                SetPieceField(piece, "m_name", title);
                SetPieceField(piece, "m_description", description);
                SetPieceEnumField(piece, "m_category", "Furniture");
            }

            if (!ContainsPiece(hammer, prefabName) && registerInTable != null)
            {
                try { registerInTable.Invoke(manager, new object[] { prefab, "Hammer", "Furniture" }); }
                catch { }
            }

            // Last-resort live-table repair. This is the same defensive pattern used by the
            // SmartStorage bridge and prevents a successfully created prefab from being invisible
            // in the Hammer menu for the remainder of the session.
            if (!ContainsPiece(hammer, prefabName))
                hammer.m_pieces.Add(prefab);

            return ContainsPiece(hammer, prefabName);
        }


        private static PieceTable ResolveLiveHammerTable()
        {
            try
            {
                ObjectDB db = ObjectDB.instance;
                GameObject hammerItem = db != null ? db.GetItemPrefab("Hammer") : null;
                ItemDrop drop = hammerItem != null ? hammerItem.GetComponent<ItemDrop>() : null;
                PieceTable table = drop?.m_itemData?.m_shared?.m_buildPieces;
                if (table != null && table.m_pieces != null) return table;
            }
            catch { }

            try
            {
                PieceTable[] tables = Resources.FindObjectsOfTypeAll<PieceTable>();
                if (tables != null)
                {
                    for (int i = 0; i < tables.Length; i++)
                    {
                        PieceTable table = tables[i];
                        if (table == null || table.m_pieces == null) continue;
                        string n = table.name ?? string.Empty;
                        if (n.IndexOf("hammer", StringComparison.OrdinalIgnoreCase) >= 0) return table;
                    }
                }
            }
            catch { }
            return null;
        }

        private static void SetPieceField(Piece piece, string fieldName, object value)
        {
            try
            {
                FieldInfo field = typeof(Piece).GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) field.SetValue(piece, value);
            }
            catch { }
        }

        private static void SetPieceEnumField(Piece piece, string fieldName, string enumName)
        {
            try
            {
                FieldInfo field = typeof(Piece).GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null || !field.FieldType.IsEnum) return;
                object value = Enum.Parse(field.FieldType, enumName, true);
                field.SetValue(piece, value);
            }
            catch { }
        }

        private static bool ContainsPiece(PieceTable table, string prefabName)
        {
            if (table == null || table.m_pieces == null || string.IsNullOrWhiteSpace(prefabName)) return false;
            for (int i = 0; i < table.m_pieces.Count; i++)
            {
                GameObject go = table.m_pieces[i];
                if (go == null) continue;
                string current = WorkerStorageRuntime.NormalizeObjectName(go.name ?? string.Empty);
                if (string.Equals(current, prefabName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void Set(object target, string name, object value)
        {
            PropertyInfo property = target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                try { property.SetValue(target, value); } catch { }
                return;
            }
            FieldInfo field = target?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { field.SetValue(target, value); } catch { }
            }
        }
    }

}