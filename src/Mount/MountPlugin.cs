using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using System.Reflection;

namespace Valheim_Workers
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("germa.valheim.workers", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class MountPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "germa.valheim.workers.mount";
        public const string PluginName = "Valheim_Workers_Mount";
        public const string PluginVersion = "4.5.2";

        private Harmony _harmony;
        private ConfigEntry<float> _wingbeatIntensity;
        private ConfigEntry<bool> _debugLogging;
        private ConfigEntry<float> _egoDrakeX, _egoDrakeY, _egoDrakeZ;
        private ConfigEntry<float> _egoTrollX, _egoTrollY, _egoTrollZ;
        private ConfigEntry<float> _egoWerewolfX, _egoWerewolfY, _egoWerewolfZ;
        private ConfigEntry<float> _egoSerpentX, _egoSerpentY, _egoSerpentZ;
        private bool _egoSettingsPatched;
        private float _nextEgoSettingsPatchAttempt;
        private static MountPlugin _instance;

        internal static void LogGuardWarning(string message)
        {
            if (_instance != null) _instance.Logger.LogWarning(message);
            else UnityEngine.Debug.LogWarning("[Valheim_Workers_Mount] " + message);
        }
        private GUIStyle _skillHudNameStyle;
        private GUIStyle _skillHudKeyStyle;
        private GUIStyle _skillHudTimerStyle;
        private GUIStyle _skillHudHeaderStyle;
        private float _skillHudStyleScale = -1f;

        private void Awake()
        {
            _instance = this;
            _wingbeatIntensity = Config.Bind(
                "Ego",
                "DrakeWingbeatIntensity",
                1.0f,
                "Strength of the animation-synchronised Drake wingbeat camera motion in first person. 0 = off, 1 = normal, 2 = 200%."
            );

            _debugLogging = Config.Bind(
                "General",
                "DebugLogging",
                false,
                "Enable verbose Symbiosis runtime logging. Leave disabled for normal play."
            );

            _egoDrakeX = Config.Bind("Ego FineTune", "DrakeX", 0f, "Saved Drake Ego lateral offset in metres.");
            _egoDrakeY = Config.Bind("Ego FineTune", "DrakeY", 0f, "Saved Drake Ego vertical offset in metres.");
            _egoDrakeZ = Config.Bind("Ego FineTune", "DrakeZ", 0f, "Saved Drake Ego forward offset in metres.");
            _egoTrollX = Config.Bind("Ego FineTune", "TrollX", 0f, "Saved Troll Ego lateral offset in metres.");
            _egoTrollY = Config.Bind("Ego FineTune", "TrollY", 0f, "Saved Troll Ego vertical offset in metres.");
            _egoTrollZ = Config.Bind("Ego FineTune", "TrollZ", 0f, "Saved Troll Ego forward offset in metres.");
            _egoWerewolfX = Config.Bind("Ego FineTune", "WerewolfX", 0f, "Saved Werewolf Ego lateral offset in metres.");
            _egoWerewolfY = Config.Bind("Ego FineTune", "WerewolfY", 0f, "Saved Werewolf Ego vertical offset in metres.");
            _egoWerewolfZ = Config.Bind("Ego FineTune", "WerewolfZ", 0f, "Saved Werewolf Ego forward offset in metres.");
            _egoSerpentX = Config.Bind("Ego FineTune", "SerpentX", 0f, "Saved Serpent Ego lateral offset in metres.");
            _egoSerpentY = Config.Bind("Ego FineTune", "SerpentY", 0f, "Saved Serpent Ego vertical offset in metres.");
            _egoSerpentZ = Config.Bind("Ego FineTune", "SerpentZ", 0f, "Saved Serpent Ego forward offset in metres.");

            DrakeMountRuntime.Initialize(DebugLog, SetStatus);
            WerewolfVisionRuntime.Initialize(DebugLog);
            DrakeMountRuntime.SetWingbeatIntensityProvider(() => Mathf.Clamp(_wingbeatIntensity.Value, 0f, 2f));
            DrakeMountRuntime.SetEgoFineTuneBridge(GetEgoFineTune, SaveEgoFineTune);
            DrakeMountAssignmentRuntime.Initialize(SetStatus, DebugLog);
            RegisterExternalControlBridge();

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(MountPlugin).Assembly);
            PatchOptionalLifecycleHooks();
            TryPatchEgoSettings();
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded. Feature-freeze release candidate: lifecycle recovery, defensive cleanup and multi-profile installer consistency enabled.");
        }



        private void PatchOptionalLifecycleHooks()
        {
            if (_harmony == null) return;
            try
            {
                MethodInfo worldExitPrefix = typeof(MountPlugin).GetMethod(nameof(WorldExitLifecyclePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo playerDeathPrefix = typeof(MountPlugin).GetMethod(nameof(PlayerDeathLifecyclePrefix), BindingFlags.Static | BindingFlags.NonPublic);

                PatchAllNamedMethods(typeof(Game), "Logout", worldExitPrefix);
                PatchAllNamedMethods(typeof(Game), "Shutdown", worldExitPrefix);
                PatchAllNamedMethods(typeof(Player), "OnDeath", playerDeathPrefix);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Optional symbiosis lifecycle hooks were not installed completely: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void PatchAllNamedMethods(Type type, string methodName, MethodInfo prefix)
        {
            if (type == null || prefix == null || string.IsNullOrEmpty(methodName)) return;
            int patched = 0;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)) continue;
                try
                {
                    _harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                    patched++;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Lifecycle hook skipped for {type.Name}.{methodName}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (patched > 0)
                Logger.LogInfo($"Lifecycle safety hook active: {type.Name}.{methodName} ({patched} overload(s)).");
        }

        private static void WorldExitLifecyclePrefix()
        {
            try { DrakeMountRuntime.PrepareForWorldExit(); }
            catch { }
        }

        private static void PlayerDeathLifecyclePrefix(Player __instance)
        {
            try
            {
                if (__instance == Player.m_localPlayer)
                    DrakeMountRuntime.PrepareForLocalPlayerDeath(__instance);
            }
            catch { }
        }

        private Vector3 GetEgoFineTune(string form)
        {
            if (string.Equals(form, "Troll", StringComparison.OrdinalIgnoreCase)) return new Vector3(_egoTrollX.Value, _egoTrollY.Value, _egoTrollZ.Value);
            if (string.Equals(form, "Werewolf", StringComparison.OrdinalIgnoreCase)) return new Vector3(_egoWerewolfX.Value, _egoWerewolfY.Value, _egoWerewolfZ.Value);
            if (string.Equals(form, "Serpent", StringComparison.OrdinalIgnoreCase)) return new Vector3(_egoSerpentX.Value, _egoSerpentY.Value, _egoSerpentZ.Value);
            return new Vector3(_egoDrakeX.Value, _egoDrakeY.Value, _egoDrakeZ.Value);
        }

        private void SaveEgoFineTune(string form, Vector3 value)
        {
            if (string.Equals(form, "Troll", StringComparison.OrdinalIgnoreCase)) { _egoTrollX.Value = value.x; _egoTrollY.Value = value.y; _egoTrollZ.Value = value.z; }
            else if (string.Equals(form, "Werewolf", StringComparison.OrdinalIgnoreCase)) { _egoWerewolfX.Value = value.x; _egoWerewolfY.Value = value.y; _egoWerewolfZ.Value = value.z; }
            else if (string.Equals(form, "Serpent", StringComparison.OrdinalIgnoreCase)) { _egoSerpentX.Value = value.x; _egoSerpentY.Value = value.y; _egoSerpentZ.Value = value.z; }
            else { _egoDrakeX.Value = value.x; _egoDrakeY.Value = value.y; _egoDrakeZ.Value = value.z; }
            Config.Save();
        }

        private void ResetEgoFineTune(string form)
        {
            SaveEgoFineTune(form, Vector3.zero);
            string display = string.Equals(form, "Serpent", StringComparison.OrdinalIgnoreCase) ? "Seeschlange" :
                string.Equals(form, "Werewolf", StringComparison.OrdinalIgnoreCase) ? "Werwolf" : form;
            SetStatus($"{display}-Ego Feinposition zurueckgesetzt: X=0.00m Y=0.00m Z=0.00m");
            Logger.LogInfo($"{display} Ego fine-tune reset to zero from SmartCore Ego tab.");
        }

        private static void DrawFineTuneResetRow(MountPlugin self, string form, string displayName)
        {
            Vector3 value = self.GetEgoFineTune(form);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{displayName}: X={value.x:+0.00;-0.00;0.00}  Y={value.y:+0.00;-0.00;0.00}  Z={value.z:+0.00;-0.00;0.00} m");
            if (GUILayout.Button("Zuruecksetzen", GUILayout.Width(110f)))
                self.ResetEgoFineTune(form);
            GUILayout.EndHorizontal();
        }

        private void RegisterExternalControlBridge()
        {
            try
            {
                MethodInfo register6 = null;
                MethodInfo register4 = null;
                MethodInfo[] methods = typeof(WorkerExternalControlBridge).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (MethodInfo method in methods)
                {
                    if (!string.Equals(method.Name, "Register", StringComparison.Ordinal)) continue;
                    int count = method.GetParameters().Length;
                    if (count == 6) register6 = method;
                    else if (count == 4) register4 = method;
                }

                Func<Character, bool> isControlled = DrakeMountRuntime.IsMounted;
                Func<Character, float, bool> applyControls = DrakeMountRuntime.ApplyControls;
                Func<Character, string> hoverText = GetExternalHoverText;

                if (register6 != null)
                {
                    // v0.2.0: necklace symbiosis is the only user-facing Drake possession path.
                    // Keep the old assignment runtime alive only for save compatibility, but do not
                    // expose the obsolete "personal mount" worker-menu tab for new assignments.
                    Func<ZDO, bool> hasWorkerMenuTab = _ => false;
                    Action<ZDO, bool> drawWorkerMenuTab = (_, __) => { };
                    register6.Invoke(null, new object[]
                    {
                        this, isControlled, applyControls, hoverText, hasWorkerMenuTab, drawWorkerMenuTab
                    });
                    Logger.LogInfo("Workers bridge registered. Legacy personal-mount tab is hidden; necklace symbiosis is the active Drake control path.");
                    return;
                }

                if (register4 != null)
                {
                    register4.Invoke(null, new object[] { this, isControlled, applyControls, hoverText });
                    Logger.LogInfo("Workers core exposes the 4-argument control bridge; necklace symbiosis registered without the obsolete personal-mount tab.");
                    return;
                }

                Logger.LogError("Workers external-control bridge Register method was not found.");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                Logger.LogError($"Workers bridge registration failed: {inner.Message}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Workers bridge registration failed: {ex.Message}");
            }
        }

        private float _nextRuntimeFaultLogAt;
        private float _nextVisionFaultLogAt;
        private float _nextAssignmentFaultLogAt;

        private void Update()
        {
            if (!_egoSettingsPatched && Time.unscaledTime >= _nextEgoSettingsPatchAttempt)
            {
                _nextEgoSettingsPatchAttempt = Time.unscaledTime + 2f;
                TryPatchEgoSettings();
            }

            try
            {
                DrakeMountRuntime.Tick();
            }
            catch (Exception ex)
            {
                LogThrottledRuntimeFault(ref _nextRuntimeFaultLogAt, "Update", ex);
                DrakeMountRuntime.EmergencyRelease("Symbiose wurde nach einem Runtime-Fehler sicher beendet.");
            }

            try
            {
                WerewolfVisionRuntime.Tick(DrakeMountRuntime.GetWerewolfVisionWeight());
            }
            catch (Exception ex)
            {
                LogThrottledSubsystemFault(ref _nextVisionFaultLogAt, "Werewolf vision", ex);
            }

            try
            {
                DrakeMountAssignmentRuntime.Tick();
            }
            catch (Exception ex)
            {
                LogThrottledSubsystemFault(ref _nextAssignmentFaultLogAt, "assignment", ex);
            }
        }

        private void FixedUpdate()
        {
            try
            {
                DrakeMountRuntime.FixedTick();
            }
            catch (Exception ex)
            {
                LogThrottledRuntimeFault(ref _nextRuntimeFaultLogAt, "FixedUpdate", ex);
                DrakeMountRuntime.EmergencyRelease("Symbiose wurde nach einem Physik-Fehler sicher beendet.");
            }
        }

        private void LateUpdate()
        {
            try
            {
                DrakeMountRuntime.LateTick();
            }
            catch (Exception ex)
            {
                LogThrottledRuntimeFault(ref _nextRuntimeFaultLogAt, "LateUpdate", ex);
                DrakeMountRuntime.EmergencyRelease("Symbiose wurde nach einem Kamera-Fehler sicher beendet.");
            }
        }

        private void LogThrottledRuntimeFault(ref float nextLogAt, string phase, Exception ex)
        {
            float now = Time.unscaledTime;
            if (now < nextLogAt) return;
            nextLogAt = now + 5f;
            Logger.LogError($"Symbiosis {phase} failed safely: {ex.GetType().Name}: {ex.Message}");
        }

        private void LogThrottledSubsystemFault(ref float nextLogAt, string subsystem, Exception ex)
        {
            float now = Time.unscaledTime;
            if (now < nextLogAt) return;
            nextLogAt = now + 10f;
            Logger.LogWarning($"Symbiosis {subsystem} subsystem suspended this frame: {ex.GetType().Name}: {ex.Message}");
        }

        private void OnGUI()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            try { WerewolfVisionRuntime.DrawFallbackOverlay(); } catch { }
            try { DrawFormSkillHud(); } catch { }
        }

        private void DrawFormSkillHud()
        {
            DrakeMountRuntime.FormSkillHudState state = DrakeMountRuntime.GetFormSkillHudState();
            if (!state.Visible) return;

            // One compact, consistent combat strip for every symbiosis form. Cooldowns are read
            // from the same runtime timers that gate the abilities, so the HUD never polls/scans
            // the world and adds no gameplay-side work.
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.75f, 1.55f);
            EnsureSkillHudStyles(scale);

            float x = 150f * scale;
            float bottom = 68f * scale;
            float slotW = 64f * scale;
            float slotH = 58f * scale;
            float gap = 5f * scale;
            float y = Screen.height - bottom - slotH;

            GUI.Label(new Rect(x, y - 18f * scale, slotW * 3f + gap * 2f, 18f * scale), state.Header ?? "SYMBIOSE", _skillHudHeaderStyle);
            DrawSkillSlot(new Rect(x, y, slotW, slotH), state.Key1 ?? "LMB", state.Name1 ?? "ANGRIFF", state.Remaining1, state.Duration1, state.Active1, state.ActiveText1);
            DrawSkillSlot(new Rect(x + slotW + gap, y, slotW, slotH), state.Key2 ?? "RMB", state.Name2 ?? "FAEHIGKEIT", state.Remaining2, state.Duration2, state.Active2, state.ActiveText2);
            DrawSkillSlot(new Rect(x + (slotW + gap) * 2f, y, slotW, slotH), state.Key3 ?? "MMB", state.Name3 ?? "FAEHIGKEIT", state.Remaining3, state.Duration3, state.Active3, state.ActiveText3);
        }

        private void EnsureSkillHudStyles(float scale)
        {
            if (_skillHudNameStyle != null && Mathf.Abs(_skillHudStyleScale - scale) < 0.02f) return;
            _skillHudStyleScale = scale;

            _skillHudNameStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.LowerCenter,
                fontStyle = FontStyle.Bold,
                fontSize = Mathf.Max(9, Mathf.RoundToInt(10f * scale))
            };
            _skillHudNameStyle.normal.textColor = Color.white;

            _skillHudKeyStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperCenter,
                fontStyle = FontStyle.Bold,
                fontSize = Mathf.Max(8, Mathf.RoundToInt(9f * scale))
            };
            _skillHudKeyStyle.normal.textColor = new Color(0.93f, 0.88f, 0.76f, 1f);

            _skillHudTimerStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = Mathf.Max(11, Mathf.RoundToInt(14f * scale))
            };
            _skillHudTimerStyle.normal.textColor = Color.white;

            _skillHudHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold,
                fontSize = Mathf.Max(9, Mathf.RoundToInt(10f * scale))
            };
            _skillHudHeaderStyle.normal.textColor = new Color(0.86f, 0.82f, 0.75f, 0.95f);
        }

        private void DrawSkillSlot(Rect rect, string key, string name, float remaining, float duration, bool active, string activeText)
        {
            Color oldColor = GUI.color;

            GUI.color = new Color(0.03f, 0.025f, 0.025f, 0.78f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);

            float readyFraction = duration > 0.001f ? 1f - Mathf.Clamp01(remaining / duration) : 1f;
            if (remaining > 0.01f && !active)
            {
                Rect fill = new Rect(rect.x + 2f, rect.y + 2f + (rect.height - 4f) * readyFraction, rect.width - 4f, (rect.height - 4f) * (1f - readyFraction));
                GUI.color = new Color(0f, 0f, 0f, 0.58f);
                GUI.DrawTexture(fill, Texture2D.whiteTexture);
            }

            Color accent = active ? new Color(0.92f, 0.20f, 0.13f, 1f)
                : remaining <= 0.01f ? new Color(0.55f, 0.82f, 0.43f, 1f)
                : new Color(0.47f, 0.47f, 0.47f, 0.95f);
            GUI.color = accent;
            float border = Mathf.Max(1f, Mathf.Round(2f * _skillHudStyleScale));
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, border), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - border, rect.width, border), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, border, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - border, rect.y, border, rect.height), Texture2D.whiteTexture);

            GUI.color = oldColor;
            GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width - 4f, rect.height * 0.30f), key, _skillHudKeyStyle);
            GUI.Label(new Rect(rect.x + 2f, rect.yMax - rect.height * 0.31f, rect.width - 4f, rect.height * 0.28f), name, _skillHudNameStyle);

            string center = active ? (string.IsNullOrEmpty(activeText) ? "AKTIV" : activeText)
                : remaining > 0.05f ? remaining.ToString("0.0")
                : "BEREIT";
            GUI.Label(new Rect(rect.x + 2f, rect.y + rect.height * 0.26f, rect.width - 4f, rect.height * 0.45f), center, _skillHudTimerStyle);
        }

        private void OnApplicationQuit()
        {
            try { DrakeMountRuntime.PrepareForWorldExit(); } catch { }
        }

        private void OnDestroy()
        {
            try { DrakeMountRuntime.PrepareForWorldExit(); } catch { }
            WorkerExternalControlBridge.Unregister(this);
            WerewolfVisionRuntime.Shutdown();
            DrakeMountRuntime.Shutdown();
            DrakeMountAssignmentRuntime.Shutdown();
            _instance = null;
            _harmony?.UnpatchSelf();
        }


        private void TryPatchEgoSettings()
        {
            if (_egoSettingsPatched || _harmony == null) return;
            try
            {
                // Do not use AccessTools.TypeByName while Valheim Ego may still be loading; HarmonyX
                // logs a scary warning for an expected "not loaded yet" state. Scan loaded assemblies
                // quietly and let Update() retry once the optional plugin exists.
                Type egoType = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    egoType = assembly.GetType("Valheim_Ego.FirstPersonPlugin", false);
                    if (egoType != null) break;
                }
                MethodInfo draw = egoType != null ? AccessTools.Method(egoType, "DrawSmartCoreSettings") : null;
                MethodInfo postfix = AccessTools.Method(typeof(MountPlugin), nameof(DrawMountEgoSettingsPostfix));
                if (draw == null || postfix == null) return;

                _harmony.Patch(draw, postfix: new HarmonyMethod(postfix));
                _egoSettingsPatched = true;
                Logger.LogInfo("Drake wingbeat intensity slider appended to SmartCore Ego tab.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Could not append Drake setting to Ego tab yet: {ex.Message}");
            }
        }

        private static void DrawMountEgoSettingsPostfix()
        {
            MountPlugin self = _instance;
            if (self == null || self._wingbeatIntensity == null) return;

            GUILayout.Space(12f);
            GUILayout.Label("Drake Mount");
            float oldValue = Mathf.Clamp(self._wingbeatIntensity.Value, 0f, 2f);
            GUILayout.Label($"Wingbeat camera intensity: {Mathf.RoundToInt(oldValue * 100f)}%");
            float newValue = GUILayout.HorizontalSlider(oldValue, 0f, 2f);
            if (Mathf.Abs(newValue - oldValue) >= 0.001f)
                self._wingbeatIntensity.Value = newValue;
            GUILayout.Label("0% = off · 100% = realistic default · 200% = maximum");

            GUILayout.Space(14f);
            GUILayout.Label("Symbiose Ego - Kamera/Kopf Feinposition");
            GUILayout.Label("Setzt nur die gespeicherte XYZ-Feinjustierung der gewaehlten Kreatur auf 0 zurueck. Andere Kreaturen bleiben unveraendert.");
            DrawFineTuneResetRow(self, "Drake", "Drache");
            DrawFineTuneResetRow(self, "Troll", "Troll");
            DrawFineTuneResetRow(self, "Werewolf", "Werwolf");
            DrawFineTuneResetRow(self, "Serpent", "Seeschlange");
        }

        private void SetStatus(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            Plugin.Instance?.SetExternalStatus(message);
            if (_debugLogging != null && _debugLogging.Value) Logger.LogInfo(message);
        }

        private void DebugLog(string message)
        {
            if (!string.IsNullOrWhiteSpace(message) && _debugLogging != null && _debugLogging.Value)
                Logger.LogInfo(message);
        }

        private static string GetExternalHoverText(Character worker)
        {
            if (worker == null) return null;
            string species = SpeciesRoleRegistry.NormalizePrefabName(worker.gameObject.name);
            if (!string.Equals(species, "Drake", StringComparison.OrdinalIgnoreCase)) return null;
            if (DrakeMountRuntime.IsMounted(worker)) return string.Empty;
            return "Drake-Symbiose: gebundene Drachenhalskette in die Schnellleiste legen und ihre Zahl druecken\n[<color=yellow><b>Shift+E</b></color>] Arbeiter-Inventar";
        }
    }
}
