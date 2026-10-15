using UnityEngine;

namespace Valheim_Workers
{
    internal static class SpawnerRecruitmentZdo
    {
        internal const int CurrentSchema = 6;
        internal const int DefaultRequiredProgress = 100;

        internal const string Schema = "vw_recruit_schema";
        internal const string ProfileId = "vw_recruit_profile";
        internal const string PacifyProgress = "vw_pacify_progress";
        internal const string PacifyRequired = "vw_pacify_required";
        internal const string Pacified = "vw_pacified";
        // Historical name kept for save compatibility. It now means that the initial 100%-ritual
        // has released its first Worker; the pacified nest itself remains a permanent caller.
        internal const string Consumed = "vw_recruit_consumed";
        internal const string LastOfferingPlayerId = "vw_last_offering_player";
        internal const string SelectedSpecies = "vw_recruit_selected_species";
        internal const string ReleasedSpecies = "vw_recruit_released_species";
        internal const string ReleasedPlayerId = "vw_recruit_released_player";
        internal const string HomeSeedGranted = "vw_home_seed_granted";
        internal const string HomeSeedGrantedPlayerId = "vw_home_seed_granted_player";
        internal const string GathererWeight = "vw_recruit_weight_gatherer";
        internal const string HaulerWeight = "vw_recruit_weight_hauler";
        internal const string BerserkerWeight = "vw_recruit_weight_berserker";
        internal const string ShamanWeight = "vw_recruit_weight_shaman";
        internal const string RepeatSummonCount = "vw_recruit_repeat_summons";

        internal static int ReadProgress(ZDO zdo) => zdo?.GetInt(PacifyProgress, 0) ?? 0;
        internal static int ReadRequired(ZDO zdo) => zdo?.GetInt(PacifyRequired, DefaultRequiredProgress) ?? DefaultRequiredProgress;
        internal static bool IsPacified(ZDO zdo) => zdo != null && zdo.GetBool(Pacified, false);
        internal static bool IsConsumed(ZDO zdo) => zdo != null && zdo.GetBool(Consumed, false);
        internal static string ReadSelectedSpecies(ZDO zdo) => zdo?.GetString(SelectedSpecies, string.Empty) ?? string.Empty;
        internal static string ReadReleasedSpecies(ZDO zdo) => zdo?.GetString(ReleasedSpecies, string.Empty) ?? string.Empty;
        internal static bool IsHomeSeedGranted(ZDO zdo) => zdo != null && zdo.GetBool(HomeSeedGranted, false);

        internal static int AddProgress(ZDO zdo, int amount, long playerId, string profileId)
        {
            return AddProgressAndOffering(zdo, amount, GreydwarfNestOfferingKind.None, playerId, profileId, out _);
        }

        internal static int AddProgressAndOffering(
            ZDO zdo,
            int amount,
            GreydwarfNestOfferingKind kind,
            long playerId,
            string profileId,
            out int creditedProgress)
        {
            creditedProgress = 0;
            if (zdo == null || amount <= 0) return ReadProgress(zdo);

            MigrateIncompleteOfferingMix(zdo);
            int required = Mathf.Max(1, ReadRequired(zdo));
            int current = Mathf.Clamp(ReadProgress(zdo), 0, required);
            int next = Mathf.Clamp(current + amount, 0, required);
            creditedProgress = Mathf.Max(0, next - current);

            zdo.Set(Schema, CurrentSchema);
            zdo.Set(ProfileId, profileId ?? string.Empty);
            zdo.Set(PacifyRequired, required);
            zdo.Set(PacifyProgress, next);
            zdo.Set(LastOfferingPlayerId, playerId);

            if (creditedProgress > 0)
            {
                string key = GetWeightKey(kind);
                if (!string.IsNullOrEmpty(key))
                    zdo.Set(key, Mathf.Max(0, zdo.GetInt(key, 0)) + creditedProgress);
            }

            if (next >= required)
                zdo.Set(Pacified, true);

            return next;
        }

        internal static int GetOfferingWeight(ZDO zdo, GreydwarfNestOfferingKind kind)
        {
            if (zdo == null) return 0;
            string key = GetWeightKey(kind);
            return string.IsNullOrEmpty(key) ? 0 : Mathf.Max(0, zdo.GetInt(key, 0));
        }

        internal static int GetTotalOfferingWeight(ZDO zdo)
        {
            return GetOfferingWeight(zdo, GreydwarfNestOfferingKind.Gatherer) +
                   GetOfferingWeight(zdo, GreydwarfNestOfferingKind.Hauler) +
                   GetOfferingWeight(zdo, GreydwarfNestOfferingKind.Berserker) +
                   GetOfferingWeight(zdo, GreydwarfNestOfferingKind.Shaman);
        }

        internal static void RecordRepeatSummon(ZDO zdo)
        {
            if (zdo == null) return;
            zdo.Set(Schema, CurrentSchema);
            zdo.Set(RepeatSummonCount, Mathf.Max(0, zdo.GetInt(RepeatSummonCount, 0)) + 1);
        }

        internal static void WriteSelectedSpecies(ZDO zdo, string prefabName)
        {
            if (zdo == null || string.IsNullOrEmpty(prefabName)) return;
            zdo.Set(Schema, CurrentSchema);
            zdo.Set(SelectedSpecies, prefabName);
        }

        internal static void MarkHomeSeedGranted(ZDO zdo, long playerId)
        {
            if (zdo == null) return;
            zdo.Set(Schema, CurrentSchema);
            zdo.Set(HomeSeedGranted, true);
            zdo.Set(HomeSeedGrantedPlayerId, playerId);
        }

        internal static void MarkConsumed(ZDO zdo, string releasedSpecies, long playerId)
        {
            if (zdo == null) return;
            zdo.Set(Schema, CurrentSchema);
            zdo.Set(Pacified, true);
            zdo.Set(Consumed, true);
            zdo.Set(ReleasedSpecies, releasedSpecies ?? string.Empty);
            zdo.Set(ReleasedPlayerId, playerId);
        }

        private static void MigrateIncompleteOfferingMix(ZDO zdo)
        {
            if (zdo == null) return;
            int schema = zdo.GetInt(Schema, 0);
            if (schema >= CurrentSchema) return;

            // The pre-403 mix grouped berries and used resin/seeds, so its three weights cannot
            // be translated honestly into the new four-food system. Completed nests are preserved
            // for save compatibility; an unfinished test ritual starts clean on its next donation.
            if (!IsConsumed(zdo) && !IsPacified(zdo) && ReadProgress(zdo) > 0)
            {
                zdo.Set(PacifyProgress, 0);
                zdo.Set(GathererWeight, 0);
                zdo.Set(HaulerWeight, 0);
                zdo.Set(BerserkerWeight, 0);
                zdo.Set(ShamanWeight, 0);
                zdo.Set(SelectedSpecies, string.Empty);
            }

            zdo.Set(Schema, CurrentSchema);
        }

        private static string GetWeightKey(GreydwarfNestOfferingKind kind)
        {
            switch (kind)
            {
                case GreydwarfNestOfferingKind.Gatherer: return GathererWeight;
                case GreydwarfNestOfferingKind.Hauler: return HaulerWeight;
                case GreydwarfNestOfferingKind.Berserker: return BerserkerWeight;
                case GreydwarfNestOfferingKind.Shaman: return ShamanWeight;
                default: return string.Empty;
            }
        }
    }
}
