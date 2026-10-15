using System;
using System.Collections.Generic;

namespace Valheim_Workers_DrakeForm
{
    internal sealed class SymbiosisFormProfile
    {
        internal readonly string FormId;
        internal readonly string DisplayName;
        internal readonly string NecklaceName;
        internal readonly string VisualPrefab;
        internal readonly string Capability;

        internal SymbiosisFormProfile(string formId, string displayName, string necklaceName, string visualPrefab, string capability)
        {
            FormId = formId;
            DisplayName = displayName;
            NecklaceName = necklaceName;
            VisualPrefab = visualPrefab;
            Capability = capability;
        }
    }

    /// <summary>
    /// Stable identifiers and presentation metadata for creature-specific transformation necklaces.
    /// Every necklace binds exactly one creature form; the player remains the authoritative character
    /// (HP, food, skills and inventory stay on Player). Drake provides flight; Sea Serpent provides
    /// fast water mobility; Troll is the tank/breacher; Werewolf is the high-speed land/assassin form.
    /// Forms are intentionally curated: a creature only gets a necklace when it adds a distinct gameplay
    /// niche, not simply because it exists in Valheim.
    /// </summary>
    internal static class SymbiosisFormCatalog
    {
        internal const string Drake = "Drake";
        internal const string Serpent = "Serpent";
        internal const string Troll = "Troll";
        internal const string Werewolf = "Werewolf";
        internal const string PlayerFormKey = "vw_player_symbiosis_form";

        private static readonly Dictionary<string, SymbiosisFormProfile> Profiles =
            new Dictionary<string, SymbiosisFormProfile>(StringComparer.OrdinalIgnoreCase)
            {
                [Drake] = new SymbiosisFormProfile(
                    Drake,
                    "Drachenform",
                    "Drachenhalskette",
                    "Hatchling",
                    "Flug + Frostangriffe"),
                [Serpent] = new SymbiosisFormProfile(
                    Serpent,
                    "Seeschlangenform",
                    "Seeschlangenhalskette",
                    "Serpent",
                    "schnelle Wasserbewegung + native Angriffe"),
                [Troll] = new SymbiosisFormProfile(
                    Troll,
                    "Trollform",
                    "Trollhalskette",
                    "Troll",
                    "Tank/Brecher: physische Defensive + Wankresistenz + native Trollangriffe"),
                [Werewolf] = new SymbiosisFormProfile(
                    Werewolf,
                    "Werwolfform",
                    "Werwolfhalskette",
                    "Fenring",
                    "High-Speed-Landmobilitaet + Blutjagd-Kettenkombo")
            };

        internal static bool IsKnown(string formId) => TryGet(formId, out _);

        internal static bool TryGet(string formId, out SymbiosisFormProfile profile)
        {
            profile = null;
            return !string.IsNullOrWhiteSpace(formId) && Profiles.TryGetValue(formId, out profile);
        }
    }
}
