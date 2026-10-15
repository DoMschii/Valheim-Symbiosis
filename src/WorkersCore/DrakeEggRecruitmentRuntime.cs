using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Dedicated Drake recruitment path. The player places one Dragon Egg into a buildable
    /// incubation nest. Test timing in v0.5.436 uses a 60-second staged hatch sequence so the
    /// visuals/sound/automatic hatch can be validated before restoring the final two-day duration.
    /// which becomes a Tier-2 Air Hauler and immediately follows the recruiting player home.
    /// The recruitment nest is not a HomeAnchor; the Drake still needs a separate landing pad.
    /// </summary>
    internal sealed class DrakeEggRecruitmentRuntime : MonoBehaviour
    {
        internal const string EggActiveKey = "vw_drake_egg_active";
        internal const string HatchAtKey = "vw_drake_egg_hatch_at";
        internal const string RecruiterKey = "vw_drake_egg_recruiter";
        internal const string HatchedCountKey = "vw_drake_egg_hatched_count";
        internal const string IncubationStartKey = "vw_drake_egg_start_at";
        internal const string IncubationTimingVersionKey = "vw_drake_egg_timing_version";
        internal const string EggPrefab = "DragonEgg";

        // v0.5.436 TEST TIMING: keep this at 60 seconds until wobble -> cracks -> auto-hatch
        // has been validated in-game. Afterwards this can go back to two Valheim days without
        // changing the staged runtime itself.
        internal const long RequiredIncubationSeconds = 60L;
        internal const long FirstMovementSecond = 20L;
        internal const long CrackSecond = 40L;
        private const int IncubationTimingVersion = 1;
        private const float FallbackDayLengthSeconds = 1800f;

        private static readonly MethodInfo GetTimeSecondsMethod = typeof(ZNet).GetMethod(
            "GetTimeSeconds", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);

        private ZNetView _view;
        private GameObject _eggVisual;
        private GameObject _crackRoot;
        private Material _crackMaterial;
        private Vector3 _eggBaseLocalPosition;
        private Quaternion _eggBaseLocalRotation;
        private bool _eggBaseTransformCached;
        private bool _cachedEggActive;
        private int _cachedIncubationStage;
        private long _cachedStartAt;
        private long _cachedHatchAt;
        private int _lastSoundStage = -1;
        private float _nextIncubationSoundAt;
        private float _nextVisualRefresh;
        private float _nextAutoHatchAttempt;
        private static AudioClip _cachedIncubationClip;
        private static bool _incubationClipResolved;

        private void Awake()
        {
            _view = GetComponent<ZNetView>() ?? GetComponentInParent<ZNetView>();
            CacheEggVisual();
        }

        private void Start()
        {
            RefreshEggVisual();
            if (!_cachedEggActive)
                enabled = false;
        }

        private void Update()
        {
            // Visual motion itself is local-only and does not touch the ZDO every frame.
            UpdateEggMotion();
            UpdateIncubationAudio();

            // Refresh persisted timing twice per second. No scene/container/physics scan is involved.
            if (Time.unscaledTime < _nextVisualRefresh) return;
            _nextVisualRefresh = Time.unscaledTime + 0.5f;
            RefreshEggVisual();
            TryAutoHatch();
        }

        private void OnDestroy()
        {
            if (_crackMaterial != null) Destroy(_crackMaterial);
        }

        internal bool TryInteract(Player player)
        {
            if (player == null) return false;
            ZDO zdo = GetZdo();
            if (zdo == null)
            {
                Notify(player, "Das Drachenbrutnest ist noch nicht netzwerkbereit.");
                return true;
            }
            if (_view == null || !_view.IsOwner())
            {
                Notify(player, "Das Drachenbrutnest wird gerade von einem anderen Netzwerkbesitzer verwaltet.");
                return true;
            }

            bool active = zdo.GetBool(EggActiveKey, false);
            long recruiter = zdo.GetLong(RecruiterKey, 0L);
            if (active && recruiter != 0L && recruiter != player.GetPlayerID())
            {
                Notify(player, "Dieses Drachenei wird bereits von einem anderen Spieler ausgebrütet.");
                return true;
            }

            if (!active)
            {
                Inventory inventory = player.GetInventory();
                if (!TryConsumePrefabItems(inventory, EggPrefab, 1))
                {
                    Notify(player, "Du brauchst 1 Drachenei für dieses Brutnest.");
                    return true;
                }

                long startAt = GetWorldSeconds();
                long hatchAt = startAt + RequiredIncubationSeconds;
                zdo.Set(EggActiveKey, true);
                zdo.Set(IncubationStartKey, startAt);
                zdo.Set(HatchAtKey, hatchAt);
                zdo.Set(IncubationTimingVersionKey, IncubationTimingVersion);
                zdo.Set(RecruiterKey, player.GetPlayerID());
                RefreshEggVisual();
                enabled = true;
                _nextVisualRefresh = 0f;
                Notify(player, "Du legst ein Drachenei ins Brutnest. Testbrut: nach 20 Sek. bewegt es sich, nach 40 Sek. entstehen Risse und nach 60 Sek. schlüpft der Drake automatisch.");
                Plugin.Log?.LogInfo($"Drake egg incubation started at {transform.position}; recruiter={player.GetPlayerID()}, hatchAt={hatchAt}.");
                return true;
            }

            EnsureCurrentIncubationTiming(zdo);
            long remaining = Math.Max(0L, zdo.GetLong(HatchAtKey, 0L) - GetWorldSeconds());
            if (remaining > 0L)
            {
                Notify(player, $"Das Drachenei ist noch nicht bereit. Noch ungefähr {FormatDuration(remaining)}.");
                return true;
            }

            if (!TryCompleteHatch(zdo, player, out string failure))
                Notify(player, "Der Drake konnte gerade nicht schlüpfen: " + failure);
            return true;
        }

        internal string BuildHover()
        {
            RefreshEggVisual();
            if (_cachedEggActive && !enabled)
            {
                enabled = true;
                _nextVisualRefresh = 0f;
            }

            ZDO zdo = GetZdo();
            if (zdo == null) return "Drachenbrutnest\nNetzwerkstatus nicht verfügbar";

            bool active = zdo.GetBool(EggActiveKey, false);
            int count = Math.Max(0, zdo.GetInt(HatchedCountKey, 0));
            string history = count > 0 ? $"\nBereits geschlüpft: {count}" : string.Empty;
            if (!active)
            {
                int eggs = CountPrefabItems(Player.m_localPlayer?.GetInventory(), EggPrefab);
                return "<color=#9CC7FF><b>Drachenbrutnest</b></color>" + history +
                       $"\nDracheneier im Inventar: <color=#D6D6FF>{eggs}</color>" +
                       "\n[<color=yellow><b>E</b></color>] 1 Drachenei einlegen";
            }

            EnsureCurrentIncubationTiming(zdo);
            long now = GetWorldSeconds();
            long startAt = zdo.GetLong(IncubationStartKey, now);
            long hatchAt = zdo.GetLong(HatchAtKey, now + RequiredIncubationSeconds);
            long elapsed = Math.Max(0L, now - startAt);
            long remaining = Math.Max(0L, hatchAt - now);
            float progress = Mathf.Clamp01(elapsed / (float)RequiredIncubationSeconds) * 100f;
            int stage = GetIncubationStage(elapsed);
            string stageText = stage >= 2
                ? "<color=#FFB36B>Risse ziehen sich über die Schale. Etwas will heraus.</color>"
                : stage >= 1
                    ? "<color=#EFC85B>Das Ei bewegt sich. Aus dem Inneren kommen Geräusche.</color>"
                    : "<color=#BFCBFF>Das Ei ruht noch still im Nest.</color>";

            if (remaining > 0L)
            {
                return "<color=#9CC7FF><b>Drachenbrutnest</b></color>" + history +
                       $"\nDrachenei: <color=#EFC85B>{progress:0}%</color>" +
                       $"\nNoch ungefähr {FormatDuration(remaining)}" +
                       "\n" + stageText;
            }

            return "<color=#9CC7FF><b>Drachenbrutnest</b></color>" + history +
                   "\nDrachenei: <color=#7CFF8C>schlüpft...</color>" +
                   "\n<color=#BFCBFF>Der automatische Schlupfversuch läuft.</color>" +
                   "\n[<color=yellow><b>E</b></color>] notfalls manuell erneut versuchen";
        }

        private void EnsureCurrentIncubationTiming(ZDO zdo)
        {
            if (zdo == null || !zdo.GetBool(EggActiveKey, false)) return;

            long now = GetWorldSeconds();
            int timingVersion = zdo.GetInt(IncubationTimingVersionKey, 0);
            long startAt = zdo.GetLong(IncubationStartKey, 0L);
            long hatchAt = zdo.GetLong(HatchAtKey, 0L);

            // Existing eggs from the old two-Valheim-day implementation are intentionally reset
            // to a fresh 60-second test sequence so the player can validate every stage now.
            bool oldTiming = timingVersion != IncubationTimingVersion || startAt <= 0L || hatchAt <= startAt;
            bool obviouslyOldDuration = hatchAt > now + RequiredIncubationSeconds + 10L;
            if (!oldTiming && !obviouslyOldDuration) return;

            startAt = now;
            hatchAt = now + RequiredIncubationSeconds;
            zdo.Set(IncubationStartKey, startAt);
            zdo.Set(HatchAtKey, hatchAt);
            zdo.Set(IncubationTimingVersionKey, IncubationTimingVersion);
            Plugin.Log?.LogInfo($"Drake egg timing migrated to 60-second test sequence at {transform.position}; hatchAt={hatchAt}.");
        }

        private static int GetIncubationStage(long elapsedSeconds)
        {
            if (elapsedSeconds >= RequiredIncubationSeconds) return 3;
            if (elapsedSeconds >= CrackSecond) return 2;
            if (elapsedSeconds >= FirstMovementSecond) return 1;
            return 0;
        }

        private bool TryCompleteHatch(ZDO zdo, Player recruiter, out string failure)
        {
            failure = string.Empty;
            if (zdo == null)
            {
                failure = "Brutnest-ZDO fehlt";
                return false;
            }
            if (recruiter == null)
            {
                failure = "rekrutierender Spieler ist gerade nicht geladen";
                return false;
            }
            if (!TryHatchWorker(recruiter, out failure)) return false;

            int count = Math.Max(0, zdo.GetInt(HatchedCountKey, 0)) + 1;
            zdo.Set(HatchedCountKey, count);
            zdo.Set(EggActiveKey, false);
            zdo.Set(IncubationStartKey, 0L);
            zdo.Set(HatchAtKey, 0L);
            zdo.Set(IncubationTimingVersionKey, IncubationTimingVersion);
            zdo.Set(RecruiterKey, 0L);
            _cachedEggActive = false;
            _cachedIncubationStage = 0;
            _lastSoundStage = -1;
            RefreshEggVisual();
            enabled = false;
            Notify(recruiter, "Die Schale bricht auf! Der Drake schlüpft und folgt dir. Du kannst ihn als Lufttransporter behalten oder in deine Drachenhalskette binden.");
            return true;
        }

        private void TryAutoHatch()
        {
            if (_view == null || !_view.IsOwner() || Time.unscaledTime < _nextAutoHatchAttempt) return;
            ZDO zdo = GetZdo();
            if (zdo == null || !zdo.GetBool(EggActiveKey, false)) return;
            EnsureCurrentIncubationTiming(zdo);
            if (GetWorldSeconds() < zdo.GetLong(HatchAtKey, long.MaxValue)) return;

            _nextAutoHatchAttempt = Time.unscaledTime + 2f;
            long recruiterId = zdo.GetLong(RecruiterKey, 0L);
            Player recruiter = FindLoadedPlayer(recruiterId);
            if (recruiter == null) return;

            if (!TryCompleteHatch(zdo, recruiter, out string failure))
                Plugin.Log?.LogWarning($"Automatic Drake hatch pending at {transform.position}: {failure}");
        }

        private static Player FindLoadedPlayer(long playerId)
        {
            if (playerId == 0L) return null;
            Player local = Player.m_localPlayer;
            if (local != null && local.GetPlayerID() == playerId) return local;

            List<Player> players = WorkerCharacterIndexRuntime.GetLivePlayers();
            for (int i = 0; i < players.Count; i++)
                if (players[i] != null && players[i].GetPlayerID() == playerId) return players[i];
            return null;
        }

        private bool TryHatchWorker(Player player, out string failure)
        {
            failure = string.Empty;
            if (ZNetScene.instance == null)
            {
                failure = "ZNetScene nicht bereit";
                return false;
            }

            GameObject prefab = null;
            try { prefab = ZNetScene.instance.GetPrefab("Hatchling"); } catch { }
            if (prefab == null)
            {
                failure = "Vanilla-Prefab Hatchling fehlt";
                return false;
            }

            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 spawnPosition = transform.position + forward * 2.5f + Vector3.up * 2.5f;
            GameObject instance = Instantiate(prefab, spawnPosition, Quaternion.LookRotation(-forward, Vector3.up));
            if (instance == null)
            {
                failure = "Instanziierung fehlgeschlagen";
                return false;
            }

            Character drake = instance.GetComponent<Character>() ?? instance.GetComponentInChildren<Character>();
            ZNetView workerView = drake != null ? (drake.GetComponent<ZNetView>() ?? drake.GetComponentInParent<ZNetView>()) : null;
            ZDO workerZdo = workerView != null && workerView.IsValid() ? workerView.GetZDO() : null;
            if (drake == null || workerZdo == null || !workerView.IsOwner())
            {
                Destroy(instance);
                failure = "kein lokaler gültiger Drake-ZDO";
                return false;
            }

            if (!SpeciesRoleRegistry.TryGetRole("Drake", out WorkerRole role))
            {
                Destroy(instance);
                failure = "Drake-Workerrolle fehlt";
                return false;
            }

            NaturalRecruitmentCompletionRuntime.Complete(
                drake,
                workerZdo,
                player.GetPlayerID(),
                role,
                "Drake",
                spawnPosition,
                40f,
                minimumDefensive: false,
                followPlayer: player);
            // v0.5.426: a naturally hatched Drake can either remain an Air Hauler or be
            // bound into the optional DrakeForm add-on's medallion. Creator-spawned Drakes
            // deliberately do not receive this marker.
            workerZdo.Set("vw_drake_morph_eligible", true);
            workerZdo.Set("vw_drake_morph_hatch_owner", player.GetPlayerID());

            Plugin.Log?.LogInfo($"Drake hatched from recruitment nest as Worker: zdo={workerZdo.m_uid}, owner={player.GetPlayerID()}, pos={spawnPosition}.");
            return true;
        }


        private void CacheEggVisual()
        {
            if (_eggVisual != null) return;
            Transform direct = transform.Find(DrakeEggNestPieceRuntime.EggVisualName);
            if (direct != null)
            {
                _eggVisual = direct.gameObject;
                CacheEggBaseTransform();
                return;
            }

            Transform[] children = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child == null || !string.Equals(child.name, DrakeEggNestPieceRuntime.EggVisualName, StringComparison.Ordinal)) continue;
                _eggVisual = child.gameObject;
                CacheEggBaseTransform();
                return;
            }
        }

        private void CacheEggBaseTransform()
        {
            if (_eggVisual == null || _eggBaseTransformCached) return;
            _eggBaseLocalPosition = _eggVisual.transform.localPosition;
            _eggBaseLocalRotation = _eggVisual.transform.localRotation;
            _eggBaseTransformCached = true;
        }

        private void RefreshEggVisual()
        {
            CacheEggVisual();
            if (_eggVisual == null) return;
            ZDO zdo = GetZdo();
            bool shouldShow = zdo != null && zdo.GetBool(EggActiveKey, false);
            if (shouldShow) EnsureCurrentIncubationTiming(zdo);

            if (_eggVisual.activeSelf != shouldShow) _eggVisual.SetActive(shouldShow);
            _cachedEggActive = shouldShow;
            if (!shouldShow)
            {
                _cachedIncubationStage = 0;
                _cachedStartAt = 0L;
                _cachedHatchAt = 0L;
                SetCracksVisible(false);
                ResetEggTransform();
                return;
            }

            long now = GetWorldSeconds();
            _cachedStartAt = zdo.GetLong(IncubationStartKey, now);
            _cachedHatchAt = zdo.GetLong(HatchAtKey, now + RequiredIncubationSeconds);
            int stage = GetIncubationStage(Math.Max(0L, now - _cachedStartAt));
            if (stage != _cachedIncubationStage)
            {
                _cachedIncubationStage = stage;
                _nextIncubationSoundAt = 0f;
            }
            SetCracksVisible(stage >= 2);
        }

        private void UpdateEggMotion()
        {
            if (_eggVisual == null || !_cachedEggActive || !_eggVisual.activeSelf)
                return;
            CacheEggBaseTransform();
            if (!_eggBaseTransformCached) return;

            if (_cachedIncubationStage < 1)
            {
                ResetEggTransform();
                return;
            }

            float strength = _cachedIncubationStage >= 2 ? 1f : 0.48f;
            float t = Time.unscaledTime;
            float burst = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(t * (_cachedIncubationStage >= 2 ? 3.9f : 2.5f))), 3f);
            float x = Mathf.Sin(t * 7.1f) * 0.026f * strength * burst;
            float z = Mathf.Sin(t * 5.3f + 0.8f) * 0.018f * strength * burst;
            float pitch = Mathf.Sin(t * 6.1f) * 4.2f * strength * burst;
            float roll = Mathf.Sin(t * 8.3f + 1.2f) * 6.0f * strength * burst;
            _eggVisual.transform.localPosition = _eggBaseLocalPosition + new Vector3(x, 0f, z);
            _eggVisual.transform.localRotation = _eggBaseLocalRotation * Quaternion.Euler(pitch, 0f, roll);
        }

        private void ResetEggTransform()
        {
            if (_eggVisual == null || !_eggBaseTransformCached) return;
            _eggVisual.transform.localPosition = _eggBaseLocalPosition;
            _eggVisual.transform.localRotation = _eggBaseLocalRotation;
        }

        private void SetCracksVisible(bool visible)
        {
            if (visible && _crackRoot == null) CreateCrackVisuals();
            if (_crackRoot != null && _crackRoot.activeSelf != visible) _crackRoot.SetActive(visible);
        }

        private void CreateCrackVisuals()
        {
            if (_eggVisual == null || _crackRoot != null) return;
            _crackRoot = new GameObject("Workers_DrakeEgg_Cracks");
            _crackRoot.transform.SetParent(_eggVisual.transform, false);

            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            if (shader != null)
            {
                _crackMaterial = new Material(shader);
                _crackMaterial.color = new Color(0.035f, 0.02f, 0.025f, 1f);
            }
            else
            {
                Renderer sourceRenderer = _eggVisual.GetComponentInChildren<Renderer>(true);
                if (sourceRenderer != null && sourceRenderer.sharedMaterial != null)
                {
                    _crackMaterial = new Material(sourceRenderer.sharedMaterial);
                    try { _crackMaterial.mainTexture = Texture2D.whiteTexture; } catch { }
                    if (_crackMaterial.HasProperty("_Color")) _crackMaterial.SetColor("_Color", new Color(0.025f, 0.015f, 0.02f, 1f));
                    if (_crackMaterial.HasProperty("_EmissionColor")) _crackMaterial.SetColor("_EmissionColor", Color.black);
                }
            }

            CreateCrackLine("A", new[]
            {
                new Vector3(-0.03f, 0.34f, -0.43f), new Vector3(0.03f, 0.24f, -0.46f),
                new Vector3(-0.02f, 0.14f, -0.47f), new Vector3(0.06f, 0.03f, -0.44f)
            });
            CreateCrackLine("B", new[]
            {
                new Vector3(0.01f, 0.23f, -0.46f), new Vector3(0.13f, 0.17f, -0.43f),
                new Vector3(0.18f, 0.07f, -0.39f)
            });
            CreateCrackLine("C", new[]
            {
                new Vector3(-0.02f, 0.14f, -0.47f), new Vector3(-0.13f, 0.08f, -0.42f),
                new Vector3(-0.19f, -0.01f, -0.37f)
            });
            // Mirror a few lines to the back so the cracked state is readable from either side.
            CreateCrackLine("D", new[]
            {
                new Vector3(0.04f, 0.30f, 0.43f), new Vector3(-0.02f, 0.20f, 0.46f),
                new Vector3(0.05f, 0.10f, 0.45f), new Vector3(-0.04f, 0.01f, 0.42f)
            });
            CreateCrackLine("E", new[]
            {
                new Vector3(-0.01f, 0.20f, 0.46f), new Vector3(-0.13f, 0.14f, 0.42f),
                new Vector3(-0.17f, 0.04f, 0.38f)
            });
            _crackRoot.SetActive(false);
        }

        private void CreateCrackLine(string suffix, Vector3[] points)
        {
            if (_crackRoot == null || points == null || points.Length < 2) return;
            GameObject go = new GameObject("Crack_" + suffix);
            go.transform.SetParent(_crackRoot.transform, false);
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.positionCount = points.Length;
            line.SetPositions(points);
            line.startWidth = 0.022f;
            line.endWidth = 0.012f;
            line.numCapVertices = 1;
            line.numCornerVertices = 1;
            if (_crackMaterial != null) line.sharedMaterial = _crackMaterial;
            line.startColor = new Color(0.04f, 0.015f, 0.02f, 1f);
            line.endColor = new Color(0.02f, 0.01f, 0.015f, 1f);
        }

        private void UpdateIncubationAudio()
        {
            if (!_cachedEggActive || _cachedIncubationStage < 1 || Time.unscaledTime < _nextIncubationSoundAt) return;
            Player local = Player.m_localPlayer;
            if (local == null || Vector3.Distance(local.transform.position, transform.position) > 24f) return;

            int stage = _cachedIncubationStage;
            if (_lastSoundStage != stage)
            {
                _lastSoundStage = stage;
                _nextIncubationSoundAt = 0f;
            }
            PlayIncubationSound(stage);
            _nextIncubationSoundAt = Time.unscaledTime + (stage >= 2 ? UnityEngine.Random.Range(2.5f, 4.0f) : UnityEngine.Random.Range(5.0f, 7.0f));
        }

        private void PlayIncubationSound(int stage)
        {
            AudioClip clip = ResolveIncubationClip();
            if (clip == null) return;
            GameObject soundObject = new GameObject("Workers_DrakeEgg_IncubationSound");
            soundObject.transform.position = transform.position + Vector3.up * 0.7f;
            AudioSource source = soundObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.volume = stage >= 2 ? 0.55f : 0.35f;
            source.pitch = stage >= 2 ? UnityEngine.Random.Range(0.72f, 0.88f) : UnityEngine.Random.Range(0.82f, 0.98f);
            source.spatialBlend = 1f;
            source.minDistance = 2f;
            source.maxDistance = 20f;
            source.Play();
            Destroy(soundObject, Mathf.Max(1f, clip.length / Mathf.Max(0.1f, source.pitch) + 0.25f));
        }

        private static AudioClip ResolveIncubationClip()
        {
            if (_incubationClipResolved) return _cachedIncubationClip;
            _incubationClipResolved = true;
            if (ZNetScene.instance == null) return null;

            string[] candidates = { "Hatchling", "DragonEgg" };
            for (int p = 0; p < candidates.Length; p++)
            {
                GameObject prefab = null;
                try { prefab = ZNetScene.instance.GetPrefab(candidates[p]); } catch { }
                if (prefab == null) continue;

                AudioSource[] sources = prefab.GetComponentsInChildren<AudioSource>(true);
                for (int i = 0; i < sources.Length; i++)
                {
                    if (sources[i] != null && sources[i].clip != null)
                    {
                        _cachedIncubationClip = sources[i].clip;
                        return _cachedIncubationClip;
                    }
                }

                Component[] components = prefab.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null || component.GetType().Name.IndexOf("ZSFX", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    FieldInfo[] fields = component.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    for (int f = 0; f < fields.Length; f++)
                    {
                        try
                        {
                            object value = fields[f].GetValue(component);
                            if (value is AudioClip direct && direct != null)
                            {
                                _cachedIncubationClip = direct;
                                return direct;
                            }
                            if (value is Array array)
                            {
                                for (int a = 0; a < array.Length; a++)
                                {
                                    if (array.GetValue(a) is AudioClip clip && clip != null)
                                    {
                                        _cachedIncubationClip = clip;
                                        return clip;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            return null;
        }

        private ZDO GetZdo()
        {
            if (_view == null) _view = GetComponent<ZNetView>() ?? GetComponentInParent<ZNetView>();
            return _view != null && _view.IsValid() ? _view.GetZDO() : null;
        }

        private static int CountPrefabItems(Inventory inventory, string prefab)
        {
            if (inventory == null || string.IsNullOrEmpty(prefab)) return 0;
            List<ItemDrop.ItemData> items = inventory.GetAllItems();
            if (items == null) return 0;
            int total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (!IsPrefabItem(item, prefab)) continue;
                total += Mathf.Max(0, item.m_stack);
            }
            return total;
        }

        private static bool TryConsumePrefabItems(Inventory inventory, string prefab, int amount)
        {
            if (inventory == null || amount <= 0 || CountPrefabItems(inventory, prefab) < amount) return false;
            List<ItemDrop.ItemData> source = inventory.GetAllItems();
            if (source == null) return false;
            List<ItemDrop.ItemData> copy = new List<ItemDrop.ItemData>(source);
            int remaining = amount;
            for (int i = 0; i < copy.Count && remaining > 0; i++)
            {
                ItemDrop.ItemData item = copy[i];
                if (!IsPrefabItem(item, prefab)) continue;
                int take = Mathf.Min(remaining, Mathf.Max(0, item.m_stack));
                if (take <= 0) continue;
                inventory.RemoveItem(item, take);
                remaining -= take;
            }
            return remaining == 0;
        }

        private static bool IsPrefabItem(ItemDrop.ItemData item, string prefab)
        {
            if (item == null || item.m_stack <= 0 || item.m_dropPrefab == null) return false;
            string normalized = SpeciesRoleRegistry.NormalizePrefabName(item.m_dropPrefab.name);
            return string.Equals(normalized, prefab, StringComparison.OrdinalIgnoreCase);
        }

        private static float GetDayLengthSeconds()
        {
            try
            {
                EnvMan env = EnvMan.instance;
                if (env != null && env.m_dayLengthSec > 60f) return env.m_dayLengthSec;
            }
            catch { }
            return FallbackDayLengthSeconds;
        }

        private static long GetWorldSeconds()
        {
            try
            {
                ZNet net = ZNet.instance;
                if (net != null && GetTimeSecondsMethod != null)
                {
                    object value = GetTimeSecondsMethod.Invoke(net, null);
                    if (value != null) return (long)Math.Floor(Convert.ToDouble(value));
                }
            }
            catch { }
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        }

        private static string FormatDuration(long seconds)
        {
            if (RequiredIncubationSeconds <= 120L) return $"{Mathf.Max(0, (int)seconds)} Sek.";
            float dayLength = Mathf.Max(60f, GetDayLengthSeconds());
            float days = seconds / dayLength;
            if (days >= 1f) return $"{days:0.0} Valheim-Tage";
            int minutes = Mathf.Max(1, Mathf.CeilToInt(seconds / 60f));
            return $"{minutes} Min.";
        }

        private static void Notify(Player player, string text)
        {
            if (player == null || string.IsNullOrEmpty(text)) return;
            try { player.Message(MessageHud.MessageType.Center, text); } catch { }
        }
    }
}
