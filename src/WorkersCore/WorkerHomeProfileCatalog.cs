using System;
using System.Collections.Generic;

namespace Valheim_Workers
{
    internal sealed class WorkerHomeProfile
    {
        internal readonly string Species;
        internal readonly HomeAnchorType AnchorType;
        internal readonly string AnchorKind;
        internal readonly string DisplayName;
        internal readonly bool Implemented;

        internal WorkerHomeProfile(string species, HomeAnchorType anchorType, string anchorKind, string displayName, bool implemented)
        {
            Species = species;
            AnchorType = anchorType;
            AnchorKind = anchorKind;
            DisplayName = displayName;
            Implemented = implemented;
        }
    }

    internal static class WorkerHomeProfileCatalog
    {
        internal const string TrollCampKind = "TrollCamp";
        internal const string FireHearthKind = "FireHearth";
        internal const string WatersideNestKind = "WatersideNest";
        internal const string WolfDenKind = "WolfDen";
        internal const string LoxShelterKind = "LoxShelter";
        internal const string StoneGolemShelterKind = "StoneGolemShelter";
        internal const string DraugrRestKind = "DraugrRest";
        internal const string HunterStandKind = "HunterStand";
        internal const string DrakePerchKind = "DrakePerch";
        internal const string FulingCampKind = "FulingCamp";

        private static readonly Dictionary<string, WorkerHomeProfile> Profiles =
            new Dictionary<string, WorkerHomeProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Greyling"] = new WorkerHomeProfile("Greyling", HomeAnchorType.GreydwarfNest, GreydwarfHomeAnchorZdo.GreydwarfNestKind, "Grauzwerg-Wurzelnest", true),
                ["Greydwarf"] = new WorkerHomeProfile("Greydwarf", HomeAnchorType.GreydwarfNest, GreydwarfHomeAnchorZdo.GreydwarfNestKind, "Grauzwerg-Wurzelnest", true),
                ["Greydwarf_Elite"] = new WorkerHomeProfile("Greydwarf_Elite", HomeAnchorType.GreydwarfNest, GreydwarfHomeAnchorZdo.GreydwarfNestKind, "Grauzwerg-Wurzelnest", true),
                ["Greydwarf_Shaman"] = new WorkerHomeProfile("Greydwarf_Shaman", HomeAnchorType.GreydwarfNest, GreydwarfHomeAnchorZdo.GreydwarfNestKind, "Grauzwerg-Wurzelnest", true),
                ["Draugr"] = new WorkerHomeProfile("Draugr", HomeAnchorType.DraugrRest, DraugrRestKind, "Draugr-Ruhestätte", true),
                ["Draugr_Elite"] = new WorkerHomeProfile("Draugr_Elite", HomeAnchorType.DraugrRest, DraugrRestKind, "Draugr-Ruhestätte", true),
                ["Draugr_Ranged"] = new WorkerHomeProfile("Draugr_Ranged", HomeAnchorType.HunterStand, HunterStandKind, "Draugr-Jagdplattform", true),
                ["Dverger"] = new WorkerHomeProfile("Dverger", HomeAnchorType.HunterStand, HunterStandKind, "Jagdplattform", true),
                ["Troll"] = new WorkerHomeProfile("Troll", HomeAnchorType.TrollCamp, TrollCampKind, "Troll-Lehnplatz", true),
                ["StoneGolem"] = new WorkerHomeProfile("StoneGolem", HomeAnchorType.StoneGolemShelter, StoneGolemShelterKind, "Golem-Kristallstätte", true),
                ["Surtling"] = new WorkerHomeProfile("Surtling", HomeAnchorType.FireHearth, FireHearthKind, "Surtling-Ruhestätte", true),
                ["Neck"] = new WorkerHomeProfile("Neck", HomeAnchorType.WatersideNest, WatersideNestKind, "Ufernest", true),
                ["Wolf"] = new WorkerHomeProfile("Wolf", HomeAnchorType.WolfDen, WolfDenKind, "Wolfsbau (Legacy)", false),
                ["Lox"] = new WorkerHomeProfile("Lox", HomeAnchorType.LoxShelter, LoxShelterKind, "Lox-Ruheplatz (Legacy)", false),
                ["Drake"] = new WorkerHomeProfile("Drake", HomeAnchorType.DrakePerch, DrakePerchKind, "Drake-Landeplatz", true),
                ["Fuling"] = new WorkerHomeProfile("Fuling", HomeAnchorType.FulingCamp, FulingCampKind, "Fuling-Ruhestätte", true),
                ["Fuling_Shaman"] = new WorkerHomeProfile("Fuling_Shaman", HomeAnchorType.FulingCamp, FulingCampKind, "Fuling-Ruhestätte", true),
                ["Fuling_Berserker"] = new WorkerHomeProfile("Fuling_Berserker", HomeAnchorType.FulingCamp, FulingCampKind, "Fuling-Ruhestätte", true)
            };

        internal static bool TryGet(string species, out WorkerHomeProfile profile) =>
            Profiles.TryGetValue(SpeciesRoleRegistry.NormalizePrefabName(species), out profile);

        internal static IEnumerable<WorkerHomeProfile> GetAll() => Profiles.Values;
    }
}
