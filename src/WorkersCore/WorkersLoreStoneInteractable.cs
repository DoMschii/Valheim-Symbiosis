using System;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal enum WorkersLorePage
    {
        GreydwarfBond = 0,
        FoodAndSupply = 1,
        FulingDiplomacy = 2
    }

    /// <summary>
    /// Lightweight read-only lore interaction for the Workers knowledge stones.  The stone stores no
    /// simulation state and performs no scans; pressing E only opens Valheim's vanilla rune viewer.
    /// </summary>
    internal sealed class WorkersLoreStoneInteractable : MonoBehaviour, Hoverable, Interactable
    {
        [SerializeField] private WorkersLorePage _page;
        [SerializeField] private string _displayName = "Wissensstein";

        internal void Configure(WorkersLorePage page, string displayName)
        {
            _page = page;
            _displayName = string.IsNullOrWhiteSpace(displayName) ? "Wissensstein" : displayName;
        }

        public string GetHoverName() => _displayName;

        public string GetHoverText()
        {
            return _displayName + "\n[<color=yellow><b>$KEY_Use</b></color>] Lesen";
        }

        public float GetHoverOffset() => 0f;

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || alt) return false;
            Player player = user as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            string topic;
            string label;
            string text;
            switch (_page)
            {
                case WorkersLorePage.FoodAndSupply:
                    topic = "Nahrung der Verbündeten";
                    label = "Workers: Nahrung & Versorgung";
                    text = WorkerSustenanceRuntime.BuildFavoriteFoodLore();
                    break;
                case WorkersLorePage.FulingDiplomacy:
                    topic = "Der Handel mit den Fulings";
                    label = "Workers: Fuling-Diplomatie";
                    text = WorkersLoreRuntime.BuildFulingLoreText();
                    break;
                default:
                    topic = "Der Bund mit den Grauzwergen";
                    label = "Workers: Grauzwerg-Nester";
                    text = WorkersLoreRuntime.BuildGreydwarfLoreText();
                    break;
            }

            try { player.AddKnownText(label, text); } catch { }

            if (TryShowRuneText(topic, text)) return true;

            try { player.Message(MessageHud.MessageType.Center, topic + "\n" + text); } catch { }
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
        private static bool TryShowRuneText(string topic, string body)
        {
            try
            {
                TextViewer viewer = TextViewer.instance;
                if (viewer == null) return false;
                MethodInfo method = null;
                MethodInfo[] methods = typeof(TextViewer).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (!string.Equals(methods[i].Name, "ShowText", StringComparison.Ordinal)) continue;
                    ParameterInfo[] candidate = methods[i].GetParameters();
                    if (candidate.Length == 4 && candidate[0].ParameterType.IsEnum) { method = methods[i]; break; }
                }
                if (method == null) return false;

                ParameterInfo[] parameters = method.GetParameters();
                object runeStyle = Enum.ToObject(parameters[0].ParameterType, 0);
                method.Invoke(viewer, new object[] { runeStyle, topic, body, true });
                return true;
            }
            catch { return false; }
        }
    }
}
