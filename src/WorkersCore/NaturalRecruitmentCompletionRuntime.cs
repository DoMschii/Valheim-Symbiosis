using System;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Shared completion path for real in-world recruitment routes.
    ///
    /// The recruitment minigames/rituals stay species-specific, but the final hand-off to the Worker
    /// runtime should always write the same durable identity before any downstream system sees the
    /// creature. This avoids subtle differences between Troll/Golem/Fuling/etc. after recruitment.
    ///
    /// This helper is deliberately interaction/event driven. It performs no scene/world scan.
    /// </summary>
    internal static class NaturalRecruitmentCompletionRuntime
    {
        internal static void Complete(
            Character character,
            ZDO zdo,
            long ownerPlayerId,
            WorkerRole role,
            string species,
            Vector3 initialHome,
            float workRadius,
            bool minimumDefensive,
            Player followPlayer = null)
        {
            if (character == null || zdo == null || ownerPlayerId == 0L) return;

            string canonicalSpecies = SpeciesRoleRegistry.NormalizePrefabName(species);
            if (string.IsNullOrEmpty(canonicalSpecies) && character.gameObject != null)
                canonicalSpecies = SpeciesRoleRegistry.NormalizePrefabName(character.gameObject.name);

            WorkerZdo.WriteInitialState(
                zdo,
                ownerPlayerId,
                role,
                initialHome,
                Mathf.Max(5f, workRadius),
                progressionEligible: true);

            if (!string.IsNullOrEmpty(canonicalSpecies))
                zdo.Set(WorkerZdo.SpeciesKey, canonicalSpecies);

            try { character.SetTamed(true); } catch { }

            WorkerStance stance = WorkerCombatStanceRuntime.ReadStance(zdo);
            if (minimumDefensive && stance == WorkerStance.Passive)
                stance = WorkerStance.Defensive;

            WorkerCombatStanceRuntime.SetStance(character, zdo, stance);
            WorkerCombatStanceRuntime.ClearWorkAttackTargets(character.GetComponent<MonsterAI>());
            WorkerFastLookupRuntime.RegisterKnownWorker(character, zdo);

            if (followPlayer == null || followPlayer.GetPlayerID() != ownerPlayerId) return;

            // A newly recruited creature is usually far away from the player's base. Until a real
            // rest station is assigned, make the normal Worker Follow state the transport mechanism.
            // This is the same persisted state already used by the Neck/Drake recruitment routes.
            WorkerZdo.ClearTarget(zdo);
            zdo.Set(WorkerZdo.State, (int)WorkerState.Follow);

            MonsterAI ai = character.GetComponent<MonsterAI>() ?? character.GetComponentInChildren<MonsterAI>();
            if (ai != null)
                WorkerNavigationRuntime.SetNativeFollowTarget(ai, followPlayer.gameObject);
        }
    }
}
