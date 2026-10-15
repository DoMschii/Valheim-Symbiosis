using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Harvests a real mature Cultivator Pickable through its native network RPC. The Pickable
    /// itself spawns the vanilla harvest ItemDrops. The dedicated Harvester triggers the native harvest and leaves the real drops in the world.
    /// A separate GatherResources worker then owns ItemDrop -> cargo -> storage.
    /// </summary>
    internal static class GardenHarvestRuntime
    {
        internal const float HarvestRadius = 3.2f;
        private static readonly Collider[] Nearby = new Collider[96];
        private static Action<string> _debugLog;
        private static readonly FieldInfo HideWhenPickedField = typeof(Pickable).GetField("m_hideWhenPicked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo DestroyWhenPickedField = typeof(Pickable).GetField("m_destroyWhenPicked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static bool _loggedMissingPersistentPickField;

        internal static void Initialize(Action<string> debugLog) => _debugLog = debugLog;
        internal static void Shutdown() => _debugLog = null;

        internal static bool TryHarvest(Character worker, ZDO zdo)
        {
            if (worker == null || zdo == null) return false;
            if (!WorkerZdo.TryReadTarget(zdo, out _, out string targetKind, out Vector3 targetPosition) ||
                !string.Equals(targetKind, GardenHarvestTargetFinder.TargetKind, StringComparison.Ordinal))
                return false;

            Pickable pickable = FindNearbyHarvestable(zdo, targetPosition, worker.transform.position);
            if (pickable == null)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            ZNetView nview = SmartReplantBridge.FindNetworkView(pickable);
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
            {
                WorkerZdo.ClearTarget(zdo);
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
                return true;
            }

            string harvested = GardenHarvestTargetFinder.GetIdentityName(pickable);
            Transform slotTransform = nview.transform != null ? nview.transform : pickable.transform;

            // Berry bushes and SmartReplant Flora are persistent harvestables. Harvesting must
            // behave like clicking a normal berry bush: only the produce is picked, while the
            // plant stays in the world and uses its normal respawn cycle. Never turn these into a
            // crop replant slot; doing so used to destroy the bush and then schedule a replacement.
            bool managedWildGrowth = GardenTargetFinder.TryReadWildGrowthMarker(nview, out int managedSlotId, out string managedWorkAreaId);
            bool persistentFlora = managedWildGrowth ||
                                   GardenHarvestTargetFinder.IsWildGatherable(pickable) ||
                                   SmartReplantBridge.IsSmartReplantPlantingPiece(nview.gameObject);
            if (persistentFlora)
            {
                EnsurePersistentHarvestBehaviour(pickable, harvested);
                // v0.5.383 self-heal: an object planted by older builds may still have an active
                // ghost because planting completion was deliberately delayed. Its durable managed
                // marker is stronger evidence than the current picked/visible state, so close that
                // slot before RPC_Pick. The physical flora remains and respawns normally.
                if (managedWildGrowth)
                    GardenReplantPlanRuntime.ConfirmPersistentWildGrowthSlot(managedWorkAreaId, managedSlotId);
            }

            // Ordinary field crops are still harvested destructively and may enter the persistent
            // replant queue. Wild/persistent flora explicitly bypass that crop lifecycle.
            bool planKnown = !persistentFlora &&
                             (SmartReplantBridge.TryGetSeedReserveInfo(harvested, out _, out _) ||
                              GardenWorkRuntime.TryGetFallbackReplantPlan(harvested, out _, out _, out _, out _));
            bool savedSlot = planKnown && GardenReplantPlanRuntime.EnqueueFromWorker(zdo, harvested, slotTransform.position, slotTransform.eulerAngles.y);

            // Barley/Flax are self-seeding crops: the harvested item itself is the next planting
            // resource. Reserve the exact planting cost directly from THIS harvest, instead of
            // waiting for another worker to collect the drop, store it, and then fetch it again.
            // Reducing m_amount before the native RPC keeps the economy exact: one harvested unit
            // becomes planting cargo and only the remaining yield is spawned into the world.
            bool reservedSelfSeed = false;
            int originalPickAmount = Mathf.Max(0, pickable.m_amount);
            ProductionMetricsRuntime.RecordProduced(zdo, harvested, originalPickAmount);
            if (savedSlot &&
                GardenWorkRuntime.TryGetSelfSeedingReplantPlan(harvested, out string selfResource, out string selfPlant, out int selfAmount, out float selfSpacing) &&
                selfAmount > 0 && originalPickAmount >= selfAmount &&
                GardenReplantPlanRuntime.TryGetPendingAtForWorker(zdo, slotTransform.position, 0.35f, out GardenReplantSlot pendingSlot))
            {
                pickable.m_amount = originalPickAmount - selfAmount;
                WorkerZdo.AddCargoAmount(zdo, selfResource, selfAmount);
                WorkerGardenPlan directPlan = new WorkerGardenPlan(
                    pendingSlot.SlotId, pendingSlot.HarvestPrefab, selfPlant, selfResource, selfAmount,
                    selfSpacing, pendingSlot.Position, false, 0, 0, pendingSlot.RotationY);
                WorkerZdo.SetGardenPlan(zdo, directPlan);
                reservedSelfSeed = true;
            }

            // Pickable.RPC_Pick is Valheim's native network path for spawning the harvested item.
            // Zero keeps its native yield; for self-seeding crops m_amount was temporarily reduced
            // above so the reserved planting unit is not duplicated as a world drop.
            nview.InvokeRPC("RPC_Pick", 0);
            if (reservedSelfSeed) pickable.m_amount = originalPickAmount;

            // v0.5.377: an equal-priority Gardener alternates one completed Harvest action with
            // one Garden action. Record the safe-boundary handoff only after the native harvest
            // really fired; Gatherers use the same safe-boundary handoff for their Gather/Harvest tie.
            WorkerRole runtimeRole = (WorkerRole)zdo.GetInt(WorkerZdo.Role, (int)WorkerRole.None);
            if (runtimeRole == WorkerRole.Gardener || runtimeRole == WorkerRole.Gatherer)
                WorkerJobProfileRuntime.NoteCompletedAction(zdo, runtimeRole, WorkOrder.Harvest);

            if (persistentFlora && WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(harvested, out _))
            {
                // Keep a first immediate repair here. The Harmony RPC_Pick postfix below performs
                // the delayed repair after Valheim has finished applying its picked-state hierarchy.
                WorkersWildFloraRuntime.EnsurePersistentBushVisual(pickable);
            }

            LocalWorkFallbackRuntime.Clear(zdo);
            WorkerZdo.ClearTarget(zdo);
            if (reservedSelfSeed && WorkerZdo.TryReadGardenPlan(zdo, out WorkerGardenPlan immediatePlan))
            {
                // The just-harvested Barley/Flax unit is already reserved as real cargo, so do not
                // wait for another scheduler pass before handing the same worker to planting.
                if (runtimeRole == WorkerRole.Gardener)
                    zdo.Set(WorkerZdo.WorkOrderKey, (int)WorkOrder.Garden);
                WorkerZdo.WriteTarget(zdo, immediatePlan.PlantPrefab, immediatePlan.Position, GardenTargetFinder.PlantTargetKind);
                float distance = Vector3.Distance(worker.transform.position, immediatePlan.Position);
                zdo.Set(WorkerZdo.State, (int)(distance <= WorkerNavigationRuntime.TargetStopDistance ? WorkerState.Work : WorkerState.MoveToTarget));
                _debugLog?.Invoke($"Garden self-seed reserved from harvest: worker={worker.gameObject.name}, crop={harvested}, resource={immediatePlan.ResourcePrefab} x{immediatePlan.ResourceAmount}, slot={immediatePlan.SlotId}. Replant starts immediately.");
            }
            else
            {
                zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
            }
            _debugLog?.Invoke($"Harvester harvested mature crop/flora: worker={worker.gameObject.name}, crop={harvested}, smartReplant={SmartReplantBridge.IsSmartReplantHarvestable(pickable)}, savedSlot={savedSlot}, selfSeedReserved={reservedSelfSeed}. Real remaining ItemDrops are left for the separate collector job.");
            return true;
        }



        private static void EnsurePersistentHarvestBehaviour(Pickable pickable, string harvested)
        {
            if (pickable == null) return;
            bool changed = false;
            try
            {
                // Never rewrite m_hideWhenPicked. Depending on the Valheim build this field can
                // be a hierarchy reference or a boolean, and forcing it used to be able to hide
                // the complete planted bush. Preserve the prefab's native hide target/value and
                // only disable destructive picking below.

                // Compatibility with Valheim/SmartReplant generations that expose a dedicated
                // destruction toggle in addition to m_hideWhenPicked. Negative reflection results
                // are cached by the static FieldInfo, so this adds no per-harvest lookup churn.
                if (DestroyWhenPickedField != null)
                {
                    object value = DestroyWhenPickedField.GetValue(pickable);
                    if (!(value is bool destroy) || destroy)
                    {
                        DestroyWhenPickedField.SetValue(pickable, false);
                        changed = true;
                    }
                }

                if (HideWhenPickedField == null && DestroyWhenPickedField == null && !_loggedMissingPersistentPickField)
                {
                    _loggedMissingPersistentPickField = true;
                    _debugLog?.Invoke("Persistent flora harvest compatibility: Pickable exposes neither m_hideWhenPicked nor m_destroyWhenPicked; native harvest behaviour is used unchanged.");
                }
                else if (changed)
                {
                    _debugLog?.Invoke($"Persistent flora harvest protected: {harvested}; bush/flora remains and only yield is picked.");
                }
            }
            catch (Exception ex)
            {
                _debugLog?.Invoke($"Persistent flora harvest protection failed for {harvested}: {ex.Message}");
            }
        }

        private static Pickable FindNearbyHarvestable(ZDO zdo, Vector3 targetPosition, Vector3 workerPosition)
        {
            // The broad target search already uses the lifecycle index. Use the same index for the
            // final interaction lookup instead of touching surrounding building colliders again.
            return GardenHarvestTargetFinder.FindLoadedHarvestableNear(targetPosition, HarvestRadius * 1.5f, targetPosition, pickable => GatherHarvestCategoryFilterRuntime.IsHarvestPickableAllowed(zdo, pickable));
        }

        private static string NormalizePrefabName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return string.Empty;
            int clone = objectName.IndexOf("(Clone)", StringComparison.Ordinal);
            if (clone >= 0) objectName = objectName.Substring(0, clone);
            return objectName.Trim();
        }
    }

    /// <summary>
    /// Event-driven bush visual guard. It runs for worker and player harvests alike, so a freshly
    /// planted Workers bush cannot visually vanish just because Pickable.RPC_Pick toggled the
    /// source prefab hierarchy after GardenHarvestRuntime returned.
    /// </summary>
    [HarmonyPatch(typeof(Pickable), "RPC_Pick")]
    internal static class WorkersWildFloraRpcPickVisualPatch
    {
        private static void Postfix(Pickable __instance)
        {
            if (__instance == null || __instance.gameObject == null) return;
            string identity = GardenHarvestTargetFinder.GetIdentityName(__instance);
            if (!WorkersWildFloraRuntime.TryCanonicalizeLegacyIdentity(identity, out WorkersWildFloraRuntime.Definition def)) return;
            if (!string.Equals(def.Id, "Raspberry", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(def.Id, "Blueberries", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(def.Id, "Cloudberry", StringComparison.OrdinalIgnoreCase)) return;

            WorkersWildFloraRuntime.EnsurePersistentBushVisual(__instance);
            if (Plugin.Instance != null)
                Plugin.Instance.StartCoroutine(WorkersWildFloraRuntime.RestorePersistentBushVisualDelayed(__instance));
        }
    }

}
