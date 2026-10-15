using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// In-world onboarding for recruitment. Hugin gives only the broad idea on first contact;
    /// exact offering/favourite-food knowledge lives on old runestones spawned around nests.
    /// No gameplay polling loop is added by this runtime.
    /// </summary>
    internal sealed class WorkersLoreRuntime
    {
        internal const string GreydwarfStonePrefab = "ValheimWorkers_GreydwarfLoreStone";
        internal const string FoodStonePrefab = "ValheimWorkers_FoodLoreStone";
        internal const string FulingStonePrefab = "ValheimWorkers_FulingLoreStone";
        private const string StoneBasePrefab = "Runestone_Greydwarfs";
        private const string NestPrefab = "Spawner_GreydwarfNest";
        private const string RavenGuideChild = "ValheimWorkers_GreydwarfNestGuide";
        private const string TutorialKey = "vw_workers_greydwarf_nest_intro_v2";

        private readonly Action<string> _debugLog;
        private readonly Action<string> _infoLog;
        private bool _registered;
        private bool _gaveUp;
        private float _nextAttempt;
        private float _attemptDeadline;
        private string _status = "waiting for Jotunn vanilla prefabs";

        private static GameObject _greydwarfStoneNetworkPrefab;
        private static GameObject _foodStoneNetworkPrefab;
        private static GameObject _fulingStoneNetworkPrefab;

        internal string Status => _registered ? "registered" : _status;

        internal WorkersLoreRuntime(Action<string> debugLog, Action<string> infoLog)
        {
            _debugLog = debugLog;
            _infoLog = infoLog;
        }

        internal void Begin()
        {
            _attemptDeadline = float.PositiveInfinity;
            if (JotunnPrefabLifecycleRuntime.Subscribe(TryRegister, _debugLog))
                return;

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
                _status = "lore registration unavailable";
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

                bool tutorialOk = RegisterGreydwarfRavenTutorial(jotunn);
                bool greydwarfStoneOk = RegisterWorldLorePrefab(
                    jotunn,
                    GreydwarfStonePrefab,
                    WorkersLorePage.GreydwarfBond,
                    "Alte Steintafel");
                bool foodStoneOk = RegisterWorldLorePrefab(
                    jotunn,
                    FoodStonePrefab,
                    WorkersLorePage.FoodAndSupply,
                    "Verwitterte Steintafel");
                bool fulingStoneOk = RegisterWorldLorePrefab(
                    jotunn,
                    FulingStonePrefab,
                    WorkersLorePage.FulingDiplomacy,
                    "Alte Ebenen-Steintafel");
                bool nestSpawnerOk = RegisterNestLoreSpawner(jotunn);

                if (!tutorialOk || !greydwarfStoneOk || !foodStoneOk || !fulingStoneOk || !nestSpawnerOk)
                {
                    _status = $"pending: raven={tutorialOk}, greydwarfStone={greydwarfStoneOk}, foodStone={foodStoneOk}, fulingStone={fulingStoneOk}, nestSpawner={nestSpawnerOk}";
                    return;
                }

                _registered = true;
                _status = "registered";
                _infoLog?.Invoke("[Lore] Hugin first-contact + world-only Workers runestones registered; no Hammer recipes added.");
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex.InnerException ?? ex;
                _status = $"lore registration pending: {root.GetType().Name}: {root.Message}";
                _debugLog?.Invoke(_status);
            }
            catch (Exception ex)
            {
                _status = $"lore registration pending: {ex.GetType().Name}: {ex.Message}";
                _debugLog?.Invoke(_status);
            }
        }

        private bool RegisterGreydwarfRavenTutorial(Assembly jotunn)
        {
            GameObject nest = ResolvePrefab(jotunn, NestPrefab);
            if (nest == null) return false;

            Transform existing = nest.transform.Find(RavenGuideChild);
            GuidePoint guide = existing != null ? existing.GetComponent<GuidePoint>() : null;
            if (guide == null)
            {
                GameObject go = existing != null ? existing.gameObject : new GameObject(RavenGuideChild);
                if (existing == null)
                {
                    go.transform.SetParent(nest.transform, false);
                    go.transform.localPosition = new Vector3(0f, 3.2f, 0f);
                }
                guide = go.AddComponent<GuidePoint>();
            }

            if (guide.m_ravenPrefab == null)
            {
                GameObject workbench = ResolvePrefab(jotunn, "piece_workbench");
                GuidePoint source = workbench != null ? workbench.GetComponentInChildren<GuidePoint>(true) : null;
                if (source != null) guide.m_ravenPrefab = source.m_ravenPrefab;
            }

            if (guide.m_ravenPrefab == null)
            {
                GuidePoint[] loadedGuides = Resources.FindObjectsOfTypeAll<GuidePoint>();
                for (int i = 0; loadedGuides != null && i < loadedGuides.Length; i++)
                {
                    GuidePoint candidate = loadedGuides[i];
                    if (candidate == null || candidate == guide || candidate.m_ravenPrefab == null) continue;
                    guide.m_ravenPrefab = candidate.m_ravenPrefab;
                    break;
                }
            }

            if (guide.m_ravenPrefab == null)
            {
                _debugLog?.Invoke("[Lore] Greydwarf nest GuidePoint is waiting for vanilla Raven prefab.");
                return false;
            }

            if (!TryAssignGuideText(guide))
            {
                _debugLog?.Invoke("[Lore] Greydwarf nest GuidePoint RavenText layout is unavailable in this Valheim build.");
                return false;
            }

            return true;
        }

        private bool RegisterWorldLorePrefab(Assembly jotunn, string prefabName, WorkersLorePage page, string displayName)
        {
            Type prefabManagerType = jotunn.GetType("Jotunn.Managers.PrefabManager", false);
            object manager = prefabManagerType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            MethodInfo getPrefab = prefabManagerType?.GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            MethodInfo clonePrefab = prefabManagerType?.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "CreateClonedPrefab" && m.GetParameters().Length == 2 &&
                                     m.GetParameters()[0].ParameterType == typeof(string) &&
                                     m.GetParameters()[1].ParameterType == typeof(string));
            MethodInfo addPrefab = prefabManagerType?.GetMethod("AddPrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(GameObject) }, null);
            if (manager == null || getPrefab == null || clonePrefab == null || addPrefab == null) return false;

            GameObject prefab = getPrefab.Invoke(manager, new object[] { prefabName }) as GameObject;
            bool newlyCreated = false;
            if (prefab == null)
            {
                GameObject source = getPrefab.Invoke(manager, new object[] { StoneBasePrefab }) as GameObject;
                if (source == null) return false;
                prefab = clonePrefab.Invoke(manager, new object[] { prefabName, StoneBasePrefab }) as GameObject;
                newlyCreated = prefab != null;
            }
            if (prefab == null) return false;

            StripVanillaRunestoneBehaviour(prefab);
            WorkersLoreStoneInteractable interactable = prefab.GetComponent<WorkersLoreStoneInteractable>();
            if (interactable == null) interactable = prefab.AddComponent<WorkersLoreStoneInteractable>();
            interactable.Configure(page, displayName);

            if (newlyCreated)
                addPrefab.Invoke(manager, new object[] { prefab });

            if (page == WorkersLorePage.GreydwarfBond) _greydwarfStoneNetworkPrefab = prefab;
            else if (page == WorkersLorePage.FoodAndSupply) _foodStoneNetworkPrefab = prefab;
            else if (page == WorkersLorePage.FulingDiplomacy) _fulingStoneNetworkPrefab = prefab;
            return true;
        }

        private bool RegisterNestLoreSpawner(Assembly jotunn)
        {
            GameObject nest = ResolvePrefab(jotunn, NestPrefab);
            if (nest == null) return false;
            if (nest.GetComponent<WorkersLoreNearbyStoneSpawner>() == null)
                nest.AddComponent<WorkersLoreNearbyStoneSpawner>();
            return true;
        }

        internal static bool TryGetWorldLorePrefab(WorkersLorePage page, out GameObject prefab)
        {
            switch (page)
            {
                case WorkersLorePage.FoodAndSupply:
                    prefab = _foodStoneNetworkPrefab;
                    break;
                case WorkersLorePage.FulingDiplomacy:
                    prefab = _fulingStoneNetworkPrefab;
                    break;
                default:
                    prefab = _greydwarfStoneNetworkPrefab;
                    break;
            }
            return prefab != null;
        }

        private static void StripVanillaRunestoneBehaviour(GameObject prefab)
        {
            if (prefab == null) return;
            string[] removeTypes =
            {
                "RuneStone", "GuidePoint", "HoverText", "CreatureSpawner", "RandomSpawn",
                "Location", "TerrainModifier", "MusicLocation", "Destructible", "DropOnDestroyed",
                "PrivateArea", "Switch", "CircleProjector", "EffectArea"
            };

            Component[] components = prefab.GetComponentsInChildren<Component>(true);
            for (int i = components.Length - 1; i >= 0; i--)
            {
                Component component = components[i];
                if (component == null || component is Transform || component is ZNetView) continue;
                string typeName = component.GetType().Name;
                for (int j = 0; j < removeTypes.Length; j++)
                {
                    if (!string.Equals(typeName, removeTypes[j], StringComparison.Ordinal)) continue;
                    UnityEngine.Object.DestroyImmediate(component);
                    break;
                }
            }
        }

        private static bool TryAssignGuideText(GuidePoint guide)
        {
            if (guide == null) return false;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type guideType = guide.GetType();
            FieldInfo textField = guideType.GetField("m_text", flags);
            PropertyInfo textProperty = textField == null ? guideType.GetProperty("m_text", flags) : null;
            Type textType = textField != null ? textField.FieldType : textProperty?.PropertyType;
            if (textType == null) return false;

            object ravenText;
            try { ravenText = Activator.CreateInstance(textType, true); }
            catch { return false; }
            if (ravenText == null) return false;

            SetMember(ravenText, "m_alwaysSpawn", false);
            SetMember(ravenText, "m_munin", false);
            SetMember(ravenText, "m_priority", 25);
            SetMember(ravenText, "m_key", TutorialKey);
            SetMember(ravenText, "m_topic", "Nicht jedes Nest muss brennen");
            SetMember(ravenText, "m_label", "Grauzwerg-Nester");
            SetMember(ravenText, "m_text", BuildRavenIntroText());

            try
            {
                if (textField != null)
                {
                    textField.SetValue(guide, ravenText);
                    return true;
                }
                if (textProperty != null && textProperty.CanWrite)
                {
                    textProperty.SetValue(guide, ravenText);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static void SetMember(object target, string name, object value)
        {
            if (target == null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = target.GetType();
            FieldInfo field = type.GetField(name, flags);
            if (field != null)
            {
                try { field.SetValue(target, value); } catch { }
                return;
            }
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.CanWrite)
            {
                try { property.SetValue(target, value); } catch { }
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

        private static string BuildRavenIntroText()
        {
            return "Nicht jedes Grauzwerg-Nest muss vernichtet werden, Krieger. " +
                   "Ein Nest laesst sich mit passenden Opfergaben besaenftigen. Welche Gaben du waehrend der Bindung waehlt, bestimmt dauerhaft die Chancen der Kreaturen, die sich spaeter deinem Ruf anschliessen.\n\n" +
                   "Erst bei 100% ist das Nest befriedet. Dann gibt es einen Uralten Samen frei. Einen Worker erhaeltst du aber nicht umsonst: Benutze einen Uralten Samen direkt am befriedeten Nest, um genau einen Verbuendeten zu rufen. Auch jeder weitere Ruf kostet wieder einen Uralten Samen.\n\n" +
                   "Halte in der Umgebung der Nester nach alten Steintafeln Ausschau. Einige verraten, welche Pflanzen die verschiedenen Grauzwerge bevorzugen; andere berichten von den Leibspeisen weiterer Verbuendeter.";
        }


        internal static string BuildFulingLoreText()
        {
            return "DIE DÖRFER DER EBENEN\n\n" +
                   "Ein Fuling-Dorf lässt sich nicht mit Gewalt für dich gewinnen. Lege oder wirf Geschenke in die Nähe seiner Bewohner, solange sie dir noch misstrauen. Das Vertrauen gehört dem ganzen Dorf, nicht nur einem einzelnen Fuling.\n\n" +
                   "DORFGABEN\n" +
                   "Brot -> +6 Vertrauen\n" +
                   "Gekochtes Loxfleisch -> +6 Vertrauen\n" +
                   "Moltebeeren -> +3 Vertrauen\n" +
                   "Gerste -> +3 Vertrauen\n\n" +
                   "Ab 60 Vertrauen duldet dich das Dorf kurzzeitig in seiner Nähe. Bei 100 Vertrauen gilt das Dorf als befreundet und seine Bewohner greifen dich nicht mehr an. Diebstahl, Ernten fremder Vorräte und das Beschädigen des Dorfes können Vertrauen wieder kosten.\n\n" +
                   "ERST DAS DORF, DANN DER EINZELNE\n" +
                   "Nach der Freundschaft mit dem Dorf muss jeder Arbeiter persönlich überzeugt werden. Vier Lieblingsgaben reichen für 100 persönliches Vertrauen:\n" +
                   "Fuling -> Moltebeeren\n" +
                   "Fuling-Schamane -> Brot\n" +
                   "Fuling-Berserker -> gekochtes Loxfleisch\n\n" +
                   "Erst danach schließt sich genau dieser Bewohner dir als Worker an.";
        }

        internal static string BuildGreydwarfLoreText()
        {
            return "DIE VIER GABEN DES NESTES\n\n" +
                   "Himbeeren -> Grauling / Sammler\n" +
                   "Blaubeeren -> Grauzwerg / Traeger\n" +
                   "Disteln -> Grauzwerg-Schamane / Gaertner\n" +
                   "Loewenzahn -> Grauzwerg-Berserker / Holzfaeller\n\n" +
                   "Jede Gabe erhoeht die Bindung um 5 Punkte und zugleich das dauerhafte Rufgewicht genau dieser Kreatur. Dein Mischungsverhaeltnis wird zur Spawnchance: Spendest du z.B. 60% Himbeeren und 40% Disteln, hat jeder spaetere Ruf 60% Chance auf einen Grauling und 40% auf einen Schamanen.\n\n" +
                   "Bei 100% ist das Nest befriedet und stellt seine normalen Angriffe ein. Es gibt einmalig 1x Uralten Samen frei.\n\n" +
                   "JEDER WORKER KOSTET EINEN SAMEN\n" +
                   "Benutze einen Uralten Samen direkt am befriedeten Nest. Erst dann wird ein Worker nach dem gespeicherten Chancenverhaeltnis gerufen. Das gilt bereits fuer den ersten Worker und fuer jeden weiteren Ruf. Neue Opfergaben sind nach 100% nicht mehr noetig.";
        }
    }
}
