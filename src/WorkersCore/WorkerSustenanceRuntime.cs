using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Worker sustenance / provisioning.
    ///
    /// v0.5.369 separates recruitment identity from daily food configuration. Every species exposes
    /// only a small thematic whitelist. Players may enable/disable entries and order the enabled
    /// entries as a priority list at the Worker's rest station. Rest-station stock is stored as a
    /// compact per-prefab manifest so one Worker can genuinely hold several accepted foods without
    /// polling containers or scanning the world.
    /// </summary>
    internal static class WorkerSustenanceRuntime
    {
        internal const string HomeSupplyPrefabKey = "vw_home_supply_prefab";              // preferred food / legacy compatibility
        internal const string HomeSupplyCountKey = "vw_home_supply_count";                // legacy v1 stock
        internal const string HomeSupplyAltCountKey = "vw_home_supply_alt_count";          // legacy v1 alternate stock
        internal const string HomeSupplyTargetKey = "vw_home_supply_target";
        internal const string HomeSupplyManifestKey = "vw_home_supply_stock_v2";
        internal const string HomeSupplyEnabledPrefabsKey = "vw_home_supply_enabled_v2";
        internal const string FoodSelectionKey = "vw_supply_food_select_v2";
        internal const string NextMealTicksKey = "vw_supply_next_meal_ticks";
        internal const string LastConsumedPrefabKey = "vw_supply_last_prefab";
        internal const string LastConsumedTicksKey = "vw_supply_last_ticks";
        private const string HomeSupplyMigratedKey = "vw_home_supply_v2_migrated";
        private const string BackgroundStarvedKey = "vw_supply_bg_starved";
        private const string DrakeAllRawMeatMigratedKey = "vw_supply_drake_all_raw_meat_v398";
        private const string TrollDeerStewMigratedKey = "vw_supply_troll_deer_stew_v404";

        internal static readonly TimeSpan MealInterval = TimeSpan.FromMinutes(30.0);
        internal const int DefaultTargetStock = 10;

        internal sealed class FoodOption
        {
            internal string Prefab = string.Empty;
            internal string DisplayName = string.Empty;
            internal string Tag = string.Empty;
            internal bool Enabled;
            internal int Priority;
        }

        private readonly struct FoodDefinition
        {
            internal readonly string Prefab;
            internal readonly string DisplayName;
            internal readonly string Tag;

            internal FoodDefinition(string prefab, string displayName, string tag = "")
            {
                Prefab = prefab ?? string.Empty;
                DisplayName = displayName ?? prefab ?? string.Empty;
                Tag = tag ?? string.Empty;
            }
        }

        private readonly struct SupplyDefinition
        {
            internal readonly string NeedWord;
            internal readonly string LegacyPrimary;
            internal readonly string LegacyAlternate;
            internal readonly FoodDefinition[] Foods;
            internal readonly string[] DefaultEnabled;

            internal SupplyDefinition(string legacyPrimary, string needWord, FoodDefinition[] foods, string[] defaultEnabled, string legacyAlternate = "")
            {
                LegacyPrimary = legacyPrimary ?? string.Empty;
                LegacyAlternate = legacyAlternate ?? string.Empty;
                NeedWord = needWord ?? "Nahrung";
                Foods = foods ?? Array.Empty<FoodDefinition>();
                DefaultEnabled = defaultEnabled ?? Array.Empty<string>();
            }
        }

        internal const string RoastedMushroomPrefab = "VW_RoastedMushrooms";

        private static FoodDefinition F(string prefab, string display, string tag = "") => new FoodDefinition(prefab, display, tag);

        private static readonly Dictionary<string, SupplyDefinition> SpeciesSupply =
            new Dictionary<string, SupplyDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                // Greydwarf family: individual identity food remains available, while roasted
                // mushrooms are the shared "Festmahl" for every member of the family.
                ["Greyling"] = new SupplyDefinition("Raspberry", "Nahrung",
                    new[] { F(RoastedMushroomPrefab, "Gebratene Pilze", "Festmahl"), F("Raspberry", "Himbeeren", "Leibspeise") },
                    new[] { "Raspberry", RoastedMushroomPrefab }),
                ["Greydwarf"] = new SupplyDefinition("Blueberries", "Nahrung",
                    new[] { F(RoastedMushroomPrefab, "Gebratene Pilze", "Festmahl"), F("Blueberries", "Blaubeeren", "Leibspeise") },
                    new[] { "Blueberries", RoastedMushroomPrefab }),
                ["Greydwarf_Elite"] = new SupplyDefinition("Dandelion", "Nahrung",
                    new[] { F(RoastedMushroomPrefab, "Gebratene Pilze", "Festmahl"), F("Dandelion", "Loewenzahn", "Leibspeise") },
                    new[] { "Dandelion", RoastedMushroomPrefab }),
                ["Greydwarf_Shaman"] = new SupplyDefinition("Thistle", "Nahrung",
                    new[] { F(RoastedMushroomPrefab, "Gebratene Pilze", "Festmahl"), F("Thistle", "Disteln", "Leibspeise") },
                    new[] { "Thistle", RoastedMushroomPrefab }),

                ["Draugr"] = Single("Entrails", "Innereien"),
                ["Draugr_Elite"] = Single("Entrails", "Innereien"),
                ["Draugr_Ranged"] = Single("Bloodbag", "Blutbeutel"),

                // Troll maintenance is intentionally cooked-only. Raw meat is retained solely as
                // the legacy migration identity; it is NOT in the accepted daily-food whitelist.
                ["Troll"] = new SupplyDefinition("RawMeat", "Nahrung",
                    new[]
                    {
                        // T3 identity food: the same prepared Black-Forest meal used to win over
                        // a natural cave Troll. Every accepted daily food remains cooked/prepared.
                        F("DeerStew", "Hirschragout", "Leibspeise"),
                        F("CarrotSoup", "Karottensuppe", "gekocht"),
                        F("OnionSoup", "Zwiebelsuppe", "gekocht"),
                        F("TurnipStew", "Rueben-Eintopf", "gekocht"),
                        F("MincedMeatSauce", "Hackfleischsosse", "gekocht"),
                        F("Sausages", "Wuerstchen", "gekocht"),
                        F("BlackSoup", "Schwarze Suppe", "gekocht"),
                        F("WolfSkewer", "Wolfsspiess", "gekocht"),
                        F("SerpentStew", "Schlangeneintopf", "gekocht"),
                        F("CookedLoxMeat", "gekochtes Loxfleisch", "gekocht")
                    },
                    new[] { "DeerStew" }),

                ["StoneGolem"] = Single("Stone", "Stein"),
                ["IceGolem"] = Single("Stone", "Stein"),
                ["Surtling"] = Single("Coal", "Kohle", "Brennstoff"),
                // Drakes are aggressive carnivores. They accept every vanilla raw meat that can
                // sensibly be grilled/cooked, plus raw fish. Rotten meat and prepared uncooked
                // recipes are deliberately excluded.
                ["Drake"] = new SupplyDefinition("FishRaw", "Nahrung",
                    new[]
                    {
                        F("FishRaw", "roher Fisch", "Leibspeise"),
                        F("WolfMeat", "rohes Wolfsfleisch", "roh"),
                        F("RawMeat", "rohes Wildschweinfleisch", "roh"),
                        F("DeerMeat", "rohes Hirschfleisch", "roh"),
                        F("BjornMeat", "rohes Baerenfleisch", "roh"),
                        F("NeckTail", "roher Nixenschwanz", "roh"),
                        F("LoxMeat", "rohes Loxfleisch", "roh"),
                        F("SerpentMeat", "rohes Schlangenfleisch", "roh"),
                        F("ChickenMeat", "rohes Huehnerfleisch", "roh"),
                        F("HareMeat", "rohes Hasenfleisch", "roh"),
                        F("BugMeat", "rohes Sucherfleisch", "roh"),
                        F("AsksvinMeat", "roher Asksvin-Schwanz", "roh"),
                        F("VoltureMeat", "rohes Volturenfleisch", "roh"),
                        F("BoneMawSerpentMeat", "rohes Knochenmaul-Fleisch", "roh")
                    },
                    new[]
                    {
                        "FishRaw", "WolfMeat", "RawMeat", "DeerMeat", "BjornMeat", "NeckTail",
                        "LoxMeat", "SerpentMeat", "ChickenMeat", "HareMeat", "BugMeat",
                        "AsksvinMeat", "VoltureMeat", "BoneMawSerpentMeat"
                    }, "WolfMeat"),
                ["Wolf"] = Single("RawMeat", "rohes Fleisch"),
                ["Lox"] = Single("Cloudberry", "Moltebeeren"),
                ["Neck"] = Single("FishRaw", "roher Fisch"),
                ["Fuling"] = Single("Cloudberry", "Moltebeeren"),
                ["Fuling_Shaman"] = Single("Bread", "Brot"),
                ["Fuling_Berserker"] = Single("CookedLoxMeat", "gekochtes Loxfleisch"),
                ["Dverger"] = Single("Bread", "Brot")
            };

        private static SupplyDefinition Single(string prefab, string display, string needWord = "Nahrung")
        {
            return new SupplyDefinition(prefab, needWord,
                new[] { F(prefab, display, "Leibspeise") }, new[] { prefab });
        }

        /// <summary>
        /// Human-readable reference text for the optional Workers lore stone.  It is generated from
        /// the same species food definitions that drive provisioning, so the in-world help cannot
        /// silently drift away from the actual accepted/default foods.
        /// </summary>
        internal static string BuildFavoriteFoodLore()
        {
            var rows = new (string Species, string Label)[]
            {
                ("Greyling", "Grauling"),
                ("Greydwarf", "Grauzwerg"),
                ("Greydwarf_Elite", "Grauzwerg-Berserker"),
                ("Greydwarf_Shaman", "Grauzwerg-Schamane"),
                ("Draugr", "Draugr"),
                ("Draugr_Elite", "Draugr-Elite"),
                ("Draugr_Ranged", "Draugr-Bogenschuetze"),
                ("Troll", "Troll"),
                ("StoneGolem", "Steingolem"),
                ("Surtling", "Surtling"),
                ("Drake", "Drake"),
                ("Neck", "Nixe"),
                ("Fuling", "Fuling"),
                ("Fuling_Shaman", "Fuling-Schamane"),
                ("Fuling_Berserker", "Fuling-Berserker"),
                ("Dverger", "Dvergr")
            };

            StringBuilder sb = new StringBuilder(900);
            sb.AppendLine("LEIBSPEISEN & VERSORGUNG");
            sb.AppendLine();
            for (int i = 0; i < rows.Length; i++)
            {
                if (!SpeciesSupply.TryGetValue(rows[i].Species, out SupplyDefinition def)) continue;
                string primary = ResolveLorePrimaryFood(def);
                if (string.IsNullOrEmpty(primary)) continue;

                sb.Append(rows[i].Label).Append(": ").Append(primary);
                if (string.Equals(rows[i].Species, "Drake", StringComparison.OrdinalIgnoreCase))
                    sb.Append(" (frisst zusaetzlich alle freigeschalteten rohen Fleischsorten)");
                else if (string.Equals(rows[i].Species, "Troll", StringComparison.OrdinalIgnoreCase))
                    sb.Append(" (weitere gekochte Speisen koennen am Ruheplatz freigeschaltet werden)");
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine("GEZAEHMTE TIERE");
            sb.AppendLine("Wolf: rohes Fleisch");
            sb.AppendLine("Lox: Moltebeeren");
            sb.AppendLine();
            sb.Append("Am jeweiligen Schlaf- oder Ruheplatz kannst du die erlaubten Nahrungen eines Workers aktivieren, deaktivieren und priorisieren. Die erste aktivierte Nahrung ist seine bevorzugte Versorgung.");
            return sb.ToString();
        }

        private static string ResolveLorePrimaryFood(SupplyDefinition def)
        {
            // Lore stones should reveal the creature's identity food, not a shared family feast
            // that merely happens to be first in the runtime priority list.
            if (def.Foods != null)
            {
                for (int i = 0; i < def.Foods.Length; i++)
                {
                    FoodDefinition candidate = def.Foods[i];
                    if (!string.Equals(candidate.Tag, "Leibspeise", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(candidate.Prefab))
                        return string.IsNullOrEmpty(candidate.DisplayName) ? candidate.Prefab : candidate.DisplayName;
                }
            }

            if (def.DefaultEnabled != null && def.DefaultEnabled.Length > 0)
            {
                FoodDefinition preferred = FindFood(def, def.DefaultEnabled[0]);
                if (!string.IsNullOrEmpty(preferred.Prefab))
                    return string.IsNullOrEmpty(preferred.DisplayName) ? preferred.Prefab : preferred.DisplayName;
            }

            if (def.Foods != null && def.Foods.Length > 0)
                return string.IsNullOrEmpty(def.Foods[0].DisplayName) ? def.Foods[0].Prefab : def.Foods[0].DisplayName;
            return string.Empty;
        }

        private static readonly HashSet<string> KnownSupplyPrefabs = BuildKnownSupplyPrefabs();

        private static HashSet<string> BuildKnownSupplyPrefabs()
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SupplyDefinition def in SpeciesSupply.Values)
            {
                if (def.Foods == null) continue;
                for (int i = 0; i < def.Foods.Length; i++)
                    if (!string.IsNullOrWhiteSpace(def.Foods[i].Prefab)) result.Add(def.Foods[i].Prefab);
            }
            return result;
        }

        internal static bool IsProvisioningPrefab(string prefab)
        {
            return !string.IsNullOrWhiteSpace(prefab) && KnownSupplyPrefabs.Contains(prefab);
        }

        private static bool TryGetDefinition(ZDO workerZdo, out SupplyDefinition def)
        {
            def = default;
            if (!WorkerZdo.IsWorker(workerZdo)) return false;
            string species = SpeciesRoleRegistry.NormalizePrefabName(workerZdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
            if (!string.IsNullOrEmpty(species) && SpeciesSupply.TryGetValue(species, out def)) return true;

            WorkerRole role = WorkerJobProfileRuntime.NormalizeRuntimeRole(workerZdo);
            switch (role)
            {
                case WorkerRole.Gatherer: def = Single("Raspberry", "Himbeeren"); return true;
                case WorkerRole.Butcher: def = Single("Entrails", "Innereien"); return true;
                case WorkerRole.Firekeeper: def = Single("Coal", "Kohle", "Brennstoff"); return true;
                case WorkerRole.Stonemason: def = Single("Stone", "Stein"); return true;
                case WorkerRole.Fisher: def = Single("FishRaw", "roher Fisch"); return true;
                case WorkerRole.Hunter: def = Single("RawMeat", "rohes Fleisch"); return true;
                case WorkerRole.Baker: def = Single("Bread", "Brot"); return true;
                default: return false;
            }
        }

        private static FoodDefinition FindFood(SupplyDefinition def, string prefab)
        {
            if (def.Foods != null)
            {
                for (int i = 0; i < def.Foods.Length; i++)
                    if (string.Equals(def.Foods[i].Prefab, prefab, StringComparison.OrdinalIgnoreCase)) return def.Foods[i];
            }
            return default;
        }

        private static List<string> ParseSelection(string raw)
        {
            List<string> result = new List<string>(8);
            if (string.IsNullOrWhiteSpace(raw)) return result;
            string[] parts = raw.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                string value = (parts[i] ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(value)) continue;
                bool duplicate = false;
                for (int j = 0; j < result.Count; j++)
                    if (string.Equals(result[j], value, StringComparison.OrdinalIgnoreCase)) { duplicate = true; break; }
                if (!duplicate) result.Add(value);
            }
            return result;
        }

        private static string SerializeSelection(List<string> items)
        {
            if (items == null || items.Count == 0) return string.Empty;
            return string.Join("|", items.ToArray());
        }

        private static bool IsDrake(ZDO workerZdo)
        {
            if (workerZdo == null) return false;
            string species = SpeciesRoleRegistry.NormalizePrefabName(workerZdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
            return string.Equals(species, "Drake", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTroll(ZDO workerZdo)
        {
            if (workerZdo == null) return false;
            string species = SpeciesRoleRegistry.NormalizePrefabName(workerZdo.GetString(WorkerZdo.SpeciesKey, string.Empty));
            return string.Equals(species, "Troll", StringComparison.OrdinalIgnoreCase);
        }

        private static void MigrateDrakeAllRawMeatV398(ZDO workerZdo, SupplyDefinition def, List<string> selected, bool persistDefault)
        {
            if (!persistDefault || workerZdo == null || !IsDrake(workerZdo) || workerZdo.GetBool(DrakeAllRawMeatMigratedKey, false)) return;

            // v0.5.397 and older used exactly FishRaw + WolfMeat as the untouched Drake default.
            // Expand only that untouched default (or a never-initialized Drake). If the player had
            // already disabled/reordered foods, preserve that custom selection.
            bool oldUntouchedDefault = selected.Count == 0 ||
                (selected.Count == 2 &&
                 string.Equals(selected[0], "FishRaw", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(selected[1], "WolfMeat", StringComparison.OrdinalIgnoreCase));

            if (oldUntouchedDefault)
            {
                selected.Clear();
                if (def.DefaultEnabled != null)
                {
                    for (int i = 0; i < def.DefaultEnabled.Length; i++)
                    {
                        FoodDefinition food = FindFood(def, def.DefaultEnabled[i]);
                        if (!string.IsNullOrEmpty(food.Prefab)) selected.Add(food.Prefab);
                    }
                }
                if (selected.Count > 0) workerZdo.Set(FoodSelectionKey, SerializeSelection(selected));
            }

            workerZdo.Set(DrakeAllRawMeatMigratedKey, true);
        }

        private static void MigrateTrollDeerStewV404(ZDO workerZdo, SupplyDefinition def, List<string> selected, bool persistDefault)
        {
            if (!persistDefault || workerZdo == null || !IsTroll(workerZdo) || workerZdo.GetBool(TrollDeerStewMigratedKey, false)) return;

            // v0.5.403 and older initialized an untouched Troll with only CarrotSoup. Move exactly
            // that old default to DeerStew; any custom multi-food/reordered setup is preserved.
            bool oldUntouchedDefault = selected.Count == 0 ||
                (selected.Count == 1 && string.Equals(selected[0], "CarrotSoup", StringComparison.OrdinalIgnoreCase));
            if (oldUntouchedDefault)
            {
                selected.Clear();
                FoodDefinition favorite = FindFood(def, "DeerStew");
                if (!string.IsNullOrEmpty(favorite.Prefab)) selected.Add(favorite.Prefab);
                if (selected.Count > 0) workerZdo.Set(FoodSelectionKey, SerializeSelection(selected));
            }

            workerZdo.Set(TrollDeerStewMigratedKey, true);
        }

        private static List<string> GetEnabledPrefabs(ZDO workerZdo, SupplyDefinition def, bool persistDefault)
        {
            List<string> selected = ParseSelection(workerZdo?.GetString(FoodSelectionKey, string.Empty));
            MigrateDrakeAllRawMeatV398(workerZdo, def, selected, persistDefault);
            MigrateTrollDeerStewV404(workerZdo, def, selected, persistDefault);
            if (selected.Count == 0 && def.DefaultEnabled != null)
            {
                for (int i = 0; i < def.DefaultEnabled.Length; i++)
                {
                    string prefab = def.DefaultEnabled[i];
                    if (string.IsNullOrWhiteSpace(prefab)) continue;
                    FoodDefinition food = FindFood(def, prefab);
                    if (string.IsNullOrEmpty(food.Prefab)) continue;
                    selected.Add(food.Prefab);
                }
                if (persistDefault && workerZdo != null && selected.Count > 0)
                    workerZdo.Set(FoodSelectionKey, SerializeSelection(selected));
            }

            // Remove stale/foreign values while preserving player priority order.
            for (int i = selected.Count - 1; i >= 0; i--)
            {
                FoodDefinition food = FindFood(def, selected[i]);
                if (string.IsNullOrEmpty(food.Prefab)) selected.RemoveAt(i);
                else selected[i] = food.Prefab;
            }

            if (selected.Count == 0 && def.Foods != null && def.Foods.Length > 0)
                selected.Add(def.Foods[0].Prefab);
            return selected;
        }

        internal static List<FoodOption> GetFoodOptions(ZDO workerZdo)
        {
            List<FoodOption> result = new List<FoodOption>(20);
            if (!TryGetDefinition(workerZdo, out SupplyDefinition def)) return result;
            List<string> selected = GetEnabledPrefabs(workerZdo, def, true);

            // Enabled foods first in their configured priority order.
            for (int p = 0; p < selected.Count; p++)
            {
                FoodDefinition food = FindFood(def, selected[p]);
                if (string.IsNullOrEmpty(food.Prefab)) continue;
                result.Add(new FoodOption { Prefab = food.Prefab, DisplayName = food.DisplayName, Tag = food.Tag, Enabled = true, Priority = p + 1 });
            }
            if (def.Foods != null)
            {
                for (int i = 0; i < def.Foods.Length; i++)
                {
                    FoodDefinition food = def.Foods[i];
                    bool enabled = false;
                    for (int p = 0; p < selected.Count; p++)
                        if (string.Equals(selected[p], food.Prefab, StringComparison.OrdinalIgnoreCase)) { enabled = true; break; }
                    if (enabled) continue;
                    result.Add(new FoodOption { Prefab = food.Prefab, DisplayName = food.DisplayName, Tag = food.Tag, Enabled = false, Priority = 0 });
                }
            }
            return result;
        }

        internal static bool SetFoodEnabled(ZDO workerZdo, string prefab, bool enabled)
        {
            if (!TryGetDefinition(workerZdo, out SupplyDefinition def) || string.IsNullOrWhiteSpace(prefab)) return false;
            FoodDefinition food = FindFood(def, prefab);
            if (string.IsNullOrEmpty(food.Prefab)) return false;
            List<string> selected = GetEnabledPrefabs(workerZdo, def, true);
            int found = -1;
            for (int i = 0; i < selected.Count; i++)
                if (string.Equals(selected[i], food.Prefab, StringComparison.OrdinalIgnoreCase)) { found = i; break; }

            if (enabled)
            {
                if (found < 0) selected.Add(food.Prefab);
            }
            else
            {
                if (found < 0) return false;
                if (selected.Count <= 1) return false; // A Worker must always retain one valid meal.
                selected.RemoveAt(found);
            }

            workerZdo.Set(FoodSelectionKey, SerializeSelection(selected));
            ZDO home = ResolveHomeZdo(workerZdo);
            if (home != null) SyncHomeSelection(workerZdo, home, def, selected);
            WorkerProvisioningLogisticsRuntime.NotifyWorkerSupplyChanged(workerZdo, false);
            return true;
        }

        internal static bool MoveFoodPriority(ZDO workerZdo, string prefab, int direction)
        {
            if (!TryGetDefinition(workerZdo, out SupplyDefinition def) || string.IsNullOrWhiteSpace(prefab) || direction == 0) return false;
            List<string> selected = GetEnabledPrefabs(workerZdo, def, true);
            int index = -1;
            for (int i = 0; i < selected.Count; i++)
                if (string.Equals(selected[i], prefab, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
            if (index < 0) return false;
            int next = Mathf.Clamp(index + (direction < 0 ? -1 : 1), 0, selected.Count - 1);
            if (next == index) return false;
            string tmp = selected[index];
            selected[index] = selected[next];
            selected[next] = tmp;
            workerZdo.Set(FoodSelectionKey, SerializeSelection(selected));
            ZDO home = ResolveHomeZdo(workerZdo);
            if (home != null) SyncHomeSelection(workerZdo, home, def, selected);
            WorkerProvisioningLogisticsRuntime.NotifyWorkerSupplyChanged(workerZdo, false);
            return true;
        }

        internal static string GetEnabledFoodSummary(ZDO workerZdo)
        {
            if (!TryGetDefinition(workerZdo, out SupplyDefinition def)) return string.Empty;
            List<string> selected = GetEnabledPrefabs(workerZdo, def, true);
            List<string> names = new List<string>(selected.Count);
            for (int i = 0; i < selected.Count; i++)
            {
                FoodDefinition food = FindFood(def, selected[i]);
                names.Add(string.IsNullOrEmpty(food.DisplayName) ? selected[i] : food.DisplayName);
            }
            return string.Join(" / ", names.ToArray());
        }

        internal static bool TryGetRequirement(ZDO workerZdo, out string prefab, out string displayName, out string needWord)
        {
            prefab = string.Empty;
            displayName = string.Empty;
            needWord = "Nahrung";
            if (!TryGetDefinition(workerZdo, out SupplyDefinition def)) return false;
            List<string> selected = GetEnabledPrefabs(workerZdo, def, true);
            if (selected.Count == 0) return false;
            FoodDefinition food = FindFood(def, selected[0]);
            prefab = selected[0];
            displayName = string.IsNullOrEmpty(food.DisplayName) ? GetDisplayNameForPrefab(prefab) : food.DisplayName;
            needWord = def.NeedWord;
            return !string.IsNullOrEmpty(prefab);
        }

        internal static bool IsAcceptedSupplyPrefab(ZDO workerZdo, string prefab)
        {
            if (string.IsNullOrWhiteSpace(prefab) || !TryGetDefinition(workerZdo, out SupplyDefinition def)) return false;
            List<string> selected = GetEnabledPrefabs(workerZdo, def, true);
            for (int i = 0; i < selected.Count; i++)
                if (string.Equals(selected[i], prefab, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Economy-only prediction for flexible food profiles. If an enabled food is already in
        /// the rest-station stock, that is the meal the Worker will actually consume next (priority
        /// order). Otherwise keep the configured priority-1 food as the expected demand. This reads
        /// one known Home ZDO only and never discovers/scans containers.
        /// </summary>
        internal static bool TryGetExpectedConsumptionPrefab(ZDO workerZdo, out string prefab)
        {
            prefab = string.Empty;
            if (!TryGetDefinition(workerZdo, out SupplyDefinition def)) return false;
            List<string> selected = GetEnabledPrefabs(workerZdo, def, true);
            if (selected.Count == 0) return false;

            ZDO home = ResolveHomeZdo(workerZdo);
            if (home != null)
            {
                EnsureHomeSupplyMigrated(workerZdo, home, def);
                SyncHomeSelection(workerZdo, home, def, selected);
                Dictionary<string, int> stock = ParseStockManifest(home.GetString(HomeSupplyManifestKey, string.Empty));
                for (int i = 0; i < selected.Count; i++)
                {
                    string candidate = selected[i];
                    if (stock.TryGetValue(candidate, out int count) && count > 0)
                    {
                        prefab = candidate;
                        return true;
                    }
                }
            }

            prefab = selected[0];
            return !string.IsNullOrWhiteSpace(prefab);
        }

        internal static ZDO ResolveHomeZdo(ZDO workerZdo)
        {
            if (!WorkerZdo.IsWorker(workerZdo)) return null;
            ZDOID id = WorkerZdo.ReadHomeAnchorZdoId(workerZdo);
            if (!id.Equals(ZDOID.None) && ZDOMan.instance != null)
            {
                try
                {
                    ZDO zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo != null) return zdo;
                }
                catch { }
            }
            WorkerRestStation loaded = WorkerRestStation.ResolveLoadedForWorker(workerZdo);
            if (loaded != null)
            {
                ZNetView view = WorkerRestStationFactory.ResolveNetworkView(loaded);
                if (view != null && view.IsValid()) return view.GetZDO();
            }
            return null;
        }

        internal static void EnsureInitialized(ZDO workerZdo)
        {
            if (!TryGetDefinition(workerZdo, out SupplyDefinition def)) return;
            List<string> selected = GetEnabledPrefabs(workerZdo, def, true);
            long now = DateTime.UtcNow.Ticks;
            long next = workerZdo.GetLong(NextMealTicksKey, 0L);
            if (next <= 0L) workerZdo.Set(NextMealTicksKey, now + MealInterval.Ticks);

            ZDO home = ResolveHomeZdo(workerZdo);
            if (home == null) return;
            EnsureHomeSupplyMigrated(workerZdo, home, def);
            SyncHomeSelection(workerZdo, home, def, selected);
        }

        internal static void EnsureHomeSupplyIdentity(ZDO homeZdo, string requiredPrefab)
        {
            if (homeZdo == null) return;
            if (!string.IsNullOrEmpty(requiredPrefab) && string.IsNullOrEmpty(homeZdo.GetString(HomeSupplyPrefabKey, string.Empty)))
                homeZdo.Set(HomeSupplyPrefabKey, requiredPrefab);
            int storedTarget = homeZdo.GetInt(HomeSupplyTargetKey, int.MinValue);
            if (storedTarget == int.MinValue) homeZdo.Set(HomeSupplyTargetKey, DefaultTargetStock);
        }

        private static void SyncHomeSelection(ZDO workerZdo, ZDO homeZdo, SupplyDefinition def, List<string> selected = null)
        {
            if (homeZdo == null) return;
            selected ??= GetEnabledPrefabs(workerZdo, def, true);
            string serialized = SerializeSelection(selected);
            if (!string.Equals(homeZdo.GetString(HomeSupplyEnabledPrefabsKey, string.Empty), serialized, StringComparison.Ordinal))
                homeZdo.Set(HomeSupplyEnabledPrefabsKey, serialized);
            if (selected.Count > 0 && !string.Equals(homeZdo.GetString(HomeSupplyPrefabKey, string.Empty), selected[0], StringComparison.OrdinalIgnoreCase))
                homeZdo.Set(HomeSupplyPrefabKey, selected[0]);
            EnsureHomeSupplyIdentity(homeZdo, selected.Count > 0 ? selected[0] : string.Empty);
        }

        private static Dictionary<string, int> ParseStockManifest(string raw)
        {
            Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return result;
            string[] parts = raw.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrWhiteSpace(part)) continue;
                int eq = part.IndexOf('=');
                if (eq <= 0 || eq >= part.Length - 1) continue;
                string prefab = part.Substring(0, eq).Trim();
                if (string.IsNullOrEmpty(prefab) || !int.TryParse(part.Substring(eq + 1), out int count) || count <= 0) continue;
                result.TryGetValue(prefab, out int existing);
                long total = (long)existing + count;
                result[prefab] = total >= int.MaxValue ? int.MaxValue : (int)total;
            }
            return result;
        }

        private static string SerializeStockManifest(Dictionary<string, int> stock)
        {
            if (stock == null || stock.Count == 0) return string.Empty;
            List<string> keys = new List<string>(stock.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            List<string> parts = new List<string>(keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                if (string.IsNullOrWhiteSpace(key) || !stock.TryGetValue(key, out int count) || count <= 0) continue;
                parts.Add(key + "=" + count);
            }
            return string.Join(";", parts.ToArray());
        }

        private static void SaveStock(ZDO homeZdo, Dictionary<string, int> stock)
        {
            if (homeZdo == null) return;
            homeZdo.Set(HomeSupplyManifestKey, SerializeStockManifest(stock));
        }

        private static void EnsureHomeSupplyMigrated(ZDO workerZdo, ZDO homeZdo, SupplyDefinition def)
        {
            if (homeZdo == null || homeZdo.GetBool(HomeSupplyMigratedKey, false)) return;
            Dictionary<string, int> stock = ParseStockManifest(homeZdo.GetString(HomeSupplyManifestKey, string.Empty));

            string legacyPrimary = homeZdo.GetString(HomeSupplyPrefabKey, string.Empty);
            if (string.IsNullOrEmpty(legacyPrimary)) legacyPrimary = def.LegacyPrimary;
            int primaryCount = Math.Max(0, homeZdo.GetInt(HomeSupplyCountKey, 0));
            int alternateCount = Math.Max(0, homeZdo.GetInt(HomeSupplyAltCountKey, 0));
            if (primaryCount > 0 && !string.IsNullOrEmpty(legacyPrimary)) AddStock(stock, legacyPrimary, primaryCount);
            if (alternateCount > 0 && !string.IsNullOrEmpty(def.LegacyAlternate)) AddStock(stock, def.LegacyAlternate, alternateCount);

            SaveStock(homeZdo, stock);
            homeZdo.Set(HomeSupplyCountKey, 0);
            homeZdo.Set(HomeSupplyAltCountKey, 0);
            homeZdo.Set(HomeSupplyMigratedKey, true);
        }

        private static void AddStock(Dictionary<string, int> stock, string prefab, int amount)
        {
            if (stock == null || string.IsNullOrWhiteSpace(prefab) || amount <= 0) return;
            stock.TryGetValue(prefab, out int existing);
            long total = (long)existing + amount;
            stock[prefab] = total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        internal static int GetFoodStock(ZDO workerZdo, string prefab)
        {
            if (!WorkerZdo.IsWorker(workerZdo) || string.IsNullOrWhiteSpace(prefab)) return 0;
            EnsureInitialized(workerZdo);
            ZDO home = ResolveHomeZdo(workerZdo);
            if (home == null) return 0;
            Dictionary<string, int> stock = ParseStockManifest(home.GetString(HomeSupplyManifestKey, string.Empty));
            return stock.TryGetValue(prefab, out int count) ? Math.Max(0, count) : 0;
        }

        internal static int GetStock(ZDO workerZdo)
        {
            if (!TryGetDefinition(workerZdo, out SupplyDefinition def)) return 0;
            EnsureInitialized(workerZdo);
            ZDO home = ResolveHomeZdo(workerZdo);
            if (home == null) return 0;
            Dictionary<string, int> stock = ParseStockManifest(home.GetString(HomeSupplyManifestKey, string.Empty));
            List<string> selected = GetEnabledPrefabs(workerZdo, def, true);
            int total = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                if (!stock.TryGetValue(selected[i], out int count) || count <= 0) continue;
                long sum = (long)total + count;
                total = sum >= int.MaxValue ? int.MaxValue : (int)sum;
            }
            return total;
        }

        internal static int GetHomeStock(ZDO homeZdo)
        {
            if (homeZdo == null) return 0;
            Dictionary<string, int> stock = ParseStockManifest(homeZdo.GetString(HomeSupplyManifestKey, string.Empty));
            List<string> enabled = ParseSelection(homeZdo.GetString(HomeSupplyEnabledPrefabsKey, string.Empty));
            long total = 0;
            if (enabled.Count > 0)
            {
                for (int i = 0; i < enabled.Count; i++)
                    if (stock.TryGetValue(enabled[i], out int count) && count > 0) total += count;
            }
            else
            {
                foreach (KeyValuePair<string, int> pair in stock) if (pair.Value > 0) total += pair.Value;
                total += Math.Max(0, homeZdo.GetInt(HomeSupplyCountKey, 0));
                total += Math.Max(0, homeZdo.GetInt(HomeSupplyAltCountKey, 0));
            }
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        internal static bool IsHomeSupplyEnabled(ZDO homeZdo, string prefab)
        {
            if (homeZdo == null || string.IsNullOrWhiteSpace(prefab)) return false;
            List<string> enabled = ParseSelection(homeZdo.GetString(HomeSupplyEnabledPrefabsKey, string.Empty));
            if (enabled.Count == 0)
            {
                string preferred = homeZdo.GetString(HomeSupplyPrefabKey, string.Empty);
                return string.IsNullOrEmpty(preferred) || string.Equals(preferred, prefab, StringComparison.OrdinalIgnoreCase);
            }
            for (int i = 0; i < enabled.Count; i++)
                if (string.Equals(enabled[i], prefab, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static int AddHomeSupply(ZDO homeZdo, string prefab, int amount)
        {
            if (homeZdo == null || string.IsNullOrWhiteSpace(prefab) || amount <= 0) return 0;
            Dictionary<string, int> stock = ParseStockManifest(homeZdo.GetString(HomeSupplyManifestKey, string.Empty));
            AddStock(stock, prefab, amount);
            SaveStock(homeZdo, stock);
            return amount;
        }

        internal static int GetTargetStock(ZDO workerZdo)
        {
            if (!TryGetRequirement(workerZdo, out string prefab, out _, out _)) return 0;
            ZDO home = ResolveHomeZdo(workerZdo);
            if (home == null) return DefaultTargetStock;
            EnsureHomeSupplyIdentity(home, prefab);
            return Math.Max(0, home.GetInt(HomeSupplyTargetKey, DefaultTargetStock));
        }

        internal static void SetTargetStock(ZDO workerZdo, int value)
        {
            if (!TryGetRequirement(workerZdo, out string prefab, out _, out _)) return;
            ZDO home = ResolveHomeZdo(workerZdo);
            if (home == null) return;
            EnsureInitialized(workerZdo);
            EnsureHomeSupplyIdentity(home, prefab);
            home.Set(HomeSupplyTargetKey, Mathf.Clamp(value, 0, 999));
            WorkerProvisioningLogisticsRuntime.NotifyWorkerSupplyChanged(workerZdo, false);
        }

        internal static bool IsHungry(ZDO workerZdo, long nowTicks = 0L)
        {
            if (!TryGetRequirement(workerZdo, out _, out _, out _)) return false;
            EnsureInitialized(workerZdo);
            if (nowTicks <= 0L) nowTicks = DateTime.UtcNow.Ticks;
            return nowTicks >= workerZdo.GetLong(NextMealTicksKey, nowTicks + MealInterval.Ticks);
        }

        internal static double SecondsUntilHungry(ZDO workerZdo)
        {
            if (!TryGetRequirement(workerZdo, out _, out _, out _)) return 0.0;
            EnsureInitialized(workerZdo);
            long now = DateTime.UtcNow.Ticks;
            long next = workerZdo.GetLong(NextMealTicksKey, now);
            return Math.Max(0.0, TimeSpan.FromTicks(Math.Max(0L, next - now)).TotalSeconds);
        }

        private static bool TryConsumeFromHome(ZDO workerZdo, ZDO homeZdo, long nowTicks)
        {
            if (!TryGetDefinition(workerZdo, out SupplyDefinition def) || homeZdo == null) return false;
            EnsureHomeSupplyMigrated(workerZdo, homeZdo, def);
            List<string> selected = GetEnabledPrefabs(workerZdo, def, true);
            SyncHomeSelection(workerZdo, homeZdo, def, selected);
            Dictionary<string, int> stock = ParseStockManifest(homeZdo.GetString(HomeSupplyManifestKey, string.Empty));

            string consumedPrefab = string.Empty;
            for (int i = 0; i < selected.Count; i++)
            {
                string candidate = selected[i];
                if (!stock.TryGetValue(candidate, out int count) || count <= 0) continue;
                if (count <= 1) stock.Remove(candidate); else stock[candidate] = count - 1;
                consumedPrefab = candidate;
                break;
            }
            if (string.IsNullOrEmpty(consumedPrefab)) return false;

            SaveStock(homeZdo, stock);
            ProductionMetricsRuntime.RecordConsumed(workerZdo, consumedPrefab, 1);
            workerZdo.Set(LastConsumedPrefabKey, consumedPrefab);
            workerZdo.Set(LastConsumedTicksKey, nowTicks);
            workerZdo.Set(NextMealTicksKey, nowTicks + MealInterval.Ticks);
            workerZdo.Set(BackgroundStarvedKey, false);
            WorkerProvisioningLogisticsRuntime.NotifyWorkerSupplyChanged(workerZdo, false);
            return true;
        }

        internal static bool TryEvaluatePhysical(Character worker, ZDO workerZdo, out WorkerState desiredState, out string reasonText)
        {
            desiredState = WorkerState.Idle;
            reasonText = null;
            if (worker == null || !WorkerZdo.IsWorker(workerZdo)) return false;
            if (!WorkerRestRuntime.HasAssignedHome(workerZdo)) return false;
            if (!TryGetRequirement(workerZdo, out _, out _, out string needWord)) return false;

            EnsureInitialized(workerZdo);
            long now = DateTime.UtcNow.Ticks;
            if (!IsHungry(workerZdo, now)) return false;

            Vector3 homePos = WorkerZdo.ReadHome(workerZdo, worker.transform.position);
            float distance = Vector3.Distance(worker.transform.position, homePos);
            bool atHome = distance <= WorkerRestRuntime.GetRestDistance(workerZdo);
            ZDO home = ResolveHomeZdo(workerZdo);

            WorkerState current = (WorkerState)workerZdo.GetInt(WorkerZdo.State, (int)WorkerState.Idle);
            if (current == WorkerState.Recover ||
                workerZdo.GetInt(WorkerZdo.RestReason, (int)WorkerRestReason.None) == (int)WorkerRestReason.Recovery)
                return false;

            if (atHome && TryConsumeFromHome(workerZdo, home, now))
            {
                if (current == WorkerState.WaitAtHome || current == WorkerState.ReturnHome)
                    workerZdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return false;
            }

            desiredState = atHome ? WorkerState.WaitAtHome : WorkerState.ReturnHome;
            int stock = GetStock(workerZdo);
            string allowed = GetEnabledFoodSummary(workerZdo);
            reasonText = stock <= 0
                ? $"{needWord} fehlt: wartet auf {allowed}"
                : $"hungrig -> zur Ruhestaette fuer {allowed}";
            return true;
        }

        internal static int SettleBackgroundMeals(ZDO workerZdo, long throughTicks, int maxMeals, out bool waitingForSupply)
        {
            waitingForSupply = false;
            if (!WorkerZdo.IsWorker(workerZdo) || throughTicks <= 0L || !WorkerRestRuntime.HasAssignedHome(workerZdo)) return 0;
            if (!TryGetDefinition(workerZdo, out SupplyDefinition def)) return 0;

            EnsureInitialized(workerZdo);
            long next = workerZdo.GetLong(NextMealTicksKey, throughTicks + MealInterval.Ticks);
            if (next <= 0L) next = throughTicks + MealInterval.Ticks;
            if (next > throughTicks) return 0;

            ZDO home = ResolveHomeZdo(workerZdo);
            if (home == null)
            {
                waitingForSupply = true;
                workerZdo.Set(BackgroundStarvedKey, true);
                workerZdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
                return 0;
            }

            EnsureHomeSupplyMigrated(workerZdo, home, def);
            List<string> selected = GetEnabledPrefabs(workerZdo, def, true);
            SyncHomeSelection(workerZdo, home, def, selected);
            Dictionary<string, int> stock = ParseStockManifest(home.GetString(HomeSupplyManifestKey, string.Empty));
            Dictionary<string, int> consumedByPrefab = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int consumed = 0;
            int guard = Math.Max(1, maxMeals);
            string lastPrefab = string.Empty;
            long lastMealTicks = 0L;
            bool resumingFromStarvation = workerZdo.GetBool(BackgroundStarvedKey, false);
            if (resumingFromStarvation) guard = 1;

            while (next <= throughTicks && consumed < guard)
            {
                string chosen = string.Empty;
                for (int i = 0; i < selected.Count; i++)
                {
                    string candidate = selected[i];
                    if (!stock.TryGetValue(candidate, out int count) || count <= 0) continue;
                    if (count <= 1) stock.Remove(candidate); else stock[candidate] = count - 1;
                    chosen = candidate;
                    break;
                }
                if (string.IsNullOrEmpty(chosen))
                {
                    waitingForSupply = true;
                    break;
                }

                consumedByPrefab.TryGetValue(chosen, out int existing);
                consumedByPrefab[chosen] = existing + 1;
                consumed++;
                lastPrefab = chosen;
                lastMealTicks = resumingFromStarvation ? throughTicks : next;
                next = resumingFromStarvation ? throughTicks + MealInterval.Ticks : next + MealInterval.Ticks;
            }

            if (consumed <= 0)
            {
                if (waitingForSupply)
                {
                    workerZdo.Set(BackgroundStarvedKey, true);
                    workerZdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
                }
                return 0;
            }

            SaveStock(home, stock);
            workerZdo.Set(NextMealTicksKey, next);
            workerZdo.Set(LastConsumedPrefabKey, lastPrefab);
            workerZdo.Set(LastConsumedTicksKey, lastMealTicks > 0L ? lastMealTicks : throughTicks);
            foreach (KeyValuePair<string, int> pair in consumedByPrefab)
                if (pair.Value > 0) ProductionMetricsRuntime.RecordConsumed(workerZdo, pair.Key, pair.Value);

            if (!resumingFromStarvation && next <= throughTicks)
            {
                bool any = false;
                for (int i = 0; i < selected.Count; i++)
                    if (stock.TryGetValue(selected[i], out int count) && count > 0) { any = true; break; }
                waitingForSupply = !any;
            }

            workerZdo.Set(BackgroundStarvedKey, waitingForSupply);
            if (waitingForSupply) workerZdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
            WorkerProvisioningLogisticsRuntime.NotifyWorkerSupplyChanged(workerZdo, false);
            return consumed;
        }

        internal static bool ShouldPauseBackground(ZDO workerZdo, long nowTicks, out string status)
        {
            status = null;
            if (!WorkerZdo.IsWorker(workerZdo) || !WorkerRestRuntime.HasAssignedHome(workerZdo)) return false;
            if (!TryGetRequirement(workerZdo, out _, out _, out _)) return false;
            EnsureInitialized(workerZdo);
            if (!IsHungry(workerZdo, nowTicks)) return false;
            ZDO home = ResolveHomeZdo(workerZdo);
            if (TryConsumeFromHome(workerZdo, home, nowTicks)) return false;
            workerZdo.Set(WorkerZdo.State, (int)WorkerState.ReturnHome);
            status = "versorgung:wartet auf " + GetEnabledFoodSummary(workerZdo);
            return true;
        }

        internal static int CountPlayerSupply(Player player, string prefab)
        {
            Inventory inv = player != null ? player.GetInventory() : null;
            if (inv == null || string.IsNullOrEmpty(prefab)) return 0;
            List<ItemDrop.ItemData> items = inv.GetAllItems();
            int total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_stack <= 0 || !WorkerStorageRuntime.ItemMatchesPrefab(item, prefab)) continue;
                total += item.m_stack;
            }
            return total;
        }

        internal static int CountPlayerSupply(Player player, ZDO workerZdo)
        {
            if (!TryGetDefinition(workerZdo, out SupplyDefinition def)) return 0;
            List<string> selected = GetEnabledPrefabs(workerZdo, def, true);
            int total = 0;
            for (int i = 0; i < selected.Count; i++) total += CountPlayerSupply(player, selected[i]);
            return total;
        }

        internal static int DepositFromPlayer(Player player, ZDO workerZdo, int requested)
        {
            if (player == null || requested <= 0 || !TryGetDefinition(workerZdo, out SupplyDefinition def)) return 0;
            ZDO home = ResolveHomeZdo(workerZdo);
            if (home == null) return 0;
            EnsureInitialized(workerZdo);
            List<string> selected = GetEnabledPrefabs(workerZdo, def, true);
            Inventory inv = player.GetInventory();
            if (inv == null) return 0;

            Dictionary<string, int> stock = ParseStockManifest(home.GetString(HomeSupplyManifestKey, string.Empty));
            int remaining = requested;
            int moved = 0;
            for (int s = 0; s < selected.Count && remaining > 0; s++)
            {
                string prefab = selected[s];
                List<ItemDrop.ItemData> items = inv.GetAllItems();
                for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    ItemDrop.ItemData item = items[i];
                    if (item == null || item.m_stack <= 0 || !WorkerStorageRuntime.ItemMatchesPrefab(item, prefab)) continue;
                    int take = Math.Min(remaining, item.m_stack);
                    try
                    {
                        using (WorkerInventoryMutationGuard.EnterWorkerMutation()) inv.RemoveItem(item, take);
                        AddStock(stock, prefab, take);
                        moved += take;
                        remaining -= take;
                    }
                    catch { }
                }
            }

            if (moved > 0)
            {
                SaveStock(home, stock);
                WorkerProvisioningLogisticsRuntime.NotifyWorkerSupplyChanged(workerZdo, false);
            }
            return moved;
        }

        internal static int WithdrawToPlayer(Player player, ZDO workerZdo, int requested)
        {
            if (player == null || requested <= 0 || !TryGetDefinition(workerZdo, out SupplyDefinition def)) return 0;
            ZDO home = ResolveHomeZdo(workerZdo);
            if (home == null) return 0;
            EnsureInitialized(workerZdo);
            Inventory inv = player.GetInventory();
            if (inv == null) return 0;

            Dictionary<string, int> stock = ParseStockManifest(home.GetString(HomeSupplyManifestKey, string.Empty));
            List<string> order = GetEnabledPrefabs(workerZdo, def, true);
            foreach (string key in stock.Keys)
            {
                bool exists = false;
                for (int i = 0; i < order.Count; i++)
                    if (string.Equals(order[i], key, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                if (!exists) order.Add(key);
            }

            int moved = 0;
            for (int i = 0; i < order.Count && moved < requested; i++)
            {
                string prefab = order[i];
                if (!stock.TryGetValue(prefab, out int available) || available <= 0) continue;
                int take = Math.Min(available, requested - moved);
                int added = AddPrefabToInventory(inv, prefab, take);
                if (added <= 0) continue;
                moved += added;
                int left = available - added;
                if (left <= 0) stock.Remove(prefab); else stock[prefab] = left;
            }

            if (moved > 0)
            {
                SaveStock(home, stock);
                WorkerProvisioningLogisticsRuntime.NotifyWorkerSupplyChanged(workerZdo, false);
            }
            return moved;
        }

        private static int AddPrefabToInventory(Inventory inv, string prefab, int requested)
        {
            if (inv == null || string.IsNullOrEmpty(prefab) || requested <= 0) return 0;
            GameObject itemPrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            if (itemPrefab == null && ZNetScene.instance != null) itemPrefab = ZNetScene.instance.GetPrefab(prefab);
            ItemDrop drop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
            if (drop?.m_itemData == null) return 0;
            int moved = 0;
            for (int i = 0; i < requested; i++)
            {
                ItemDrop.ItemData one = drop.m_itemData.Clone();
                one.m_stack = 1;
                if (!WorkerStorageRuntime.TryAddItem(inv, one)) break;
                moved++;
            }
            return moved;
        }

        internal static int GetMissingToTarget(ZDO workerZdo)
        {
            int target = GetTargetStock(workerZdo);
            if (target <= 0) return 0;
            return Math.Max(0, target - GetStock(workerZdo));
        }

        internal static double GetCoverageDays(ZDO workerZdo)
        {
            if (!TryGetRequirement(workerZdo, out _, out _, out _)) return 0.0;
            double activeFraction = SecondsUntilHungry(workerZdo) / Math.Max(1.0, MealInterval.TotalSeconds);
            return Math.Max(0.0, GetStock(workerZdo) + activeFraction);
        }

        internal static string FormatCoverageDays(ZDO workerZdo)
        {
            double days = GetCoverageDays(workerZdo);
            if (days < 0.05) return "< 0,1 Tag";
            if (days < 1.0) return $"{days:0.0} Tag";
            return $"{days:0.0} Tage";
        }

        // v0.5.381: strictly read-only diagnostic summary for the explicit Workzone audit.
        // Unlike the normal gameplay helpers this does not migrate defaults, initialize timers,
        // sync home settings or notify logistics. It only reads already persisted worker/home state.
        internal static string GetAuditSummary(ZDO workerZdo, out bool hungry)
        {
            hungry = false;
            if (!TryGetDefinition(workerZdo, out SupplyDefinition def)) return "kein Bedarf";

            List<string> selected = GetEnabledPrefabs(workerZdo, def, false);
            if (selected.Count == 0) return "keine Nahrung aktiviert";

            ZDO home = ResolveHomeZdo(workerZdo);
            Dictionary<string, int> stock = home != null
                ? ParseStockManifest(home.GetString(HomeSupplyManifestKey, string.Empty))
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            long total = 0;
            for (int i = 0; i < selected.Count; i++)
                if (stock.TryGetValue(selected[i], out int count) && count > 0) total += count;

            long now = DateTime.UtcNow.Ticks;
            long next = workerZdo.GetLong(NextMealTicksKey, now + MealInterval.Ticks);
            hungry = now >= next;
            double activeFraction = Math.Max(0.0, Math.Min(1.0,
                TimeSpan.FromTicks(Math.Max(0L, next - now)).TotalSeconds / Math.Max(1.0, MealInterval.TotalSeconds)));
            double coverage = Math.Max(0.0, total + activeFraction);

            List<string> names = new List<string>(selected.Count);
            for (int i = 0; i < selected.Count; i++)
            {
                FoodDefinition food = FindFood(def, selected[i]);
                names.Add(string.IsNullOrEmpty(food.DisplayName) ? selected[i] : food.DisplayName);
            }
            string foods = string.Join(" / ", names.ToArray());
            string days = coverage < 0.05 ? "< 0,1 Tag" : coverage < 1.0 ? $"{coverage:0.0} Tag" : $"{coverage:0.0} Tage";
            return $"{foods} • Vorrat {Math.Min(int.MaxValue, total)} • {days}";
        }

        internal static string BuildHomeHoverSummary(ZDO homeZdo)
        {
            if (homeZdo == null) return string.Empty;
            string preferred = homeZdo.GetString(HomeSupplyPrefabKey, string.Empty);
            string enabledRaw = homeZdo.GetString(HomeSupplyEnabledPrefabsKey, string.Empty);
            List<string> enabled = ParseSelection(enabledRaw);
            if (string.IsNullOrEmpty(preferred) && enabled.Count > 0) preferred = enabled[0];
            if (string.IsNullOrEmpty(preferred)) return string.Empty;

            int stock = GetHomeStock(homeZdo);
            int rawTarget = homeZdo.GetInt(HomeSupplyTargetKey, int.MinValue);
            int target = rawTarget == int.MinValue ? DefaultTargetStock : Math.Max(0, rawTarget);
            string display = enabled.Count > 1 ? "gemischte Nahrung" : GetDisplayNameForPrefab(preferred);
            if (target <= 0) return $"<color=#ffd166>Versorgung: {display} x{stock} • Auto-Nachfuellen AUS</color>";
            int missing = Math.Max(0, target - stock);
            if (stock <= 0) return $"<color=#ff6666>Versorgung: {display} LEER • fehlen {target}</color>";
            if (missing > 0) return $"<color=#ffd166>Versorgung: {display} {stock}/{target} • fehlen {missing} • ~{stock} Tag(e)</color>";
            return $"<color=#66ff99>Versorgung: {display} {stock}/{target} • ~{stock} Tag(e)</color>";
        }

        internal static string GetDisplayNameForPrefab(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return "Versorgung";
            foreach (KeyValuePair<string, SupplyDefinition> pair in SpeciesSupply)
            {
                SupplyDefinition def = pair.Value;
                if (def.Foods == null) continue;
                for (int i = 0; i < def.Foods.Length; i++)
                    if (string.Equals(def.Foods[i].Prefab, prefab, StringComparison.OrdinalIgnoreCase)) return def.Foods[i].DisplayName;
            }
            try
            {
                string display = CookingRecipeRuntime.DisplayIngredient(prefab);
                if (!string.IsNullOrWhiteSpace(display) && !string.Equals(display, prefab, StringComparison.OrdinalIgnoreCase)) return display;
            }
            catch { }
            return prefab;
        }

        internal static string FormatTimeUntilMeal(ZDO workerZdo)
        {
            double seconds = SecondsUntilHungry(workerZdo);
            if (seconds <= 0.5) return "HUNGRIG";
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            if (t.TotalMinutes >= 60.0) return $"satt fuer ca. {t.TotalHours:0.0} h";
            return $"satt fuer ca. {Math.Max(1.0, t.TotalMinutes):0} min";
        }
    }
}
