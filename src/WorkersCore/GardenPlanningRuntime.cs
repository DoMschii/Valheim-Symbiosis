using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Worker planting planner. P toggles planning. The selected Cultivator crop remains the
    /// anchor ghost. Hold Right Ctrl and use the arrow keys to grow/shrink a signed corner grid,
    /// modelled after the proven SmartReplant field workflow. A click stores persistent
    /// Workers planting slots instead of spawning plants or consuming seed.
    /// </summary>
    internal sealed class GardenPlanningRuntime
    {
        private const int MaxAxisCells = 16;
        private const int MaxPlannedCells = 192;
        private const float ArrowRepeatDelay = 0.35f;
        private const float ArrowRepeatInterval = 0.08f;
        private const float PersistentGhostRefreshActiveInterval = 0.25f;
        private const float PersistentGhostRefreshIdleInterval = 1.25f;
        private const int MaxVisiblePersistentGhosts = 512;
        private const float TerrainPreviewRefreshInterval = 0.08f;

        private readonly Action<string> _debugLog;
        private readonly Action<string> _status;
        private readonly List<Vector3> _gridPositions = new List<Vector3>(MaxPlannedCells);
        private readonly List<GameObject> _previewGhosts = new List<GameObject>(MaxPlannedCells - 1);
        private readonly List<GardenPersistentVisualSlot> _persistentSlots = new List<GardenPersistentVisualSlot>(MaxPlannedCells);
        private readonly Dictionary<string, PersistentGhostVisual> _persistentGhosts = new Dictionary<string, PersistentGhostVisual>(StringComparer.Ordinal);
        private readonly HashSet<string> _persistentSeen = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _persistentRemove = new List<string>();
        private readonly Dictionary<Material, Material> _persistentGhostMaterials = new Dictionary<Material, Material>();
        private readonly Dictionary<Material, Material> _deleteGhostMaterials = new Dictionary<Material, Material>();
        private readonly HashSet<string> _deleteHighlightKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly ArrowRepeatState _leftRepeat = new ArrowRepeatState();
        private readonly ArrowRepeatState _rightRepeat = new ArrowRepeatState();
        private readonly ArrowRepeatState _upRepeat = new ArrowRepeatState();
        private readonly ArrowRepeatState _downRepeat = new ArrowRepeatState();

        private bool _active;
        private int _horizontalExtent;
        private int _verticalExtent;
        private string _lastPlanned = string.Empty;
        private int _lastSlotId;
        private int _lastFieldCount;
        private string _lastPlanFeedback = "Noch kein Planungsklick bestaetigt.";
        private int _previewSourceId;
        private float _nextPersistentGhostRefreshAt;
        private float _nextTerrainPreviewRefreshAt;
        private int _lastPersistentSlotCount;
        private int _lastPersistentGhostCount;
        private int _lastPersistentResolveFailures;
        private GardenPersistentPlanSelection _editSelection;
        private float _nextEditSelectionProbeAt;
        private bool _lastDeleteShiftHeld;
        private int _directPlanningClickFrame = -1;
        private float _lastPlanResultAt = -999f;
        private const float PlanningRotationStep = 22.5f;
        private float _fallbackRotationY;
        private bool _fallbackRotationInitialized;
        private string _fallbackRotationPieceKey = string.Empty;

        private sealed class PersistentGhostVisual
        {
            internal readonly GameObject Root;
            internal readonly string PlantPrefab;
            internal readonly Vector3 BaseScale;
            internal readonly Renderer[] Renderers;
            internal readonly Material[][] BaseMaterials;
            internal Material[][] DeleteMaterials;
            internal bool DeleteHighlighted;
            internal int FieldId;

            internal PersistentGhostVisual(GameObject root, string plantPrefab, int fieldId)
            {
                Root = root;
                PlantPrefab = plantPrefab ?? string.Empty;
                BaseScale = root != null ? root.transform.localScale : Vector3.one;
                FieldId = fieldId;
                Renderers = root != null ? root.GetComponentsInChildren<Renderer>(true) : Array.Empty<Renderer>();
                BaseMaterials = new Material[Renderers.Length][];
                for (int i = 0; i < Renderers.Length; i++)
                    BaseMaterials[i] = Renderers[i] != null ? Renderers[i].sharedMaterials : Array.Empty<Material>();
            }
        }

        // Cached once. No per-frame reflection lookup.
        private static readonly FieldInfo PlacementGhostField = AccessTools.Field(typeof(Player), "m_placementGhost");
        private static readonly FieldInfo PlantHealthyField = AccessTools.Field(typeof(Plant), "m_healthy");
        private static readonly FieldInfo PlantHealthyGrownField = AccessTools.Field(typeof(Plant), "m_healthyGrown");
        private static readonly RaycastHit[] GroundHits = new RaycastHit[32];
        private static readonly int TerrainLayer = LayerMask.NameToLayer("terrain");

        internal bool Active => _active;

        internal bool IsSupportedPlanningPiece(Piece piece)
        {
            return piece != null && TryGetUnifiedPlanningData(piece, out _, out _, out _, out _, out _, out _);
        }

        internal bool IsSupportedPlanningPiece(Player player, Piece suppliedPiece)
        {
            return TryResolveEffectivePlanningPiece(player, suppliedPiece, out _, out _);
        }
        internal int Columns => Mathf.Abs(_horizontalExtent) + 1;
        internal int Rows => Mathf.Abs(_verticalExtent) + 1;
        internal int CellCount => Columns * Rows;

        internal GardenPlanningRuntime(Action<string> debugLog, Action<string> status)
        {
            _debugLog = debugLog;
            _status = status;
        }

        internal void Tick()
        {
            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead())
            {
                Deactivate();
                DestroyPersistentGhosts();
                return;
            }

            if (Input.GetKeyDown(KeyCode.P))
            {
                _active = !_active;
                ResetInput();
                if (_active)
                {
                    // SmartReplant may already have an asynchronous field-placement job running
                    // from a previous click. Cancel it once when Workers becomes the exclusive
                    // owner of planting input so it cannot continue spawning real crops behind
                    // our planning ghosts.
                    SmartReplantPlanningCompatibility.CancelActiveFieldJob(_debugLog);
                }
                else
                {
                    HidePreview();
                    _editSelection = default;
                }
                _status?.Invoke(_active
                    ? "Pflanzplanung AN: Workers besitzt den Pflanz-Klick exklusiv. Rechts-Strg + Pfeile = Feldgroesse."
                    : "Pflanzplanung AUS: Kultivator/SmartReplant pflanzt wieder normal.");
                _debugLog?.Invoke($"Garden planning mode toggled: active={_active}, size={Columns}x{Rows}");
            }

            if (_active)
            {
                UpdateEditSelection(player);
                HandleDeleteInput(player);
            }
            else
            {
                _editSelection = default;
                _deleteHighlightKeys.Clear();
            }
            UpdatePersistentPlanGhosts(player);

            if (!_active)
                return;

            HandleSizingInput(player);
            HandleRotationInput(player);
            UpdatePreview(player);
            HandleDirectPlanningClick(player);
        }

        /// <summary>
        /// Called directly from the TryPlacePiece prefix. Returning true means the supported
        /// crop placement was consumed by Workers and Vanilla must NOT continue.
        /// </summary>
        internal bool TryInterceptPlacement(Player player, Piece piece)
        {
            if (!_active || player == null || player != Player.m_localPlayer || piece == null)
                return false;

            // The planner can consume the primary click directly when SmartReplant intentionally
            // provides no native placement ghost. If Valheim/SmartReplant reaches this prefix later
            // in the same frame, suppress the real placement without storing the plan twice.
            if (_directPlanningClickFrame == Time.frameCount)
                return true;

            // SmartReplant keeps its own flora selection and does not always update
            // Player.GetSelectedPiece / the Piece passed into TryPlacePiece. The live placement
            // ghost is authoritative while planning, otherwise switching Raspberry -> Thistle
            // can keep planning the previous Raspberry piece forever.
            if (!TryResolveEffectivePlanningPiece(player, piece, out Piece effectivePiece, out string planningSource) ||
                !TryGetUnifiedPlanningData(effectivePiece, out _, out _, out string plantPrefab, out _, out _, out _))
                return false;

            piece = effectivePiece;
            _debugLog?.Invoke($"Garden planning effective piece: source={planningSource}, supplied={NormalizePieceNameForDebug(piece)}, plant={plantPrefab}");

            // From this point onward the click belongs to Workers. SmartReplant persistent flora
            // deliberately does not expose Player.m_placementGhost. Resolve the exact same
            // fallback anchor used by the preview (aim ray) instead of merely swallowing the click.
            if (!TryResolvePlanningAnchor(player, piece, out Vector3 anchor, out Quaternion rotation, out _, out string anchorSource))
            {
                _status?.Invoke("Pflanzplanung blockiert: keine Zielposition unter dem Fadenkreuz gefunden. Keine Saat verbraucht.");
                _debugLog?.Invoke($"Garden planning blocked supported crop '{plantPrefab}' because no native ghost or aim-ray anchor was available.");
                return true;
            }

            bool stored = StorePlannedPlacement(player, piece, anchor, rotation, "native/manual dispatcher/" + anchorSource);
            if (stored) _directPlanningClickFrame = Time.frameCount;
            return true;
        }

        /// <summary>
        /// Secondary SmartReplant fail-safe. SmartReplant multi-field planting ultimately enters
        /// FieldPlantingService.Plan(Player, Piece, Vector3, Quaternion, bool). If another Harmony
        /// transpiler bypasses ManualFloraPatch.Place, this gate still turns that real placement
        /// request into Workers persistent planting slots.
        /// </summary>
        internal bool TryInterceptExternalFieldPlan(Player player, Piece piece, Vector3 anchor, Quaternion rotation)
        {
            if (!_active || player == null || player != Player.m_localPlayer || piece == null)
                return false;

            if (!TryResolveEffectivePlanningPiece(player, piece, out Piece effectivePiece, out string planningSource) ||
                !TryGetUnifiedPlanningData(effectivePiece, out _, out _, out _, out _, out _, out _))
                return false;

            Quaternion yawOnly = Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
            return StorePlannedPlacement(player, effectivePiece, anchor, yawOnly,
                "SmartReplant FieldPlantingService.Plan/" + planningSource);
        }

        private bool StorePlannedPlacement(Player player, Piece piece, Vector3 anchor, Quaternion rotation, string source)
        {
            if (!TryGetUnifiedPlanningData(piece, out bool forestry, out string identityPrefab, out string plantPrefab,
                    out string resourcePrefab, out int resourceAmount, out float spacing))
                return false;

            int wanted = CellCount;
            if (wanted < 1 || wanted > MaxPlannedCells)
            {
                _status?.Invoke($"Feld nicht geplant: {wanted} Slots ueberschreiten das Limit von {MaxPlannedCells}.");
                return true;
            }

            if (!BuildPlacementGrid(anchor, rotation, Mathf.Max(0.7f, spacing), true, true, _gridPositions, out string failure))
            {
                _lastPlanFeedback = $"NICHT gespeichert: {failure}";
                _lastPlanResultAt = Time.unscaledTime;
                _status?.Invoke($"Feld nicht geplant: {failure}. Keine Saat verbraucht.");
                _debugLog?.Invoke($"Garden field geometry rejected: source={source}, plant={plantPrefab}, size={Columns}x{Rows}, reason={failure}");
                return true;
            }

            long owner = player.GetPlayerID();
            bool stored;
            int firstSlotId;
            int plannedCount;

            if (_gridPositions.Count == 1)
            {
                stored = forestry
                    ? ForestryPlanRuntime.EnqueuePlannedSlot(owner, resourcePrefab, plantPrefab, _gridPositions[0], rotation.eulerAngles.y, out firstSlotId)
                    : GardenReplantPlanRuntime.EnqueuePlannedSlot(owner, identityPrefab, _gridPositions[0], rotation.eulerAngles.y, out firstSlotId);
                plannedCount = stored ? 1 : 0;
            }
            else
            {
                stored = forestry
                    ? ForestryPlanRuntime.EnqueuePlannedField(owner, resourcePrefab, plantPrefab, _gridPositions, rotation.eulerAngles.y, out firstSlotId, out plannedCount)
                    : GardenReplantPlanRuntime.EnqueuePlannedField(owner, identityPrefab, _gridPositions, rotation.eulerAngles.y, out firstSlotId, out plannedCount);
            }

            if (!stored)
            {
                string status = forestry ? ForestryPlanRuntime.LastStatus : GardenReplantPlanRuntime.LastStatus;
                _lastPlanFeedback = $"NICHT gespeichert: {status}";
                _lastPlanResultAt = Time.unscaledTime;
                _status?.Invoke($"Feld NICHT gespeichert: {status}. Keine Ressource verbraucht.");
                _debugLog?.Invoke($"Planning field rejected but real placement blocked: source={source}, forestry={forestry}, plant={plantPrefab}, resource={resourcePrefab} x{resourceAmount}, size={Columns}x{Rows}, status={status}");
                return true;
            }

            _lastPlanned = plantPrefab;
            _lastSlotId = firstSlotId;
            _lastFieldCount = plannedCount;
            _lastPlanFeedback = plannedCount == 1
                ? $"GESPEICHERT: Slot #{firstSlotId} - {plantPrefab}"
                : $"GESPEICHERT: {plannedCount} Slots - {plantPrefab}";
            _lastPlanResultAt = Time.unscaledTime;
            _nextPersistentGhostRefreshAt = 0f;
            UpdatePersistentPlanGhosts(player);
            _status?.Invoke(plannedCount == 1
                ? $"Pflanzslot #{firstSlotId} geplant: {plantPrefab}. Keine Ressource verbraucht."
                : $"Feld geplant: {Columns} x {Rows} = {plannedCount} x {plantPrefab}. Keine Ressource verbraucht.");
            _debugLog?.Invoke($"Garden planned field: source={source}, firstId={firstSlotId}, count={plannedCount}, identity={identityPrefab}, plant={plantPrefab}, resource={resourcePrefab} x{resourceAmount}, spacing={spacing:0.00}, size={Columns}x{Rows}, h={_horizontalExtent}, v={_verticalExtent}, anchor={anchor}, rotY={rotation.eulerAngles.y:0.0}");
            return true;
        }

        internal void OnGUI()
        {
            if (!_active) return;

            float width = Mathf.Min(860f, Screen.width - 40f);
            Rect rect = new Rect((Screen.width - width) * 0.5f, 28f, width, 158f);
            GUI.Box(rect, GUIContent.none);
            GUILayout.BeginArea(new Rect(rect.x + 12f, rect.y + 8f, rect.width - 24f, rect.height - 16f));
            GUILayout.Label("PFLANZPLANUNG AN  [P = aus]");
            GUILayout.Label($"Feld: {Columns} x {Rows} = {CellCount} Slots   |   RECHTS-STRG + Pfeile = vergroessern/verkleinern   |   Mausrad = drehen (22,5°)");
            GUILayout.Label($"Richtung: horizontal {Direction(_horizontalExtent, "links", "rechts")}, vertikal {Direction(_verticalExtent, "hinten", "vorne")}   |   Klick = Raster planen");
            GUILayout.Label("Bearbeiten: Ghost anvisieren = rot   |   Mausradklick = Slot loeschen   |   SHIFT = Gruppe rot   |   SHIFT + Mausradklick = Gruppe loeschen");
            if (_editSelection.IsValid)
                GUILayout.Label($"Anvisiert: Slot #{_editSelection.SlotId}  Feld #{(_editSelection.FieldId > 0 ? _editSelection.FieldId.ToString() : "alt")}  " +
                    $"{(ShiftHeld() ? "GRUPPE" : "EINZELN")}  Entfernung {_editSelection.Distance:0.0} m");
            else if (_lastFieldCount > 0)
                GUILayout.Label($"Zuletzt: {_lastFieldCount} x {_lastPlanned}, erster Slot #{_lastSlotId}");
            else
                GUILayout.Label("Anvisiert: keine Ghost-Pflanze");
            GUILayout.Label($"Planstatus: {_lastPlanFeedback}");
            GUILayout.Label($"Plan-Ghosts: {_lastPersistentGhostCount}/{_lastPersistentSlotCount}" +
                (_lastPersistentResolveFailures > 0 ? $"   |   Visual nicht aufloesbar: {_lastPersistentResolveFailures}" : string.Empty));
            GUILayout.EndArea();
        }

        internal void Shutdown()
        {
            Deactivate();
            DestroyPreviewPool();
            DestroyPersistentGhosts();
            DestroyPersistentGhostMaterials();
            _horizontalExtent = 0;
            _verticalExtent = 0;
            _lastPlanned = string.Empty;
            _lastSlotId = 0;
            _lastFieldCount = 0;
            _editSelection = default;
            _deleteHighlightKeys.Clear();
            _lastDeleteShiftHeld = false;
            _nextEditSelectionProbeAt = 0f;
        }

        private void Deactivate()
        {
            _active = false;
            _editSelection = default;
            _deleteHighlightKeys.Clear();
            _lastDeleteShiftHeld = false;
            ResetInput();
            ResetFallbackRotation();
            _nextTerrainPreviewRefreshAt = 0f;
            HidePreview();
        }

        private void HandleSizingInput(Player player)
        {
            if (player == null || !player.InPlaceMode() || !RightControlHeld() || !TryGetSelectedPlanningPiece(player, out _))
            {
                ResetInput();
                return;
            }

            bool left = Input.GetKey(KeyCode.LeftArrow);
            bool right = Input.GetKey(KeyCode.RightArrow);
            bool up = Input.GetKey(KeyCode.UpArrow);
            bool down = Input.GetKey(KeyCode.DownArrow);

            HandleArrow(KeyCode.LeftArrow, ExpansionDirection.Left, left && !right, _leftRepeat);
            HandleArrow(KeyCode.RightArrow, ExpansionDirection.Right, right && !left, _rightRepeat);
            HandleArrow(KeyCode.UpArrow, ExpansionDirection.Forward, up && !down, _upRepeat);
            HandleArrow(KeyCode.DownArrow, ExpansionDirection.Backward, down && !up, _downRepeat);
        }

        private void HandleArrow(KeyCode key, ExpansionDirection direction, bool pressed, ArrowRepeatState state)
        {
            if (!state.Step(Input.GetKeyDown(key), pressed, Time.unscaledTime, ArrowRepeatDelay, ArrowRepeatInterval))
                return;

            int oldHorizontal = _horizontalExtent;
            int oldVertical = _verticalExtent;
            int horizontal = oldHorizontal;
            int vertical = oldVertical;

            switch (direction)
            {
                case ExpansionDirection.Left: horizontal--; break;
                case ExpansionDirection.Right: horizontal++; break;
                case ExpansionDirection.Forward: vertical++; break;
                case ExpansionDirection.Backward: vertical--; break;
            }

            horizontal = Mathf.Clamp(horizontal, -(MaxAxisCells - 1), MaxAxisCells - 1);
            vertical = Mathf.Clamp(vertical, -(MaxAxisCells - 1), MaxAxisCells - 1);
            int cells = (Mathf.Abs(horizontal) + 1) * (Mathf.Abs(vertical) + 1);
            if (cells > MaxPlannedCells)
            {
                _status?.Invoke($"Feldlimit erreicht: maximal {MaxPlannedCells} Slots.");
                return;
            }

            if (horizontal == oldHorizontal && vertical == oldVertical)
                return;

            _horizontalExtent = horizontal;
            _verticalExtent = vertical;
            _status?.Invoke($"Pflanzfeld: {Columns} x {Rows} = {CellCount} Slots");
            _debugLog?.Invoke($"Garden field extent: key={key}, horizontal={_horizontalExtent}, vertical={_verticalExtent}, size={Columns}x{Rows}");
        }

        private void ResetInput()
        {
            _leftRepeat.Reset();
            _rightRepeat.Reset();
            _upRepeat.Reset();
            _downRepeat.Reset();
        }

        private void ResetFallbackRotation()
        {
            _fallbackRotationY = 0f;
            _fallbackRotationInitialized = false;
            _fallbackRotationPieceKey = string.Empty;
        }

        private void HandleRotationInput(Player player)
        {
            if (player == null || !player.InPlaceMode() || !TryGetSelectedPlanningPiece(player, out Piece piece) ||
                !UsesPlannerOwnedRotation(player, piece))
                return;

            EnsureFallbackRotation(player, piece);
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) < 0.01f) return;

            int steps = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(wheel)));
            float direction = wheel > 0f ? 1f : -1f;
            _fallbackRotationY = Mathf.Repeat(_fallbackRotationY + direction * PlanningRotationStep * steps, 360f);
            _nextTerrainPreviewRefreshAt = 0f;
            _status?.Invoke($"Pflanzfeld gedreht: {_fallbackRotationY:0.0}°");
            _debugLog?.Invoke($"Garden planner rotation: piece={PlanningPieceKey(piece)}, wheel={wheel:0.00}, yaw={_fallbackRotationY:0.0}");
        }

        private static bool UsesPlannerOwnedRotation(Player player, Piece piece)
        {
            if (piece == null) return false;

            if (piece.gameObject != null && WorkersWildFloraRuntime.TryGetDefinition(piece.gameObject, out _))
                return true;

            // SmartReplant/manual flora can intentionally have no native Valheim placement ghost.
            // In that case Workers owns both the aim-ray anchor and rotation.
            return !TryGetPlacementGhostTransform(player, out Transform ghost) || ghost == null;
        }

        private void EnsureFallbackRotation(Player player, Piece piece)
        {
            string key = PlanningPieceKey(piece);
            if (_fallbackRotationInitialized && string.Equals(_fallbackRotationPieceKey, key, StringComparison.Ordinal))
                return;

            float yaw = player != null ? player.transform.rotation.eulerAngles.y : 0f;
            _fallbackRotationY = Mathf.Repeat(Mathf.Round(yaw / PlanningRotationStep) * PlanningRotationStep, 360f);
            _fallbackRotationPieceKey = key;
            _fallbackRotationInitialized = true;
        }

        private static string PlanningPieceKey(Piece piece)
        {
            if (piece == null || piece.gameObject == null) return string.Empty;
            return piece.gameObject.name ?? string.Empty;
        }

        private void UpdatePreview(Player player)
        {
            if (player == null || !player.InPlaceMode() || !TryGetSelectedPlanningPiece(player, out Piece piece) ||
                !TryGetUnifiedPlanningData(piece, out _, out _, out string plantPrefab, out _, out _, out float spacing) ||
                !TryResolvePlanningAnchor(player, piece, out Vector3 anchor, out Quaternion rotation, out Transform nativeGhost, out string anchorSource))
            {
                HidePreview();
                return;
            }

            // Project every preview cell onto terrain. SmartReplant flora such as Thistle can run
            // with nativeGhostRequired=false, so Workers must be able to render the COMPLETE field
            // itself instead of assuming Valheim supplies the anchor ghost.
            if (Time.unscaledTime >= _nextTerrainPreviewRefreshAt || _gridPositions.Count != CellCount)
            {
                _nextTerrainPreviewRefreshAt = Time.unscaledTime + TerrainPreviewRefreshInterval;
                if (!BuildPlacementGrid(anchor, rotation, Mathf.Max(0.7f, spacing), true, false, _gridPositions, out _))
                    BuildPlacementGrid(anchor, rotation, Mathf.Max(0.7f, spacing), false, false, _gridPositions, out _);
            }

            // SmartReplant custom flora can expose a placement transform while deliberately
            // hiding the real plant model (nativeGhostRequired=false / rootVisible=false).
            // Treating that transform as a visible anchor caused a 1x1 field to render ZERO
            // Workers ghosts. For every SmartReplant_Flora_* piece, Workers renders the full
            // preview itself from the actual flora prefab, including cell 0.
            bool smartFlora = plantPrefab.StartsWith("SmartReplant_Flora_", StringComparison.OrdinalIgnoreCase);
            bool workersWildFlora = piece != null && piece.gameObject != null &&
                                    WorkersWildFloraRuntime.TryGetDefinition(piece.gameObject, out _);
            // Never trust a native placement ghost for VW_Flora_*: on small vanilla Pickables
            // Valheim may expose no ghost or a stale ghost from the previously selected piece.
            // Workers renders every planned cell itself from the canonical vanilla visual.
            bool nativeAnchorVisible = nativeGhost != null && !smartFlora && !workersWildFlora && HasVisibleRenderer(nativeGhost);
            Transform previewSource = (smartFlora || workersWildFlora)
                ? ResolvePreviewSource(piece, plantPrefab)
                : (nativeAnchorVisible ? nativeGhost : ResolvePreviewSource(piece, plantPrefab));
            if (previewSource == null)
            {
                HidePreview();
                return;
            }

            int visualCount = nativeAnchorVisible ? Mathf.Max(0, _gridPositions.Count - 1) : _gridPositions.Count;
            EnsurePreviewPool(previewSource, visualCount);

            for (int i = 0; i < _previewGhosts.Count; i++)
            {
                GameObject visual = _previewGhosts[i];
                bool visible = i < visualCount;
                if (visual.activeSelf != visible) visual.SetActive(visible);
                if (!visible) continue;
                int gridIndex = nativeAnchorVisible ? i + 1 : i;
                visual.transform.position = _gridPositions[gridIndex];
                visual.transform.rotation = rotation;
            }

            // Do not erase a useful success/failure message on the very next frame.
            // This made rejected planning clicks look like absolutely nothing happened.
            if (Time.unscaledTime - _lastPlanResultAt > 2.5f)
                _lastPlanFeedback = $"Vorschau: {plantPrefab} via {anchorSource}";
        }

        private static bool HasVisibleRenderer(Transform root)
        {
            if (root == null || !root.gameObject.activeInHierarchy) return false;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || renderer.forceRenderingOff) continue;
                if (!renderer.gameObject.activeInHierarchy) continue;
                // Ignore tiny helper/placement-marker renderers; we need an actual plant visual.
                Bounds b = renderer.bounds;
                if (b.size.sqrMagnitude > 0.08f) return true;
            }
            return false;
        }

        private void HandleDirectPlanningClick(Player player)
        {
            if (_directPlanningClickFrame == Time.frameCount) return;
            bool primaryDown = Input.GetMouseButtonDown(0);
            try { primaryDown |= ZInput.GetButtonDown("Attack"); } catch { }
            if (!_active || player == null || !player.InPlaceMode() || !primaryDown) return;
            if (!TryGetSelectedPlanningPiece(player, out Piece piece))
            {
                _lastPlanFeedback = "KLICK erkannt, aber keine planbare Pflanze aufgeloest.";
                _lastPlanResultAt = Time.unscaledTime;
                return;
            }
            if (!TryGetUnifiedPlanningData(piece, out _, out _, out _, out _, out _, out _))
            {
                _lastPlanFeedback = "KLICK erkannt, aber Pflanzdaten fehlen.";
                _lastPlanResultAt = Time.unscaledTime;
                return;
            }
            if (!TryResolvePlanningAnchor(player, piece, out Vector3 anchor, out Quaternion rotation, out _, out string source))
            {
                _lastPlanFeedback = "KLICK erkannt, aber kein Bodenanker gefunden.";
                _lastPlanResultAt = Time.unscaledTime;
                _status?.Invoke("Pflanzplanung: kein Boden unter dem Fadenkreuz gefunden.");
                return;
            }

            // Claim this frame before storing. Any downstream Vanilla/SmartReplant placement prefix
            // will then fail closed and cannot consume resources or create a real plant.
            _directPlanningClickFrame = Time.frameCount;
            _debugLog?.Invoke($"Garden planning direct click captured: piece={NormalizePieceNameForDebug(piece)}, source={source}, anchor={anchor}");
            StorePlannedPlacement(player, piece, anchor, rotation, "direct-input/" + source);
        }

        private bool TryResolvePlanningAnchor(Player player, Piece planningPiece, out Vector3 anchor, out Quaternion rotation, out Transform nativeGhost, out string source)
        {
            anchor = Vector3.zero;
            rotation = Quaternion.identity;
            nativeGhost = null;
            source = "none";
            if (player == null) return false;

            // Workers-native WildGrowth pieces (mushroom/thistle/dandelion/bushes) deliberately
            // do not need Valheim's native placement ghost. For the small Pickable prefabs the
            // engine can leave m_placementGhost null OR keep the previous Cultivator ghost alive.
            // Using that stale ghost made planning resolve/anchor to the previous crop and produced
            // no Workers preview at all. The selected VW_Flora_* piece is authoritative: anchor it
            // directly from the player's aim ray and render our own renderer-only ghost field.
            bool workersWildFlora = planningPiece != null && planningPiece.gameObject != null &&
                                    WorkersWildFloraRuntime.TryGetDefinition(planningPiece.gameObject, out _);

            if (!workersWildFlora && TryGetPlacementGhostTransform(player, out Transform ghost) && ghost != null)
            {
                nativeGhost = ghost;
                anchor = ghost.position;
                rotation = Quaternion.Euler(0f, ghost.rotation.eulerAngles.y, 0f);
                source = "placement-ghost";
                return true;
            }

            // SmartReplant persistent flora deliberately has no native placement ghost in current
            // builds. Use the player's aim ray as an authoritative planning anchor instead. This
            // keeps planning independent from SmartReplant's private preview implementation.
            Camera camera = Camera.main;
            if (camera == null) return false;
            int mask = TerrainLayer >= 0 ? (1 << TerrainLayer) : Physics.DefaultRaycastLayers;
            if (!Physics.Raycast(camera.transform.position, camera.transform.forward, out RaycastHit hit, 80f, mask, QueryTriggerInteraction.Ignore))
                return false;

            anchor = hit.point;
            EnsureFallbackRotation(player, planningPiece);
            rotation = Quaternion.Euler(0f, _fallbackRotationY, 0f);
            source = "aim-ray/planner-rotation";
            return true;
        }

        private static Transform ResolvePreviewSource(Piece piece, string plantPrefab)
        {
            // v0.5.373: Workers WildGrowth should look like the actual vanilla plant while planning.
            // The network-safe VW_Flora_* clone remains an internal persistence detail only.
            if (piece != null && WorkersWildFloraRuntime.TryResolvePlanningVisual(piece, out GameObject vanillaWildVisual) && vanillaWildVisual != null)
                return vanillaWildVisual.transform;
            if (WorkersWildFloraRuntime.TryResolvePlanningVisual(plantPrefab, out vanillaWildVisual) && vanillaWildVisual != null)
                return vanillaWildVisual.transform;

            // SmartReplant flora pieces are wrapper/network prefabs. Their actual visible plant is
            // FloraDefinition.Target (e.g. SmartReplant_Flora_Thistle -> Pickable_Thistle). Using
            // the wrapper as preview source yields an empty marker instead of a plant ghost.
            if (piece != null && SmartReplantBridge.IsSmartReplantPlantingPiece(piece.gameObject) &&
                SmartReplantBridge.TryResolveFloraDefinition(piece, out GameObject targetPrefab, out _) && targetPrefab != null)
                return targetPrefab.transform;

            if (piece != null)
            {
                Renderer[] renderers = piece.GetComponentsInChildren<Renderer>(true);
                if (renderers != null && renderers.Length > 0) return piece.transform;
            }

            GameObject plant = GardenWorkRuntime.ResolvePlantPrefab(plantPrefab);
            if (plant != null) return plant.transform;
            return piece != null ? piece.transform : null;
        }

        private bool BuildPlacementGrid(Vector3 anchor, Quaternion rotation, float spacing, bool snapTerrain, bool validateSlope,
            List<Vector3> output, out string failure)
        {
            output.Clear();
            failure = string.Empty;

            int minX = Math.Min(0, _horizontalExtent);
            int maxX = Math.Max(0, _horizontalExtent);
            int minZ = Math.Min(0, _verticalExtent);
            int maxZ = Math.Max(0, _verticalExtent);
            Vector3 right = rotation * Vector3.right;
            Vector3 forward = rotation * Vector3.forward;

            if (!AddGridPoint(anchor, snapTerrain, validateSlope, output, out failure))
                return false;

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (x == 0 && z == 0) continue;
                    Vector3 candidate = anchor + right * (x * spacing) + forward * (z * spacing);
                    if (!AddGridPoint(candidate, snapTerrain, validateSlope, output, out failure))
                        return false;
                    if (output.Count > MaxPlannedCells)
                    {
                        failure = $"mehr als {MaxPlannedCells} Slots";
                        return false;
                    }
                }
            }

            return output.Count == CellCount;
        }

        private static bool AddGridPoint(Vector3 candidate, bool snapTerrain, bool validateSlope, List<Vector3> output, out string failure)
        {
            failure = string.Empty;
            if (!snapTerrain)
            {
                output.Add(candidate);
                return true;
            }

            if (!TrySnapToTerrain(candidate, out Vector3 ground, out Vector3 normal))
            {
                failure = $"Terrain unter Slot {output.Count + 1} nicht gefunden";
                return false;
            }
            if (validateSlope && normal.y < 0.75f)
            {
                failure = $"Slot {output.Count + 1} liegt auf zu steilem Boden";
                return false;
            }

            output.Add(ground);
            return true;
        }

        private void UpdateEditSelection(Player player)
        {
            if (!_active || player == null)
            {
                ClearEditSelection();
                return;
            }

            if (Time.unscaledTime < _nextEditSelectionProbeAt) return;
            _nextEditSelectionProbeAt = Time.unscaledTime + 0.05f;

            GardenPersistentPlanSelection previous = _editSelection;
            bool shiftHeld = ShiftHeld();
            bool shiftChanged = shiftHeld != _lastDeleteShiftHeld;
            _lastDeleteShiftHeld = shiftHeld;
            if (!TryFindAimedPersistentGhost(out _editSelection))
                _editSelection = default;

            RebuildDeleteHighlightKeys();
            if (shiftChanged || previous.SlotId != _editSelection.SlotId || previous.FieldId != _editSelection.FieldId ||
                !string.Equals(previous.VisualKey, _editSelection.VisualKey, StringComparison.Ordinal))
                _nextPersistentGhostRefreshAt = 0f;
        }

        private bool TryFindAimedPersistentGhost(out GardenPersistentPlanSelection selection)
        {
            selection = default;
            Camera camera = Camera.main;
            if (camera == null || _persistentSlots.Count == 0) return false;

            Vector3 origin = camera.transform.position;
            Vector3 forward = camera.transform.forward.normalized;
            float bestScore = float.MaxValue;
            bool found = false;

            for (int i = 0; i < _persistentSlots.Count; i++)
            {
                GardenPersistentVisualSlot slot = _persistentSlots[i];
                Vector3 aimPoint = slot.Position + Vector3.up * 0.45f;
                Vector3 to = aimPoint - origin;
                float depth = Vector3.Dot(to, forward);
                if (depth < 0.35f || depth > 24f) continue;

                Vector3 closest = origin + forward * depth;
                float lateral = Vector3.Distance(aimPoint, closest);
                float allowed = Mathf.Clamp(0.32f + depth * 0.018f, 0.38f, 0.78f);
                if (lateral > allowed) continue;

                float score = lateral * lateral + depth * 0.0008f;
                if (score >= bestScore) continue;
                bestScore = score;
                selection = new GardenPersistentPlanSelection(slot.VisualKey, slot.SlotId, slot.FieldId, slot.HarvestPrefab,
                    slot.Position, slot.RotationY, true, depth);
                found = true;
            }

            return found;
        }

        private void RebuildDeleteHighlightKeys()
        {
            _deleteHighlightKeys.Clear();
            if (!_editSelection.IsValid) return;

            _deleteHighlightKeys.Add(_editSelection.VisualKey);
            if (!ShiftHeld()) return;

            if (_editSelection.FieldId > 0)
            {
                for (int i = 0; i < _persistentSlots.Count; i++)
                    if (_persistentSlots[i].FieldId == _editSelection.FieldId)
                        _deleteHighlightKeys.Add(_persistentSlots[i].VisualKey);
                return;
            }

            // Legacy fields (planned before stored field IDs existed): mirror the existing
            // connected-cluster delete fallback for the currently visible/open ghost slots.
            List<int> connected = new List<int>();
            int seedIndex = -1;
            for (int i = 0; i < _persistentSlots.Count; i++)
            {
                if (string.Equals(_persistentSlots[i].VisualKey, _editSelection.VisualKey, StringComparison.Ordinal))
                {
                    seedIndex = i;
                    break;
                }
            }
            if (seedIndex < 0) return;

            GardenPersistentVisualSlot seed = _persistentSlots[seedIndex];
            float nearest = float.MaxValue;
            for (int i = 0; i < _persistentSlots.Count; i++)
            {
                if (i == seedIndex || !string.Equals(_persistentSlots[i].HarvestPrefab, seed.HarvestPrefab, StringComparison.OrdinalIgnoreCase)) continue;
                Vector3 delta = _persistentSlots[i].Position - seed.Position;
                delta.y = 0f;
                float distance = delta.magnitude;
                if (distance > 0.15f && distance < nearest) nearest = distance;
            }
            if (nearest == float.MaxValue) return;

            float connectSqr = Mathf.Pow(Mathf.Clamp(nearest * 1.45f, 0.85f, 2.6f), 2f);
            bool[] visited = new bool[_persistentSlots.Count];
            Queue<int> queue = new Queue<int>();
            visited[seedIndex] = true;
            queue.Enqueue(seedIndex);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                connected.Add(current);
                Vector3 currentPosition = _persistentSlots[current].Position;
                for (int i = 0; i < _persistentSlots.Count; i++)
                {
                    if (visited[i] || !string.Equals(_persistentSlots[i].HarvestPrefab, seed.HarvestPrefab, StringComparison.OrdinalIgnoreCase)) continue;
                    Vector3 delta = _persistentSlots[i].Position - currentPosition;
                    delta.y = 0f;
                    if (delta.sqrMagnitude > connectSqr) continue;
                    visited[i] = true;
                    queue.Enqueue(i);
                }
            }
            for (int i = 0; i < connected.Count; i++)
                _deleteHighlightKeys.Add(_persistentSlots[connected[i]].VisualKey);
        }

        private void HandleDeleteInput(Player player)
        {
            if (!_active || player == null || !_editSelection.IsValid || !Input.GetMouseButtonDown(2)) return;

            bool wholeField = ShiftHeld();
            Vector3 target = _editSelection.Position;
            bool forestry = _editSelection.VisualKey.StartsWith("forestry:", StringComparison.Ordinal);
            int removedCount;
            int targetSlotId;
            int fieldId;
            bool removed = forestry
                ? ForestryPlanRuntime.TryRemovePlannedAt(player.GetPlayerID(), target, 0.45f, wholeField, out removedCount, out targetSlotId, out fieldId)
                : GardenReplantPlanRuntime.TryRemovePlannedAt(player.GetPlayerID(), target, 0.30f, wholeField, out removedCount, out targetSlotId, out fieldId);

            _nextPersistentGhostRefreshAt = 0f;
            _nextEditSelectionProbeAt = 0f;
            _editSelection = default;
            _deleteHighlightKeys.Clear();

            if (removed)
            {
                _status?.Invoke(wholeField
                    ? $"Pflanzfeld geloescht: {removedCount} Plan-Slots. Bestehende Pflanzen bleiben stehen."
                    : $"Pflanzplan Slot #{targetSlotId} geloescht.");
                _debugLog?.Invoke($"Garden plan removed by middle click: wholeField={wholeField}, removed={removedCount}, targetSlot={targetSlotId}, fieldId={fieldId}, at={target}");
            }
            else
            {
                string status = forestry ? ForestryPlanRuntime.LastStatus : GardenReplantPlanRuntime.LastStatus;
                _status?.Invoke($"Pflanzplan konnte nicht geloescht werden. ({status})");
            }
        }

        private void ClearEditSelection()
        {
            if (!_editSelection.IsValid && _deleteHighlightKeys.Count == 0) return;
            _editSelection = default;
            _deleteHighlightKeys.Clear();
            _nextPersistentGhostRefreshAt = 0f;
        }

        private void UpdatePersistentPlanGhosts(Player player)
        {
            if (player == null || Time.unscaledTime < _nextPersistentGhostRefreshAt) return;
            _nextPersistentGhostRefreshAt = Time.unscaledTime + (_active ? PersistentGhostRefreshActiveInterval : PersistentGhostRefreshIdleInterval);
            long perfGhosts = WorkersPerformanceProfiler.Begin();

            long owner = player.GetPlayerID();
            GardenReplantPlanRuntime.CollectLoadedPersistentActiveSlots(owner, _persistentSlots, MaxVisiblePersistentGhosts);
            ForestryPlanRuntime.AppendLoadedPersistentActiveSlots(owner, _persistentSlots, MaxVisiblePersistentGhosts);
            _persistentSeen.Clear();
            int resolveFailures = 0;

            for (int i = 0; i < _persistentSlots.Count; i++)
            {
                GardenPersistentVisualSlot slot = _persistentSlots[i];
                if (string.IsNullOrEmpty(slot.VisualKey) || string.IsNullOrEmpty(slot.HarvestPrefab)) continue;
                if (!TryResolvePersistentVisualPlant(slot.HarvestPrefab, out string plantPrefab, out GameObject sourcePrefab))
                {
                    // Never let a successfully stored plan become completely invisible merely
                    // because a modded flora prefab has not been resolved yet. SmartReplant flora
                    // can register after Workers, so preserve an explicit fallback marker and
                    // upgrade it to the real plant visual automatically on a later refresh.
                    string raw = GardenWorkRuntime.NormalizePrefabName(slot.HarvestPrefab);
                    if (!raw.StartsWith("SmartReplant_Flora_", StringComparison.OrdinalIgnoreCase))
                    {
                        resolveFailures++;
                        continue;
                    }
                    plantPrefab = raw;
                    sourcePrefab = null;
                }

                _persistentSeen.Add(slot.VisualKey);
                if (!_persistentGhosts.TryGetValue(slot.VisualKey, out PersistentGhostVisual visual) ||
                    visual == null || visual.Root == null ||
                    !string.Equals(visual.PlantPrefab, plantPrefab, StringComparison.OrdinalIgnoreCase))
                {
                    if (visual != null && visual.Root != null) UnityEngine.Object.Destroy(visual.Root);
                    GameObject root = sourcePrefab != null
                        ? CreatePersistentGhostVisual(sourcePrefab)
                        : CreateFallbackPersistentMarker(plantPrefab);
                    if (root == null)
                    {
                        resolveFailures++;
                        continue;
                    }
                    root.name = $"Valheim_Workers Planned Ghost #{slot.SlotId} ({plantPrefab})";
                    visual = new PersistentGhostVisual(root, plantPrefab, slot.FieldId);
                    _persistentGhosts[slot.VisualKey] = visual;
                }

                visual.FieldId = slot.FieldId;
                bool selected = _active && _deleteHighlightKeys.Contains(slot.VisualKey);
                SetDeleteHighlight(visual, selected);
                Transform transform = visual.Root.transform;
                Vector3 displayPosition = slot.Position + (selected ? Vector3.up * 0.08f : Vector3.zero);
                transform.SetPositionAndRotation(displayPosition, Quaternion.Euler(0f, slot.RotationY, 0f));
                transform.localScale = visual.BaseScale * (selected ? 1.18f : 1f);
                if (!visual.Root.activeSelf) visual.Root.SetActive(true);
            }

            _persistentRemove.Clear();
            foreach (KeyValuePair<string, PersistentGhostVisual> pair in _persistentGhosts)
                if (!_persistentSeen.Contains(pair.Key)) _persistentRemove.Add(pair.Key);

            for (int i = 0; i < _persistentRemove.Count; i++)
            {
                string key = _persistentRemove[i];
                if (_persistentGhosts.TryGetValue(key, out PersistentGhostVisual visual) && visual != null && visual.Root != null)
                    UnityEngine.Object.Destroy(visual.Root);
                _persistentGhosts.Remove(key);
            }
            _persistentRemove.Clear();

            int visible = 0;
            foreach (KeyValuePair<string, PersistentGhostVisual> pair in _persistentGhosts)
                if (pair.Value != null && pair.Value.Root != null && pair.Value.Root.activeInHierarchy) visible++;

            if (_lastPersistentSlotCount != _persistentSlots.Count || _lastPersistentGhostCount != visible ||
                _lastPersistentResolveFailures != resolveFailures)
            {
                _debugLog?.Invoke($"Garden persistent ghosts: slots={_persistentSlots.Count}, visible={visible}, resolveFailures={resolveFailures}");
            }
            _lastPersistentSlotCount = _persistentSlots.Count;
            _lastPersistentGhostCount = visible;
            _lastPersistentResolveFailures = resolveFailures;
            WorkersPerformanceProfiler.End(WorkersPerfBucket.GardenGhosts, perfGhosts);
        }

        private static bool TryResolvePersistentVisualPlant(string harvestPrefab, out string plantPrefab, out GameObject sourcePrefab)
        {
            plantPrefab = string.Empty;
            sourcePrefab = null;
            if (string.IsNullOrEmpty(harvestPrefab)) return false;

            // v0.5.326: resolve Workers WildGrowth ghosts directly from their canonical definition.
            // Do not make persistent plan visibility depend on GardenWorkRuntime's recipe cache.
            // The VW_Flora_* piece is registered independently and is the durable slot identity.
            if (WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(harvestPrefab, out WorkersWildFloraRuntime.Definition wildDef))
            {
                plantPrefab = wildDef.PrefabName;
                // v0.5.373: render the original vanilla mushroom/bush/thistle model as the saved
                // blueprint. The persistent VW_Flora_* clone is spawned only when the worker plants.
                if (WorkersWildFloraRuntime.TryResolvePlanningVisual(harvestPrefab, out sourcePrefab) && sourcePrefab != null)
                    return true;
                sourcePrefab = GardenWorkRuntime.ResolvePlantPrefab(plantPrefab);
                if (sourcePrefab == null && ZNetScene.instance != null)
                    sourcePrefab = ZNetScene.instance.GetPrefab(plantPrefab);
                return sourcePrefab != null;
            }

            if (GardenWorkRuntime.TryGetFallbackReplantPlan(harvestPrefab, out _, out string resolvedPlant, out _, out _))
            {
                plantPrefab = resolvedPlant;
                sourcePrefab = GardenWorkRuntime.ResolvePlantPrefab(plantPrefab);
                return sourcePrefab != null;
            }

            // SmartReplant persistent flora use the same prefab for the planned/harvested object.
            GameObject direct = GardenWorkRuntime.ResolvePlantPrefab(harvestPrefab);
            if (direct != null && SmartReplantBridge.IsSmartReplantPlantingPiece(direct))
            {
                plantPrefab = harvestPrefab;
                sourcePrefab = direct;
                return true;
            }

            // Forestry slots store the actual sapling prefab as their visual identity.
            if (ForestryWorkRuntime.IsSaplingPrefab(harvestPrefab))
            {
                plantPrefab = harvestPrefab;
                sourcePrefab = ForestryWorkRuntime.ResolveSaplingPrefab(harvestPrefab);
                return sourcePrefab != null;
            }
            return false;
        }

        /// <summary>
        /// Creates a renderer-only persistent planting ghost. Valheim Plant prefabs keep their
        /// healthy seedling branch inactive until Plant.SUpdate runs. A visual clone therefore
        /// cannot rely on activeSelf. Prefer Plant.m_healthy explicitly (same model selection
        /// principle used by SmartReplant's native preview repair), preserve its transform tree,
        /// and copy only render geometry/materials. No Piece, Plant, ZNetView, collider, audio,
        /// particle or other gameplay component is instantiated.
        /// </summary>
        private GameObject CreatePersistentGhostVisual(GameObject sourcePrefab)
        {
            if (sourcePrefab == null) return null;

            Transform preferredRoot = ResolvePreferredPlantVisualRoot(sourcePrefab);
            Transform sourceRoot = sourcePrefab.transform;
            if (preferredRoot == null) preferredRoot = sourceRoot;

            GameObject root = new GameObject("Workers persistent planting ghost");
            int ghostLayer = LayerMask.NameToLayer("ghost");
            if (ghostLayer >= 0) root.layer = ghostLayer;
            root.transform.localScale = sourceRoot.localScale;

            Dictionary<Transform, Transform> mapping = new Dictionary<Transform, Transform>();
            mapping[sourceRoot] = root.transform;
            CopyPersistentTransformTree(sourceRoot, root.transform, sourceRoot, preferredRoot, mapping);

            HashSet<Renderer> excludedLods = BuildExcludedLodSet(preferredRoot);
            int copied = CopyPersistentRenderers(preferredRoot, mapping, excludedLods);
            if (copied <= 0)
            {
                // Modded flora may not expose Plant.m_healthy. Fall back to any usable render
                // geometry under the source prefab while still avoiding gameplay components.
                copied = CopyPersistentRenderers(sourceRoot, mapping, BuildExcludedLodSet(sourceRoot));
            }

            if (copied <= 0)
            {
                UnityEngine.Object.Destroy(root);
                return CreateFallbackPersistentMarker(sourcePrefab.name);
            }

            SetVisualBranchActive(preferredRoot, sourceRoot, mapping);
            root.SetActive(true);
            return root;
        }

        private GameObject CreateFallbackPersistentMarker(string plantName)
        {
            // Last-resort visual feedback: never leave a successfully stored slot invisible.
            // This object has no collider/gameplay components and exists only client-side.
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            marker.name = $"Workers Planned Ghost Fallback ({plantName})";
            Collider collider = marker.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);
            int ghostLayer = LayerMask.NameToLayer("ghost");
            if (ghostLayer >= 0) marker.layer = ghostLayer;
            marker.transform.localScale = new Vector3(0.35f, 1.25f, 0.35f);
            Renderer renderer = marker.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                Material source = renderer.sharedMaterial;
                if (source != null)
                {
                    Material ghost = new Material(source);
                    ghost.name = "Workers Planned Slot Fallback";
                    if (ghost.HasProperty("_Color")) ghost.SetColor("_Color", new Color(0.25f, 0.90f, 1f, 0.85f));
                    if (ghost.HasProperty("_EmissionColor")) ghost.SetColor("_EmissionColor", new Color(0.08f, 0.35f, 0.45f, 1f));
                    renderer.sharedMaterial = ghost;
                }
            }
            marker.SetActive(true);
            _debugLog?.Invoke($"Planning ghost fallback marker created for {plantName} because no safe renderer geometry could be copied.");
            return marker;
        }

        private static Transform ResolvePreferredPlantVisualRoot(GameObject sourcePrefab)
        {
            if (sourcePrefab == null) return null;
            Plant plant = sourcePrefab.GetComponent<Plant>();
            if (plant == null) plant = sourcePrefab.GetComponentInChildren<Plant>(true);
            if (plant == null) return sourcePrefab.transform;

            try
            {
                GameObject healthy = PlantHealthyField?.GetValue(plant) as GameObject;
                if (healthy != null) return healthy.transform;
                GameObject healthyGrown = PlantHealthyGrownField?.GetValue(plant) as GameObject;
                if (healthyGrown != null) return healthyGrown.transform;
            }
            catch
            {
                // Reflection is cached and best-effort. Modded Plant subclasses can still use
                // the renderer fallback below without breaking planning.
            }
            return sourcePrefab.transform;
        }

        private static void CopyPersistentTransformTree(Transform source, Transform destination, Transform sourceRoot,
            Transform preferredRoot, Dictionary<Transform, Transform> mapping)
        {
            for (int i = 0; i < source.childCount; i++)
            {
                Transform childSource = source.GetChild(i);
                GameObject child = new GameObject(childSource.name + " planned ghost");
                Transform childTarget = child.transform;
                childTarget.SetParent(destination, false);
                childTarget.localPosition = childSource.localPosition;
                childTarget.localRotation = childSource.localRotation;
                childTarget.localScale = childSource.localScale;
                int ghostLayer = LayerMask.NameToLayer("ghost");
                if (ghostLayer >= 0) child.layer = ghostLayer;

                // Keep only the preferred plant branch/path active. Other source branches may be
                // unhealthy/grown/marker variants and must not overlap the planned seedling model.
                bool active = IsOnPreferredVisualPath(childSource, sourceRoot, preferredRoot);
                child.SetActive(active);
                mapping[childSource] = childTarget;
                CopyPersistentTransformTree(childSource, childTarget, sourceRoot, preferredRoot, mapping);
            }
        }

        private static bool IsOnPreferredVisualPath(Transform candidate, Transform sourceRoot, Transform preferredRoot)
        {
            if (candidate == null || preferredRoot == null) return false;
            if (candidate == preferredRoot || candidate.IsChildOf(preferredRoot)) return true;
            return preferredRoot.IsChildOf(candidate) && candidate != sourceRoot;
        }

        private static HashSet<Renderer> BuildExcludedLodSet(Transform root)
        {
            HashSet<Renderer> excluded = new HashSet<Renderer>();
            if (root == null) return excluded;
            LODGroup[] groups = root.GetComponentsInChildren<LODGroup>(true);
            for (int g = 0; g < groups.Length; g++)
            {
                LOD[] levels = groups[g].GetLODs();
                for (int l = 1; l < levels.Length; l++)
                    for (int r = 0; r < levels[l].renderers.Length; r++)
                        if (levels[l].renderers[r] != null) excluded.Add(levels[l].renderers[r]);
                if (levels.Length > 0)
                    for (int r = 0; r < levels[0].renderers.Length; r++)
                        if (levels[0].renderers[r] != null) excluded.Remove(levels[0].renderers[r]);
            }
            return excluded;
        }

        private int CopyPersistentRenderers(Transform visualRoot, Dictionary<Transform, Transform> mapping,
            HashSet<Renderer> excludedLods)
        {
            if (visualRoot == null) return 0;
            int copied = 0;
            Renderer[] renderers = visualRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer source = renderers[i];
                if (source == null || excludedLods.Contains(source) || !mapping.TryGetValue(source.transform, out Transform target)) continue;

                if (source is MeshRenderer meshRenderer)
                {
                    MeshFilter sourceFilter = source.GetComponent<MeshFilter>();
                    if (sourceFilter == null || sourceFilter.sharedMesh == null) continue;
                    MeshFilter filter = target.gameObject.GetComponent<MeshFilter>() ?? target.gameObject.AddComponent<MeshFilter>();
                    filter.sharedMesh = sourceFilter.sharedMesh;
                    MeshRenderer renderer = target.gameObject.AddComponent<MeshRenderer>();
                    ConfigurePersistentRenderer(source, renderer);
                    copied++;
                }
                else if (source is SkinnedMeshRenderer sourceSkin && sourceSkin.sharedMesh != null)
                {
                    SkinnedMeshRenderer skin = target.gameObject.AddComponent<SkinnedMeshRenderer>();
                    skin.sharedMesh = sourceSkin.sharedMesh;
                    skin.localBounds = sourceSkin.localBounds;
                    skin.updateWhenOffscreen = true;
                    Transform[] sourceBones = sourceSkin.bones;
                    Transform[] bones = new Transform[sourceBones.Length];
                    for (int b = 0; b < sourceBones.Length; b++)
                        if (sourceBones[b] != null && mapping.TryGetValue(sourceBones[b], out Transform mappedBone)) bones[b] = mappedBone;
                    skin.bones = bones;
                    if (sourceSkin.rootBone != null && mapping.TryGetValue(sourceSkin.rootBone, out Transform mappedRoot)) skin.rootBone = mappedRoot;
                    for (int b = 0; b < sourceSkin.sharedMesh.blendShapeCount; b++)
                        skin.SetBlendShapeWeight(b, sourceSkin.GetBlendShapeWeight(b));
                    ConfigurePersistentRenderer(source, skin);
                    copied++;
                }
            }
            return copied;
        }

        private void ConfigurePersistentRenderer(Renderer source, Renderer target)
        {
            target.sharedMaterials = BuildPersistentGhostMaterials(source.sharedMaterials);
            target.enabled = true;
            target.forceRenderingOff = false;
            target.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            target.receiveShadows = false;
        }

        private Material[] BuildPersistentGhostMaterials(Material[] materials)
        {
            if (materials == null || materials.Length == 0) return materials;
            Material[] result = new Material[materials.Length];
            for (int i = 0; i < materials.Length; i++) result[i] = GetPersistentGhostMaterial(materials[i]);
            return result;
        }

        private Material GetPersistentGhostMaterial(Material source)
        {
            if (source == null) return null;
            if (_persistentGhostMaterials.TryGetValue(source, out Material cached) && cached != null) return cached;

            // Match Valheim's placement-ghost material cleanup rather than forcing generic alpha
            // blending onto custom Valheim shaders. The latter can make foliage disappear entirely.
            Material ghost = new Material(source);
            ghost.name = source.name + " (Workers Planned Ghost)";
            if (ghost.HasProperty("_ValueNoise")) ghost.SetFloat("_ValueNoise", 0f);
            if (ghost.HasProperty("_TriplanarLocalPos")) ghost.SetFloat("_TriplanarLocalPos", 1f);
            if (ghost.HasProperty("_Color"))
            {
                Color sourceColor = source.GetColor("_Color");
                Color tint = Color.Lerp(sourceColor, new Color(0.32f, 0.90f, 1f, sourceColor.a), 0.50f);
                // Preserve the shader's native alpha. Do not force a transparent render queue;
                // many Valheim foliage shaders are cutout/opaque and vanish if coerced to blend.
                tint.a = sourceColor.a;
                ghost.SetColor("_Color", tint);
            }
            if (ghost.HasProperty("_EmissionColor"))
            {
                Color emission = source.GetColor("_EmissionColor");
                ghost.SetColor("_EmissionColor", Color.Lerp(emission, new Color(0.05f, 0.22f, 0.28f, 1f), 0.65f));
            }

            _persistentGhostMaterials[source] = ghost;
            return ghost;
        }

        private void SetDeleteHighlight(PersistentGhostVisual visual, bool highlighted)
        {
            if (visual == null || visual.Root == null || visual.DeleteHighlighted == highlighted) return;

            if (highlighted && visual.DeleteMaterials == null)
            {
                visual.DeleteMaterials = new Material[visual.Renderers.Length][];
                for (int i = 0; i < visual.Renderers.Length; i++)
                    visual.DeleteMaterials[i] = BuildDeleteGhostMaterials(visual.BaseMaterials[i]);
            }

            for (int i = 0; i < visual.Renderers.Length; i++)
            {
                Renderer renderer = visual.Renderers[i];
                if (renderer == null) continue;
                renderer.sharedMaterials = highlighted ? visual.DeleteMaterials[i] : visual.BaseMaterials[i];
            }
            visual.DeleteHighlighted = highlighted;
        }

        private Material[] BuildDeleteGhostMaterials(Material[] materials)
        {
            if (materials == null || materials.Length == 0) return materials;
            Material[] result = new Material[materials.Length];
            for (int i = 0; i < materials.Length; i++) result[i] = GetDeleteGhostMaterial(materials[i]);
            return result;
        }

        private Material GetDeleteGhostMaterial(Material source)
        {
            if (source == null) return null;
            if (_deleteGhostMaterials.TryGetValue(source, out Material cached) && cached != null) return cached;

            Material red = new Material(source);
            red.name = source.name + " (Workers Delete Highlight)";
            if (red.HasProperty("_Color"))
            {
                Color c = red.GetColor("_Color");
                Color target = new Color(1f, 0.06f, 0.04f, c.a);
                red.SetColor("_Color", Color.Lerp(c, target, 0.88f));
            }
            if (red.HasProperty("_EmissionColor"))
            {
                Color e = red.GetColor("_EmissionColor");
                red.SetColor("_EmissionColor", Color.Lerp(e, new Color(0.65f, 0.015f, 0.01f, 1f), 0.90f));
            }

            _deleteGhostMaterials[source] = red;
            return red;
        }

        private static void SetVisualBranchActive(Transform preferredRoot, Transform sourceRoot,
            Dictionary<Transform, Transform> mapping)
        {
            if (preferredRoot == null || sourceRoot == null) return;
            Transform cursor = preferredRoot;
            while (cursor != null)
            {
                if (mapping.TryGetValue(cursor, out Transform mapped)) mapped.gameObject.SetActive(true);
                if (cursor == sourceRoot) break;
                cursor = cursor.parent;
            }

            Transform[] descendants = preferredRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < descendants.Length; i++)
                if (mapping.TryGetValue(descendants[i], out Transform mapped)) mapped.gameObject.SetActive(true);
        }

        private void DestroyPersistentGhosts()
        {
            foreach (KeyValuePair<string, PersistentGhostVisual> pair in _persistentGhosts)
                if (pair.Value != null && pair.Value.Root != null) UnityEngine.Object.Destroy(pair.Value.Root);
            _persistentGhosts.Clear();
            _persistentSlots.Clear();
            _persistentSeen.Clear();
            _persistentRemove.Clear();
            _nextPersistentGhostRefreshAt = 0f;
            _lastPersistentSlotCount = 0;
            _lastPersistentGhostCount = 0;
            _lastPersistentResolveFailures = 0;
        }

        private void DestroyPersistentGhostMaterials()
        {
            foreach (KeyValuePair<Material, Material> pair in _persistentGhostMaterials)
                if (pair.Value != null) UnityEngine.Object.Destroy(pair.Value);
            _persistentGhostMaterials.Clear();
            foreach (KeyValuePair<Material, Material> pair in _deleteGhostMaterials)
                if (pair.Value != null) UnityEngine.Object.Destroy(pair.Value);
            _deleteGhostMaterials.Clear();
        }

        private void EnsurePreviewPool(Transform sourceGhost, int required)
        {
            if (sourceGhost == null)
            {
                HidePreview();
                return;
            }

            int sourceId = sourceGhost.gameObject.GetInstanceID();
            if (_previewSourceId != sourceId)
            {
                DestroyPreviewPool();
                _previewSourceId = sourceId;
            }

            while (_previewGhosts.Count < required)
            {
                GameObject visual = CreatePersistentGhostVisual(sourceGhost.gameObject);
                if (visual == null)
                    visual = CreateFallbackMarker();
                visual.name = "Valheim_Workers PlantingSlot Preview";
                _previewGhosts.Add(visual);
            }
        }

        private static GameObject CreateRendererOnlyVisual(Transform source)
        {
            if (source == null) return null;
            GameObject root = new GameObject("Workers planting visual");
            int copied = CopyRendererNode(source, root.transform, true);
            if (copied <= 0)
            {
                UnityEngine.Object.Destroy(root);
                return null;
            }
            root.transform.localScale = source.localScale;
            return root;
        }

        private static int CopyRendererNode(Transform source, Transform destination, bool root)
        {
            int copied = 0;
            Transform target = destination;
            if (!root)
            {
                GameObject child = new GameObject(source.name + " visual");
                target = child.transform;
                target.SetParent(destination, false);
                target.localPosition = source.localPosition;
                target.localRotation = source.localRotation;
                target.localScale = source.localScale;
                // SmartReplant's registered target prefabs live under inactive prefab roots in
                // ZNetScene/ObjectDB. activeInHierarchy is therefore false even though their
                // renderers are the exact visuals used by SmartReplant's own custom flora ghost.
                // Preview clones must be visible independently of that prefab-registration state.
                child.SetActive(true);
            }

            int ghostLayer = LayerMask.NameToLayer("ghost");
            if (ghostLayer >= 0) target.gameObject.layer = ghostLayer;

            MeshFilter sourceFilter = source.GetComponent<MeshFilter>();
            MeshRenderer sourceRenderer = source.GetComponent<MeshRenderer>();
            if (sourceFilter != null && sourceFilter.sharedMesh != null && sourceRenderer != null &&
                sourceRenderer.enabled)
            {
                MeshFilter filter = target.gameObject.AddComponent<MeshFilter>();
                filter.sharedMesh = sourceFilter.sharedMesh;
                MeshRenderer renderer = target.gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = sourceRenderer.sharedMaterials;
                renderer.enabled = true;
                copied++;
            }

            for (int i = 0; i < source.childCount; i++)
                copied += CopyRendererNode(source.GetChild(i), target, false);
            return copied;
        }

        private static GameObject CreateFallbackMarker()
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Collider collider = marker.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);
            marker.transform.localScale = new Vector3(0.22f, 0.02f, 0.22f);
            int ghostLayer = LayerMask.NameToLayer("ghost");
            if (ghostLayer >= 0) marker.layer = ghostLayer;
            return marker;
        }

        private void HidePreview()
        {
            for (int i = 0; i < _previewGhosts.Count; i++)
                if (_previewGhosts[i] != null && _previewGhosts[i].activeSelf)
                    _previewGhosts[i].SetActive(false);
        }

        private void DestroyPreviewPool()
        {
            for (int i = 0; i < _previewGhosts.Count; i++)
                if (_previewGhosts[i] != null)
                    UnityEngine.Object.Destroy(_previewGhosts[i]);
            _previewGhosts.Clear();
            _previewSourceId = 0;
        }

        private static bool TryGetUnifiedPlanningData(Piece piece, out bool forestry, out string identityPrefab, out string plantPrefab,
            out string resourcePrefab, out int resourceAmount, out float spacing)
        {
            forestry = false; identityPrefab = string.Empty; plantPrefab = string.Empty; resourcePrefab = string.Empty; resourceAmount = 0; spacing = 0f;
            if (piece == null) return false;
            if (GardenWorkRuntime.TryGetPlanningData(piece, out identityPrefab, out plantPrefab, out resourcePrefab, out resourceAmount, out spacing))
                return true;
            if (ForestryWorkRuntime.TryGetPlanningData(piece, out resourcePrefab, out plantPrefab, out spacing))
            {
                forestry = true;
                identityPrefab = plantPrefab;
                resourceAmount = 1;
                return true;
            }
            return false;
        }

        private static bool TryGetSelectedPlanningPiece(Player player, out Piece piece)
        {
            Piece selected = player != null ? player.GetSelectedPiece() : null;
            return TryResolveEffectivePlanningPiece(player, selected, out piece, out _);
        }

        /// <summary>
        /// Resolves the piece the player is ACTUALLY previewing. SmartReplant flora selection is
        /// maintained outside Valheim's normal PieceTable selection, so Player.GetSelectedPiece()
        /// and even TryPlacePiece's Piece argument can remain on the previously selected flora.
        /// The live placement ghost therefore wins whenever it resolves to a supported plant.
        /// </summary>
        internal static bool TryResolveEffectivePlanningPiece(Player player, Piece suppliedPiece, out Piece piece, out string source)
        {
            piece = null;
            source = "none";
            if (player == null) return false;

            // v0.5.375: VW_Flora_* is selected from our own Cultivator entries and may have no
            // native Valheim placement ghost (especially Mushroom/Thistle/Dandelion). In that
            // case m_placementGhost can still point at the previously selected crop. Prefer the
            // explicit/current Workers WildGrowth selection before consulting any ghost.
            if (suppliedPiece != null && suppliedPiece.gameObject != null &&
                WorkersWildFloraRuntime.TryGetDefinition(suppliedPiece.gameObject, out _) &&
                TryGetUnifiedPlanningData(suppliedPiece, out _, out _, out _, out _, out _, out _))
            {
                piece = suppliedPiece;
                source = "workers-wildflora-supplied";
                return true;
            }

            Piece currentSelected = player.GetSelectedPiece();
            if (currentSelected != null && currentSelected.gameObject != null &&
                WorkersWildFloraRuntime.TryGetDefinition(currentSelected.gameObject, out _) &&
                TryGetUnifiedPlanningData(currentSelected, out _, out _, out _, out _, out _, out _))
            {
                piece = currentSelected;
                source = "workers-wildflora-selected";
                return true;
            }

            if (TryGetPlacementGhostTransform(player, out Transform ghost) && ghost != null)
            {
                Piece ghostPiece = ghost.GetComponent<Piece>();
                if (ghostPiece == null) ghostPiece = ghost.GetComponentInParent<Piece>();
                if (ghostPiece == null) ghostPiece = ghost.GetComponentInChildren<Piece>(true);
                if (ghostPiece != null && TryGetUnifiedPlanningData(ghostPiece, out _, out _, out _, out _, out _, out _))
                {
                    piece = ghostPiece;
                    source = "placement-ghost-component";
                    return true;
                }

                string ghostPrefabName = GardenWorkRuntime.NormalizePrefabName(ghost.gameObject.name);
                if (TryResolvePlanningPieceByPrefabName(ghostPrefabName, out Piece resolvedGhostPiece))
                {
                    piece = resolvedGhostPiece;
                    source = "placement-ghost-prefab:" + ghostPrefabName;
                    return true;
                }

                // SmartReplant custom flora ghosts are visual-only objects named like
                // "SmartReplant_Flora_Thistle SR visual". They intentionally carry no Piece.
                // Resolve the canonical SmartReplant_Flora_* prefix before exact prefab lookup.
                string canonicalGhostFlora = CanonicalSmartReplantFloraName(ghostPrefabName);
                if (!string.IsNullOrEmpty(canonicalGhostFlora) &&
                    TryResolvePlanningPieceByPrefabName(canonicalGhostFlora, out Piece canonicalGhostPiece))
                {
                    piece = canonicalGhostPiece;
                    source = "placement-ghost-flora:" + canonicalGhostFlora;
                    return true;
                }

                // Some SmartReplant versions wrap the flora prefab below a generic placement
                // ghost root. Walk the transform names once and resolve the first actual
                // SmartReplant_Flora_* identity instead of falling back to the stale selected piece.
                Transform[] ghostChildren = ghost.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < ghostChildren.Length; i++)
                {
                    Transform child = ghostChildren[i];
                    if (child == null) continue;
                    string childName = GardenWorkRuntime.NormalizePrefabName(child.gameObject.name);
                    string canonicalChild = CanonicalSmartReplantFloraName(childName);
                    if (string.IsNullOrEmpty(canonicalChild)) continue;
                    if (!TryResolvePlanningPieceByPrefabName(canonicalChild, out Piece childPiece)) continue;
                    piece = childPiece;
                    source = "placement-ghost-child:" + canonicalChild;
                    return true;
                }
            }

            if (suppliedPiece != null && TryGetUnifiedPlanningData(suppliedPiece, out _, out _, out _, out _, out _, out _))
            {
                piece = suppliedPiece;
                source = "supplied-piece";
                return true;
            }

            Piece selected = player.GetSelectedPiece();
            if (selected != null && selected != suppliedPiece &&
                TryGetUnifiedPlanningData(selected, out _, out _, out _, out _, out _, out _))
            {
                piece = selected;
                source = "player-selected-piece";
                return true;
            }
            return false;
        }


        private static string CanonicalSmartReplantFloraName(string rawName)
        {
            string normalized = GardenWorkRuntime.NormalizePrefabName(rawName ?? string.Empty);
            const string prefix = "SmartReplant_Flora_";
            int start = normalized.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;
            string candidate = normalized.Substring(start);
            int cut = candidate.Length;
            char[] separators = { ' ', '(', '[', '{', '/', '\\' };
            for (int i = 0; i < separators.Length; i++)
            {
                int idx = candidate.IndexOf(separators[i]);
                if (idx > 0 && idx < cut) cut = idx;
            }
            if (cut < candidate.Length) candidate = candidate.Substring(0, cut);
            return candidate.Trim();
        }

        private static bool TryResolvePlanningPieceByPrefabName(string prefabName, out Piece piece)
        {
            piece = null;
            if (string.IsNullOrEmpty(prefabName)) return false;
            GameObject resolved = GardenWorkRuntime.ResolvePlantPrefab(prefabName);
            if (resolved == null) return false;
            piece = resolved.GetComponent<Piece>();
            if (piece == null) piece = resolved.GetComponentInChildren<Piece>(true);
            return piece != null && TryGetUnifiedPlanningData(piece, out _, out _, out _, out _, out _, out _);
        }

        private static string NormalizePieceNameForDebug(Piece piece)
        {
            return piece == null || piece.gameObject == null ? "none" : GardenWorkRuntime.NormalizePrefabName(piece.gameObject.name);
        }

        private static bool TryGetPlacementGhostTransform(Player player, out Transform transform)
        {
            transform = null;
            if (player == null) return false;

            // Vanilla/native cultivator path. SmartReplant persistent flora can deliberately run
            // with nativeGhostRequired=false, so m_placementGhost may be null even while a valid
            // custom flora preview is visible. Prefer the native ghost when present, then fall
            // back to SmartReplant's own FieldGhostController.
            if (PlacementGhostField != null)
            {
                try
                {
                    object value = PlacementGhostField.GetValue(player);
                    if (TryCoerceTransform(value, out transform)) return true;
                }
                catch
                {
                    // Continue into the SmartReplant custom-ghost fallback below.
                }
            }

            return SmartReplantPlanningGhostBridge.TryGetFloraGhost(out transform);
        }

        private static bool TryCoerceTransform(object value, out Transform transform)
        {
            transform = null;
            if (value is GameObject go)
            {
                transform = go.transform;
                return transform != null;
            }
            if (value is Transform directTransform)
            {
                transform = directTransform;
                return true;
            }
            if (value is Component component)
            {
                transform = component.transform;
                return transform != null;
            }
            return false;
        }

        /// <summary>
        /// Reflection-only bridge to SmartReplant's custom flora preview. Newer SmartReplant
        /// versions intentionally render persistent flora through FieldGhostController instead
        /// of Player.m_placementGhost (log: nativeGhostRequired=false). Cache both positive and
        /// negative discovery so planning does not add a per-frame assembly/reflection scan.
        /// </summary>
        private static class SmartReplantPlanningGhostBridge
        {
            private static bool _resolved;
            private static Type _controllerType;
            private static PropertyInfo _ghostProperty;
            private static FieldInfo _ghostField;
            private static PropertyInfo _instanceProperty;
            private static FieldInfo _instanceField;
            private static float _nextInstanceProbeAt;
            private static object _cachedInstance;

            internal static bool TryGetFloraGhost(out Transform transform)
            {
                transform = null;
                ResolveOnce();
                if (_controllerType == null) return false;

                object instance = ResolveInstance();
                try
                {
                    object value = null;
                    if (_ghostProperty != null)
                    {
                        MethodInfo getter = _ghostProperty.GetGetMethod(true);
                        value = getter != null && getter.IsStatic
                            ? _ghostProperty.GetValue(null, null)
                            : instance != null ? _ghostProperty.GetValue(instance, null) : null;
                    }
                    else if (_ghostField != null)
                    {
                        value = _ghostField.IsStatic ? _ghostField.GetValue(null) : instance != null ? _ghostField.GetValue(instance) : null;
                    }

                    return TryCoerceTransform(value, out transform) && transform != null && transform.gameObject.activeInHierarchy;
                }
                catch
                {
                    return false;
                }
            }

            private static void ResolveOnce()
            {
                if (_resolved) return;
                _resolved = true;

                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int a = 0; a < assemblies.Length && _controllerType == null; a++)
                {
                    Type[] types;
                    try { types = assemblies[a].GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                    catch { continue; }
                    if (types == null) continue;
                    for (int i = 0; i < types.Length; i++)
                    {
                        Type type = types[i];
                        if (type == null) continue;
                        string name = type.FullName ?? type.Name ?? string.Empty;
                        if (name.IndexOf("SmartReplant", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (!name.EndsWith("FieldGhostController", StringComparison.OrdinalIgnoreCase)) continue;
                        _controllerType = type;
                        break;
                    }
                }
                if (_controllerType == null) return;

                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
                string[] ghostNames = { "FloraGhost", "m_floraGhost", "floraGhost", "FloraAnchorGhost", "m_floraAnchorGhost", "ghost", "m_ghost" };
                for (int i = 0; i < ghostNames.Length && _ghostProperty == null && _ghostField == null; i++)
                {
                    _ghostProperty = _controllerType.GetProperty(ghostNames[i], flags);
                    if (_ghostProperty == null) _ghostField = _controllerType.GetField(ghostNames[i], flags);
                }

                string[] instanceNames = { "Instance", "instance", "m_instance" };
                for (int i = 0; i < instanceNames.Length && _instanceProperty == null && _instanceField == null; i++)
                {
                    _instanceProperty = _controllerType.GetProperty(instanceNames[i], flags);
                    if (_instanceProperty == null) _instanceField = _controllerType.GetField(instanceNames[i], flags);
                }
            }

            private static object ResolveInstance()
            {
                if (_controllerType == null) return null;
                if (_cachedInstance is UnityEngine.Object unityObject && unityObject == null) _cachedInstance = null;
                if (_cachedInstance != null) return _cachedInstance;
                if (Time.unscaledTime < _nextInstanceProbeAt) return null;
                _nextInstanceProbeAt = Time.unscaledTime + 1f;

                try
                {
                    if (_instanceProperty != null)
                    {
                        MethodInfo getter = _instanceProperty.GetGetMethod(true);
                        if (getter != null && getter.IsStatic) _cachedInstance = _instanceProperty.GetValue(null, null);
                    }
                    if (_cachedInstance == null && _instanceField != null && _instanceField.IsStatic)
                        _cachedInstance = _instanceField.GetValue(null);
                    if (_cachedInstance == null && typeof(UnityEngine.Object).IsAssignableFrom(_controllerType))
                    {
                        UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(_controllerType);
                        if (all != null && all.Length > 0) _cachedInstance = all[0];
                    }
                }
                catch
                {
                    _cachedInstance = null;
                }
                return _cachedInstance;
            }
        }

        private static bool TrySnapToTerrain(Vector3 xzPoint, out Vector3 ground, out Vector3 normal)
        {
            ground = default;
            normal = Vector3.up;

            // Valheim's authoritative terrain height is ZoneSystem.GetGroundHeight().  Unity 6 no
            // longer guarantees that the cultivated Heightmap surface is represented by a collider
            // that our old Heightmap/collider-name filter recognises.  That made perfectly valid
            // farm soil fail with "Terrain unter Slot 1 nicht gefunden" and therefore prevented
            // both the preview ghosts and the actual planning click.
            if (ZoneSystem.instance != null)
            {
                try
                {
                    float y = ZoneSystem.instance.GetGroundHeight(xzPoint);
                    if (!float.IsNaN(y) && !float.IsInfinity(y))
                    {
                        ground = new Vector3(xzPoint.x, y + 0.03f, xzPoint.z);

                        // Derive a cheap terrain normal from neighbouring ground heights so slope
                        // validation still works without depending on a specific Unity collider.
                        const float d = 0.35f;
                        Vector3 px = new Vector3(xzPoint.x + d, y, xzPoint.z);
                        Vector3 nx = new Vector3(xzPoint.x - d, y, xzPoint.z);
                        Vector3 pz = new Vector3(xzPoint.x, y, xzPoint.z + d);
                        Vector3 nz = new Vector3(xzPoint.x, y, xzPoint.z - d);
                        float ypx = ZoneSystem.instance.GetGroundHeight(px);
                        float ynx = ZoneSystem.instance.GetGroundHeight(nx);
                        float ypz = ZoneSystem.instance.GetGroundHeight(pz);
                        float ynz = ZoneSystem.instance.GetGroundHeight(nz);
                        Vector3 tangentX = new Vector3(2f * d, ypx - ynx, 0f);
                        Vector3 tangentZ = new Vector3(0f, ypz - ynz, 2f * d);
                        Vector3 n = Vector3.Cross(tangentZ, tangentX).normalized;
                        if (n.y < 0f) n = -n;
                        if (n.sqrMagnitude > 0.5f) normal = n;
                        return true;
                    }
                }
                catch
                {
                    // Fall through to collider probing for unusual worlds/modded terrain.
                }
            }

            Vector3 origin = xzPoint + Vector3.up * 30f;
            int hits = Physics.RaycastNonAlloc(origin, Vector3.down, GroundHits, 70f, ~0, QueryTriggerInteraction.Ignore);
            bool found = false;
            float bestY = float.MinValue;

            for (int i = 0; i < hits; i++)
            {
                RaycastHit hit = GroundHits[i];
                GroundHits[i] = default;
                if (hit.collider == null || !LooksLikeTerrain(hit.collider) || hit.point.y <= bestY) continue;
                bestY = hit.point.y;
                ground = hit.point + Vector3.up * 0.03f;
                normal = hit.normal;
                found = true;
            }

            return found;
        }

        private static bool LooksLikeTerrain(Collider collider)
        {
            if (collider == null) return false;
            if (TerrainLayer >= 0 && collider.gameObject.layer == TerrainLayer) return true;
            if (collider.GetComponentInParent<Heightmap>() != null) return true;
            string name = collider.gameObject.name ?? string.Empty;
            return name.IndexOf("terrain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("heightmap", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool RightControlHeld() => Input.GetKey(KeyCode.RightControl);
        private static bool ShiftHeld() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        private static string Direction(int extent, string negative, string positive) => extent == 0 ? "Mitte" : extent < 0 ? $"{Mathf.Abs(extent)} {negative}" : $"{extent} {positive}";

        private enum ExpansionDirection { Left, Right, Forward, Backward }

        private sealed class ArrowRepeatState
        {
            private bool _held;
            private float _first;
            private float _last;

            internal void Reset() => _held = false;

            internal bool Step(bool down, bool pressed, float now, float delay, float interval)
            {
                if (!pressed)
                {
                    Reset();
                    return false;
                }
                if (down || !_held)
                {
                    _held = true;
                    _first = _last = now;
                    return true;
                }
                if (now - _first < delay || now - _last < interval)
                    return false;
                _last = now;
                return true;
            }
        }
    }

    /// <summary>
    /// Current Valheim placement gate. A supported crop click in Workers planning mode is
    /// consumed here before Vanilla can instantiate the plant or spend its requirement.
    /// </summary>
    [HarmonyPatch(typeof(Player), "TryPlacePiece", new Type[] { typeof(Piece) })]
    [HarmonyPriority(Priority.First)]
    [HarmonyBefore("local.valheim.smartbuilding", "local.valheim.smartstorage")]
    internal static class GardenPlanningTryPlacePiecePatch
    {
        private static bool Prefix(Player __instance, Piece piece, ref bool __result)
        {
            try
            {
                GardenPlanningRuntime runtime = Plugin.Instance?.GardenPlanning;
                if (runtime == null || !runtime.TryInterceptPlacement(__instance, piece))
                    return true;

                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Garden planning TryPlacePiece interception failed safely: {ex.GetType().Name}: {ex.Message}");
                return true;
            }
        }
    }
    /// <summary>
    /// Planning is intentionally resource-free. Valheim asks HaveRequirements before
    /// TryPlacePiece, so without this narrow gate a missing seed prevents the planning
    /// click from ever reaching Workers. Only supported plant/sapling pieces while the
    /// local Workers planner is active are affected.
    /// </summary>
    [HarmonyPatch]
    [HarmonyPriority(Priority.First)]
    internal static class GardenPlanningRequirementGatePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(Player)))
            {
                if (!string.Equals(method.Name, "HaveRequirements", StringComparison.Ordinal) || method.ReturnType != typeof(bool))
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                bool hasPiece = false;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType == typeof(Piece))
                    {
                        hasPiece = true;
                        break;
                    }
                }

                if (hasPiece)
                    yield return method;
            }
        }

        private static bool Prefix(Player __instance, object[] __args, ref bool __result)
        {
            try
            {
                GardenPlanningRuntime runtime = Plugin.Instance?.GardenPlanning;
                if (runtime == null || !runtime.Active || __instance == null || __instance != Player.m_localPlayer)
                    return true;

                Piece piece = null;
                if (__args != null)
                {
                    for (int i = 0; i < __args.Length; i++)
                    {
                        if (__args[i] is Piece candidate)
                        {
                            piece = candidate;
                            break;
                        }
                    }
                }

                // IMPORTANT: requirement checks also run while Valheim evaluates recipe/piece
                // discovery. Never substitute the currently selected SmartReplant flora here.
                // Doing so makes every unrelated Piece look like the selected Thistle and can
                // unlock large parts of the build catalogue. Only bypass requirements for the
                // exact Piece being checked.
                if (!runtime.IsSupportedPlanningPiece(piece))
                    return true;

                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Garden planning requirement gate failed safely: {ex.GetType().Name}: {ex.Message}");
                return true;
            }
        }
    }

}
