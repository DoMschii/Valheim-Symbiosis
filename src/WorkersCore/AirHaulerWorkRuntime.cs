using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using ItemData = ItemDrop.ItemData;

namespace Valheim_Workers;

internal static class AirHaulerWorkRuntime
{
	private sealed class AirDropVisualState
	{
		internal GameObject Visual;

		internal float StartedAt;

		internal Vector3 Start;

		internal Vector3 End;

		internal string Prefab;

		internal int Amount;

		internal string TargetKind;

		internal int DestinationInstanceId;
	}

	internal const string OutpostSourceTargetKind = "airhaul_outpost_source";

	internal const string BaseSourceTargetKind = "airhaul_base_source";

	internal const string BaseDepositTargetKind = "airhaul_base_deposit";

	internal const string OutpostDepositTargetKind = "airhaul_outpost_deposit";

	internal const string ReturnHomeTargetKind = "airhaul_return_home";

	internal const string ReturnHomeTargetName = "__airhaul_home__";

	internal const string RouteInbound = "airhaul_inbound";

	internal const string RouteOutbound = "airhaul_outbound";

	internal const string SortInboundSourceTargetKind = "airsort_inbound_source";

	internal const string SortOutboundSourceTargetKind = "airsort_outbound_source";

	internal const string SortBaseDepositTargetKind = "airsort_base_deposit";

	internal const string RouteSortInbound = "airsort_inbound";

	internal const string RouteSortOutbound = "airsort_outbound";

	private static readonly Dictionary<int, AirDropVisualState> AirDropStates = new Dictionary<int, AirDropVisualState>();

	private const float AirDropDuration = 0.9f;

	private const float AirDropArcHeight = 0.55f;

	private static readonly List<string> _outpostRequestScratch = new List<string>(32);

	private static readonly Dictionary<string, int> _outpostDemandScratch = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private static readonly List<string> _networkRequestScratch = new List<string>(32);

	private static readonly List<string> _requestScratch = new List<string>(32);

	private static readonly List<string> _persistentInboundScratch = new List<string>(32);

	private static readonly List<string> _provisionRequestScratch = new List<string>(32);

