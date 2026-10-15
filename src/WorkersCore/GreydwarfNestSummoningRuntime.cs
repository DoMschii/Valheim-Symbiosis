using System;
using UnityEngine;

namespace Valheim_Workers
{
    internal static class GreydwarfNestSummoningRuntime
    {
        internal const string AncientSeedPrefab = "AncientSeed";
        internal const string GathererSpecies = "Greyling";
        internal const string HaulerSpecies = "Greydwarf";
        internal const string BerserkerSpecies = "Greydwarf_Elite";
        internal const string ShamanSpecies = "Greydwarf_Shaman";

        internal static bool IsAncientSeed(ItemDrop.ItemData item)
        {
            if (item?.m_dropPrefab == null) return false;
            string prefab = SpeciesRoleRegistry.NormalizePrefabName(item.m_dropPrefab.name);
            return string.Equals(prefab, AncientSeedPrefab, StringComparison.OrdinalIgnoreCase);
        }

        internal static string ChooseSpecies(ZDO zdo)
        {
            int gatherer = SpawnerRecruitmentZdo.GetOfferingWeight(zdo, GreydwarfNestOfferingKind.Gatherer);
            int hauler = SpawnerRecruitmentZdo.GetOfferingWeight(zdo, GreydwarfNestOfferingKind.Hauler);
            int berserker = SpawnerRecruitmentZdo.GetOfferingWeight(zdo, GreydwarfNestOfferingKind.Berserker);
            int shaman = SpawnerRecruitmentZdo.GetOfferingWeight(zdo, GreydwarfNestOfferingKind.Shaman);
            int total = gatherer + hauler + berserker + shaman;

            if (total > 0)
            {
                int roll = UnityEngine.Random.Range(0, total);
                if (roll < gatherer) return GathererSpecies;
                roll -= gatherer;
                if (roll < hauler) return HaulerSpecies;
                roll -= hauler;
                if (roll < berserker) return BerserkerSpecies;
                return ShamanSpecies;
            }

            // Save-migration fallback for already completed nests from older builds.
            string selected = SpawnerRecruitmentZdo.ReadSelectedSpecies(zdo);
            if (IsSummonSpecies(selected)) return selected;
            string released = SpawnerRecruitmentZdo.ReadReleasedSpecies(zdo);
            if (IsSummonSpecies(released)) return released;
            return string.Empty;
        }

        internal static bool IsSummonSpecies(string prefabName)
        {
            return string.Equals(prefabName, GathererSpecies, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(prefabName, HaulerSpecies, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(prefabName, BerserkerSpecies, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(prefabName, ShamanSpecies, StringComparison.OrdinalIgnoreCase);
        }

        internal static string BuildChanceText(ZDO zdo)
        {
            int gatherer = SpawnerRecruitmentZdo.GetOfferingWeight(zdo, GreydwarfNestOfferingKind.Gatherer);
            int hauler = SpawnerRecruitmentZdo.GetOfferingWeight(zdo, GreydwarfNestOfferingKind.Hauler);
            int berserker = SpawnerRecruitmentZdo.GetOfferingWeight(zdo, GreydwarfNestOfferingKind.Berserker);
            int shaman = SpawnerRecruitmentZdo.GetOfferingWeight(zdo, GreydwarfNestOfferingKind.Shaman);
            int total = gatherer + hauler + berserker + shaman;
            if (total <= 0) return "Rufchancen: noch nicht festgelegt";

            float g = gatherer * 100f / total;
            float h = hauler * 100f / total;
            float b = berserker * 100f / total;
            float s = shaman * 100f / total;
            return $"Rufchance: Grauling {g:0.#}% | Grauzwerg {h:0.#}% | Berserker {b:0.#}% | Schamane {s:0.#}%";
        }

        internal static string GetSpeciesDisplayName(string species)
        {
            if (string.Equals(species, GathererSpecies, StringComparison.OrdinalIgnoreCase)) return "Grauling-Sammler";
            if (string.Equals(species, HaulerSpecies, StringComparison.OrdinalIgnoreCase)) return "Grauzwerg-Traeger";
            if (string.Equals(species, BerserkerSpecies, StringComparison.OrdinalIgnoreCase)) return "Grauzwerg-Berserker";
            if (string.Equals(species, ShamanSpecies, StringComparison.OrdinalIgnoreCase)) return "Grauzwerg-Schamane";
            return species ?? string.Empty;
        }
    }
}
