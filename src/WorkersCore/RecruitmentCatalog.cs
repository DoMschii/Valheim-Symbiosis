using System;
using System.Collections.Generic;

namespace Valheim_Workers
{
    internal enum RecruitmentMethod
    {
        DebugDirect = 0,
        PacifySpawner = 1,
        Offering = 2,
        Ritual = 3,
        RaiseYoung = 4,
        VillageDiplomacy = 5
    }

    internal enum HomeAnchorType
    {
        None = 0,
        GreydwarfNest = 1,
        TrollCamp = 2,
        FireHearth = 3,
        WatersideNest = 4,
        WolfDen = 5,
        LoxShelter = 6,
        StoneGolemShelter = 7,
        DraugrRest = 8,
        HunterStand = 9,
        DrakePerch = 10,
        FulingCamp = 11
    }

    internal sealed class RecruitmentProfile
    {
        internal readonly string Id;
        internal readonly string DisplayName;
        internal readonly RecruitmentMethod Method;
        internal readonly string SourcePrefab;
        internal readonly HomeAnchorType HomeAnchor;
        internal readonly string[] ResultSpecies;

        internal RecruitmentProfile(
            string id,
            string displayName,
            RecruitmentMethod method,
            string sourcePrefab,
            HomeAnchorType homeAnchor,
            params string[] resultSpecies)
        {
            Id = id;
            DisplayName = displayName;
            Method = method;
            SourcePrefab = sourcePrefab;
            HomeAnchor = homeAnchor;
            ResultSpecies = resultSpecies ?? Array.Empty<string>();
        }
    }

    internal static class RecruitmentCatalog
    {
        internal const string GreydwarfFamilyId = "greydwarf_family";
        internal const string TrollCaveId = "troll_cave";
        internal const string FulingFamilyId = "fuling_family";
        internal const string StoneGolemId = "stone_golem";
        internal const string SurtlingSignalFireId = "surtling_signal_fire";
        internal const string DraugrHonorId = "draugr_honor";
        internal const string NeckFishingId = "neck_fishing";

        private static readonly Dictionary<string, RecruitmentProfile> Profiles =
            new Dictionary<string, RecruitmentProfile>(StringComparer.OrdinalIgnoreCase)
            {
                [GreydwarfFamilyId] = new RecruitmentProfile(
                    GreydwarfFamilyId,
                    "Greydwarf family",
                    RecruitmentMethod.PacifySpawner,
                    "Spawner_GreydwarfNest",
                    HomeAnchorType.GreydwarfNest,
                    "Greyling",
                    "Greydwarf",
                    "Greydwarf_Elite",
                    "Greydwarf_Shaman"),
                [TrollCaveId] = new RecruitmentProfile(
                    TrollCaveId,
                    "Troll cave",
                    RecruitmentMethod.Offering,
                    "Troll",
                    HomeAnchorType.TrollCamp,
                    "Troll"),
                [FulingFamilyId] = new RecruitmentProfile(
                    FulingFamilyId,
                    "Fuling village",
                    RecruitmentMethod.VillageDiplomacy,
                    "Goblin",
                    HomeAnchorType.FulingCamp,
                    "Fuling",
                    "Fuling_Shaman",
                    "Fuling_Berserker")
,
                [StoneGolemId] = new RecruitmentProfile(
                    StoneGolemId,
                    "Stone Golem trial of strength + crystal binding",
                    RecruitmentMethod.Ritual,
                    "StoneGolem",
                    HomeAnchorType.StoneGolemShelter,
                    "StoneGolem"),
                [SurtlingSignalFireId] = new RecruitmentProfile(
                    SurtlingSignalFireId,
                    "Surtling signal-fire ritual",
                    RecruitmentMethod.Ritual,
                    "Spawner_imp_respawn",
                    HomeAnchorType.FireHearth,
                    "Surtling"),
                [DraugrHonorId] = new RecruitmentProfile(
                    DraugrHonorId,
                    "Draugr ancestor-honor ritual",
                    RecruitmentMethod.Ritual,
                    "Spawner_DraugrPile",
                    HomeAnchorType.DraugrRest,
                    "Draugr",
                    "Draugr_Ranged",
                    "Draugr_Elite")
                ,
                [NeckFishingId] = new RecruitmentProfile(
                    NeckFishingId,
                    "Neck shoreline trap + raw-fish trust",
                    RecruitmentMethod.Ritual,
                    "ValheimWorkers_NeckTrap",
                    HomeAnchorType.WatersideNest,
                    "Neck")
            };

        internal static RecruitmentProfile GreydwarfFamily => Profiles[GreydwarfFamilyId];
        internal static RecruitmentProfile TrollCave => Profiles[TrollCaveId];
        internal static RecruitmentProfile FulingFamily => Profiles[FulingFamilyId];
        internal static RecruitmentProfile StoneGolem => Profiles[StoneGolemId];
        internal static RecruitmentProfile SurtlingSignalFire => Profiles[SurtlingSignalFireId];
        internal static RecruitmentProfile DraugrHonor => Profiles[DraugrHonorId];
        internal static RecruitmentProfile NeckFishing => Profiles[NeckFishingId];

        internal static bool IsGreydwarfSpawner(string prefabName) =>
            string.Equals(
                SpeciesRoleRegistry.NormalizePrefabName(prefabName),
                GreydwarfFamily.SourcePrefab,
                StringComparison.OrdinalIgnoreCase);
    }
}