	private static GameObject CreateCargoDropVisual(Vector3 position, string prefabName)
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Expected O, but got Unknown
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0091: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_012d: Unknown result type (might be due to invalid IL or missing references)
		//IL_014d: Unknown result type (might be due to invalid IL or missing references)
		//IL_015e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0168: Unknown result type (might be due to invalid IL or missing references)
		GameObject val = new GameObject("ValheimWorkers_AirCargoVisual");
		val.transform.position = position;
		GameObject val2 = null;
		if (!string.IsNullOrEmpty(prefabName) && (Object)(object)ObjectDB.instance != (Object)null)
		{
			val2 = ObjectDB.instance.GetItemPrefab(prefabName);
		}
		if ((Object)(object)val2 == (Object)null && !string.IsNullOrEmpty(prefabName) && (Object)(object)ZNetScene.instance != (Object)null)
		{
			val2 = ZNetScene.instance.GetPrefab(prefabName);
		}
		GameObject val3 = (((Object)(object)val2 != (Object)null) ? StaticVisualCloneRuntime.Create(val2, val.transform, "CargoItem") : null);
		if ((Object)(object)val3 != (Object)null)
		{
			val3.transform.localPosition = Vector3.zero;
			val3.transform.localRotation = Quaternion.Euler(18f, 25f, -12f);
			val3.transform.localScale = Vector3.one * 0.72f;
			val3.SetActive(true);
			return val;
		}
		GameObject val4 = (((Object)(object)ZNetScene.instance != (Object)null) ? ZNetScene.instance.GetPrefab("piece_chest_wood") : null);
		GameObject val5 = (((Object)(object)val4 != (Object)null) ? StaticVisualCloneRuntime.Create(val4, val.transform, "CargoBundle") : null);
		if ((Object)(object)val5 != (Object)null)
		{
			val5.transform.localPosition = Vector3.zero;
			val5.transform.localRotation = Quaternion.Euler(0f, 25f, 0f);
			val5.transform.localScale = Vector3.one * 0.3f;
			val5.SetActive(true);
		}
		else
		{
			((Object)val).name = ((Object)val).name + "_NoVisualPrefab";
		}
		return val;
	}

	private static void ClearAirDropState(Character worker)
	{
		if ((Object)(object)worker == (Object)null)
		{
			return;
		}
		int instanceID = ((Object)worker).GetInstanceID();
		if (AirDropStates.TryGetValue(instanceID, out var value))
		{
			if ((Object)(object)value.Visual != (Object)null)
			{
				Object.Destroy((Object)(object)value.Visual);
			}
			AirDropStates.Remove(instanceID);
		}
	}

	private static bool TickAirDropAnimation(Character worker, ZDO zdo, Container destination, string prefab, int amount, string targetKind)
	{
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_015b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0161: Unknown result type (might be due to invalid IL or missing references)
		//IL_0167: Unknown result type (might be due to invalid IL or missing references)
		//IL_016c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0196: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)worker == (Object)null || zdo == null || (Object)(object)destination == (Object)null || string.IsNullOrEmpty(prefab) || amount <= 0)
		{
			return false;
		}
		int instanceID = ((Object)worker).GetInstanceID();
		int instanceID2 = ((Object)destination).GetInstanceID();
		if (!AirDropStates.TryGetValue(instanceID, out var value) || value == null || value.DestinationInstanceId != instanceID2 || !string.Equals(value.Prefab, prefab, StringComparison.OrdinalIgnoreCase) || value.Amount != amount || !string.Equals(value.TargetKind, targetKind, StringComparison.Ordinal))
		{
			ClearAirDropState(worker);
			Vector3 val = ((Component)worker).transform.position + Vector3.down * 0.35f;
			Vector3 end = ((Component)destination).transform.position + Vector3.up * 0.62f;
			value = new AirDropVisualState
			{
				Visual = CreateCargoDropVisual(val, prefab),
				StartedAt = Time.time,
				Start = val,
				End = end,
				Prefab = prefab,
				Amount = amount,
				TargetKind = targetKind,
				DestinationInstanceId = instanceID2
			};
			AirDropStates[instanceID] = value;
			WorkerStorageRuntime.SetDebug(worker, $"air drop started: {prefab} x{amount}");
			return false;
		}
		float num = Mathf.Clamp01((Time.time - value.StartedAt) / 0.9f);
		if ((Object)(object)value.Visual != (Object)null)
		{
			Vector3 position = Vector3.Lerp(value.Start, value.End, num);
			position.y += Mathf.Sin(num * MathF.PI) * 0.55f;
			value.Visual.transform.position = position;
			value.Visual.transform.Rotate(95f * Time.deltaTime, 125f * Time.deltaTime, 70f * Time.deltaTime, (Space)1);
		}
		if (num < 1f)
		{
			return false;
		}
		ClearAirDropState(worker);
		return true;
	}

	internal static bool IsAirHauler(Character worker)
	{
		if ((Object)(object)worker != (Object)null)
		{
			return string.Equals(SpeciesRoleRegistry.NormalizePrefabName(((Object)((Component)worker).gameObject).name), "Drake", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	internal static bool HasAssignedAirDepot(Character worker, ZDO zdo, out Container depot, out Vector3 position)
	{
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
		depot = null;
		position = default(Vector3);
		if ((Object)(object)worker == (Object)null || zdo == null || !WorkerZdo.TryReadWorkArea(zdo, out var _, out var center, out var radius))
		{
			return false;
		}
		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		float best = float.MaxValue;
		Container foundDepot = null;
		Vector3 foundPosition = default(Vector3);
		WorkerStorageRuntime.ForEachContainerNear(center, Mathf.Clamp(radius + 8f, 16f, 180f), delegate(Container c)
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_003b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0041: Unknown result type (might be due to invalid IL or missing references)
			//IL_0046: Unknown result type (might be due to invalid IL or missing references)
			//IL_004b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0073: Unknown result type (might be due to invalid IL or missing references)
			//IL_0078: Unknown result type (might be due to invalid IL or missing references)
			if (IsUsableAirDepot(c, ownerId) && WorkerContainerRoleRuntime.GetAirDepotMode(c) == AirDepotMode.Outpost && WorkerLogisticsRuntime.IsInsideAssignedOutpost(zdo, ((Component)c).transform.position))
			{
				Vector3 val = ((Component)c).transform.position - center;
				float sqrMagnitude = val.sqrMagnitude;
				if (!(sqrMagnitude >= best))
				{
					best = sqrMagnitude;
					foundDepot = c;
					foundPosition = ((Component)c).transform.position;
				}
			}
		});
		depot = foundDepot;
		position = foundPosition;
		return (Object)(object)depot != (Object)null;
	}

	internal static bool HasAssignedAirDepotEndpoint(Character worker, ZDO zdo, out Vector3 position)
	{
		position = default(Vector3);
		if ((Object)(object)worker == (Object)null || zdo == null || !WorkerZdo.TryReadWorkArea(zdo, out var _, out var center, out var radius))
		{
			return false;
		}
		if (HasAssignedAirDepot(worker, zdo, out var _, out var position2))
		{
			position = position2;
			return true;
		}

		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		float allowed = Mathf.Max(4f, radius) + 4f;
		float allowedSqr = allowed * allowed;
		float best = float.MaxValue;
		bool found = false;
		Vector3 foundPosition = default(Vector3);
		WorkerProvisioningLogisticsRuntime.ForEachCachedOutpostAirHub((candidate, owner, p) =>
		{
			if (candidate == null || (owner != 0L && ownerId != 0L && owner != ownerId)) return;
			float dx = p.x - center.x;
			float dz = p.z - center.z;
			float sqr = dx * dx + dz * dz;
			if (sqr > allowedSqr || sqr >= best) return;
			best = sqr;
			found = true;
			foundPosition = p;
		});
		position = foundPosition;
		return found;
	}

	internal static bool TryFindInboundSource(Character worker, ZDO zdo, out Container source, out string prefab, out int amount, out Vector3 position)
	{
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_012b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		source = null;
		prefab = string.Empty;
		amount = 0;
		position = default(Vector3);
		if (!HasAssignedAirDepot(worker, zdo, out var depot, out var _))
		{
			return false;
		}
		Inventory inventory = depot.GetInventory();
		if (inventory == null)
		{
			return false;
		}
		long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
		ZDO depotZdo = WorkerContainerRoleRuntime.GetZdo(depot);
		ZDOID depotId = depotZdo != null ? depotZdo.m_uid : ZDOID.None;
		int num = 0;
		string text = string.Empty;
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<ItemData> allItems = inventory.GetAllItems();
		for (int i = 0; i < allItems.Count; i++)
		{
			ItemData val = allItems[i];
			if (val == null || val.m_stack <= 0)
			{
				continue;
			}
			string prefabName = WorkerStorageRuntime.GetPrefabName(val);
			if (string.IsNullOrEmpty(prefabName) || !hashSet.Add(prefabName))
			{
				continue;
			}
			int num2 = WorkerStorageRuntime.CountPrefab(inventory, prefabName);
			int num3 = Mathf.Max(WorkerContainerRoleRuntime.ProtectLastItem(depot) ? 1 : 0, Mathf.Max(WorkerLogisticsRuntime.GetRequestedStock(depot, prefabName), FieldDepotStockRuleRuntime.GetTransportReserve(depot, prefabName)));
			int num4 = Mathf.Max(0, num2 - num3);
			if (!depotId.Equals(ZDOID.None))
			{
				int claimedByOtherDrakes = BackgroundWorkerScheduler.GetAirInboundPickupClaimAmountForSource(ownerId, prefabName, depotId, zdo.m_uid);
				num4 = Mathf.Max(0, num4 - claimedByOtherDrakes);
			}
			if (num4 > 0)
			{
				int carryCapacity = HaulerWorkRuntime.GetCarryCapacity(worker, prefabName, num4);
				int baseAirDepotCapacity = GetBaseAirDepotCapacity(worker, zdo, prefabName);
				carryCapacity = Mathf.Min(carryCapacity, baseAirDepotCapacity);
				if (carryCapacity > num)
				{
					num = carryCapacity;
					text = prefabName;
				}
			}
		}
		if (num <= 0)
		{
			return false;
		}
		source = depot;
		prefab = text;
		amount = num;
		position = ((Component)depot).transform.position;
		BackgroundWorkerScheduler.SetAirEmptyPickupClaim(zdo, prefab, amount, depotId);
		return true;
	}

	internal static bool TryFindOutboundSource(Character worker, ZDO zdo, out Container source, out string prefab, out int amount, out Vector3 position, out Vector3 outpostPosition)
	{
		source = null;
		prefab = string.Empty;
		amount = 0;
		position = default(Vector3);
		outpostPosition = default(Vector3);
		if ((Object)(object)worker == (Object)null || zdo == null || !TryGetAirOutpostRequest(worker, zdo, out var requestedOutpost, out var requestedPrefab, out var need))
		{
			return false;
		}

		long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
		float best = float.MaxValue;
		Container bestSource = null;
		ZDOID bestSourceId = ZDOID.None;
		int bestAmount = 0;
		AdvancedFieldDepotRuntime.ForEachLoadedDepot(delegate(Container c)
		{
			if (!IsUsableAirDepot(c, ownerId) || WorkerContainerRoleRuntime.GetAirDepotMode(c) != AirDepotMode.BaseHub ||
				WorkerLogisticsRuntime.IsInsideAssignedOutpost(zdo, ((Component)c).transform.position)) return;

			ZDO sourceZdo = WorkerContainerRoleRuntime.GetZdo(c);
			ZDOID sourceId = sourceZdo != null ? sourceZdo.m_uid : ZDOID.None;
			int stock = WorkerStorageRuntime.CountPrefab(c.GetInventory(), requestedPrefab);
			if (stock <= 0) return;
			int claimedAtSource = BackgroundWorkerScheduler.GetAirEmptyPickupClaimAmountForSource(ownerId, requestedPrefab, sourceId, zdo.m_uid);
			int sourceAvailable = Mathf.Max(0, stock - claimedAtSource);
			if (sourceAvailable <= 0) return;

			int movable = Mathf.Min(HaulerWorkRuntime.GetCarryCapacity(worker, requestedPrefab, sourceAvailable), need);
			if (movable <= 0) return;
			Vector3 delta = ((Component)c).transform.position - ((Component)worker).transform.position;
			float distanceSqr = delta.sqrMagnitude;
			if (distanceSqr >= best) return;
			best = distanceSqr;
			bestSource = c;
			bestSourceId = sourceId;
			bestAmount = movable;
		});

		if ((Object)(object)bestSource != (Object)null && bestAmount > 0)
		{
		source = bestSource;
		prefab = requestedPrefab;
		amount = bestAmount;
		position = ((Component)bestSource).transform.position;
		outpostPosition = requestedOutpost;
		BackgroundWorkerScheduler.SetAirEmptyPickupClaim(zdo, prefab, amount, bestSourceId);
		return true;
		}

		if (TryFindPersistentBaseStagedStockClaimAware(ownerId, requestedPrefab, zdo.m_uid, out var baseHubZdo, out var persistentPosition, out var persistentAvailable))
		{
			int movable = Mathf.Min(HaulerWorkRuntime.GetCarryCapacity(worker, requestedPrefab, persistentAvailable), need);
			if (movable > 0)
			{
				prefab = requestedPrefab;
				amount = movable;
				position = persistentPosition;
				outpostPosition = requestedOutpost;
				BackgroundWorkerScheduler.SetAirEmptyPickupClaim(zdo, prefab, amount, baseHubZdo != null ? baseHubZdo.m_uid : ZDOID.None);
				return true;
			}
		}

		BackgroundWorkerScheduler.ClearAirEmptyPickupClaim(zdo);
		return false;
	}


	// v0.5.389: AirHaulers have the same two-slot cargo inventory as every other Worker.
	// A Drake must therefore fill spare slots with additional cargo for the SAME route before it
	// departs. Only stock from the already reached source depot is considered here, so this adds no
	// extra path stop and no new world/container scan.
	private static int TryTopUpLoadedAirCargo(Character worker, ZDO zdo, Container source, string sourceKind, Vector3 routeTarget, string primaryPrefab)
	{
		if ((Object)(object)worker == (Object)null || zdo == null || (Object)(object)source == (Object)null ||
			WorkerCargoRuntime.GetUsedSlots(zdo) >= WorkerCargoRuntime.InventorySlots)
		{
			return 0;
		}

		Inventory inventory = source.GetInventory();
		if (inventory == null) return 0;

		long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
		ZDO sourceZdo = WorkerContainerRoleRuntime.GetZdo(source);
		ZDOID sourceId = sourceZdo != null ? sourceZdo.m_uid : ZDOID.None;
		bool inbound = string.Equals(sourceKind, OutpostSourceTargetKind, StringComparison.Ordinal);
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<ItemData> items = inventory.GetAllItems();
		int addedTotal = 0;

		for (int i = 0; i < items.Count && WorkerCargoRuntime.GetUsedSlots(zdo) < WorkerCargoRuntime.InventorySlots; i++)
		{
			ItemData item = items[i];
			if (item == null || item.m_stack <= 0) continue;
			string candidate = WorkerStorageRuntime.GetPrefabName(item);
			if (string.IsNullOrEmpty(candidate) || !seen.Add(candidate) ||
				string.Equals(candidate, primaryPrefab, StringComparison.OrdinalIgnoreCase)) continue;

			int stock = WorkerStorageRuntime.CountPrefab(inventory, candidate);
			if (stock <= 0) continue;
			int available;

			if (inbound)
			{
				int protectedAmount = Mathf.Max(WorkerContainerRoleRuntime.ProtectLastItem(source) ? 1 : 0,
					Mathf.Max(WorkerLogisticsRuntime.GetRequestedStock(source, candidate), FieldDepotStockRuleRuntime.GetTransportReserve(source, candidate)));
				available = Mathf.Max(0, stock - protectedAmount);
				if (!sourceId.Equals(ZDOID.None))
					available = Mathf.Max(0, available - BackgroundWorkerScheduler.GetAirInboundPickupClaimAmountForSource(ownerId, candidate, sourceId, zdo.m_uid));
				available = Mathf.Min(available, GetBaseAirDepotCapacity(worker, zdo, candidate));
			}
			else
			{
				int destinationNeed = GetOutpostNeedAt(worker, zdo, candidate, routeTarget);
				destinationNeed = Mathf.Max(0, destinationNeed - BackgroundWorkerScheduler.GetAirEmptyPickupClaimAmountExceptWorker(ownerId, candidate, routeTarget, zdo.m_uid));
				int sourceAvailable = stock;
				if (!sourceId.Equals(ZDOID.None))
					sourceAvailable = Mathf.Max(0, sourceAvailable - BackgroundWorkerScheduler.GetAirEmptyPickupClaimAmountForSource(ownerId, candidate, sourceId, zdo.m_uid));
				available = Mathf.Min(sourceAvailable, destinationNeed);
			}

			if (available <= 0) continue;
			int canAdd = WorkerCargoRuntime.GetMaxAddAmount(worker, zdo, candidate, available);
			if (canAdd <= 0) continue;
			int removed = RemoveItems(inventory, candidate, canAdd);
			if (removed <= 0) continue;
			WorkerZdo.AddCargoAmount(zdo, candidate, removed);
			addedTotal += removed;
		}

		return addedTotal;
	}

	// Persistent/off-screen counterpart of the loaded top-up. It intentionally reuses the existing
	// cached demand + container-ledger data; no scene scan is introduced.
	internal static int TryTopUpPersistentOutboundCargo(ZDO workerZdo, long ownerId, ZDO baseHubZdo, Vector3 outpostPosition, string primaryPrefab)
	{
		if (workerZdo == null || baseHubZdo == null || WorkerCargoRuntime.GetUsedSlots(workerZdo) >= WorkerCargoRuntime.InventorySlots) return 0;
		CollectPersistentOutpostDemandForAirHub(ownerId, ResolvePersistentOutpostHub(ownerId, outpostPosition), outpostPosition, _outpostDemandScratch);
		if (_outpostDemandScratch.Count == 0) return 0;
		ContainerLedgerRuntime.EnsureSnapshotFromPersistentZdo(baseHubZdo);
		int addedTotal = 0;

		foreach (KeyValuePair<string, int> entry in _outpostDemandScratch)
		{
			if (WorkerCargoRuntime.GetUsedSlots(workerZdo) >= WorkerCargoRuntime.InventorySlots) break;
			string candidate = entry.Key;
			if (string.IsNullOrEmpty(candidate) || string.Equals(candidate, primaryPrefab, StringComparison.OrdinalIgnoreCase)) continue;
			int need = Mathf.Max(0, entry.Value - BackgroundWorkerScheduler.GetAirEmptyPickupClaimAmountExceptWorker(ownerId, candidate, outpostPosition, workerZdo.m_uid));
			if (need <= 0) continue;

			int sourceAvailable = 0;
			ContainerLedgerRuntime.TryGetCount(baseHubZdo, candidate, out sourceAvailable);
			sourceAvailable += AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(baseHubZdo, candidate);
			sourceAvailable = Mathf.Max(0, sourceAvailable - BackgroundWorkerScheduler.GetAirEmptyPickupClaimAmountForSource(ownerId, candidate, baseHubZdo.m_uid, workerZdo.m_uid));
			int canAdd = WorkerCargoRuntime.GetMaxAddAmount(workerZdo, candidate, Mathf.Min(need, sourceAvailable));
			if (canAdd <= 0) continue;
			int taken = TakePersistentCargo(baseHubZdo, candidate, canAdd, 0, true, "background-air-multislot-outbound");
			if (taken <= 0) continue;
			WorkerZdo.AddCargoAmount(workerZdo, candidate, taken);
			addedTotal += taken;
		}
		return addedTotal;
	}

	internal static int TryTopUpPersistentInboundCargo(ZDO workerZdo, long ownerId, ZDO outpostZdo, Vector3 outpostPosition, ZDO baseHubZdo, string primaryPrefab)
	{
		if (workerZdo == null || outpostZdo == null || baseHubZdo == null || WorkerCargoRuntime.GetUsedSlots(workerZdo) >= WorkerCargoRuntime.InventorySlots) return 0;
		if (!ContainerLedgerRuntime.EnsureSnapshotFromPersistentZdo(outpostZdo)) return 0;
		ContainerLedgerRuntime.CollectSnapshotPrefabs(outpostZdo, _persistentInboundScratch);
		int addedTotal = 0;

		for (int i = 0; i < _persistentInboundScratch.Count && WorkerCargoRuntime.GetUsedSlots(workerZdo) < WorkerCargoRuntime.InventorySlots; i++)
		{
			string candidate = _persistentInboundScratch[i];
			if (string.IsNullOrEmpty(candidate) || string.Equals(candidate, primaryPrefab, StringComparison.OrdinalIgnoreCase)) continue;
			int keep = WorkerContainerRoleRuntime.ProtectLastItem(outpostZdo) ? 1 : 0;
			int requested = WorkerLogisticsRuntime.GetRequestedStock(outpostZdo, candidate);
			int reserve = FieldDepotStockRuleRuntime.GetTransportReserve(outpostZdo, candidate);
			int protectedAmount = Mathf.Max(keep, Mathf.Max(requested, reserve));
			if (!ContainerLedgerRuntime.TryGetAvailable(outpostZdo, candidate, protectedAmount, out int sourceAvailable) || sourceAvailable <= 0) continue;
			sourceAvailable = Mathf.Max(0, sourceAvailable - BackgroundWorkerScheduler.GetAirInboundPickupClaimAmountForSource(ownerId, candidate, outpostZdo.m_uid, workerZdo.m_uid));
			int destinationCapacity = ContainerStockLimitRuntime.GetPersistentAcceptCapacity(baseHubZdo, candidate);
			int canAdd = WorkerCargoRuntime.GetMaxAddAmount(workerZdo, candidate, Mathf.Min(sourceAvailable, destinationCapacity));
			if (canAdd <= 0) continue;
			int taken = TakePersistentCargo(outpostZdo, candidate, canAdd, protectedAmount, false, "background-air-multislot-inbound");
			if (taken <= 0) continue;
			WorkerZdo.AddCargoAmount(workerZdo, candidate, taken);
			addedTotal += taken;
		}
		return addedTotal;
	}

	private static ZDO ResolvePersistentOutpostHub(long ownerId, Vector3 outpostPosition)
	{
		return WorkerLogisticsRuntime.TryFindPersistentAirDepot(outpostPosition, ownerId, AirDepotMode.Outpost, out ZDO hub, out Vector3 resolved) &&
			Vector3.Distance(resolved, outpostPosition) <= 80f ? hub : null;
	}

	private static int TakePersistentCargo(ZDO sourceZdo, string prefab, int requested, int minimumRemain, bool allowPending, string ownerTag)
	{
		if (sourceZdo == null || string.IsNullOrEmpty(prefab) || requested <= 0) return 0;
		int taken = 0;
		if (allowPending && minimumRemain <= 0 && AdvancedFieldDepotRuntime.TryTakePendingBackgroundDelivery(sourceZdo, prefab, requested, out int pendingTaken) && pendingTaken > 0)
			taken += pendingTaken;
		int remaining = requested - taken;
		if (remaining <= 0) return taken;
		if (!ContainerLedgerRuntime.EnsureSnapshotFromPersistentZdo(sourceZdo)) return taken;
		string transactionId = ContainerLedgerRuntime.CreateTransactionId();
		if (ContainerLedgerRuntime.TryReserveRemoval(sourceZdo, transactionId, prefab, remaining, minimumRemain, ownerTag, out int reserved, out string status) &&
			reserved > 0 && ContainerLedgerRuntime.TryCommitRemoval(sourceZdo, transactionId, out ContainerLedgerRuntime.TransactionInfo transaction, out status) && transaction != null)
		{
			taken += transaction.Amount;
			ContainerLedgerRuntime.CompleteTransaction(sourceZdo, transactionId);
		}
		else
		{
			ContainerLedgerRuntime.RollbackTransaction(sourceZdo, transactionId);
		}
		return taken;
	}

	internal static bool TryCollect(Character worker, ZDO zdo, string sourceKind)
	{
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0136: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bd: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)worker == (Object)null || zdo == null || WorkerZdo.HasCargo(zdo))
		{
			return false;
		}
		if (!WorkerZdo.TryReadTarget(zdo, out var targetName, out var targetKind, out var position) || !string.Equals(targetKind, sourceKind, StringComparison.Ordinal))
		{
			return false;
		}
		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		AirDepotMode value = ((!string.Equals(sourceKind, "airhaul_outpost_source", StringComparison.Ordinal)) ? AirDepotMode.BaseHub : AirDepotMode.Outpost);
		Container val = FindAirDepotNear(position, ownerId, requireRoom: false, null, value);
		if ((Object)(object)val == (Object)null || !WorkerStorageRuntime.IsInsideContainerAura(((Component)worker).transform.position, ((Component)val).transform.position))
		{
			WorkerZdo.ClearTarget(zdo);
			zdo.Set("vw_state", 1);
			return true;
		}
		if (!WorkerStorageRuntime.TryOwn(val))
		{
			return true;
		}

		if (string.Equals(sourceKind, BaseSourceTargetKind, StringComparison.Ordinal) ||
			string.Equals(sourceKind, OutpostSourceTargetKind, StringComparison.Ordinal))
		{
			ZDOID claimedSource = BackgroundWorkerScheduler.GetAirEmptyPickupClaimSourceForWorker(zdo);
			ZDO actualSourceZdo = WorkerContainerRoleRuntime.GetZdo(val);
			ZDOID actualSource = actualSourceZdo != null ? actualSourceZdo.m_uid : ZDOID.None;
			if (!claimedSource.Equals(ZDOID.None) && !claimedSource.Equals(actualSource))
			{
				BackgroundWorkerScheduler.ClearAirEmptyPickupClaim(zdo);
				WorkerZdo.ClearTarget(zdo);
				zdo.Set(WorkerZdo.State, (int)WorkerState.FindJob);
				WorkerStorageRuntime.SetDebug(worker, "air pickup claim source changed; replanning");
				return true;
			}
		}

		Inventory inventory = val.GetInventory();
		int num = WorkerStorageRuntime.CountPrefab(inventory, targetName);
		Vector3 target = default(Vector3);
		int carryCapacity;
		string text;
		if (string.Equals(sourceKind, "airhaul_outpost_source", StringComparison.Ordinal))
		{
			int num2 = Mathf.Max(WorkerContainerRoleRuntime.ProtectLastItem(val) ? 1 : 0, Mathf.Max(WorkerLogisticsRuntime.GetRequestedStock(val, targetName), FieldDepotStockRuleRuntime.GetTransportReserve(val, targetName)));
			carryCapacity = HaulerWorkRuntime.GetCarryCapacity(worker, targetName, Mathf.Max(0, num - num2));
			carryCapacity = Mathf.Min(carryCapacity, GetBaseAirDepotCapacity(worker, zdo, targetName));
			int claimedAmount = BackgroundWorkerScheduler.GetAirEmptyPickupClaimAmountForWorker(zdo, targetName);
			if (claimedAmount > 0) carryCapacity = Mathf.Min(carryCapacity, claimedAmount);
			text = "airhaul_inbound";
		}
		else
		{
			WorkerZdo.TryReadCargoRoute(zdo, out var _, out target);
			int outpostNeedAt = GetOutpostNeedAt(worker, zdo, targetName, target);
			carryCapacity = Mathf.Min(HaulerWorkRuntime.GetCarryCapacity(worker, targetName, num), outpostNeedAt);
			int claimedAmount = BackgroundWorkerScheduler.GetAirEmptyPickupClaimAmountForWorker(zdo, targetName);
			if (claimedAmount > 0) carryCapacity = Mathf.Min(carryCapacity, claimedAmount);
			text = RouteOutbound;
		}
		if (carryCapacity <= 0)
		{
			BackgroundWorkerScheduler.ClearAirEmptyPickupClaim(zdo);
			WorkerZdo.ClearTarget(zdo);
			zdo.Set("vw_state", 1);
			return true;
		}
		int num3 = RemoveItems(inventory, targetName, carryCapacity);
		if (num3 <= 0)
		{
			BackgroundWorkerScheduler.ClearAirEmptyPickupClaim(zdo);
			WorkerZdo.ClearTarget(zdo);
			zdo.Set("vw_state", 1);
			return true;
		}
		WorkerZdo.SetCargo(zdo, targetName, num3);
		int extraLoaded = TryTopUpLoadedAirCargo(worker, zdo, val, sourceKind, target, targetName);
		WorkerStorageRuntime.PersistContainer(val, inventory);
		string flowFallbackArea = string.Equals(sourceKind, "airhaul_outpost_source", StringComparison.Ordinal)
			? WorkerZdo.ReadWorkAreaId(zdo)
			: "base";
		WorkzoneLogisticsFlowRuntime.TagCargoSource(zdo, val, flowFallbackArea);
		BackgroundWorkerScheduler.ClearAirEmptyPickupClaim(zdo);
		WorkerZdo.SetCargoRoute(zdo, text, target);
		WorkerZdo.ClearTarget(zdo);
		zdo.Set("vw_state", 5);
		WorkerStorageRuntime.SetDebug(worker, $"air haul collected: {targetName} x{num3}; extra={extraLoaded}; cargo={WorkerCargoRuntime.GetSummary(worker, zdo)}; route={text}");
		return true;
	}

	internal static bool TryDeposit(Character worker, ZDO zdo, string targetKind)
	{
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)worker == (Object)null || zdo == null || !WorkerZdo.TryReadCargo(zdo, out var prefabName, out var amount))
		{
			ClearAirDropState(worker);
			return false;
		}
		if (!WorkerZdo.TryReadTarget(zdo, out var _, out var targetKind2, out var position) || !string.Equals(targetKind2, targetKind, StringComparison.Ordinal))
		{
			ClearAirDropState(worker);
			return false;
		}
		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		AirDepotMode value = (string.Equals(targetKind, "airhaul_base_deposit", StringComparison.Ordinal) ? AirDepotMode.BaseHub : AirDepotMode.Outpost);
		Container val = FindAirDepotNear(position, ownerId, requireRoom: true, prefabName, value);
		if ((Object)(object)val == (Object)null || !WorkerStorageRuntime.IsInsideContainerAura(((Component)worker).transform.position, ((Component)val).transform.position))
		{
			ClearAirDropState(worker);
			return true;
		}
		if (!WorkerStorageRuntime.TryOwn(val))
		{
			return true;
		}
		int physicalCapacity = WorkerStorageRuntime.GetAvailableCapacityForPrefab(val.GetInventory(), prefabName);
		int availableCapacityForPrefab = ContainerStockLimitRuntime.GetAcceptCapacity(val, prefabName, physicalCapacity);
		if (availableCapacityForPrefab <= 0)
		{
			ClearAirDropState(worker);
			WorkerStorageRuntime.SetDebug(worker, $"air drop waiting: {prefabName} x{amount}; destination full");
			return true;
		}
		int amount2 = Mathf.Min(amount, availableCapacityForPrefab);
		if (!TickAirDropAnimation(worker, zdo, val, prefabName, amount2, targetKind))
		{
			return true;
		}
		int num = AddItems(val, prefabName, amount2);
		if (num <= 0)
		{
			return true;
		}
		string flowFallback = string.Equals(targetKind, BaseDepositTargetKind, StringComparison.Ordinal)
			? WorkerZdo.ReadWorkAreaId(zdo)
			: "base";
		WorkzoneLogisticsFlowRuntime.RecordTaggedExport(zdo, prefabName, num, flowFallback);
		// Preserve every other cargo stack. SetCargo/ClearCargo are single-manifest replacement
		// operations and would discard slot 2 here. Remove only the stack that was actually delivered.
		WorkerZdo.RemoveCargoAmount(zdo, prefabName, num);
		bool hasRemainingCargo = WorkerZdo.HasCargo(zdo);
		if (!hasRemainingCargo)
		{
			WorkerZdo.ClearCargo(zdo);
			WorkzoneLogisticsFlowRuntime.ClearCargoSource(zdo);
		}
		BackgroundWorkerScheduler.CompletePhysicalAirHybridRoute(zdo);
		WorkerZdo.ClearTarget(zdo);
		zdo.Set("vw_state", 1);
		WorkerStorageRuntime.SetDebug(worker, $"air drop delivered: {prefabName} x{num} -> Advanced FieldDepot; remaining={WorkerCargoRuntime.GetSummary(worker, zdo)}");
		return true;
	}

	internal static bool TryFindBaseDestination(Character worker, ZDO zdo, string prefab, out Vector3 position)
	{
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
		position = default(Vector3);
		if ((Object)(object)worker == (Object)null || zdo == null)
		{
			return false;
		}
		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		float best = float.MaxValue;
		Container bestDepot = null;
		bool hasLoadedBaseHub = false;
		AdvancedFieldDepotRuntime.ForEachLoadedDepot(delegate(Container c)
		{
			//IL_0039: Unknown result type (might be due to invalid IL or missing references)
			//IL_0049: Unknown result type (might be due to invalid IL or missing references)
			//IL_004e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0053: Unknown result type (might be due to invalid IL or missing references)
			if (IsUsableAirDepot(c, ownerId) && WorkerContainerRoleRuntime.GetAirDepotMode(c) == AirDepotMode.BaseHub)
			{
				hasLoadedBaseHub = true;
				if (WorkerStorageRuntime.HasLikelyRoomFor(c.GetInventory(), prefab))
				{
					Vector3 val = ((Component)c).transform.position - ((Component)worker).transform.position;
					float sqrMagnitude = val.sqrMagnitude;
					if (!(sqrMagnitude >= best))
					{
						best = sqrMagnitude;
						bestDepot = c;
					}
				}
			}
		});
		if ((Object)(object)bestDepot != (Object)null)
		{
			position = ((Component)bestDepot).transform.position;
			return true;
		}
		if (!hasLoadedBaseHub && WorkerLogisticsRuntime.TryFindPersistentAirDepot(((Component)worker).transform.position, ownerId, AirDepotMode.BaseHub, out var _, out var position2))
		{
			position = position2;
			return true;
		}
		return false;
	}

	internal static bool TryFindOutpostDestination(Character worker, ZDO zdo, string prefab, Vector3 requestedPosition, out Vector3 position)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		position = default(Vector3);
		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		Container val = FindAirDepotNear(requestedPosition, ownerId, requireRoom: true, prefab, AirDepotMode.Outpost);
		if ((Object)(object)val != (Object)null)
		{
			position = ((Component)val).transform.position;
			return true;
		}
		if (WorkerLogisticsRuntime.TryFindPersistentAirDepot(requestedPosition, ownerId, AirDepotMode.Outpost, out var _, out var position2) && Vector3.Distance(position2, requestedPosition) <= 40f)
		{
			position = position2;
			return true;
		}
		return false;
	}

	internal static bool TryFindSorterInboundSource(Character worker, ZDO zdo, out Container source, out string prefab, out int amount, out Vector3 position)
	{
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		source = null;
		prefab = string.Empty;
		amount = 0;
		position = default(Vector3);
		if ((Object)(object)worker == (Object)null || zdo == null)
		{
			return false;
		}
		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		Vector3 center = WorkerZdo.ReadHome(zdo, ((Component)worker).transform.position);
		float best = float.MaxValue;
		Container foundSource = null;
		string foundPrefab = string.Empty;
		int foundAmount = 0;
		Vector3 foundPosition = default(Vector3);
		WorkerStorageRuntime.ForEachContainerNear(center, 80f, delegate(Container c)
		{
			//IL_00df: Unknown result type (might be due to invalid IL or missing references)
			//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
			//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
			//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
			//IL_0134: Unknown result type (might be due to invalid IL or missing references)
			//IL_0139: Unknown result type (might be due to invalid IL or missing references)
			if (IsUsableAirDepot(c, ownerId) && WorkerContainerRoleRuntime.GetAirDepotMode(c) == AirDepotMode.BaseHub)
			{
				Inventory inventory = c.GetInventory();
				if (inventory != null)
				{
					HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					List<ItemData> allItems = inventory.GetAllItems();
					for (int i = 0; i < allItems.Count; i++)
					{
						ItemData val = allItems[i];
						if (val != null && val.m_stack > 0)
						{
							string prefabName = WorkerStorageRuntime.GetPrefabName(val);
							if (!string.IsNullOrEmpty(prefabName) && hashSet.Add(prefabName) && WorkerLogisticsRuntime.TryFindNormalDestination(worker, zdo, prefabName, out var _, out var position2))
							{
								int num = WorkerStorageRuntime.CountPrefab(inventory, prefabName);
								int sorterInboundProtectedAmount = GetSorterInboundProtectedAmount(c, ownerId, prefabName);
								int num2 = Mathf.Max(0, num - sorterInboundProtectedAmount);
								if (num2 > 0)
								{
									int carryCapacity = HaulerWorkRuntime.GetCarryCapacity(worker, prefabName, num2);
									if (carryCapacity > 0)
									{
										position2 = ((Component)c).transform.position - ((Component)worker).transform.position;
										float sqrMagnitude = position2.sqrMagnitude;
										if (!(sqrMagnitude >= best))
										{
											best = sqrMagnitude;
											foundSource = c;
											foundPrefab = prefabName;
											foundAmount = carryCapacity;
											foundPosition = ((Component)c).transform.position;
										}
									}
								}
							}
						}
					}
				}
			}
		});
		source = foundSource;
		prefab = foundPrefab;
		amount = foundAmount;
		position = foundPosition;
		if ((Object)(object)source != (Object)null)
		{
			return amount > 0;
		}
		return false;
	}

	internal static int GetSorterInboundProtectedAmount(Container depot, long ownerId, string prefab)
	{
		if ((Object)(object)depot == (Object)null || string.IsNullOrWhiteSpace(prefab))
		{
			return 0;
		}
		bool num = WorkerContainerRoleRuntime.ProtectLastItem(depot);
		int requestedStock = WorkerLogisticsRuntime.GetRequestedStock(depot, prefab);
		int transportReserve = FieldDepotStockRuleRuntime.GetTransportReserve(depot, prefab);
		int num2 = Mathf.Max(num ? 1 : 0, Mathf.Max(requestedStock, transportReserve));
		int networkAirOutstandingNeed = GetNetworkAirOutstandingNeed(ownerId, prefab);
		long num3 = (long)num2 + (long)Mathf.Max(0, networkAirOutstandingNeed);
		if (num3 < int.MaxValue)
		{
			return (int)num3;
		}
		return int.MaxValue;
	}

	internal static int GetOutpostAirOutstandingNeed(long ownerId, string prefab)
	{
		if (string.IsNullOrEmpty(prefab)) return 0;

		List<ZDO> hubs = new List<ZDO>(16);
		List<Vector3> hubPositions = new List<Vector3>(16);
		WorkerProvisioningLogisticsRuntime.ForEachCachedOutpostAirHub((zdo, owner, position) =>
		{
			if (zdo == null || (owner != 0L && ownerId != 0L && owner != ownerId)) return;
			hubs.Add(zdo);
			hubPositions.Add(position);
		});
		if (hubs.Count == 0) return 0;

		Dictionary<ZDOID, int> needByHub = new Dictionary<ZDOID, int>();
		WorkerProvisioningLogisticsRuntime.ForEachCachedFieldDepot((zdo, owner, position) =>
		{
			if (zdo == null || (owner != 0L && ownerId != 0L && owner != ownerId)) return;
			int requestedStock = WorkerLogisticsRuntime.GetRequestedStock(zdo, prefab);
			if (requestedStock <= 0) return;

			int hubIndex = -1;
			for (int i = 0; i < hubs.Count; i++)
			{
				if (!WorkerLogisticsRuntime.IsSupplyBoundToAirHub(zdo, hubs[i])) continue;
				hubIndex = i;
				break;
			}
			if (hubIndex < 0) return;

			int count = 0;
			ContainerLedgerRuntime.TryGetCount(zdo, prefab, out count);
			count += AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(zdo, prefab);
			int deficit = Mathf.Max(0, requestedStock - count);
			if (deficit <= 0) return;

			ZDOID hubId = hubs[hubIndex].m_uid;
			needByHub.TryGetValue(hubId, out int previous);
			long combined = (long)previous + deficit;
			needByHub[hubId] = combined >= int.MaxValue ? int.MaxValue : (int)combined;
		});

		for (int i = 0; i < hubs.Count; i++)
		{
			int provisionNeed = WorkerProvisioningLogisticsRuntime.GetPersistentAirTransportNeed(hubs[i], prefab);
			if (provisionNeed <= 0) continue;
			ZDOID hubId = hubs[i].m_uid;
			needByHub.TryGetValue(hubId, out int previous);
			long combined = (long)previous + provisionNeed;
			needByHub[hubId] = combined >= int.MaxValue ? int.MaxValue : (int)combined;
		}

		long total = 0L;
		for (int i = 0; i < hubs.Count; i++)
		{
			needByHub.TryGetValue(hubs[i].m_uid, out int need);
			if (need <= 0) continue;
			int inTransit = BackgroundWorkerScheduler.GetAirOutboundInTransitAmount(ownerId, prefab, hubPositions[i]);
			int pickupClaimed = BackgroundWorkerScheduler.GetAirEmptyPickupClaimAmount(ownerId, prefab, hubPositions[i]);
			total += Mathf.Max(0, need - inTransit - pickupClaimed);
			if (total >= int.MaxValue) return int.MaxValue;
		}
		return (int)total;
	}

	internal static bool IsLinkedToOutpostAirHub(long ownerId, Vector3 position, float maxDistance = 250f)
	{
		float maxSqr = Mathf.Max(1f, maxDistance) * Mathf.Max(1f, maxDistance);
		bool linked = false;
		WorkerProvisioningLogisticsRuntime.ForEachCachedOutpostAirHub((zdo, owner, hubPosition) =>
		{
			if (linked || zdo == null || (owner != 0L && ownerId != 0L && owner != ownerId)) return;
			Vector3 delta = hubPosition - position;
			if (delta.sqrMagnitude <= maxSqr) linked = true;
		});
		return linked;
	}

	internal static void CollectOutpostAirDemandPrefabs(long ownerId, List<string> target)
	{
		if (target == null) return;
		target.Clear();

		List<ZDO> hubZdos = new List<ZDO>(16);
		WorkerProvisioningLogisticsRuntime.ForEachCachedOutpostAirHub((zdo, owner, position) =>
		{
			if (zdo == null || (owner != 0L && ownerId != 0L && owner != ownerId)) return;
			hubZdos.Add(zdo);
		});
		if (hubZdos.Count == 0) return;

		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<string> local = new List<string>(16);
		WorkerProvisioningLogisticsRuntime.ForEachCachedFieldDepot((zdo, owner, position) =>
		{
			if (zdo == null || (owner != 0L && ownerId != 0L && owner != ownerId)) return;
			bool bound = false;
			for (int i = 0; i < hubZdos.Count; i++)
			{
				if (!WorkerLogisticsRuntime.IsSupplyBoundToAirHub(zdo, hubZdos[i])) continue;
				bound = true;
				break;
			}
			if (!bound) return;

			WorkerLogisticsRuntime.CollectRequestedPrefabs(zdo, local);
			for (int i = 0; i < local.Count; i++)
			{
				string requestedPrefab = local[i];
				if (!string.IsNullOrWhiteSpace(requestedPrefab) && seen.Add(requestedPrefab))
					target.Add(requestedPrefab);
			}
		});

		for (int i = 0; i < hubZdos.Count; i++)
		{
			WorkerProvisioningLogisticsRuntime.CollectPersistentAirDemandPrefabs(hubZdos[i], _provisionRequestScratch);
			for (int j = 0; j < _provisionRequestScratch.Count; j++)
			{
				string provisionPrefab = _provisionRequestScratch[j];
				if (!string.IsNullOrWhiteSpace(provisionPrefab) && seen.Add(provisionPrefab))
					target.Add(provisionPrefab);
			}
		}
	}

	internal static int GetNetworkAirOutstandingNeed(long ownerId, string prefab)
	{
		int outpostAirOutstandingNeed = GetOutpostAirOutstandingNeed(ownerId, prefab);
		if (outpostAirOutstandingNeed <= 0)
		{
			return 0;
		}
		return outpostAirOutstandingNeed;
	}

	private static void CollectPersistentOutpostDemandForAirHub(long ownerId, ZDO outpostHubZdo, Vector3 outpostPosition, Dictionary<string, int> demand)
	{
		if (demand == null) return;
		demand.Clear();
		if (outpostHubZdo == null) return;

		bool knownHub = false;
		WorkerProvisioningLogisticsRuntime.ForEachCachedOutpostAirHub((candidate, owner, position) =>
		{
			if (knownHub || candidate == null || candidate.m_uid != outpostHubZdo.m_uid) return;
			if (owner != 0L && ownerId != 0L && owner != ownerId) return;
			knownHub = true;
			outpostPosition = position;
		});
		if (!knownHub) return;

		WorkerProvisioningLogisticsRuntime.ForEachCachedFieldDepot((candidate, owner, position) =>
		{
			if (candidate == null || (owner != 0L && ownerId != 0L && owner != ownerId)) return;
			if (!WorkerLogisticsRuntime.IsSupplyBoundToAirHub(candidate, outpostHubZdo)) return;

			WorkerLogisticsRuntime.CollectRequestedPrefabs(candidate, _outpostRequestScratch);
			for (int j = 0; j < _outpostRequestScratch.Count; j++)
			{
				string requestedPrefab = _outpostRequestScratch[j];
				int requestedStock = WorkerLogisticsRuntime.GetRequestedStock(candidate, requestedPrefab);
				if (requestedStock <= 0) continue;
				int count = 0;
				ContainerLedgerRuntime.TryGetCount(candidate, requestedPrefab, out count);
				count += AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(candidate, requestedPrefab);
				int deficit = Mathf.Max(0, requestedStock - count);
				if (deficit <= 0) continue;
				demand.TryGetValue(requestedPrefab, out int previous);
				long combined = (long)previous + deficit;
				demand[requestedPrefab] = combined >= int.MaxValue ? int.MaxValue : (int)combined;
			}
		});

		WorkerProvisioningLogisticsRuntime.CollectPersistentAirDemandPrefabs(outpostHubZdo, _provisionRequestScratch);
		for (int i = 0; i < _provisionRequestScratch.Count; i++)
		{
			string provisionPrefab = _provisionRequestScratch[i];
			int provisionNeed = WorkerProvisioningLogisticsRuntime.GetPersistentAirTransportNeed(outpostHubZdo, provisionPrefab);
			if (provisionNeed <= 0) continue;
			demand.TryGetValue(provisionPrefab, out int previous);
			long combined = (long)previous + provisionNeed;
			demand[provisionPrefab] = combined >= int.MaxValue ? int.MaxValue : (int)combined;
		}

		if (demand.Count == 0) return;
		List<string> keys = new List<string>(demand.Keys);
		for (int i = 0; i < keys.Count; i++)
		{
			string requestedPrefab = keys[i];
			int inTransit = BackgroundWorkerScheduler.GetAirOutboundInTransitAmount(ownerId, requestedPrefab, outpostPosition);
			int remaining = Mathf.Max(0, demand[requestedPrefab] - inTransit);
			if (remaining > 0) demand[requestedPrefab] = remaining;
			else demand.Remove(requestedPrefab);
		}
	}

	internal static bool TryGetPersistentOutpostDemandForAirHub(long ownerId, ZDO outpostHubZdo, Vector3 outpostPosition, out string prefab, out int need)
	{
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		prefab = string.Empty;
		need = 0;
		CollectPersistentOutpostDemandForAirHub(ownerId, outpostHubZdo, outpostPosition, _outpostDemandScratch);
		foreach (KeyValuePair<string, int> item in _outpostDemandScratch)
		{
			if (item.Value > need)
			{
				prefab = item.Key;
				need = item.Value;
			}
		}
		if (need > 0)
		{
			return !string.IsNullOrEmpty(prefab);
		}
		return false;
	}

	internal static int GetPersistentOutpostNeedForAirHub(long ownerId, Vector3 outpostPosition, string prefab)
	{
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		if (string.IsNullOrWhiteSpace(prefab))
		{
			return 0;
		}
		if (!WorkerLogisticsRuntime.TryFindPersistentAirDepot(outpostPosition, ownerId, AirDepotMode.Outpost, out var depotZdo, out var position))
		{
			return 0;
		}
		if (Vector3.Distance(position, outpostPosition) > 80f)
		{
			return 0;
		}
		CollectPersistentOutpostDemandForAirHub(ownerId, depotZdo, position, _outpostDemandScratch);
		if (!_outpostDemandScratch.TryGetValue(prefab, out var value))
		{
			return 0;
		}
		return Mathf.Max(0, value);
	}

	internal static bool TryFindPersistentBaseStagedStockForOutpostDemand(long ownerId, ZDO outpostZdo, Vector3 outpostPosition, out string prefab, out ZDO baseHubZdo, out Vector3 position, out int available)
	{
		prefab = string.Empty;
		baseHubZdo = null;
		position = default(Vector3);
		available = 0;
		if (outpostZdo == null) return false;

		CollectPersistentOutpostDemandForAirHub(ownerId, outpostZdo, outpostPosition, _outpostDemandScratch);
		foreach (KeyValuePair<string, int> item in _outpostDemandScratch)
		{
			string candidate = item.Key;
			int demand = item.Value;
			if (demand <= 0) continue;
			int claimedDemand = BackgroundWorkerScheduler.GetAirEmptyPickupClaimAmount(ownerId, candidate, outpostPosition);
			int remainingDemand = Mathf.Max(0, demand - claimedDemand);
			if (remainingDemand <= 0) continue;

			if (!TryFindPersistentBaseStagedStockClaimAware(ownerId, candidate, ZDOID.None, out var candidateHub, out var candidatePosition, out var sourceAvailable)) continue;
			int movable = Mathf.Min(remainingDemand, sourceAvailable);
			if (movable <= available) continue;
			prefab = candidate;
			baseHubZdo = candidateHub;
			position = candidatePosition;
			available = movable;
		}
		return baseHubZdo != null && available > 0 && !string.IsNullOrEmpty(prefab);
	}

	internal static bool TryFindAnyPersistentBaseStagedStockForDemand(long ownerId, out string prefab, out ZDO baseHubZdo, out Vector3 position, out int available)
	{
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		prefab = string.Empty;
		baseHubZdo = null;
		position = default(Vector3);
		available = 0;
		List<string> list = new List<string>(32);
		CollectOutpostAirDemandPrefabs(ownerId, list);
		for (int i = 0; i < list.Count; i++)
		{
			string text = list[i];
			int outpostAirOutstandingNeed = GetOutpostAirOutstandingNeed(ownerId, text);
			if (outpostAirOutstandingNeed > 0 && TryFindPersistentBaseStagedStock(ownerId, text, out var baseHubZdo2, out var position2, out var available2))
			{
				int num = Mathf.Min(outpostAirOutstandingNeed, available2);
				if (num > available)
				{
					prefab = text;
					baseHubZdo = baseHubZdo2;
					position = position2;
					available = num;
				}
			}
		}
		if (baseHubZdo != null && available > 0)
		{
			return !string.IsNullOrEmpty(prefab);
		}
		return false;
	}

	internal static bool TryFindPersistentInboundStockForOutpost(long ownerId, ZDO outpostZdo, Vector3 outpostPosition, out string prefab, out int amount, out int minimumRemain, out ZDO baseHubZdo, out Vector3 baseHubPosition)
	{
		prefab = string.Empty;
		amount = 0;
		minimumRemain = 0;
		baseHubZdo = null;
		baseHubPosition = default(Vector3);
		if (outpostZdo == null)
		{
			return false;
		}
		if (!outpostZdo.GetBool(WorkerContainerRoleRuntime.AirTransportKey, false) || Mathf.Clamp(outpostZdo.GetInt(WorkerContainerRoleRuntime.AirDepotModeKey, 0), 0, 1) != 0)
		{
			return false;
		}
		if (!ContainerLedgerRuntime.EnsureSnapshotFromPersistentZdo(outpostZdo))
		{
			return false;
		}
		if (!WorkerLogisticsRuntime.TryFindPersistentAirDepot(outpostPosition, ownerId, AirDepotMode.BaseHub, out baseHubZdo, out baseHubPosition) || baseHubZdo == null)
		{
			return false;
		}

		ContainerLedgerRuntime.CollectSnapshotPrefabs(outpostZdo, _persistentInboundScratch);
		for (int i = 0; i < _persistentInboundScratch.Count; i++)
		{
			string candidate = _persistentInboundScratch[i];
			if (string.IsNullOrEmpty(candidate))
			{
				continue;
			}
			int keep = WorkerContainerRoleRuntime.ProtectLastItem(outpostZdo) ? 1 : 0;
			int requested = WorkerLogisticsRuntime.GetRequestedStock(outpostZdo, candidate);
			int reserve = FieldDepotStockRuleRuntime.GetTransportReserve(outpostZdo, candidate);
			int protectedAmount = Mathf.Max(keep, Mathf.Max(requested, reserve));
			if (!ContainerLedgerRuntime.TryGetAvailable(outpostZdo, candidate, protectedAmount, out var available) || available <= 0)
			{
				continue;
			}

			int destinationCapacity = ContainerStockLimitRuntime.GetPersistentAcceptCapacity(baseHubZdo, candidate);
			if (destinationCapacity <= 0)
			{
				continue;
			}

			int movable = Mathf.Min(20, Mathf.Min(available, destinationCapacity));
			if (movable > amount)
			{
				prefab = candidate;
				amount = movable;
				minimumRemain = protectedAmount;
			}
		}
		return amount > 0 && !string.IsNullOrEmpty(prefab);
	}

	internal static bool TryFindPersistentBaseStagedStock(long ownerId, string prefab, out ZDO baseHubZdo, out Vector3 position, out int available)
	{
		ZDO foundHub = null;
		Vector3 foundPosition = default(Vector3);
		int foundAvailable = 0;
		if (!string.IsNullOrEmpty(prefab))
		{
			WorkerProvisioningLogisticsRuntime.ForEachCachedBaseAirHub((candidate, owner, p) =>
			{
				if (candidate == null || (owner != 0L && ownerId != 0L && owner != ownerId)) return;
				int count = 0;
				ContainerLedgerRuntime.TryGetCount(candidate, prefab, out count);
				count += AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(candidate, prefab);
				if (count <= foundAvailable) return;
				foundHub = candidate;
				foundPosition = p;
				foundAvailable = count;
			});
		}
		baseHubZdo = foundHub;
		position = foundPosition;
		available = foundAvailable;
		return foundHub != null && foundAvailable > 0;
	}

	private static bool TryFindPersistentBaseStagedStockClaimAware(long ownerId, string prefab, ZDOID excludeWorker, out ZDO baseHubZdo, out Vector3 position, out int available)
	{
		ZDO foundHub = null;
		Vector3 foundPosition = default(Vector3);
		int foundAvailable = 0;
		if (!string.IsNullOrEmpty(prefab))
		{
			WorkerProvisioningLogisticsRuntime.ForEachCachedBaseAirHub((candidate, owner, p) =>
			{
				if (candidate == null || (owner != 0L && ownerId != 0L && owner != ownerId)) return;
				int count = 0;
				ContainerLedgerRuntime.TryGetCount(candidate, prefab, out count);
				count += AdvancedFieldDepotRuntime.GetPendingBackgroundDelivery(candidate, prefab);
				int claimed = BackgroundWorkerScheduler.GetAirEmptyPickupClaimAmountForSource(ownerId, prefab, candidate.m_uid, excludeWorker);
				int unclaimed = Mathf.Max(0, count - claimed);
				if (unclaimed <= foundAvailable) return;
				foundHub = candidate;
				foundPosition = p;
				foundAvailable = unclaimed;
			});
		}
		baseHubZdo = foundHub;
		position = foundPosition;
		available = foundAvailable;
		return foundHub != null && foundAvailable > 0;
	}

	internal static bool TryFindSorterOutboundSource(Character worker, ZDO zdo, out Container source, out string prefab, out int amount, out Vector3 position, out Vector3 airDepotPosition)
	{
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		source = null;
		prefab = string.Empty;
		amount = 0;
		position = default(Vector3);
		airDepotPosition = default(Vector3);
		if ((Object)(object)worker == (Object)null || zdo == null)
		{
			return false;
		}
		if (!TryGetNetworkAirRequest(worker, zdo, out var prefab2, out var need, out var baseHubPosition))
		{
			return false;
		}
		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		Container val = FindAirDepotNear(baseHubPosition, ownerId, requireRoom: true, prefab2, AirDepotMode.BaseHub);
		if ((Object)(object)val == (Object)null)
		{
			return false;
		}
		if (!WorkerLogisticsRuntime.TryFindNormalSource(worker, zdo, prefab2, need, out var source2, out var usable, out var position2))
		{
			return false;
		}
		int physicalCapacity = WorkerStorageRuntime.GetAvailableCapacityForPrefab(val.GetInventory(), prefab2);
		int availableCapacityForPrefab = ContainerStockLimitRuntime.GetAcceptCapacity(val, prefab2, physicalCapacity);
		int num = Mathf.Min(HaulerWorkRuntime.GetCarryCapacity(worker, prefab2, usable), Mathf.Min(need, availableCapacityForPrefab));
		if (num <= 0)
		{
			return false;
		}
		source = source2;
		prefab = prefab2;
		amount = num;
		position = position2;
		airDepotPosition = ((Component)val).transform.position;
		return true;
	}

	internal static int GetSpecificAirDepotFreeCapacity(Character worker, ZDO zdo, string prefab, Vector3 position)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		Container val = FindAirDepotNear(position, ownerId, requireRoom: true, prefab, AirDepotMode.BaseHub);
		if (!((Object)(object)val == (Object)null))
		{
			int physical = WorkerStorageRuntime.GetAvailableCapacityForPrefab(val.GetInventory(), prefab);
			return ContainerStockLimitRuntime.GetAcceptCapacity(val, prefab, physical);
		}
		return 0;
	}

	private static bool TryGetNetworkAirRequest(Character worker, ZDO zdo, out string prefab, out int need, out Vector3 baseHubPosition)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		prefab = string.Empty;
		need = 0;
		baseHubPosition = default(Vector3);
		if ((Object)(object)worker == (Object)null || zdo == null)
		{
			return false;
		}
		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		Vector3 baseOrigin = WorkerZdo.ReadHome(zdo, ((Component)worker).transform.position);
		ZDO baseHubZdo;
		return WorkerLogisticsRuntime.TryFindBaseAirStagingOrder(ownerId, baseOrigin, out baseHubZdo, out baseHubPosition, out prefab, out need);
	}

	private static bool TryFindBaseAirDepotNearHome(Character worker, ZDO zdo, string prefab, bool requireRoom, out Container depot)
	{
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		depot = null;
		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		Vector3 home = WorkerZdo.ReadHome(zdo, ((Component)worker).transform.position);
		float best = float.MaxValue;
		Container foundDepot = null;
		WorkerStorageRuntime.ForEachContainerNear(home, 80f, delegate(Container c)
		{
			//IL_003a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0040: Unknown result type (might be due to invalid IL or missing references)
			//IL_0045: Unknown result type (might be due to invalid IL or missing references)
			//IL_004a: Unknown result type (might be due to invalid IL or missing references)
			if (IsUsableAirDepot(c, ownerId) && WorkerContainerRoleRuntime.GetAirDepotMode(c) == AirDepotMode.BaseHub && (!requireRoom || WorkerStorageRuntime.HasLikelyRoomFor(c.GetInventory(), prefab)))
			{
				Vector3 val = ((Component)c).transform.position - home;
				float sqrMagnitude = val.sqrMagnitude;
				if (!(sqrMagnitude >= best))
				{
					best = sqrMagnitude;
					foundDepot = c;
				}
			}
		});
		depot = foundDepot;
		return (Object)(object)depot != (Object)null;
	}

	private static int GetBaseAirDepotCapacity(Character worker, ZDO zdo, string prefab)
	{
		if ((Object)(object)worker == (Object)null || zdo == null)
		{
			return 0;
		}
		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		long total = 0L;
		bool hasLoadedBaseHub = false;
		Dictionary<string, int> groupPhysical = new Dictionary<string, int>(StringComparer.Ordinal);
		Dictionary<string, Container> groupRepresentative = new Dictionary<string, Container>(StringComparer.Ordinal);
		AdvancedFieldDepotRuntime.ForEachLoadedDepot(delegate(Container c)
		{
			if (!IsUsableAirDepot(c, ownerId) || WorkerContainerRoleRuntime.GetAirDepotMode(c) != AirDepotMode.BaseHub)
			{
				return;
			}
			hasLoadedBaseHub = true;
			int physical = WorkerStorageRuntime.GetAvailableCapacityForPrefab(c.GetInventory(), prefab);
			if (AirFreightGroupRuntime.TryGetGroupId(c, out string groupId) && !string.IsNullOrEmpty(groupId))
			{
				groupPhysical.TryGetValue(groupId, out int currentPhysical);
				long combinedPhysical = (long)currentPhysical + Mathf.Max(0, physical);
				groupPhysical[groupId] = combinedPhysical >= int.MaxValue ? int.MaxValue : (int)combinedPhysical;
				if (!groupRepresentative.ContainsKey(groupId)) groupRepresentative[groupId] = c;
			}
			else
			{
				total += ContainerStockLimitRuntime.GetAcceptCapacity(c, prefab, physical);
			}
		});

		foreach (KeyValuePair<string, int> pair in groupPhysical)
		{
			if (!groupRepresentative.TryGetValue(pair.Key, out Container representative) || representative == null) continue;
			total += ContainerStockLimitRuntime.GetAcceptCapacity(representative, prefab, pair.Value);
			if (total >= int.MaxValue) return int.MaxValue;
		}

		if (total > 0) return total >= int.MaxValue ? int.MaxValue : (int)total;
		if (!hasLoadedBaseHub && WorkerLogisticsRuntime.TryFindPersistentAirDepot(((Component)worker).transform.position, ownerId, AirDepotMode.BaseHub, out var persistentHub, out var _))
		{
			if (persistentHub != null && ContainerStockLimitRuntime.HasRule(persistentHub, prefab))
				return ContainerStockLimitRuntime.GetPersistentAcceptCapacity(persistentHub, prefab);
			return int.MaxValue;
		}
		return 0;
	}

	private static bool TryGetAirOutpostRequest(Character worker, ZDO zdo, out Vector3 outpostPosition, out string prefab, out int need)
	{
		outpostPosition = default(Vector3);
		prefab = string.Empty;
		need = 0;
		if ((Object)(object)worker == (Object)null || zdo == null) return false;

		long ownerId = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
		ZDO depotZdo = null;
		if (HasAssignedAirDepot(worker, zdo, out var depot, out var position))
		{
			depotZdo = WorkerContainerRoleRuntime.GetZdo(depot);
			outpostPosition = position;
		}
		else
		{
			Vector3 home = WorkerZdo.ReadHome(zdo, ((Component)worker).transform.position);
			if (!WorkerLogisticsRuntime.TryFindPersistentAirDepot(home, ownerId, AirDepotMode.Outpost, out depotZdo, out outpostPosition) || Vector3.Distance(home, outpostPosition) > 250f)
				return false;
		}
		if (depotZdo == null) return false;

		CollectPersistentOutpostDemandForAirHub(ownerId, depotZdo, outpostPosition, _outpostDemandScratch);
		foreach (KeyValuePair<string, int> item in _outpostDemandScratch)
		{
			if (item.Value <= 0 || string.IsNullOrWhiteSpace(item.Key)) continue;
			int claimedByOthers = BackgroundWorkerScheduler.GetAirEmptyPickupClaimAmountExceptWorker(ownerId, item.Key, outpostPosition, zdo.m_uid);
			int remaining = Mathf.Max(0, item.Value - claimedByOthers);
			if (remaining <= need) continue;
			prefab = item.Key;
			need = remaining;
		}
		return need > 0 && !string.IsNullOrEmpty(prefab);
	}

	private static int GetOutpostNeedAt(Character worker, ZDO zdo, string prefab, Vector3 requestedPosition)
	{
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		return GetPersistentOutpostNeedForAirHub(zdo.GetLong("vw_owner_player_id", 0L), requestedPosition, prefab);
	}

	internal static string GetDiagnosticSummary(Character worker, ZDO zdo)
	{
		if ((Object)(object)worker == (Object)null || zdo == null)
		{
			return "air: worker/zdo unavailable";
		}
		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		int loadedDepotCount = AdvancedFieldDepotRuntime.GetLoadedDepotCount();
		int outposts = 0;
		int baseHubs = 0;
		AdvancedFieldDepotRuntime.ForEachLoadedDepot(delegate(Container c)
		{
			if (!((Object)(object)c == (Object)null) && WorkerContainerRoleRuntime.SupportsAirTransport(c))
			{
				if (WorkerContainerRoleRuntime.GetAirDepotMode(c) == AirDepotMode.BaseHub)
				{
					baseHubs++;
				}
				else
				{
					outposts++;
				}
			}
		});
		int persistentOutposts = 0;
		int persistentBaseHubs = 0;
		PersistentContainerRegistryRuntime.ForEachPersistent(delegate(ZDO candidate, WorkerContainerRole role, long owner, Vector3 p)
		{
			if (candidate != null && role == WorkerContainerRole.FieldDepot && candidate.GetBool("vw_container_air_transport", false) && (owner == 0L || ownerId == 0L || owner == ownerId))
			{
				if (Mathf.Clamp(candidate.GetInt("vw_air_depot_mode", 0), 0, 1) == 1)
				{
					persistentBaseHubs++;
				}
				else
				{
					persistentOutposts++;
				}
			}
		});
		if (!HasAssignedAirDepot(worker, zdo, out var depot, out var _))
		{
			return $"air: loadedDepots={loadedDepotCount}; loadedOutposts={outposts}; loadedBaseHubs={baseHubs}; persistentOutposts={persistentOutposts}; persistentBaseHubs={persistentBaseHubs}; assignedDepot=NO";
		}
		Inventory inventory = depot.GetInventory();
		int num = ((inventory != null) ? inventory.GetAllItems().Count : 0);
		int num2 = 0;
		if (inventory != null)
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			List<ItemData> allItems = inventory.GetAllItems();
			for (int num3 = 0; num3 < allItems.Count; num3++)
			{
				ItemData val = allItems[num3];
				if (val == null || val.m_stack <= 0)
				{
					continue;
				}
				string prefabName = WorkerStorageRuntime.GetPrefabName(val);
				if (!string.IsNullOrEmpty(prefabName) && hashSet.Add(prefabName))
				{
					int num4 = WorkerStorageRuntime.CountPrefab(inventory, prefabName);
					int num5 = Mathf.Max(WorkerContainerRoleRuntime.ProtectLastItem(depot) ? 1 : 0, Mathf.Max(WorkerLogisticsRuntime.GetRequestedStock(depot, prefabName), FieldDepotStockRuleRuntime.GetTransportReserve(depot, prefabName)));
					if (num4 > num5 && GetBaseAirDepotCapacity(worker, zdo, prefabName) > 0)
					{
						num2++;
					}
				}
			}
		}
		return $"air: loadedDepots={loadedDepotCount}; loadedOutposts={outposts}; loadedBaseHubs={baseHubs}; persistentOutposts={persistentOutposts}; persistentBaseHubs={persistentBaseHubs}; assignedDepot=yes; stacks={num}; movableKinds={num2}";
	}

	private static Container FindAirDepotNear(Vector3 center, long ownerId, bool requireRoom, string prefab, AirDepotMode? requiredMode = null)
	{
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		Container bestDepot = null;
		float best = float.MaxValue;
		WorkerStorageRuntime.ForEachContainerNear(center, 20f, delegate(Container c)
		{
			//IL_005f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0065: Unknown result type (might be due to invalid IL or missing references)
			//IL_006a: Unknown result type (might be due to invalid IL or missing references)
			//IL_006f: Unknown result type (might be due to invalid IL or missing references)
			bool hasRoom = true;
			if (requireRoom && !string.IsNullOrEmpty(prefab))
			{
				Inventory inv = c != null ? c.GetInventory() : null;
				int physical = inv != null ? WorkerStorageRuntime.GetAvailableCapacityForPrefab(inv, prefab) : 0;
				hasRoom = physical > 0 && ContainerStockLimitRuntime.GetAcceptCapacity(c, prefab, physical) > 0;
			}
			if (IsUsableAirDepot(c, ownerId) && (!requiredMode.HasValue || WorkerContainerRoleRuntime.GetAirDepotMode(c) == requiredMode.Value) && hasRoom)
			{
				Vector3 val = ((Component)c).transform.position - center;
				float sqrMagnitude = val.sqrMagnitude;
				if (!(sqrMagnitude >= best))
				{
					best = sqrMagnitude;
					bestDepot = c;
				}
			}
		});
		return bestDepot;
	}

	private static bool IsUsableAirDepot(Container c, long ownerId)
	{
		if ((Object)(object)c != (Object)null && WorkerContainerRoleRuntime.GetRole(c) == WorkerContainerRole.FieldDepot && WorkerContainerRoleRuntime.SupportsAirTransport(c) && WorkerContainerRoleRuntime.HasClearSky(c) && WorkerStorageRuntime.CanUseContainer(c, ownerId))
		{
			return WorkerContainerRoleRuntime.MatchesOwner(c, ownerId);
		}
		return false;
	}

	private static int RemoveItems(Inventory inv, string prefab, int amount)
	{
		int i;
		for (i = 0; i < amount; i++)
		{
			ItemData val = WorkerStorageRuntime.FindPrefab(inv, prefab);
			if (val == null || !WorkerStorageRuntime.TryRemoveOne(inv, val))
			{
				break;
			}
		}
		return i;
	}

	private static int AddItems(Container destination, string prefabName, int amount)
	{
		if ((Object)(object)destination == (Object)null || amount <= 0)
		{
			return 0;
		}
		Inventory inventory = destination.GetInventory();
		GameObject val = (((Object)(object)ObjectDB.instance != (Object)null) ? ObjectDB.instance.GetItemPrefab(prefabName) : null);
		if ((Object)(object)val == (Object)null && (Object)(object)ZNetScene.instance != (Object)null)
		{
			val = ZNetScene.instance.GetPrefab(prefabName);
		}
		ItemDrop val2 = (((Object)(object)val != (Object)null) ? val.GetComponent<ItemDrop>() : null);
		if (val2?.m_itemData == null)
		{
			return 0;
		}
		int i;
		for (i = 0; i < amount; i++)
		{
			ItemData val3 = val2.m_itemData.Clone();
			val3.m_stack = 1;
			if (!WorkerStorageRuntime.TryAddItem(inventory, val3))
			{
				break;
			}
		}
		if (i > 0)
		{
			WorkerStorageRuntime.PersistContainer(destination, inventory);
		}
		return i;
	}
}
