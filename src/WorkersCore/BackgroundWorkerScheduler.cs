using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Valheim_Workers;

internal static class BackgroundWorkerScheduler
{
	private sealed class WorkerRecord
	{
		internal ZDO Zdo;

		internal Character LoadedCharacter;

		internal Vector3 LastPosition;

		internal float LastSeenRealtime;

		internal string Species;
	}

	internal readonly struct TransportMapSnapshot
	{
		internal readonly ZDOID Id;

		internal readonly ZDO Zdo;

		internal readonly string Species;

		internal readonly WorkerRole Role;

		internal readonly Vector3 Position;

		internal readonly float Progress01;

		internal readonly int EtaSeconds;

		internal readonly string Mode;

		internal TransportMapSnapshot(ZDOID id, ZDO zdo, string species, WorkerRole role, Vector3 position, float progress01, int etaSeconds, string mode)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0002: Unknown result type (might be due to invalid IL or missing references)
			//IL_0027: Unknown result type (might be due to invalid IL or missing references)
			//IL_0029: Unknown result type (might be due to invalid IL or missing references)
			Id = id;
			Zdo = zdo;
			Species = species ?? string.Empty;
			Role = role;
			Position = position;
			Progress01 = Mathf.Clamp01(progress01);
			EtaSeconds = Mathf.Max(0, etaSeconds);
			Mode = mode ?? string.Empty;
		}
	}

	private const string TransitEtaTicks = "vw_bg_transit_eta_ticks";

	private const string TransitActive = "vw_bg_transit_active";

	private const string VirtualPosX = "vw_bg_virtual_x";

	private const string VirtualPosY = "vw_bg_virtual_y";

	private const string VirtualPosZ = "vw_bg_virtual_z";

	private const string VirtualPosPending = "vw_bg_virtual_pending";

	private const string BackgroundMode = "vw_bg_mode";

	private const string BackgroundJob = "vw_bg_job";

	private const string BackgroundLastTickTicks = "vw_bg_last_tick_ticks";

	private const string BackgroundPendingSeconds = "vw_bg_pending_seconds";

	// v0.5.357: stationary workzones no longer run individual worker simulation while the
	// zone is unloaded. We only remember when lazy accrual started and reconcile the elapsed
	// interval once the zone becomes physical again. Transport/sorter routes remain live.
	private const string BackgroundLazyAccrual = "vw_bg_lazy_accrual";
	private const string BackgroundLazySinceTicks = "vw_bg_lazy_since_ticks";

	private const string BackgroundStatus = "vw_bg_status";

	private const string PhysicalHandoffPending = "vw_bg_physical_handoff_pending";

	private const string PhysicalHandoffSinceTicks = "vw_bg_physical_handoff_since_ticks";

	private const float UnloadedGraceSeconds = 4f;

	private const double PhysicalHandoffWaitSeconds = 8.0;

	private const float AirSpeedMetersPerSecond = 14f;

	private const float GroundSpeedMetersPerSecond = 4.5f;

	private const float AirPhysicalHandoffDistance = 110f;

	private const float GroundPhysicalHandoffDistance = 35f;

	private const float ActiveDestinationRadius = 180f;

	private const float AirPhysicalEnterRadius = 55f;

	private const float AirPhysicalExitRadius = 75f;

	internal const float PhysicalSimulationRadius = 180f;

	private const float GroundPhysicalEnterRadius = 165f;

	private const float GroundPhysicalExitRadius = 195f;

	private const string AirHybridPhaseKey = "vw_air_hybrid_phase";

	private const string AirHybridTargetKindKey = "vw_air_hybrid_target_kind";

	private const string AirHybridTargetNameKey = "vw_air_hybrid_target_name";

	private const string AirHybridStartXKey = "vw_air_hybrid_start_x";

	private const string AirHybridStartYKey = "vw_air_hybrid_start_y";

	private const string AirHybridStartZKey = "vw_air_hybrid_start_z";

	private const string AirHybridTargetXKey = "vw_air_hybrid_target_x";

	private const string AirHybridTargetYKey = "vw_air_hybrid_target_y";

	private const string AirHybridTargetZKey = "vw_air_hybrid_target_z";

	private const string AirHybridDepartureSinceTicksKey = "vw_air_hybrid_departure_since_ticks";

	private const string AirHybridArrivalSinceTicksKey = "vw_air_hybrid_arrival_since_ticks";

	private const string AirHybridFinalActiveSinceTicksKey = "vw_air_hybrid_final_active_since_ticks";

	private const string AirHybridFinalFallbackKey = "vw_air_hybrid_final_fallback";

	private const string AirHybridRouteRevisionKey = "vw_air_hybrid_route_revision";

	private const int AirHybridRouteRevision = 491;

	private const string AirReturnTransitAuthorityKey = "vw_air_return_transit_authority";

	private const string AirEmptyPickupClaimPrefabKey = "vw_air_empty_pickup_claim_prefab";

	private const string AirEmptyPickupClaimAmountKey = "vw_air_empty_pickup_claim_amount";

	private const string AirEmptyPickupClaimSourceKey = "vw_air_empty_pickup_claim_source";

	private const int AirHybridNone = 0;

	private const int AirHybridPhysicalDeparture = 1;

	private const int AirHybridBackgroundCruise = 2;

	private const int AirHybridPhysicalArrival = 3;

	private const int AirHybridPhysicalArrivalActive = 4;

	private const float AirHybridPhysicalLegDistance = 35f;

	private const float AirHybridSafeDepartureDistance = 25f;

	private const float AirHybridMinimumRouteDistance = 71f;

	private const float AirHybridTargetTolerance = 3f;

	private const double AirHybridDepartureMaxSeconds = 4.0;

	private const double AirHybridMinimumCruiseSeconds = 4.0;

	private const double AirHybridArrivalWaitSeconds = 3.0;

	private const double AirHybridFinalApproachMaxSeconds = 10.0;

	private const double MaxBackgroundDeltaSeconds = 3600.0;

	private static readonly Dictionary<ZDOID, WorkerRecord> Records = new Dictionary<ZDOID, WorkerRecord>();

	private static readonly List<ZDOID> Cleanup = new List<ZDOID>();

	private static readonly MethodInfo ZdoSetPositionMethod = typeof(ZDO).GetMethod("SetPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[1] { typeof(Vector3) }, null);

	private static readonly MethodInfo ZNetSceneGetPrefabByHashMethod = typeof(ZNetScene).GetMethod("GetPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[1] { typeof(int) }, null);

	private static float _nextTick;
	private static float _nextPersistentAirStagingTick;
	private static float _nextBackgroundKeyRefresh;
	private static readonly List<ZDOID> BackgroundTickKeys = new List<ZDOID>(256);
	private static int _backgroundTickCursor;
	private const float BackgroundSliceSeconds = 0.10f;
	private const float BackgroundKeyRefreshSeconds = 2f;
	private const int BackgroundWorkersPerSlice = 2;

	// Player-proximity checks are hit from several background-worker branches in the same
	// scheduler slice. Cache the tiny player set once per second instead of walking every
	// loaded Character for every individual worker/destination test.
	private static readonly List<Player> PlayerProximityCache = new List<Player>(8);
	private static float _nextPlayerProximityCacheRefresh;
	private const float PlayerProximityCacheSeconds = 1f;

	private static bool _persistentProductionBootstrapComplete;

	private static int _persistentProductionBootstrapPasses;

	private static float _nextPersistentBootstrapAttempt;

	private static float _nextDemandDrivenBootstrapRequest;

	private const float PersistentMaintenanceScanSeconds = 1800f;

	private const float DemandDrivenBootstrapCooldownSeconds = 20f;

	internal static int RegisteredWorkerCount => Records.Count;

	internal static void ForEachRegisteredWorker(Action<ZDO> visitor)
	{
		if (visitor == null || Records.Count == 0) return;
		foreach (WorkerRecord record in Records.Values)
		{
			ZDO zdo = record?.Zdo;
			if (zdo == null || !WorkerZdo.IsWorker(zdo)) continue;
			visitor(zdo);
		}
	}

	internal static bool IsWorkzoneFullyBackground(string areaId)
	{
		if (string.IsNullOrWhiteSpace(areaId)) return false;
		bool anyStationary = false;
		foreach (WorkerRecord record in Records.Values)
		{
			ZDO zdo = record?.Zdo;
			if (zdo == null || !WorkerZdo.IsWorker(zdo)) continue;
			if (!string.Equals(WorkerZdo.ReadWorkAreaId(zdo), areaId, StringComparison.Ordinal)) continue;
			if (WorkerJobProfileRuntime.NormalizeRuntimeRole(zdo) == WorkerRole.Hauler) continue;

			anyStationary = true;
			Character loaded = record.LoadedCharacter;
			if ((Object)(object)loaded != (Object)null && IsLoadedCharacterPhysical(loaded))
				return false;

			string mode = zdo.GetString(BackgroundMode, "physical");
			if ((Object)(object)loaded != (Object)null &&
				(string.Equals(mode, "physical", StringComparison.Ordinal) ||
				 string.Equals(mode, "handoff", StringComparison.Ordinal)))
				return false;
		}
		return anyStationary;
	}

	internal static void RegisterLoadedWorker(Character worker, ZDO zdo)
	{
		RegisterLoadedWorker(worker, zdo, IsLoadedCharacterPhysical(worker));
	}

	// v0.5.414: WorkerAiController already needs the physical-state result during its loaded
	// heartbeat. Accept that known result so the same IsAreaReady/active-area query is not run
	// twice back-to-back for every heartbeat worker.
	internal static void RegisterLoadedWorker(Character worker, ZDO zdo, bool physicalNow)
	{
		//IL_01fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_036d: Unknown result type (might be due to invalid IL or missing references)
		//IL_036f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0457: Unknown result type (might be due to invalid IL or missing references)
		//IL_0459: Unknown result type (might be due to invalid IL or missing references)
		//IL_0651: Unknown result type (might be due to invalid IL or missing references)
		//IL_066c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0687: Unknown result type (might be due to invalid IL or missing references)
		//IL_06a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_06b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_06b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0513: Unknown result type (might be due to invalid IL or missing references)
		//IL_0518: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_0331: Unknown result type (might be due to invalid IL or missing references)
		//IL_0336: Unknown result type (might be due to invalid IL or missing references)
		//IL_033e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0586: Unknown result type (might be due to invalid IL or missing references)
		//IL_058b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0428: Unknown result type (might be due to invalid IL or missing references)
		//IL_0359: Unknown result type (might be due to invalid IL or missing references)
		//IL_0443: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)worker == (Object)null || zdo == null || !WorkerZdo.IsWorker(zdo))
		{
			return;
		}
		ZDOID uid = zdo.m_uid;
		int num;
		if (Records.TryGetValue(uid, out var value))
		{
			num = ((value == null) ? 1 : 0);
			if (num == 0)
			{
				goto IL_0047;
			}
		}
		else
		{
			num = 1;
		}
		value = new WorkerRecord();
		Records[uid] = value;
		goto IL_0047;
		IL_0047:
		value.Zdo = zdo;
		value.LoadedCharacter = worker;
		value.Species = SpeciesRoleRegistry.NormalizePrefabName(((Object)((Component)worker).gameObject).name);
		zdo.Set("vw_worker_species", value.Species ?? string.Empty);
		if (num != 0)
		{
			value.LastPosition = zdo.GetPosition();
			value.LastSeenRealtime = Time.realtimeSinceStartup - 4f - 1f;
		}

		string priorMode = zdo.GetString(BackgroundMode, "physical");
		bool wasBackgroundAuthority = !string.Equals(priorMode, "physical", StringComparison.Ordinal) &&
			!string.Equals(priorMode, "handoff", StringComparison.Ordinal);

		// v0.5.357: the loaded-worker heartbeat calls this method repeatedly. Once authority is
		// stable, do not re-run every TickLoaded adapter (some of which snapshot large storage
		// networks). The heartbeat now just refreshes the liveness/position stamp.
		if (num == 0 && !string.Equals(value.Species, "Drake", StringComparison.OrdinalIgnoreCase))
		{
			if (physicalNow && string.Equals(priorMode, "physical", StringComparison.Ordinal))
			{
				value.LastSeenRealtime = Time.realtimeSinceStartup;
				value.LastPosition = ((Component)worker).transform.position;
				return;
			}
			if (!physicalNow && string.Equals(priorMode, "background", StringComparison.Ordinal))
			{
				value.LastSeenRealtime = Time.realtimeSinceStartup - UnloadedGraceSeconds - 1f;
				return;
			}
		}

		if (!physicalNow)
		{
			bool leavingPhysicalAuthority = string.Equals(zdo.GetString("vw_bg_mode", "physical"), "physical", StringComparison.Ordinal);
			if (leavingPhysicalAuthority && !string.Equals(value.Species, "Drake", StringComparison.OrdinalIgnoreCase))
			{
				// Local stuck fallback belongs only to physical presentation. Never let it leak into
				// deterministic background authority when crossing the bubble boundary.
				LocalWorkFallbackRuntime.ResetNavigationProbe(worker);
				LocalWorkFallbackRuntime.Clear(zdo);

				// Capture the real loaded-zone snapshot exactly once at the authority boundary. This
				// replaces the old repeated heartbeat snapshots while keeping background ledgers based
				// on the last physically observed state.
				RefreshLoadedBackgroundSnapshots(worker, zdo);

				long handoffTicks = DateTime.UtcNow.Ticks;
				if (ShouldUseLazyBackgroundAccrual(zdo))
				{
					zdo.Set(BackgroundLazyAccrual, true);
					zdo.Set(BackgroundLazySinceTicks, handoffTicks);
				}
				else
				{
					zdo.Set(BackgroundLazyAccrual, false);
					zdo.Set(BackgroundLazySinceTicks, 0L);
				}

				// v0.5.45: hard authority cut. The loaded-worker heartbeat may have last stamped the
				// background clock up to one second ago while physical AI was still doing real work.
				// Starting background simulation from that older timestamp would replay the tail of the
				// physical interval and can double-consume/produce one task. Start the background clock
				// exactly at the handoff instead; cargo/targets remain untouched.
				zdo.Set(BackgroundLastTickTicks, handoffTicks);
				zdo.Set(BackgroundPendingSeconds, 0f);
			}
			if (string.Equals(value.Species, "Drake", StringComparison.OrdinalIgnoreCase))
			{
				WorkerNavigationRuntime.SetAirHaulerPhysicalPresentation(worker, visible: false);
			}
			if (string.Equals(zdo.GetString("vw_bg_mode", "physical"), "physical", StringComparison.Ordinal))
			{
				value.LastPosition = ((Component)worker).transform.position;
			}
			zdo.Set("vw_bg_mode", "background");
			bool lazyBackground = !string.Equals(value.Species, "Drake", StringComparison.OrdinalIgnoreCase) && ShouldUseLazyBackgroundAccrual(zdo);
			zdo.Set("vw_bg_status", string.Equals(value.Species, "Drake", StringComparison.OrdinalIgnoreCase)
				? "air:outside-player-radius -> simulated"
				: (lazyBackground ? "worker:background-cached-until-zone-load" : "worker:outside-player-radius -> simulated"));
			// Keep the background scheduler authoritative immediately even if Valheim still has the
			// character GameObject loaded at the edge of the active zone. Physical AI is presentation
			// only; work progress belongs to the background adapters outside the player's bubble.
			value.LastSeenRealtime = Time.realtimeSinceStartup - UnloadedGraceSeconds - 1f;
			if (leavingPhysicalAuthority && lazyBackground)
				WorkzoneEconomyBackgroundRuntime.NoteWorkerBackgroundHandoff(zdo, DateTime.UtcNow.Ticks);
			return;
		}
		if (wasBackgroundAuthority && zdo.GetBool(BackgroundLazyAccrual, false))
		{
			long loadedTicks = DateTime.UtcNow.Ticks;
			WorkzoneEconomyBackgroundRuntime.StopForLoadedWorker(zdo, loadedTicks);
			ApplyLazyBackgroundCatchup(value, loadedTicks);
		}

		if (string.Equals(value.Species, "Drake", StringComparison.OrdinalIgnoreCase))
		{
			WorkerNavigationRuntime.SetAirHaulerPhysicalPresentation(worker, visible: true);
		}
		int num2 = zdo.GetInt("vw_air_hybrid_phase", 0);
		bool num3 = string.Equals(value.Species, "Drake", StringComparison.OrdinalIgnoreCase) && num2 == 3;
		bool flag = string.Equals(value.Species, "Drake", StringComparison.OrdinalIgnoreCase) && num2 == 4;
		bool flag2 = zdo.GetBool("vw_bg_physical_handoff_pending", false);
		bool flag3 = IsAuthoritativeReturnHomeTransit(zdo);
		Vector3 position3;
		float progress;
		int etaSeconds;
		string mode;
		if (num3)
		{
			Vector3 position = zdo.GetPosition();
			((Component)worker).transform.position = position;
			try
			{
				ZdoSetPositionMethod?.Invoke(zdo, new object[1] { position });
			}
			catch
			{
			}
			value.LastPosition = position;
			RestoreAirHybridTargetIfMissing(zdo);
			RestorePhysicalAirArrivalState(zdo);
			zdo.Set("vw_air_hybrid_phase", 4);
			zdo.Set("vw_air_hybrid_final_active_since_ticks", DateTime.UtcNow.Ticks);
			zdo.Set("vw_bg_virtual_pending", false);
			zdo.Set("vw_bg_physical_handoff_pending", false);
			zdo.Set("vw_bg_physical_handoff_since_ticks", 0L);
			zdo.Set("vw_bg_transit_active", false);
			zdo.Set("vw_bg_transit_eta_ticks", 0L);
			zdo.Set("vw_air_return_transit_authority", false);
			zdo.Set("vw_air_hybrid_arrival_since_ticks", 0L);
			zdo.Set("vw_bg_mode", "physical");
			zdo.Set("vw_bg_status", "air:hybrid final-100 physical active");
			try
			{
				WorkerNavigationRuntime.TrackAirHauler(worker);
			}
			catch
			{
			}
			BackgroundMonitorRuntime.Note("air:hybrid -> physical final 100m (latched)", writeLog: false);
		}
		else if (flag)
		{
			if (zdo.GetLong("vw_air_hybrid_final_active_since_ticks", 0L) <= 0)
			{
				zdo.Set("vw_air_hybrid_final_active_since_ticks", DateTime.UtcNow.Ticks);
			}
			RestoreAirHybridTargetIfMissing(zdo);
			RestorePhysicalAirArrivalState(zdo);
			zdo.Set("vw_bg_mode", "physical");
			try
			{
				WorkerNavigationRuntime.TrackAirHauler(worker);
			}
			catch
			{
			}
		}
		else if (flag2 && !flag3)
		{
			Vector3 position2 = zdo.GetPosition();
			((Component)worker).transform.position = position2;
			try
			{
				ZdoSetPositionMethod?.Invoke(zdo, new object[1] { position2 });
			}
			catch
			{
			}
			value.LastPosition = position2;
			zdo.Set("vw_bg_virtual_pending", false);
			zdo.Set("vw_bg_physical_handoff_pending", false);
			zdo.Set("vw_bg_physical_handoff_since_ticks", 0L);
			zdo.Set("vw_bg_mode", "physical");
			zdo.Set("vw_bg_status", "handoff:background-to-physical");
			if (string.Equals(value.Species, "Drake", StringComparison.OrdinalIgnoreCase))
			{
				RestorePhysicalAirArrivalState(zdo);
				try
				{
					WorkerNavigationRuntime.TrackAirHauler(worker);
				}
				catch
				{
				}
				BackgroundMonitorRuntime.Note("air:handoff -> physical at staged ZDO position; steering resumed", writeLog: false);
			}
		}
		else if (zdo.GetBool("vw_bg_transit_active", false) && TryBuildTransportTelemetry(value, DateTime.UtcNow.Ticks, out position3, out progress, out etaSeconds, out mode))
		{
			((Component)worker).transform.position = position3;
			try
			{
				ZdoSetPositionMethod?.Invoke(zdo, new object[1] { position3 });
			}
			catch
			{
			}
			value.LastPosition = position3;
			zdo.Set("vw_bg_virtual_pending", false);
			zdo.Set("vw_bg_physical_handoff_pending", false);
			zdo.Set("vw_bg_physical_handoff_since_ticks", 0L);
			zdo.Set("vw_bg_transit_active", false);
			zdo.Set("vw_bg_transit_eta_ticks", 0L);
			zdo.Set("vw_air_return_transit_authority", false);
			ResetAirHybridRoute(zdo);
			zdo.Set("vw_bg_mode", "physical");
			zdo.Set("vw_bg_status", "handoff:background-to-physical");
			if (string.Equals(value.Species, "Drake", StringComparison.OrdinalIgnoreCase))
			{
				RestorePhysicalAirArrivalState(zdo);
				try
				{
					WorkerNavigationRuntime.TrackAirHauler(worker);
				}
				catch
				{
				}
				float num4 = (((Object)(object)Player.m_localPlayer != (Object)null) ? Vector3.Distance(((Component)Player.m_localPlayer).transform.position, position3) : (-1f));
				BackgroundMonitorRuntime.Note("air:handoff -> physical; steering resumed; playerDist=" + ((num4 >= 0f) ? (num4.ToString("0") + "m") : "n/a"), writeLog: false);
			}
		}
		else if (flag3)
		{
			RestoreAirHybridTargetIfMissing(zdo);
			zdo.Set("vw_bg_physical_handoff_pending", false);
			zdo.Set("vw_bg_physical_handoff_since_ticks", 0L);
		}
		else
		{
			value.LastPosition = ((Component)worker).transform.position;
		}
		bool enteringPhysicalAuthority = !string.Equals(zdo.GetString("vw_bg_mode", "physical"), "physical", StringComparison.Ordinal);
		if (enteringPhysicalAuthority && !string.Equals(value.Species, "Drake", StringComparison.OrdinalIgnoreCase))
		{
			// Start the visible AI from a clean navigation/fallback state. Cargo and job ZDO data
			// remain untouched, so the same authoritative task continues without duplication.
			LocalWorkFallbackRuntime.ResetNavigationProbe(worker);
			LocalWorkFallbackRuntime.Clear(zdo);
		}
		value.LastSeenRealtime = Time.realtimeSinceStartup;
		zdo.Set("vw_bg_mode", "physical");
		zdo.Set("vw_bg_status", "loaded/physical");
		zdo.Set("vw_bg_last_tick_ticks", DateTime.UtcNow.Ticks);
		zdo.Set("vw_bg_pending_seconds", 0f);
		zdo.Set("vw_bg_physical_handoff_pending", false);
		zdo.Set("vw_bg_physical_handoff_since_ticks", 0L);
		zdo.Set(BackgroundLazyAccrual, false);
		zdo.Set(BackgroundLazySinceTicks, 0L);
		RefreshLoadedBackgroundSnapshots(worker, zdo);
		if (zdo.GetBool("vw_bg_virtual_pending", false))
		{
			Vector3 val = new Vector3(zdo.GetFloat("vw_bg_virtual_x", ((Component)worker).transform.position.x), zdo.GetFloat("vw_bg_virtual_y", ((Component)worker).transform.position.y), zdo.GetFloat("vw_bg_virtual_z", ((Component)worker).transform.position.z));
			((Component)worker).transform.position = val;
			zdo.Set("vw_bg_virtual_pending", false);
			value.LastPosition = val;
		}
	}

	private static bool TryForceDeterministicAirCruise(WorkerRecord record)
	{
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		//IL_012a: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0138: Unknown result type (might be due to invalid IL or missing references)
		//IL_013a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0167: Unknown result type (might be due to invalid IL or missing references)
		//IL_0169: Unknown result type (might be due to invalid IL or missing references)
		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_010b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0110: Unknown result type (might be due to invalid IL or missing references)
		ZDO val = record?.Zdo;
		if (val == null)
		{
			return false;
		}
		if (string.IsNullOrEmpty(record.Species))
		{
			record.Species = ResolvePersistentSpecies(val);
		}
		if (!string.Equals(record.Species, "Drake", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (!WorkerZdo.TryReadWorkArea(val, out var order, out var _, out var _) || order != WorkOrder.Haul)
		{
			return false;
		}
		if (!WorkerZdo.TryReadTarget(val, out var targetName, out var targetKind, out var position) || !IsHybridAirTargetKind(targetKind))
		{
			if (!TryReadPersistedAirHybridRoute(val, out targetName, out targetKind, out position))
			{
				return false;
			}
			RestoreAirHybridTargetIfMissing(val);
		}
		switch (val.GetInt("vw_air_hybrid_phase", 0))
		{
		case 2:
			val.Set("vw_bg_mode", "background");
			record.LastSeenRealtime = Time.realtimeSinceStartup - 4f - 1f;
			return true;
		default:
			return false;
		case 1:
		{
			Vector3 position2 = val.GetPosition();
			Character loadedCharacter = record.LoadedCharacter;
			if (string.Equals(val.GetString("vw_bg_mode", "physical"), "physical", StringComparison.Ordinal) && (Object)(object)loadedCharacter != (Object)null && !loadedCharacter.IsDead())
			{
				try
				{
					position2 = ((Component)loadedCharacter).transform.position;
				}
				catch
				{
				}
			}
			if (IsPlayerPhysicalAreaReady(position2, 175f))
			{
				return false;
			}
			float num = HorizontalDistance(ReadAirHybridStart(val, position2), position2);
			float num2 = HorizontalDistance(position2, position);
			double elapsedSeconds;
			bool flag = HasPhysicalDepartureTimedOut(val, out elapsedSeconds);
			if (!flag && num < AirHybridSafeDepartureDistance)
			{
				return false;
			}
			if (num2 <= AirHybridPhysicalLegDistance)
			{
				return false;
			}
			record.LastPosition = position2;
			try
			{
				ZdoSetPositionMethod?.Invoke(val, new object[1] { position2 });
			}
			catch
			{
			}
			val.Set("vw_air_hybrid_phase", 2);
			val.Set("vw_air_hybrid_arrival_since_ticks", 0L);
			val.Set("vw_bg_mode", "background");
			val.Set("vw_bg_transit_active", false);
			val.Set("vw_bg_transit_eta_ticks", 0L);
			val.Set("vw_bg_physical_handoff_pending", false);
			val.Set("vw_bg_physical_handoff_since_ticks", 0L);
			val.Set("vw_bg_status", "air:hybrid forced simulated cruise");
			record.LastSeenRealtime = Time.realtimeSinceStartup - 4f - 1f;
			string arg = (flag ? $"{AirHybridDepartureMaxSeconds:0}s cutoff ({elapsedSeconds:0.0}s, {num:0}m)" : $"distance cutoff {num:0}m");
			BackgroundMonitorRuntime.Note($"air:hybrid scheduler forced cruise at {arg}; remaining={num2:0}m", writeLog: false);
			return true;
		}
		}
	}

	private static bool EnsureDeterministicAirCruiseClock(WorkerRecord record, long nowTicks)
	{
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		ZDO val = record?.Zdo;
		if (val == null || val.GetInt("vw_air_hybrid_phase", 0) != 2)
		{
			return false;
		}
		if (!TryReadPersistedAirHybridRoute(val, out var _, out var targetKind, out var target))
		{
			return false;
		}
		RestoreAirHybridTargetIfMissing(val);
		val.Set("vw_bg_mode", "background");
		record.LastSeenRealtime = Time.realtimeSinceStartup - 4f - 1f;
		long num = val.GetLong("vw_bg_transit_eta_ticks", 0L);
		if (!val.GetBool("vw_bg_transit_active", false) || num <= 0)
		{
			float num2 = Vector3.Distance(record.LastPosition = val.GetPosition(), target);
			double airTransitSeconds = GetAirTransitSeconds(val, num2);
			num = DateTime.UtcNow.AddSeconds(airTransitSeconds).Ticks;
			val.Set("vw_bg_transit_eta_ticks", num);
			val.Set("vw_bg_transit_active", true);
			val.Set("vw_air_return_transit_authority", string.Equals(targetKind, "airhaul_return_home", StringComparison.Ordinal));
			val.Set("vw_bg_status", $"air:hybrid cruise clock eta={airTransitSeconds:0}s");
			BackgroundMonitorRuntime.Note($"air:hybrid cruise clock started; distance={num2:0}m eta={airTransitSeconds:0}s", writeLog: false);
		}
		SyncPersistentAirTransitPosition(record, nowTicks);
		return true;
	}

	internal static bool IsLoadedCharacterPhysical(Character worker)
	{
		//IL_00ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0102: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)worker == (Object)null)
		{
			return false;
		}
		try
		{
			if (!((Behaviour)worker).enabled || (Object)(object)((Component)worker).gameObject == (Object)null || !((Component)worker).gameObject.activeInHierarchy)
			{
				return false;
			}
			string species = SpeciesRoleRegistry.NormalizePrefabName(((Object)((Component)worker).gameObject).name);
			if (!string.Equals(species, "Drake", StringComparison.OrdinalIgnoreCase))
			{
				// v0.5.43: hysteresis prevents workers hovering at the edge of the player bubble from
				// bouncing physical -> background -> physical every few scheduler slices. Workers that
				// are already physical may remain physical until the wider exit radius; background
				// workers only re-enter physical authority once clearly inside the smaller enter radius.
				ZNetView groundView = ((Component)worker).GetComponent<ZNetView>();
				ZDO groundZdo = (((Object)(object)groundView != (Object)null && groundView.IsValid()) ? groundView.GetZDO() : null);
				string groundMode = groundZdo?.GetString("vw_bg_mode", "physical") ?? "physical";
				bool alreadyPhysical = string.Equals(groundMode, "physical", StringComparison.Ordinal) ||
					string.Equals(groundMode, "handoff", StringComparison.Ordinal);
				float groundRadius = alreadyPhysical ? GroundPhysicalExitRadius : GroundPhysicalEnterRadius;
				return IsPlayerPhysicalAreaReady(((Component)worker).transform.position, groundRadius);
			}
			ZNetView component = ((Component)worker).GetComponent<ZNetView>();
			ZDO val = (((Object)(object)component != (Object)null && component.IsValid()) ? component.GetZDO() : null);
			if (val == null || !WorkerZdo.IsWorker(val))
			{
				return false;
			}
			string a = val.GetString("vw_bg_mode", "physical");
			int num;
			Vector3 position;
			if (!val.GetBool("vw_bg_physical_handoff_pending", false) && !val.GetBool("vw_bg_transit_active", false) && !string.Equals(a, "background", StringComparison.Ordinal))
			{
				num = (string.Equals(a, "handoff", StringComparison.Ordinal) ? 1 : 0);
				if (num == 0)
				{
					position = ((Component)worker).transform.position;
					goto IL_00f1;
				}
			}
			else
			{
				num = 1;
			}
			position = val.GetPosition();
			goto IL_00f1;
			IL_00f1:
			Vector3 position2 = position;
			float airRadius = ((num != 0) ? 135f : 175f);
			return IsPlayerPhysicalAreaReady(position2, airRadius);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsHybridAirTargetKind(string targetKind)
	{
		if (!string.Equals(targetKind, "airhaul_base_source", StringComparison.Ordinal) && !string.Equals(targetKind, "airhaul_outpost_source", StringComparison.Ordinal) && !string.Equals(targetKind, "airhaul_base_deposit", StringComparison.Ordinal) && !string.Equals(targetKind, "airhaul_outpost_deposit", StringComparison.Ordinal))
		{
			return string.Equals(targetKind, "airhaul_return_home", StringComparison.Ordinal);
		}
		return true;
	}

	private static bool IsAuthoritativeReturnHomeTransit(ZDO zdo)
	{
		if (zdo == null || !zdo.GetBool("vw_bg_transit_active", false))
		{
			return false;
		}
		if (zdo.GetBool("vw_air_return_transit_authority", false))
		{
			return true;
		}
		if (string.Equals(zdo.GetString("vw_air_hybrid_target_kind", string.Empty), "airhaul_return_home", StringComparison.Ordinal))
		{
			zdo.Set("vw_air_return_transit_authority", true);
			return true;
		}
		if (WorkerZdo.TryReadTarget(zdo, out var _, out var targetKind, out var _) && string.Equals(targetKind, "airhaul_return_home", StringComparison.Ordinal))
		{
			zdo.Set("vw_air_return_transit_authority", true);
			return true;
		}
		return false;
	}

	private static bool IsReturnHomeShadowTransit(ZDO zdo)
	{
		if (!IsAuthoritativeReturnHomeTransit(zdo))
		{
			return false;
		}
		int num = zdo.GetInt("vw_air_hybrid_phase", 0);
		if (num != 3 && num != 4)
		{
			return false;
		}
		return true;
	}

	// The final return-home approach deliberately remains simulation-authoritative while its
	// loaded Drake is shown near the destination. All renderer controllers must agree on this;
	// otherwise one system shows the Drake and another hides it again in the same frame.
	internal static bool ShouldKeepAirHaulerVisibleDuringReturn(ZDO zdo)
	{
		if (!IsReturnHomeShadowTransit(zdo))
		{
			return false;
		}
		if (!TryReadPersistedAirHybridRoute(zdo, out var _, out var targetKind, out var target) ||
			!string.Equals(targetKind, "airhaul_return_home", StringComparison.Ordinal))
		{
			return false;
		}
		return IsDestinationActive(target);
	}

	private static float HorizontalDistance(Vector3 a, Vector3 b)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		float num = a.x - b.x;
		float num2 = a.z - b.z;
		return Mathf.Sqrt(num * num + num2 * num2);
	}

	private static Vector3 ReadAirHybridStart(ZDO zdo, Vector3 fallback)
	{
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		if (zdo == null)
		{
			return fallback;
		}
		return new Vector3(zdo.GetFloat("vw_air_hybrid_start_x", fallback.x), zdo.GetFloat("vw_air_hybrid_start_y", fallback.y), zdo.GetFloat("vw_air_hybrid_start_z", fallback.z));
	}

	private static bool HasPhysicalDepartureTimedOut(ZDO zdo, out double elapsedSeconds)
	{
		elapsedSeconds = 0.0;
		if (zdo == null || zdo.GetInt("vw_air_hybrid_phase", 0) != 1)
		{
			return false;
		}
		long ticks = DateTime.UtcNow.Ticks;
		long num = zdo.GetLong("vw_air_hybrid_departure_since_ticks", 0L);
		if (num <= 0)
		{
			zdo.Set("vw_air_hybrid_departure_since_ticks", ticks);
			return false;
		}
		elapsedSeconds = Math.Max(0.0, TimeSpan.FromTicks(Math.Max(0L, ticks - num)).TotalSeconds);
		return elapsedSeconds >= AirHybridDepartureMaxSeconds;
	}

	private static double GetAirTransitSeconds(ZDO zdo, float distance)
	{
		double num = Math.Max(4.0, distance / 14f);
		if (zdo != null && zdo.GetInt("vw_air_hybrid_phase", 0) == 2 && !zdo.GetBool("vw_air_hybrid_final_fallback", false))
		{
			num = Math.Max(AirHybridMinimumCruiseSeconds, num);
		}
		return num;
	}

	private static Vector3 ReadAirHybridTarget(ZDO zdo, Vector3 fallback)
	{
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		if (zdo == null)
		{
			return fallback;
		}
		return new Vector3(zdo.GetFloat("vw_air_hybrid_target_x", fallback.x), zdo.GetFloat("vw_air_hybrid_target_y", fallback.y), zdo.GetFloat("vw_air_hybrid_target_z", fallback.z));
	}

	private static bool TryReadPersistedAirHybridRoute(ZDO zdo, out string targetName, out string targetKind, out Vector3 target)
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		targetName = string.Empty;
		targetKind = string.Empty;
		target = default(Vector3);
		if (zdo == null || zdo.GetInt("vw_air_hybrid_phase", 0) == 0)
		{
			return false;
		}
		targetKind = zdo.GetString("vw_air_hybrid_target_kind", string.Empty);
		if (!IsHybridAirTargetKind(targetKind))
		{
			return false;
		}
		targetName = zdo.GetString("vw_air_hybrid_target_name", string.Empty);
		if (string.IsNullOrEmpty(targetName))
		{
			string prefabName;
			int amount;
			if (string.Equals(targetKind, "airhaul_return_home", StringComparison.Ordinal))
			{
				targetName = "__airhaul_home__";
			}
			else if (WorkerZdo.TryReadCargo(zdo, out prefabName, out amount))
			{
				targetName = prefabName;
			}
		}
		if (string.IsNullOrEmpty(targetName))
		{
			targetName = "__airhaul_route__";
		}
		target = ReadAirHybridTarget(zdo, zdo.GetPosition());
		return true;
	}

	private static bool RestoreAirHybridTargetIfMissing(ZDO zdo)
	{
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		if (zdo == null)
		{
			return false;
		}
		if (WorkerZdo.TryReadTarget(zdo, out var _, out var targetKind, out var _) && IsHybridAirTargetKind(targetKind))
		{
			return true;
		}
		if (!TryReadPersistedAirHybridRoute(zdo, out var targetName2, out var targetKind2, out var target))
		{
			return false;
		}
		WorkerZdo.WriteTarget(zdo, targetName2, target, targetKind2);
		return true;
	}

	private static int EnsureAirHybridRoute(ZDO zdo, Vector3 routeOrigin, string targetKind, Vector3 target, bool physicalStart)
	{
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_020a: Unknown result type (might be due to invalid IL or missing references)
		//IL_021b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0269: Unknown result type (might be due to invalid IL or missing references)
		//IL_026a: Unknown result type (might be due to invalid IL or missing references)
		if (zdo == null || !IsHybridAirTargetKind(targetKind))
		{
			return 0;
		}
		string text = zdo.GetString("vw_air_hybrid_target_kind", string.Empty);
		Vector3 a = ReadAirHybridTarget(zdo, target);
		int num = zdo.GetInt("vw_air_hybrid_phase", 0);
		if (zdo.GetInt("vw_air_hybrid_route_revision", 0) < 491)
		{
			zdo.Set("vw_air_hybrid_route_revision", 491);
			if (num == 2 && zdo.GetBool("vw_bg_transit_active", false) && zdo.GetBool("vw_air_hybrid_final_fallback", false))
			{
				zdo.Set("vw_air_hybrid_final_fallback", false);
				BackgroundMonitorRuntime.Note("air:migrated active route for one clean loaded-destination arrival", writeLog: false);
			}
		}
		Vector3 position;
		if (!string.IsNullOrEmpty(text) && string.Equals(text, targetKind, StringComparison.Ordinal) && (num != 0 || HorizontalDistance(a, target) <= 3f))
		{
			if (WorkerZdo.TryReadTarget(zdo, out var targetName, out var targetKind2, out position) && string.Equals(targetKind2, targetKind, StringComparison.Ordinal) && !string.IsNullOrEmpty(targetName))
			{
				zdo.Set("vw_air_hybrid_target_name", targetName);
			}
			int num2 = num;
			if (num2 == 1 && zdo.GetLong("vw_air_hybrid_departure_since_ticks", 0L) <= 0)
			{
				zdo.Set("vw_air_hybrid_departure_since_ticks", DateTime.UtcNow.Ticks);
			}
			if (!physicalStart && num2 == 1)
			{
				num2 = 2;
				zdo.Set("vw_air_hybrid_phase", num2);
				zdo.Set("vw_air_hybrid_departure_since_ticks", 0L);
				zdo.Set("vw_air_hybrid_arrival_since_ticks", 0L);
			}
			return num2;
		}
		zdo.Set("vw_air_hybrid_target_kind", targetKind ?? string.Empty);
		string text2 = string.Empty;
		if (WorkerZdo.TryReadTarget(zdo, out var targetName2, out var targetKind3, out position) && string.Equals(targetKind3, targetKind, StringComparison.Ordinal))
		{
			text2 = targetName2;
		}
		if (string.IsNullOrEmpty(text2) && string.Equals(targetKind, "airhaul_return_home", StringComparison.Ordinal))
		{
			text2 = "__airhaul_home__";
		}
		zdo.Set("vw_air_hybrid_target_name", text2 ?? string.Empty);
		zdo.Set("vw_air_hybrid_start_x", routeOrigin.x);
		zdo.Set("vw_air_hybrid_start_y", routeOrigin.y);
		zdo.Set("vw_air_hybrid_start_z", routeOrigin.z);
		zdo.Set("vw_air_hybrid_target_x", target.x);
		zdo.Set("vw_air_hybrid_target_y", target.y);
		zdo.Set("vw_air_hybrid_target_z", target.z);
		zdo.Set("vw_air_hybrid_departure_since_ticks", 0L);
		zdo.Set("vw_air_hybrid_arrival_since_ticks", 0L);
		zdo.Set("vw_air_hybrid_final_active_since_ticks", 0L);
		zdo.Set("vw_air_hybrid_final_fallback", false);
		zdo.Set("vw_air_hybrid_route_revision", 491);
		float num3 = HorizontalDistance(routeOrigin, target);
		float num4 = (string.Equals(targetKind, "airhaul_return_home", StringComparison.Ordinal) ? 101f : 201f);
		int num5 = ((num3 >= num4) ? (physicalStart ? 1 : 2) : 0);
		zdo.Set("vw_air_hybrid_phase", num5);
		if (num5 == 1)
		{
			zdo.Set("vw_air_hybrid_departure_since_ticks", DateTime.UtcNow.Ticks);
		}
		return num5;
	}

	internal static void BeginPhysicalAirHybridRoute(ZDO zdo, Vector3 origin, string targetKind, Vector3 target)
	{
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		if (zdo != null && IsHybridAirTargetKind(targetKind))
		{
			ResetAirHybridRoute(zdo);
			EnsureAirHybridRoute(zdo, origin, targetKind, target, physicalStart: true);
			zdo.Set("vw_bg_transit_active", false);
			zdo.Set("vw_bg_transit_eta_ticks", 0L);
			zdo.Set("vw_air_return_transit_authority", false);
			zdo.Set("vw_bg_physical_handoff_pending", false);
			zdo.Set("vw_bg_physical_handoff_since_ticks", 0L);
		}
	}

	internal static void CompletePhysicalAirHybridRoute(ZDO zdo)
	{
		if (zdo != null)
		{
			ResetAirHybridRoute(zdo);
			zdo.Set("vw_bg_transit_active", false);
			zdo.Set("vw_bg_transit_eta_ticks", 0L);
			zdo.Set("vw_air_return_transit_authority", false);
			zdo.Set("vw_bg_physical_handoff_pending", false);
			zdo.Set("vw_bg_physical_handoff_since_ticks", 0L);
		}
	}

	private static void RestorePhysicalAirArrivalState(ZDO zdo)
	{
		if (zdo != null)
		{
			string text = zdo.GetString("vw_air_hybrid_target_kind", string.Empty);
			if (string.IsNullOrEmpty(text) && WorkerZdo.TryReadTarget(zdo, out var _, out var targetKind, out var _))
			{
				text = targetKind;
			}
			if (string.Equals(text, "airhaul_base_deposit", StringComparison.Ordinal) || string.Equals(text, "airhaul_outpost_deposit", StringComparison.Ordinal))
			{
				zdo.Set("vw_state", 5);
			}
			else if (string.Equals(text, "airhaul_base_source", StringComparison.Ordinal) || string.Equals(text, "airhaul_outpost_source", StringComparison.Ordinal))
			{
				zdo.Set("vw_state", 2);
			}
			else if (string.Equals(text, "airhaul_return_home", StringComparison.Ordinal))
			{
				zdo.Set("vw_state", 6);
			}
		}
	}

	private static void ResetAirHybridRoute(ZDO zdo)
	{
		if (zdo != null)
		{
			zdo.Set("vw_air_hybrid_phase", 0);
			zdo.Set("vw_air_hybrid_target_kind", string.Empty);
			zdo.Set("vw_air_hybrid_target_name", string.Empty);
			zdo.Set("vw_air_hybrid_departure_since_ticks", 0L);
			zdo.Set("vw_air_hybrid_arrival_since_ticks", 0L);
			zdo.Set("vw_air_hybrid_final_active_since_ticks", 0L);
			zdo.Set("vw_air_hybrid_final_fallback", false);
		}
	}

	private static bool HasUsableLoadedAirCharacter(WorkerRecord record)
	{
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		Character val = record?.LoadedCharacter;
		if ((Object)(object)val == (Object)null || val.IsDead() || (Object)(object)((Component)val).gameObject == (Object)null || !((Component)val).gameObject.activeInHierarchy || !((Behaviour)val).enabled)
		{
			return false;
		}
		try
		{
			ZNetView component = ((Component)val).GetComponent<ZNetView>();
			if ((Object)(object)component == (Object)null || !component.IsValid())
			{
				return false;
			}
			ZDO zDO = component.GetZDO();
			return zDO != null && record.Zdo != null && zDO.m_uid == record.Zdo.m_uid;
		}
		catch
		{
			return false;
		}
	}

	private static bool ExpirePhysicalFinalApproachIfNeeded(ZDO zdo, Character worker, Vector3 destination, string label)
	{
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		if (zdo == null || (Object)(object)worker == (Object)null || zdo.GetInt("vw_air_hybrid_phase", 0) != 4)
		{
			return false;
		}
		long ticks = DateTime.UtcNow.Ticks;
		long num = zdo.GetLong("vw_air_hybrid_final_active_since_ticks", 0L);
		if (num <= 0)
		{
			zdo.Set("vw_air_hybrid_final_active_since_ticks", ticks);
			return false;
		}
		double num2 = Math.Max(0.0, TimeSpan.FromTicks(Math.Max(0L, ticks - num)).TotalSeconds);
		if (num2 < 10.0)
		{
			return false;
		}
		Vector3 position = ((Component)worker).transform.position;
		try
		{
			ZdoSetPositionMethod?.Invoke(zdo, new object[1] { position });
		}
		catch
		{
		}
		zdo.Set("vw_air_hybrid_phase", 2);
		zdo.Set("vw_air_hybrid_arrival_since_ticks", 0L);
		zdo.Set("vw_air_hybrid_final_active_since_ticks", 0L);
		zdo.Set("vw_air_hybrid_final_fallback", true);
		zdo.Set("vw_bg_transit_active", false);
		zdo.Set("vw_bg_transit_eta_ticks", 0L);
		zdo.Set("vw_bg_physical_handoff_pending", false);
		zdo.Set("vw_bg_physical_handoff_since_ticks", 0L);
		zdo.Set("vw_bg_status", "air:final-approach timeout -> simulated finish (" + label + ")");
		BackgroundMonitorRuntime.Note($"air:final-approach timeout after {num2:0}s -> simulated finish ({label})", writeLog: false);
		return true;
	}

	private static bool HoldOrResumeHybridArrival(WorkerRecord record, long nowTicks, Vector3 destination, string label)
	{
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0101: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_011c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0131: Unknown result type (might be due to invalid IL or missing references)
		//IL_0136: Unknown result type (might be due to invalid IL or missing references)
		ZDO val = record?.Zdo;
		if (val == null)
		{
			return false;
		}
		int num = val.GetInt("vw_air_hybrid_phase", 0);
		if (IsReturnHomeShadowTransit(val))
		{
			if (HasUsableLoadedAirCharacter(record) && IsDestinationActive(destination))
			{
				RestoreAirHybridTargetIfMissing(val);
				RestorePhysicalAirArrivalState(val);
				if (num == 3)
				{
					val.Set("vw_air_hybrid_phase", 4);
					val.Set("vw_air_hybrid_final_active_since_ticks", nowTicks);
					BackgroundMonitorRuntime.Note("air:return-home visual final active; simulation continues (" + label + ")", writeLog: false);
				}
				val.Set("vw_bg_mode", "physical");
				val.Set("vw_bg_physical_handoff_pending", false);
				val.Set("vw_bg_physical_handoff_since_ticks", 0L);
				try
				{
					WorkerNavigationRuntime.TrackAirHauler(record.LoadedCharacter);
				}
				catch
				{
				}
			}
			return false;
		}
		bool num5;
		switch (num)
		{
		case 4:
			if (!string.Equals(val.GetString("vw_air_hybrid_target_kind", string.Empty), "airhaul_return_home", StringComparison.Ordinal))
			{
				if (HasUsableLoadedAirCharacter(record))
				{
					num5 = IsDestinationActive(destination);
					goto IL_0113;
				}
			}
			else if (HasUsableLoadedAirCharacter(record))
			{
				num5 = IsPlayerPhysicalAreaReady(((Component)record.LoadedCharacter).transform.position, 175f);
				goto IL_0113;
			}
			goto IL_0177;
		default:
			return false;
		case 3:
			{
				bool num2 = string.Equals(val.GetString("vw_air_hybrid_target_kind", string.Empty), "airhaul_return_home", StringComparison.Ordinal);
				Vector3 position = val.GetPosition();
				if (!(num2 ? IsPlayerPhysicalAreaReady(position, 115f) : IsDestinationActive(destination)))
				{
					ResumeHybridBackgroundCruise(record, "destination inactive (" + label + ")");
					return false;
				}
				if (HasUsableLoadedAirCharacter(record))
				{
					RestoreAirHybridTargetIfMissing(val);
					RestorePhysicalAirArrivalState(val);
					val.Set("vw_air_hybrid_phase", 4);
					val.Set("vw_air_hybrid_final_active_since_ticks", nowTicks);
					val.Set("vw_bg_mode", "physical");
					val.Set("vw_bg_physical_handoff_pending", false);
					val.Set("vw_bg_physical_handoff_since_ticks", 0L);
					record.LastSeenRealtime = Time.realtimeSinceStartup;
					try
					{
						WorkerNavigationRuntime.TrackAirHauler(record.LoadedCharacter);
					}
					catch
					{
					}
					val.Set("vw_bg_status", "air:hybrid final-100 physical active (" + label + ")");
					BackgroundMonitorRuntime.Note("air:hybrid final-100 latched (" + label + ")", writeLog: false);
					return true;
				}
				long num3 = val.GetLong("vw_air_hybrid_arrival_since_ticks", 0L);
				if (num3 <= 0)
				{
					num3 = nowTicks;
					val.Set("vw_air_hybrid_arrival_since_ticks", num3);
				}
				double num4 = Math.Max(0.0, TimeSpan.FromTicks(Math.Max(0L, nowTicks - num3)).TotalSeconds);
				if (num4 < 3.0)
				{
					val.Set("vw_bg_mode", "handoff");
					val.Set("vw_bg_physical_handoff_pending", true);
					val.Set("vw_bg_status", $"air:hybrid materializing final-100 {num4:0.0}/{3.0:0}s ({label})");
					return true;
				}
				ResumeHybridBackgroundCruise(record, "materialize timeout (" + label + ")");
				BackgroundMonitorRuntime.Note("air:hybrid final-100 materialize timeout -> simulated finish (" + label + ")", writeLog: false);
				return false;
			}
			IL_0113:
			if (num5)
			{
				if (ExpirePhysicalFinalApproachIfNeeded(val, record.LoadedCharacter, destination, label))
				{
					record.LastPosition = ((Component)record.LoadedCharacter).transform.position;
					return false;
				}
				RestoreAirHybridTargetIfMissing(val);
				RestorePhysicalAirArrivalState(val);
				val.Set("vw_bg_mode", "physical");
				record.LastSeenRealtime = Time.realtimeSinceStartup;
				try
				{
					WorkerNavigationRuntime.TrackAirHauler(record.LoadedCharacter);
				}
				catch
				{
				}
				return true;
			}
			goto IL_0177;
			IL_0177:
			ResumeHybridBackgroundCruise(record, "final-approach unloaded (" + label + ")");
			return false;
		}
	}

	private static void ResumeHybridBackgroundCruise(WorkerRecord record, string reason)
	{
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		ZDO val = record?.Zdo;
		if (val != null)
		{
			val.Set("vw_air_hybrid_phase", 2);
			val.Set("vw_air_hybrid_arrival_since_ticks", 0L);
			val.Set("vw_air_hybrid_final_active_since_ticks", 0L);
			val.Set("vw_air_hybrid_final_fallback", true);
			val.Set("vw_bg_transit_active", false);
			val.Set("vw_bg_transit_eta_ticks", 0L);
			val.Set("vw_bg_physical_handoff_pending", false);
			val.Set("vw_bg_physical_handoff_since_ticks", 0L);
			val.Set("vw_bg_mode", "background");
			record.LastPosition = val.GetPosition();
			record.LastSeenRealtime = Time.realtimeSinceStartup - 4f - 1f;
			val.Set("vw_bg_status", "air:hybrid simulated finish; " + reason);
		}
	}

	private static bool TryStageHybridArrival(WorkerRecord record, long nowTicks, string targetKind, Vector3 destination, string label)
	{
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		ZDO val = record?.Zdo;
		if (val == null || !IsHybridAirTargetKind(targetKind))
		{
			return false;
		}
		if (val.GetBool("vw_air_hybrid_final_fallback", false))
		{
			return false;
		}
		if (EnsureAirHybridRoute(val, record.LastPosition, targetKind, destination, physicalStart: false) != 2)
		{
			return false;
		}
		if (!TryBuildTransportTelemetry(record, nowTicks, out var position, out var _, out var _, out var _))
		{
			return false;
		}
		bool flag = string.Equals(targetKind, "airhaul_return_home", StringComparison.Ordinal);
		Vector3 position2;
		if (flag)
		{
			if (!IsDestinationActive(destination))
			{
				return false;
			}
			if (HorizontalDistance(position, destination) > 100f)
			{
				return false;
			}
			position2 = ApplyAirApproachLane(val, position, destination);
		}
		else
		{
			if (!IsDestinationActive(destination))
			{
				return false;
			}
			if (HorizontalDistance(position, destination) > 100f)
			{
				return false;
			}
			position2 = ApplyAirApproachLane(val, BuildHandoffPoint(record.LastPosition, destination, 100f, air: true), destination);
		}
		if (!IsPlayerPhysicalAreaReady(position2, flag ? 115f : 180f))
		{
			return false;
		}
		Vector3 lastPosition = record.LastPosition;
		MovePersistentWorker(val, record, position2, !flag);
		if (flag)
		{
			record.LastPosition = lastPosition;
		}
		val.Set("vw_air_hybrid_phase", 3);
		val.Set("vw_air_hybrid_arrival_since_ticks", nowTicks);
		if (!flag)
		{
			val.Set("vw_bg_transit_active", false);
			val.Set("vw_bg_transit_eta_ticks", 0L);
		}
		val.Set("vw_bg_mode", "handoff");
		val.Set("vw_bg_physical_handoff_pending", !flag);
		val.Set("vw_bg_physical_handoff_since_ticks", nowTicks);
		record.LastSeenRealtime = Time.realtimeSinceStartup - 4f - 1f;
		try
		{
			if ((Object)(object)record.LoadedCharacter != (Object)null)
			{
				if (flag)
				{
					WorkerNavigationRuntime.SetAirHaulerPhysicalPresentation(record.LoadedCharacter, visible: true);
				}
				WorkerNavigationRuntime.TrackAirHauler(record.LoadedCharacter);
			}
		}
		catch
		{
		}
		val.Set("vw_bg_status", flag ? ("air:return-home visual final-100; simulation continues (" + label + ")") : ("air:hybrid staged final-100 (" + label + ")"));
		BackgroundMonitorRuntime.Note(flag ? ("air:return-home staged visual final 100m; simulation remains authoritative (" + label + ")") : ("air:hybrid staged physical final 100m (" + label + ")"), writeLog: false);
		return true;
	}

	private static bool IsAnyPlayerWithin(Vector3 position, float radius)
	{
		float radiusSqr = radius * radius;
		RefreshPlayerProximityCacheIfDue();
		for (int i = 0; i < PlayerProximityCache.Count; i++)
		{
			Player player = PlayerProximityCache[i];
			if ((Object)(object)player == (Object)null || ((Character)player).IsDead()) continue;
			Vector3 delta = ((Component)player).transform.position - position;
			if (delta.sqrMagnitude <= radiusSqr) return true;
		}
		return false;
	}

	private static void RefreshPlayerProximityCacheIfDue()
	{
		float now = Time.realtimeSinceStartup;
		if (now < _nextPlayerProximityCacheRefresh) return;
		_nextPlayerProximityCacheRefresh = now + PlayerProximityCacheSeconds;
		PlayerProximityCache.Clear();

		try
		{
			List<Player> players = WorkerCharacterIndexRuntime.GetLivePlayers();
			for (int i = 0; i < players.Count; i++)
			{
				Player player = players[i];
				if ((Object)(object)player == (Object)null || ((Character)player).IsDead()) continue;
				PlayerProximityCache.Add(player);
			}
		}
		catch
		{
			PlayerProximityCache.Clear();
			Player local = Player.m_localPlayer;
			if ((Object)(object)local != (Object)null && !((Character)local).IsDead())
				PlayerProximityCache.Add(local);
		}
	}

	private static bool IsPlayerPhysicalAreaReady(Vector3 position, float radius)
	{
		if (!IsAnyPlayerWithin(position, radius))
		{
			return false;
		}
		try
		{
			ZNetScene scene = ZNetScene.instance;
			if ((Object)(object)scene == (Object)null || scene.OutsideActiveArea(position))
			{
				return false;
			}
			return scene.IsAreaReady(position);
		}
		catch
		{
			return false;
		}
	}

	private static bool ShouldUseLazyBackgroundAccrual(ZDO zdo)
	{
		if (zdo == null || !WorkerZdo.IsWorker(zdo)) return false;
		// Haulers can switch dynamically between Transport/Sort/Provision while remote. Keep the
		// logistics role live regardless of its currently selected module; all stationary worker
		// roles can sleep and accrue elapsed workzone time until the zone is loaded again.
		WorkerRole role = WorkerJobProfileRuntime.NormalizeRuntimeRole(zdo);
		return role != WorkerRole.Hauler;
	}

	private static void EnsureLazyBackgroundAccrual(ZDO zdo, long nowTicks)
	{
		if (zdo == null) return;
		if (!zdo.GetBool(BackgroundLazyAccrual, false))
		{
			long since = zdo.GetLong(BackgroundLastTickTicks, 0L);
			if (since <= 0L || since > nowTicks) since = nowTicks;
			zdo.Set(BackgroundLazyAccrual, true);
			zdo.Set(BackgroundLazySinceTicks, since);
			zdo.Set(BackgroundMode, "background");
			zdo.Set(BackgroundStatus, "worker:background-cached-until-zone-load");
		}
	}

	private static void ApplyLazyBackgroundCatchup(WorkerRecord record, long nowTicks)
	{
		if (record == null || record.Zdo == null) return;
		ZDO zdo = record.Zdo;
		long since = zdo.GetLong(BackgroundLazySinceTicks, zdo.GetLong(BackgroundLastTickTicks, nowTicks));
		if (since <= 0L || since > nowTicks) since = nowTicks;
		// Keep the old clock intact for one authoritative adapter pass. Existing background
		// ledgers already consume a time delta and enforce their own stock/capacity limits.
		zdo.Set(BackgroundLastTickTicks, since);
		TickBackgroundWorker(record, nowTicks);
		zdo.Set(BackgroundLazyAccrual, false);
		zdo.Set(BackgroundLazySinceTicks, 0L);
	}

	private static void RefreshLoadedBackgroundSnapshots(Character worker, ZDO zdo)
	{
		if ((Object)(object)worker == (Object)null || zdo == null) return;
		// v0.5.358: capture only the ledger that belongs to this Worker's current task.
		// The previous implementation ran every production/storage adapter for every Worker
		// crossing the bubble, so one Hauler could trigger garden, forestry, stone, wood,
		// sorting and machine scans even though none of them belonged to that Worker.
		WorkzoneBackgroundCacheRuntime.CaptureRelevantSnapshot(worker, zdo);
	}

	internal static bool CanSkipStableBackgroundPhysicalCheck(Character worker, ZDO zdo, Vector3 localPlayerPosition)
	{
		if ((Object)(object)worker == (Object)null || zdo == null || !WorkerZdo.IsWorker(zdo)) return false;
		if (!string.Equals(zdo.GetString(BackgroundMode, "physical"), "background", StringComparison.Ordinal)) return false;

		string species = SpeciesRoleRegistry.NormalizePrefabName(((Object)((Component)worker).gameObject).name);
		// Drake hybrid flight has its own exact handoff/arrival logic and must keep the full check.
		if (string.Equals(species, "Drake", StringComparison.OrdinalIgnoreCase)) return false;

		// Background transport updates the ZDO position while its streamed Character may remain at
		// an old scene position. Always use the authoritative ZDO coordinate for this cheap guard.
		Vector3 position = zdo.GetPosition();
		Vector3 delta = position - localPlayerPosition;
		delta.y = 0f;
		float enter = GroundPhysicalEnterRadius;
		return delta.sqrMagnitude > enter * enter;
	}

	internal static bool ShouldUsePhysicalSimulation(Vector3 position)
	{
		return IsPlayerPhysicalAreaReady(position, 180f);
	}

	internal static void Tick()
	{
		float now = Time.realtimeSinceStartup;
		if (now < _nextTick)
		{
			return;
		}
		_nextTick = now + BackgroundSliceSeconds;

		EnsurePersistentProductionWorkersRegistered();
		WorkzoneEconomyBackgroundRuntime.Tick();
		if (now >= _nextPersistentAirStagingTick)
		{
			_nextPersistentAirStagingTick = now + 1f;
			long perfAirStaging = WorkersPerformanceProfiler.Begin();
			try
			{
				// Provisioning itself is event-driven and performs its broad reconciliation only
				// once per Valheim day (or after a rare topology/manual change). This scheduler
				// call is therefore normally just a cheap due/dirty check.
				long perfProvisioning = WorkersPerformanceProfiler.Begin();
				try
				{
					WorkerProvisioningLogisticsRuntime.TickPersistentOutpostProvisioning();
				}
				finally
				{
					WorkersPerformanceProfiler.End(WorkersPerfBucket.Provisioning, perfProvisioning);
				}
				BackgroundSorterRuntime.TickPersistentAirStaging();
				// The sorter just rebuilt the authoritative generic Base air manifest. Refresh only
				// the already-known Base hubs; do not re-walk the persistent container registry.
				WorkerProvisioningLogisticsRuntime.RefreshBaseOrderGenericSnapshot();
			}
			finally
			{
				WorkersPerformanceProfiler.End(WorkersPerfBucket.PersistentAirStaging, perfAirStaging);
			}
		}
		if (Records.Count == 0)
		{
			BackgroundTickKeys.Clear();
			_backgroundTickCursor = 0;
			return;
		}

		// v0.5.36: never advance every remote worker on the same frame. Background work is
		// time-delta based, so spreading records across small time slices preserves the amount of
		// simulated work while removing the once-per-second CPU spike seen in large settlements.
		if (now >= _nextBackgroundKeyRefresh || (BackgroundTickKeys.Count > 0 && _backgroundTickCursor >= BackgroundTickKeys.Count))
		{
			BackgroundTickKeys.Clear();
			foreach (KeyValuePair<ZDOID, WorkerRecord> pair in Records)
			{
				ZDO z = pair.Value?.Zdo;
				if (z == null || !WorkerZdo.IsWorker(z)) continue;
				if (ShouldUseLazyBackgroundAccrual(z))
				{
					EnsureLazyBackgroundAccrual(z, DateTime.UtcNow.Ticks);
					continue;
				}
				BackgroundTickKeys.Add(pair.Key);
			}
			_backgroundTickCursor = 0;
			_nextBackgroundKeyRefresh = now + BackgroundKeyRefreshSeconds;
		}

		long ticks = DateTime.UtcNow.Ticks;
		long perfWorkerSlice = WorkersPerformanceProfiler.Begin();
		try
		{
			int processed = 0;
			while (_backgroundTickCursor < BackgroundTickKeys.Count && processed < BackgroundWorkersPerSlice)
			{
				ZDOID id = BackgroundTickKeys[_backgroundTickCursor++];
				processed++;
				if (!Records.TryGetValue(id, out WorkerRecord value)) continue;
				ZDO val = value?.Zdo;
				if (val == null || !WorkerZdo.IsWorker(val))
				{
					Records.Remove(id);
					continue;
				}

				bool forcedCruise = TryForceDeterministicAirCruise(value);
				bool returnTransit = IsAuthoritativeReturnHomeTransit(val);
				bool cruiseClockReady = forcedCruise && EnsureDeterministicAirCruiseClock(value, ticks);
				if (forcedCruise || returnTransit || !(now - value.LastSeenRealtime < UnloadedGraceSeconds))
				{
					// v0.5.357: static production zones are lazy. Nothing in the unloaded
					// scene needs an AI tick; elapsed time is reconciled when the zone becomes
					// physical again. Live transport/sorter routes remain continuously simulated.
					if (!forcedCruise && !returnTransit && ShouldUseLazyBackgroundAccrual(val))
					{
						EnsureLazyBackgroundAccrual(val, ticks);
						continue;
					}

					TickBackgroundWorker(value, ticks);
					if (forcedCruise && !cruiseClockReady) EnsureDeterministicAirCruiseClock(value, ticks);
				}
			}
		}
		finally
		{
			WorkersPerformanceProfiler.End(WorkersPerfBucket.BackgroundWorkerSlice, perfWorkerSlice);
		}
	}

	private static void EnsurePersistentProductionWorkersRegistered()
	{
		float realtimeSinceStartup = Time.realtimeSinceStartup;
		if (realtimeSinceStartup < _nextPersistentBootstrapAttempt)
		{
			return;
		}
		_nextPersistentBootstrapAttempt = realtimeSinceStartup + 2f;
		ZDOMan instance = ZDOMan.instance;
		if (instance == null)
		{
			return;
		}
		long perfBootstrap = WorkersPerformanceProfiler.Begin();
		try
		{
			int count = Records.Count;
			FieldInfo[] fields = typeof(ZDOMan).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			for (int i = 0; i < fields.Length; i++)
			{
				object value;
				try
				{
					value = fields[i].GetValue(instance);
				}
				catch
				{
					continue;
				}
				HarvestPersistentProductionWorkers(value, 0);
			}
			if (!_persistentProductionBootstrapComplete)
			{
				// v0.5.357: one startup recovery pass is enough. Newly created Workers are
				// registered by lifecycle hooks while their zone is loaded, so a second reflected
				// ZDOMan sweep ten seconds later only creates an avoidable hitch.
				_persistentProductionBootstrapPasses = 1;
				_persistentProductionBootstrapComplete = true;
				_nextPersistentBootstrapAttempt = realtimeSinceStartup + PersistentMaintenanceScanSeconds;
			}
			else
			{
				_nextPersistentBootstrapAttempt = realtimeSinceStartup + PersistentMaintenanceScanSeconds;
			}
			int num = Records.Count - count;
			if (num > 0)
			{
				BackgroundMonitorRuntime.Note($"Persistent worker scan registered {num} worker(s); total={Records.Count}");
			}
		}
		catch
		{
		}
		finally
		{
			WorkersPerformanceProfiler.End(WorkersPerfBucket.PersistentBootstrap, perfBootstrap);
		}
	}

	internal static void RequestPersistentLogisticsRescan()
	{
		// v0.5.230: A changed logistics target/order does NOT mean that new Workers appeared in
		// the world. The old implementation forced a reflected ZDOMan-wide worker discovery pass
		// (throttled to 20 s), which could create visible multi-second hitches in large worlds.
		// Existing worker/container lifecycle hooks already register topology immediately; for an
		// ordinary demand change we only need to wake the cached scheduler/order paths.
		_nextTick = 0f;
		_nextPersistentAirStagingTick = 0f;
		_nextBackgroundKeyRefresh = 0f;
	}

	private static void HarvestPersistentProductionWorkers(object value, int depth)
	{
		if (value == null || depth > 2)
		{
			return;
		}
		ZDO val = (ZDO)((value is ZDO) ? value : null);
		if (val != null)
		{
			RememberPersistentProductionWorker(val);
			return;
		}
		if (value is IDictionary dictionary)
		{
			{
				foreach (DictionaryEntry item in dictionary)
				{
					object? value2 = item.Value;
					ZDO val2 = (ZDO)((value2 is ZDO) ? value2 : null);
					if (val2 != null)
					{
						RememberPersistentProductionWorker(val2);
					}
					else if (depth < 2)
					{
						HarvestPersistentProductionWorkers(item.Value, depth + 1);
					}
				}
				return;
			}
		}
		if (!(value is IEnumerable enumerable) || value is string)
		{
			return;
		}
		int num = 0;
		foreach (object item2 in enumerable)
		{
			if (++num > 200000)
			{
				break;
			}
			ZDO val3 = (ZDO)((item2 is ZDO) ? item2 : null);
			if (val3 != null)
			{
				RememberPersistentProductionWorker(val3);
			}
			else if (depth < 2)
			{
				HarvestPersistentProductionWorkers(item2, depth + 1);
			}
		}
	}

	private static void RememberPersistentProductionWorker(ZDO zdo, string speciesHint = null)
	{
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		if (zdo == null || !WorkerZdo.IsWorker(zdo) || !WorkerZdo.TryReadWorkArea(zdo, out var order, out var _, out var _))
		{
			return;
		}
		BackgroundJobDescriptor backgroundJobDescriptor = BackgroundJobCatalog.Get(order);
		if (!backgroundJobDescriptor.ImplementedNow || backgroundJobDescriptor.Kind == BackgroundSimulationKind.None)
		{
			return;
		}
		ZDOID uid = zdo.m_uid;
		string text = SpeciesRoleRegistry.NormalizePrefabName(speciesHint ?? string.Empty);
		if (string.IsNullOrEmpty(text))
		{
			text = ResolvePersistentSpecies(zdo);
		}
		if (Records.TryGetValue(uid, out var value))
		{
			if (value != null && string.IsNullOrEmpty(value.Species) && !string.IsNullOrEmpty(text))
			{
				value.Species = text;
			}
		}
		else
		{
			Records[uid] = new WorkerRecord
			{
				Zdo = zdo,
				LastPosition = zdo.GetPosition(),
				LastSeenRealtime = Time.realtimeSinceStartup - 4f - 1f,
				Species = text
			};
			if (ShouldUseLazyBackgroundAccrual(zdo))
			{
				long remoteTicks = DateTime.UtcNow.Ticks;
				zdo.Set(BackgroundMode, "background");
				EnsureLazyBackgroundAccrual(zdo, remoteTicks);
				WorkzoneEconomyBackgroundRuntime.RegisterRemoteWorker(zdo, remoteTicks);
			}
		}
	}

	internal static bool RegisterPersistentWorkerFromHome(ZDO homeZdo)
	{
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		if (homeZdo == null || !WorkerHomeAnchorZdo.IsAssigned(homeZdo) || ZDOMan.instance == null)
		{
			return false;
		}
		ZDOID val = WorkerHomeAnchorZdo.ReadAssignedWorkerZdoId(homeZdo);
		if (val.Equals(ZDOID.None))
		{
			return false;
		}
		ZDO zDO = ZDOMan.instance.GetZDO(val);
		if (zDO == null || !WorkerZdo.IsWorker(zDO))
		{
			return false;
		}
		string text = SpeciesRoleRegistry.NormalizePrefabName(WorkerHomeAnchorZdo.ReadWorkerSpecies(homeZdo));
		if (string.IsNullOrEmpty(text) && string.Equals(WorkerHomeAnchorZdo.ReadAnchorKind(homeZdo), "DrakePerch", StringComparison.Ordinal))
		{
			text = "Drake";
		}
		bool flag = Records.ContainsKey(val);
		RememberPersistentProductionWorker(zDO, text);
		bool num = Records.ContainsKey(val);
		if (num && !flag)
		{
			BackgroundMonitorRuntime.Note($"Registered remote worker from home: species={text}, worker={val}, anchor={WorkerHomeAnchorZdo.ReadAnchorKind(homeZdo)}");
		}
		return num;
	}

	private static string ResolvePersistentSpecies(ZDO zdo)
	{
		if (zdo == null)
		{
			return string.Empty;
		}
		string text = SpeciesRoleRegistry.NormalizePrefabName(zdo.GetString("vw_worker_species", string.Empty));
		if (!string.IsNullOrEmpty(text))
		{
			return text;
		}
		if (string.Equals(zdo.GetString("vw_home_anchor_type", string.Empty), "DrakePerch", StringComparison.Ordinal))
		{
			text = "Drake";
		}
		if (string.IsNullOrEmpty(text) && (Object)(object)ZNetScene.instance != (Object)null && ZNetSceneGetPrefabByHashMethod != null)
		{
			try
			{
				object? obj = ZNetSceneGetPrefabByHashMethod.Invoke(ZNetScene.instance, new object[1] { zdo.GetPrefab() });
				GameObject val = (GameObject)((obj is GameObject) ? obj : null);
				if ((Object)(object)val != (Object)null)
				{
					text = SpeciesRoleRegistry.NormalizePrefabName(((Object)val).name);
				}
			}
			catch
			{
			}
		}
		if (!string.IsNullOrEmpty(text))
		{
			zdo.Set("vw_worker_species", text);
		}
		return text ?? string.Empty;
	}

	private const string BackgroundHaulerModuleCheckTicksKey = "vw_bg_hauler_module_check_ticks";
	private static readonly long BackgroundHaulerModuleCheckIntervalTicks = TimeSpan.FromSeconds(4.0).Ticks;

	private static WorkOrder ResolveBackgroundHaulerWorkOrder(ZDO zdo, WorkOrder current, long nowTicks)
	{
		WorkerTaskFlags tasks = WorkerJobProfileRuntime.ResolveEffectiveHaulerTasks(zdo);
		bool transport = (tasks & WorkerTaskFlags.Transport) != 0;
		bool sort = (tasks & WorkerTaskFlags.Sort) != 0;
		bool provision = (tasks & WorkerTaskFlags.Provision) != 0;
		if (!transport && !sort && !provision) return WorkOrder.None;

		int enabledCount = (transport ? 1 : 0) + (sort ? 1 : 0) + (provision ? 1 : 0);
		if (enabledCount == 1)
		{
			WorkOrder only = provision ? WorkOrder.Provision : (sort ? WorkOrder.Sort : WorkOrder.Haul);
			if (current != only) zdo.Set("vw_work_order", (int)only);
			return only;
		}

		// Never steal a real load from the module that owns its route. This mirrors the loaded AI
		// and is deliberately checked before the throttled idle demand probe.
		if (WorkerZdo.HasCargo(zdo) && WorkerZdo.TryReadCargoRoute(zdo, out string route, out _))
		{
			bool provisionRoute = string.Equals(route, WorkerProvisioningLogisticsRuntime.RouteProvisionHome, StringComparison.Ordinal);
			bool sorterRoute = string.Equals(route, WorkerLogisticsRuntime.RouteSortImport, StringComparison.Ordinal) ||
			                   string.Equals(route, WorkerLogisticsRuntime.RouteSortExport, StringComparison.Ordinal) ||
			                   string.Equals(route, AirHaulerWorkRuntime.RouteSortInbound, StringComparison.Ordinal) ||
			                   string.Equals(route, AirHaulerWorkRuntime.RouteSortOutbound, StringComparison.Ordinal);
			bool haulRoute = string.Equals(route, WorkerLogisticsRuntime.RouteHaulInbound, StringComparison.Ordinal) ||
			                 string.Equals(route, WorkerLogisticsRuntime.RouteHaulOutbound, StringComparison.Ordinal) ||
			                 string.Equals(route, AirHaulerWorkRuntime.RouteInbound, StringComparison.Ordinal) ||
			                 string.Equals(route, AirHaulerWorkRuntime.RouteOutbound, StringComparison.Ordinal);
			WorkOrder routed = provisionRoute && provision ? WorkOrder.Provision :
			                  sorterRoute && sort ? WorkOrder.Sort :
			                  haulRoute && transport ? WorkOrder.Haul : current;
			if (routed != current) zdo.Set("vw_work_order", (int)routed);
			return routed;
		}

		// Persistent probing runs only every few seconds per worker. Provision demand checks only
		// durable worker/home ZDOs and does not scan physical containers.
		long nextCheck = zdo.GetLong(BackgroundHaulerModuleCheckTicksKey, 0L);
		if (nowTicks < nextCheck) return current;
		zdo.Set(BackgroundHaulerModuleCheckTicksKey, nowTicks + BackgroundHaulerModuleCheckIntervalTicks);

		bool haulPending = transport && BackgroundGroundPickupRuntime.HasPersistentPickupWork(zdo);
		bool sortPending = sort && BackgroundSorterRuntime.HasPersistentSortWork(zdo);
		bool provisionPending = provision && WorkerProvisioningLogisticsRuntime.HasPersistentDemand(zdo);
		WorkerTaskFlags pendingTasks = WorkerTaskFlags.None;
		if (haulPending) pendingTasks |= WorkerTaskFlags.Transport;
		if (sortPending) pendingTasks |= WorkerTaskFlags.Sort;
		if (provisionPending) pendingTasks |= WorkerTaskFlags.Provision;

		WorkOrder resolved = WorkerJobProfileRuntime.SelectBestAvailableOrder(zdo, WorkerRole.Hauler, current, pendingTasks);
		if (resolved == WorkOrder.None)
		{
			if ((current == WorkOrder.Haul && transport) || (current == WorkOrder.Sort && sort) ||
			    (current == WorkOrder.Provision && provision)) resolved = current;
			else resolved = WorkerJobProfileRuntime.SelectBestAvailableOrder(zdo, WorkerRole.Hauler, current, tasks);
		}

		if (resolved != current)
		{
			zdo.Set("vw_work_order", (int)resolved);
			string reason = resolved == WorkOrder.Haul ? "persistent transport demand / worker priority" :
			                resolved == WorkOrder.Sort ? "persistent sort demand / worker priority" :
			                "persistent provision demand / worker priority";
			zdo.Set("vw_bg_hauler_module_reason", reason);
		}
		return resolved;
	}

	private static WorkOrder ResolveBackgroundGathererWorkOrder(ZDO zdo, WorkOrder current)
	{
		WorkerTaskFlags tasks = WorkerJobProfileRuntime.ResolveEffectiveGathererTasks(zdo);
		bool gather = (tasks & WorkerTaskFlags.Gather) != 0;
		bool harvest = (tasks & WorkerTaskFlags.Harvest) != 0;
		if (!gather && !harvest) return WorkOrder.None;

		bool gatherPending = gather && BackgroundGathererRuntime.HasBackgroundWork(zdo);
		bool harvestPending = harvest && BackgroundHarvesterRuntime.HasBackgroundWork(zdo);
		WorkerTaskFlags pendingTasks = WorkerTaskFlags.None;
		if (gatherPending) pendingTasks |= WorkerTaskFlags.Gather;
		if (harvestPending) pendingTasks |= WorkerTaskFlags.Harvest;

		WorkOrder selected = WorkerJobProfileRuntime.SelectBestAvailableOrder(zdo, WorkerRole.Gatherer, current, pendingTasks);
		if (selected != WorkOrder.None) return selected;

		if (current == WorkOrder.GatherResources && gather) return current;
		if (current == WorkOrder.Harvest && harvest) return current;
		return WorkerJobProfileRuntime.SelectBestAvailableOrder(zdo, WorkerRole.Gatherer, current, tasks);
	}

	private static WorkOrder ResolveBackgroundGardenerWorkOrder(ZDO zdo, WorkOrder current)
	{
		WorkerTaskFlags tasks = WorkerJobProfileRuntime.ResolveEffectiveGardenerTasks(zdo);
		bool garden = (tasks & WorkerTaskFlags.Garden) != 0;
		bool forestry = (tasks & WorkerTaskFlags.Forestry) != 0;
		bool harvest = (tasks & WorkerTaskFlags.Harvest) != 0;
		bool bake = (tasks & WorkerTaskFlags.Bake) != 0;
		if (!garden && !forestry && !harvest && !bake) return WorkOrder.None;

		bool harvestPending = harvest && BackgroundHarvesterRuntime.HasBackgroundWork(zdo);
		bool gardenPending = garden && GardenBackgroundRuntime.HasBackgroundGardenWork(zdo);
		bool forestryPending = forestry && ForestryBackgroundRuntime.HasBackgroundWork(zdo);
		bool bakePending = bake && BackgroundBakerRuntime.HasBackgroundWork(zdo);

		WorkerTaskFlags pendingTasks = WorkerTaskFlags.None;
		if (harvestPending) pendingTasks |= WorkerTaskFlags.Harvest;
		if (gardenPending) pendingTasks |= WorkerTaskFlags.Garden;
		if (forestryPending) pendingTasks |= WorkerTaskFlags.Forestry;
		if (bakePending) pendingTasks |= WorkerTaskFlags.Bake;

		WorkOrder selected = WorkerJobProfileRuntime.SelectBestAvailableOrder(zdo, WorkerRole.Gardener, current, pendingTasks);
		if (selected != WorkOrder.None) return selected;

		if (current == WorkOrder.Harvest && harvest) return current;
		if (current == WorkOrder.Garden && garden) return current;
		if (current == WorkOrder.Forestry && forestry) return current;
		if (current == WorkOrder.Bake && bake) return current;
		return WorkerJobProfileRuntime.SelectBestAvailableOrder(zdo, WorkerRole.Gardener, current, tasks);
	}

	private static void TickBackgroundWorker(WorkerRecord record, long nowTicks)
	{
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_049c: Unknown result type (might be due to invalid IL or missing references)
		ZDO zdo = record.Zdo;
		if (WorkerSustenanceRuntime.ShouldPauseBackground(zdo, nowTicks, out string supplyStatus))
		{
			zdo.Set("vw_bg_mode", "background");
			zdo.Set("vw_bg_job", 0);
			zdo.Set("vw_bg_status", supplyStatus ?? "versorgung:wartet");
			zdo.Set("vw_bg_last_tick_ticks", nowTicks);
			zdo.Set("vw_bg_pending_seconds", 0f);
			return;
		}
		if (zdo.GetBool("vw_bg_physical_handoff_pending", false) && IsAuthoritativeReturnHomeTransit(zdo))
		{
			zdo.Set("vw_bg_physical_handoff_pending", false);
			zdo.Set("vw_bg_physical_handoff_since_ticks", 0L);
		}
		else if (zdo.GetBool("vw_bg_physical_handoff_pending", false))
		{
			Vector3 position = zdo.GetPosition();
			record.LastPosition = position;
			zdo.Set("vw_bg_physical_handoff_pending", false);
			zdo.Set("vw_bg_physical_handoff_since_ticks", 0L);
			zdo.Set("vw_bg_transit_active", false);
			zdo.Set("vw_bg_transit_eta_ticks", 0L);
			zdo.Set("vw_bg_mode", "background");
			zdo.Set("vw_bg_status", "air:migrated-staged-handoff-to-continuous-background");
			BackgroundMonitorRuntime.Note("air:removed staged physical handoff; continuous background travel resumed", writeLog: false);
		}
		if (!WorkerZdo.TryReadWorkArea(zdo, out var order, out var center, out var _))
		{
			zdo.Set("vw_bg_mode", "background");
			zdo.Set("vw_bg_job", 0);
			zdo.Set("vw_bg_status", "idle:no-workzone");
			zdo.Set("vw_bg_last_tick_ticks", nowTicks);
			zdo.Set("vw_bg_pending_seconds", 0f);
			return;
		}
		WorkerRole workerRole = WorkerJobProfileRuntime.NormalizeRuntimeRole(zdo);
		if (workerRole == WorkerRole.Hauler && (order == WorkOrder.Haul || order == WorkOrder.Sort || order == WorkOrder.Provision))
		{
			order = ResolveBackgroundHaulerWorkOrder(zdo, order, nowTicks);
		}
		else if (workerRole == WorkerRole.Gatherer && (order == WorkOrder.GatherResources || order == WorkOrder.Harvest))
		{
			WorkOrder resolvedGatherer = ResolveBackgroundGathererWorkOrder(zdo, order);
			if (resolvedGatherer != WorkOrder.None && resolvedGatherer != order)
			{
				order = resolvedGatherer;
				zdo.Set("vw_work_order", (int)order);
			}
		}
		else if (workerRole == WorkerRole.Gardener && (order == WorkOrder.Garden || order == WorkOrder.Forestry || order == WorkOrder.Harvest || order == WorkOrder.Bake))
		{
			WorkOrder resolvedGardener = ResolveBackgroundGardenerWorkOrder(zdo, order);
			if (resolvedGardener != WorkOrder.None && resolvedGardener != order)
			{
				order = resolvedGardener;
				zdo.Set("vw_work_order", (int)order);
			}
		}
		else if (workerRole == WorkerRole.Hunter)
		{
			WorkerTaskFlags hunterTasks = WorkerJobProfileRuntime.ReadTasks(zdo, WorkerRole.Hunter);
			bool huntEnabled = (hunterTasks & WorkerTaskFlags.Hunt) != 0;
			bool guardEnabled = (hunterTasks & WorkerTaskFlags.Guard) != 0;
			if (!huntEnabled && !guardEnabled)
			{
				zdo.Set("vw_bg_mode", "background");
				zdo.Set("vw_bg_job", 0);
				zdo.Set("vw_bg_status", "hunter:jobs-disabled");
				zdo.Set("vw_bg_pending_seconds", 0f);
				return;
			}

			WorkerTaskFlags availableHunterTasks = WorkerTaskFlags.None;
			if (huntEnabled && BackgroundHunterRuntime.HasBackgroundWork(zdo)) availableHunterTasks |= WorkerTaskFlags.Hunt;
			if (guardEnabled) availableHunterTasks |= WorkerTaskFlags.Guard;
			WorkOrder hunterOrder = WorkerJobProfileRuntime.SelectBestAvailableOrder(zdo, WorkerRole.Hunter, order, availableHunterTasks);
			if (hunterOrder == WorkOrder.None) hunterOrder = guardEnabled ? WorkOrder.Guard : WorkOrder.Hunt;

			if (hunterOrder != order)
			{
				order = hunterOrder;
				zdo.Set("vw_work_order", (int)order);
			}
		}

		if (!WorkerJobProfileRuntime.IsOrderEnabled(zdo, workerRole, order))
		{
			zdo.Set("vw_bg_mode", "background");
			zdo.Set("vw_bg_job", (int)order);
			zdo.Set("vw_bg_status", "disabled:" + order);
			zdo.Set("vw_bg_pending_seconds", 0f);
			return;
		}

		BackgroundJobDescriptor backgroundJobDescriptor = BackgroundJobCatalog.Get(order);
		zdo.Set("vw_bg_mode", "background");
		zdo.Set("vw_bg_job", (int)order);
		long num2 = zdo.GetLong("vw_bg_last_tick_ticks", 0L);
		double val = ((num2 > 0) ? TimeSpan.FromTicks(Math.Max(0L, nowTicks - num2)).TotalSeconds : 0.0);
		val = Math.Min(3600.0, val);
		zdo.Set("vw_bg_last_tick_ticks", nowTicks);
		if (workerRole == WorkerRole.Gatherer && (order == WorkOrder.GatherResources || order == WorkOrder.Harvest))
		{
			WorkerTaskFlags gathererTasks = WorkerJobProfileRuntime.ResolveEffectiveGathererTasks(zdo);
			if (order == WorkOrder.Harvest)
			{
				if ((gathererTasks & WorkerTaskFlags.Harvest) == 0)
				{
					zdo.Set("vw_bg_pending_seconds", 0f);
					zdo.Set("vw_bg_status", "gatherer:harvest-disabled");
					return;
				}
				zdo.Set("vw_bg_status", BackgroundHarvesterRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency));
				return;
			}

			if ((gathererTasks & WorkerTaskFlags.Gather) == 0)
			{
				zdo.Set("vw_bg_pending_seconds", 0f);
				zdo.Set("vw_bg_status", "gatherer:gather-disabled");
				return;
			}
			zdo.Set("vw_bg_status", BackgroundGathererRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency));
			return;
		}

		if (workerRole == WorkerRole.Gardener && (order == WorkOrder.Garden || order == WorkOrder.Forestry || order == WorkOrder.Harvest))
		{
			WorkerTaskFlags num4 = WorkerJobProfileRuntime.ResolveEffectiveGardenerTasks(zdo);
			bool flag5 = (num4 & WorkerTaskFlags.Garden) != 0;
			bool flag6 = (num4 & WorkerTaskFlags.Forestry) != 0;
			bool flag7 = (num4 & WorkerTaskFlags.Harvest) != 0;
			if (order == WorkOrder.Harvest)
			{
				if (!flag7)
				{
					zdo.Set("vw_bg_pending_seconds", 0f);
					zdo.Set("vw_bg_status", "gardener:harvest-disabled");
					return;
				}
				zdo.Set("vw_bg_status", BackgroundHarvesterRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency));
				return;
			}
			if (order == WorkOrder.Garden && !flag5)
			{
				zdo.Set("vw_bg_pending_seconds", 0f);
				zdo.Set("vw_bg_status", flag6 ? "gardener:waiting-for-physical-task-switch-to-forestry" : (flag7 ? "gardener:waiting-for-physical-task-switch-to-harvest" : "gardener:garden-disabled"));
				return;
			}
			if (order == WorkOrder.Forestry && !flag6)
			{
				zdo.Set("vw_bg_pending_seconds", 0f);
				zdo.Set("vw_bg_status", flag5 ? "gardener:waiting-for-physical-task-switch-to-garden" : (flag7 ? "gardener:waiting-for-physical-task-switch-to-harvest" : "gardener:forestry-disabled"));
				return;
			}
		}
		if (backgroundJobDescriptor.Kind == BackgroundSimulationKind.None)
		{
			zdo.Set("vw_bg_status", "unsupported:" + order);
			zdo.Set("vw_bg_pending_seconds", 0f);
			return;
		}
		if (!backgroundJobDescriptor.ImplementedNow)
		{
			zdo.Set("vw_bg_pending_seconds", 0f);
			zdo.Set("vw_bg_status", "framework-ready:" + backgroundJobDescriptor.Name);
			return;
		}
		switch (order)
		{
		case WorkOrder.Haul:
			if (string.IsNullOrEmpty(record.Species))
			{
				record.Species = ResolvePersistentSpecies(zdo);
			}
			if (string.Equals(record.Species, "Drake", StringComparison.OrdinalIgnoreCase))
			{
				RestoreAirHybridTargetIfMissing(zdo);
				zdo.Set("vw_bg_status", "active:air-transport");
				TickAirTransport(record, nowTicks);
			}
			else
			{
				zdo.Set("vw_bg_status", "active:ground-transport");
				TickGroundTransport(record, nowTicks);
			}
			break;
		case WorkOrder.Sort:
			zdo.Set("vw_bg_pending_seconds", 0f);
			zdo.Set("vw_bg_status", BackgroundSorterRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency));
			break;
		case WorkOrder.Provision:
			zdo.Set("vw_bg_pending_seconds", 0f);
			zdo.Set("vw_bg_status", WorkerProvisioningLogisticsRuntime.HasPersistentDemand(zdo)
				? "active:provisioning-demand"
				: "idle:provisioning-no-demand");
			break;
		case WorkOrder.KeepFires:
		{
			zdo.Set("vw_bg_pending_seconds", 0f);
			if (WorkerZdo.TryReadFireAnchor(zdo, out var fireZdoId, out center) && !fireZdoId.Equals(ZDOID.None))
			{
				zdo.Set("vw_bg_status", FirekeeperAssignedFireRuntime.DescribeBackground(zdo));
			}
			else
			{
				zdo.Set("vw_bg_status", BackgroundMachineProductionRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency));
			}
			break;
		}
		case WorkOrder.Stonework:
		{
			string text8 = StoneResourceLedgerRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency);
			zdo.Set("vw_bg_status", text8);
			break;
		}
		case WorkOrder.FellTrees:
		{
			string text7 = WoodResourceLedgerRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency);
			zdo.Set("vw_bg_status", text7);
			break;
		}
		case WorkOrder.Forestry:
		{
			string text6 = ForestryBackgroundRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency);
			zdo.Set("vw_bg_status", text6);
			break;
		}
		case WorkOrder.Garden:
		{
			string text5 = GardenBackgroundRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency);
			zdo.Set("vw_bg_status", text5);
			break;
		}
		case WorkOrder.GatherResources:
		{
			string text4 = BackgroundGathererRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency);
			zdo.Set("vw_bg_status", text4);
			break;
		}
		case WorkOrder.Harvest:
		{
			string text3 = BackgroundHarvesterRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency);
			zdo.Set("vw_bg_status", text3);
			break;
		}
		case WorkOrder.Hunt:
		{
			string huntStatus = BackgroundHunterRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency);
			zdo.Set("vw_bg_status", huntStatus);
			break;
		}
		case WorkOrder.Fish:
		{
			string fishStatus = BackgroundFisherRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency);
			zdo.Set("vw_bg_status", fishStatus);
			break;
		}
		case WorkOrder.Bake:
		{
			string bakeStatus = BackgroundBakerRuntime.TickBackground(zdo, val, backgroundJobDescriptor.Efficiency);
			zdo.Set("vw_bg_status", bakeStatus);
			break;
		}
		case WorkOrder.Guard:
			zdo.Set("vw_bg_pending_seconds", 0f);
			zdo.Set("vw_bg_status", "guard:background-watch-active");
			break;
		case WorkOrder.Butcher:
			zdo.Set("vw_bg_pending_seconds", 0f);
			zdo.Set("vw_bg_status", BackgroundMaintenanceRuntime.TickButcherCatchup(record.LoadedCharacter, zdo, val, backgroundJobDescriptor.Efficiency));
			break;
		case WorkOrder.Smith:
			zdo.Set("vw_bg_pending_seconds", 0f);
			zdo.Set("vw_bg_status", BackgroundMaintenanceRuntime.TickSmithCatchup(record.LoadedCharacter, zdo, val, backgroundJobDescriptor.Efficiency));
			break;
		default:
			zdo.Set("vw_bg_status", "waiting:adapter:" + backgroundJobDescriptor.Name);
			zdo.Set("vw_bg_pending_seconds", 0f);
			break;
		}
	}

	private static bool TryRestoreAirDeliveryTargetFromCargoRoute(WorkerRecord record, string cargoPrefab, out string targetKind, out Vector3 target)
	{
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		targetKind = string.Empty;
		target = default(Vector3);
		ZDO val = record?.Zdo;
		if (val == null || string.IsNullOrEmpty(cargoPrefab))
		{
			return false;
		}
		if (!WorkerZdo.TryReadCargoRoute(val, out var route, out var target2))
		{
			return false;
		}
		long ownerId = val.GetLong("vw_owner_player_id", 0L);
		ZDO depotZdo;
		if (string.Equals(route, "airhaul_outbound", StringComparison.Ordinal))
		{
			targetKind = "airhaul_outpost_deposit";
			target = target2;
			if (WorkerLogisticsRuntime.TryFindPersistentAirDepot(target2, ownerId, AirDepotMode.Outpost, out depotZdo, out var position) && HorizontalDistance(position, target2) <= 80f)
			{
				target = position;
			}
		}
		else
		{
			if (!string.Equals(route, "airhaul_inbound", StringComparison.Ordinal))
			{
				return false;
			}
			targetKind = "airhaul_base_deposit";
			if (!WorkerLogisticsRuntime.TryFindPersistentAirDepot(record.LastPosition, ownerId, AirDepotMode.BaseHub, out depotZdo, out target))
			{
				return false;
			}
		}
		WorkerZdo.WriteTarget(val, cargoPrefab, target, targetKind);
		val.Set("vw_bg_status", "air:restored-target-from-route " + route);
		BackgroundMonitorRuntime.Note($"air:restored missing target from cargo route {route} -> {target.x:0},{target.y:0},{target.z:0}", writeLog: false);
		return true;
	}

	private static void TickAirTransport(WorkerRecord record, long nowTicks)
	{
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0147: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0202: Unknown result type (might be due to invalid IL or missing references)
		//IL_0205: Unknown result type (might be due to invalid IL or missing references)
		//IL_020a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0161: Unknown result type (might be due to invalid IL or missing references)
		//IL_0114: Unknown result type (might be due to invalid IL or missing references)
		//IL_031c: Unknown result type (might be due to invalid IL or missing references)
		//IL_021e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0272: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0340: Unknown result type (might be due to invalid IL or missing references)
		//IL_0346: Unknown result type (might be due to invalid IL or missing references)
		//IL_0351: Unknown result type (might be due to invalid IL or missing references)
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_0387: Unknown result type (might be due to invalid IL or missing references)
		//IL_038c: Unknown result type (might be due to invalid IL or missing references)
		//IL_043f: Unknown result type (might be due to invalid IL or missing references)
		//IL_04a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_04c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_04c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_04c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_04ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_05ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_0639: Unknown result type (might be due to invalid IL or missing references)
		//IL_04dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_04df: Unknown result type (might be due to invalid IL or missing references)
		//IL_04f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_0522: Unknown result type (might be due to invalid IL or missing references)
		//IL_052d: Unknown result type (might be due to invalid IL or missing references)
		//IL_052f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0537: Unknown result type (might be due to invalid IL or missing references)
		//IL_0541: Unknown result type (might be due to invalid IL or missing references)
		//IL_05de: Unknown result type (might be due to invalid IL or missing references)
		ZDO zdo = record.Zdo;
		RestoreAirHybridTargetIfMissing(zdo);
		string targetName2;
		ZDO baseHubZdo;
		if (!WorkerZdo.TryReadCargo(zdo, out var prefabName, out var amount) || amount <= 0)
		{
			if (IsAuthoritativeReturnHomeTransit(zdo))
			{
				Vector3 val = WorkerZdo.ReadHome(zdo, record.LastPosition);
				WorkerZdo.WriteTarget(zdo, "__airhaul_home__", val, "airhaul_return_home");
				zdo.Set("vw_state", 6);
				EnsureAirHybridRoute(zdo, record.LastPosition, "airhaul_return_home", val, physicalStart: false);
				TickAirReturnHomeTransit(record, nowTicks, val);
				return;
			}
			if (!WorkerZdo.TryReadTarget(zdo, out var _, out targetName2, out var _) && zdo.GetInt("vw_state", 0) == 6)
			{
				Vector3 val2 = WorkerZdo.ReadHome(zdo, record.LastPosition);
				WorkerZdo.WriteTarget(zdo, "__airhaul_home__", val2, "airhaul_return_home");
				ResetAirHybridRoute(zdo);
				zdo.Set("vw_bg_transit_active", false);
				zdo.Set("vw_bg_transit_eta_ticks", 0L);
				zdo.Set("vw_bg_status", "air:recovered legacy empty return-home route");
				BackgroundMonitorRuntime.Note("air:recovered empty return-home route from HomeAnchor", writeLog: false);
				TickAirReturnHomeTransit(record, nowTicks, val2);
				return;
			}
			if (WorkerZdo.TryReadTarget(zdo, out var targetName3, out var targetKind, out var position2))
			{
				if (string.Equals(targetKind, "airhaul_return_home", StringComparison.Ordinal))
				{
					TickAirReturnHomeTransit(record, nowTicks, position2);
					return;
				}
				if (string.Equals(targetKind, "airhaul_base_source", StringComparison.Ordinal))
				{
					TickAirPickupTransit(record, nowTicks, targetName3, position2);
					return;
				}
			}
			long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
			if (WorkerLogisticsRuntime.TryFindPersistentAirDepot(record.LastPosition, ownerId, AirDepotMode.Outpost, out var depotZdo, out var position3))
			{
				// v0.5.93: inbound Outpost -> Base must be discoverable from the persistent
				// outpost ledger. Previously this path only existed in the loaded/physical AI,
				// so an idle Drake would wait forever until the player visited the Outpost.
				if (HorizontalDistance(record.LastPosition, position3) <= 250f && AirHaulerWorkRuntime.TryFindPersistentInboundStockForOutpost(ownerId, depotZdo, position3, out var inboundPrefab, out var inboundAmount, out var minimumRemain, out var inboundBaseHub, out var inboundBasePosition))
				{
					string transactionId = ContainerLedgerRuntime.CreateTransactionId();
					if (ContainerLedgerRuntime.TryReserveRemoval(depotZdo, transactionId, inboundPrefab, inboundAmount, minimumRemain, "background-air-inbound-pickup", out var reservedAmount, out var reserveStatus) && reservedAmount > 0 && ContainerLedgerRuntime.TryCommitRemoval(depotZdo, transactionId, out var transaction, out reserveStatus) && transaction != null && transaction.Amount > 0)
					{
						int taken = transaction.Amount;
						ContainerLedgerRuntime.CompleteTransaction(depotZdo, transactionId);
						WorkerZdo.SetCargo(zdo, inboundPrefab, taken);
						int extraInbound = AirHaulerWorkRuntime.TryTopUpPersistentInboundCargo(zdo, ownerId, depotZdo, position3, inboundBaseHub, inboundPrefab);
						WorkzoneLogisticsFlowRuntime.TagCargoSource(zdo, depotZdo, position3, WorkerZdo.ReadWorkAreaId(zdo));
						ClearAirEmptyPickupClaim(zdo);
						ResetAirHybridRoute(zdo);
						WorkerZdo.SetCargoRoute(zdo, AirHaulerWorkRuntime.RouteInbound, inboundBasePosition);
						WorkerZdo.WriteTarget(zdo, inboundPrefab, inboundBasePosition, AirHaulerWorkRuntime.BaseDepositTargetKind);
						zdo.Set("vw_state", 5);
						zdo.Set("vw_bg_transit_active", false);
						zdo.Set("vw_bg_transit_eta_ticks", 0L);
						zdo.Set("vw_bg_status", $"air:bg-inbound-picked-up {inboundPrefab} x{taken}; extra={extraInbound}; flying Base");
						BackgroundMonitorRuntime.Note($"air:bg-inbound-picked-up {inboundPrefab} x{taken}; extra={extraInbound}; Outpost -> Base", writeLog: false);
						return;
					}
					ContainerLedgerRuntime.RollbackTransaction(depotZdo, transactionId);
				}

				if (AirHaulerWorkRuntime.TryFindPersistentBaseStagedStockForOutpostDemand(ownerId, depotZdo, position3, out var prefab, out baseHubZdo, out var position4, out var available) && available > 0)
				{
					WorkerZdo.SetCargoRoute(zdo, "airhaul_outbound", position3);
					WorkerZdo.WriteTarget(zdo, prefab, position4, "airhaul_base_source");
					SetAirEmptyPickupClaim(zdo, prefab, Mathf.Min(20, available), baseHubZdo != null ? baseHubZdo.m_uid : ZDOID.None);
					zdo.Set("vw_state", 2);
					zdo.Set("vw_bg_status", $"air:created-bg-pickup {prefab} x{available}");
					BackgroundMonitorRuntime.Note($"air:created-bg-pickup {prefab} x{available}", writeLog: false);
					TickAirPickupTransit(record, nowTicks, prefab, position4);
					return;
				}
			}
			Vector3 val3 = WorkerZdo.ReadHome(zdo, record.LastPosition);
			if (HorizontalDistance(record.LastPosition, val3) > 8f)
			{
				WorkerZdo.WriteTarget(zdo, "__airhaul_home__", val3, "airhaul_return_home");
				zdo.Set("vw_state", 6);
				ResetAirHybridRoute(zdo);
				zdo.Set("vw_bg_transit_active", false);
				zdo.Set("vw_bg_transit_eta_ticks", 0L);
				zdo.Set("vw_bg_status", "air:empty-away-from-home -> return-home");
				BackgroundMonitorRuntime.Note("air:recovered empty Drake at Base -> return-home", writeLog: false);
				TickAirReturnHomeTransit(record, nowTicks, val3);
			}
			else
			{
				zdo.Set("vw_bg_transit_active", false);
				zdo.Set("vw_bg_transit_eta_ticks", 0L);
				ResetAirHybridRoute(zdo);
				zdo.Set("vw_bg_status", "air:no-cargo/no-staged-request");
			}
			return;
		}
		if (!WorkerZdo.TryReadTarget(zdo, out targetName2, out var targetKind2, out var position5) && !TryRestoreAirDeliveryTargetFromCargoRoute(record, prefabName, out targetKind2, out position5))
		{
			zdo.Set("vw_bg_status", "air:no-target");
			return;
		}
		AirDepotMode mode;
		if (string.Equals(targetKind2, "airhaul_base_deposit", StringComparison.Ordinal))
		{
			mode = AirDepotMode.BaseHub;
		}
		else
		{
			if (!string.Equals(targetKind2, "airhaul_outpost_deposit", StringComparison.Ordinal))
			{
				zdo.Set("vw_bg_status", "air:target-not-depot");
				return;
			}
			mode = AirDepotMode.Outpost;
		}
		long ownerId2 = zdo.GetLong("vw_owner_player_id", 0L);
		if (!WorkerLogisticsRuntime.TryFindPersistentAirDepot(position5, ownerId2, mode, out var depotZdo2, out var position6))
		{
			zdo.Set("vw_bg_status", "air:destination-unresolved");
			return;
		}
		EnsureAirHybridRoute(zdo, record.LastPosition, targetKind2, position6, physicalStart: false);
		if (HoldOrResumeHybridArrival(record, nowTicks, position6, "delivery"))
		{
			return;
		}
		long num = zdo.GetLong("vw_bg_transit_eta_ticks", 0L);
		if (!zdo.GetBool("vw_bg_transit_active", false) || num <= 0)
		{
			float distance = Vector3.Distance(record.LastPosition, position6);
			double airTransitSeconds = GetAirTransitSeconds(zdo, distance);
			num = DateTime.UtcNow.AddSeconds(airTransitSeconds).Ticks;
			zdo.Set("vw_bg_transit_eta_ticks", num);
			zdo.Set("vw_bg_transit_active", true);
			zdo.Set("vw_air_return_transit_authority", false);
			zdo.Set("vw_bg_status", $"air:in-transit eta={airTransitSeconds:0}s");
			BackgroundMonitorRuntime.Note($"air:in-transit {prefabName} x{amount}; eta={airTransitSeconds:0}s", writeLog: false);
			return;
		}
		double num2 = Math.Max(0.0, TimeSpan.FromTicks(num - nowTicks).TotalSeconds);
		if (TryStageHybridArrival(record, nowTicks, targetKind2, position6, "delivery"))
		{
			return;
		}
		if (nowTicks < num)
		{
			SyncPersistentAirTransitPosition(record, nowTicks);
			zdo.Set("vw_bg_status", $"air:in-transit eta={num2:0}s");
			return;
		}
		int acceptedByStockRule = ContainerStockLimitRuntime.GetPersistentAcceptCapacity(depotZdo2, prefabName);
		if (acceptedByStockRule <= 0)
		{
			zdo.Set("vw_bg_transit_eta_ticks", DateTime.UtcNow.AddSeconds(5.0).Ticks);
			zdo.Set("vw_bg_transit_active", true);
			zdo.Set("vw_bg_status", $"air:delivery paused by Min/Max {prefabName} x{amount}");
			BackgroundMonitorRuntime.Note($"air:delivery waiting at stock limit {prefabName} x{amount}", writeLog: false);
			return;
		}
		int deliverNow = Mathf.Min(amount, acceptedByStockRule);
		AdvancedFieldDepotRuntime.QueueBackgroundDelivery(depotZdo2, prefabName, deliverNow);
		string flowFallbackArea = string.Equals(targetKind2, AirHaulerWorkRuntime.BaseDepositTargetKind, StringComparison.Ordinal)
			? WorkerZdo.ReadWorkAreaId(zdo)
			: "base";
		WorkzoneLogisticsFlowRuntime.RecordTaggedExport(zdo, prefabName, deliverNow, flowFallbackArea);
		// v0.5.389: remove only the delivered stack. Replacing/clearing the cargo manifest here
		// discarded slot 2 as soon as the first item type arrived.
		WorkerZdo.RemoveCargoAmount(zdo, prefabName, deliverNow);
		if (deliverNow < amount)
		{
			zdo.Set("vw_bg_transit_eta_ticks", DateTime.UtcNow.AddSeconds(5.0).Ticks);
			zdo.Set("vw_bg_transit_active", true);
			zdo.Set("vw_bg_status", $"air:partial stock-limit delivery {prefabName} x{deliverNow}; x{amount - deliverNow} waiting");
			BackgroundMonitorRuntime.Note($"air:partial stock-limit delivery {prefabName} x{deliverNow}; x{amount - deliverNow} waiting", writeLog: false);
			return;
		}
		bool hasRemainingCargo = WorkerZdo.HasCargo(zdo);
		ResetAirHybridRoute(zdo);
		zdo.Set("vw_bg_transit_active", false);
		zdo.Set("vw_bg_transit_eta_ticks", 0L);
		MovePersistentWorker(zdo, record, position6);
		if (hasRemainingCargo)
		{
			WorkerZdo.ClearTarget(zdo);
			if (WorkerZdo.TryReadCargo(zdo, out string nextCargoPrefab, out int nextCargoAmount) &&
				TryRestoreAirDeliveryTargetFromCargoRoute(record, nextCargoPrefab, out _, out _))
			{
				zdo.Set("vw_state", 5);
				zdo.Set("vw_bg_status", $"air:delivered {prefabName} x{deliverNow}; next cargo {nextCargoPrefab} x{nextCargoAmount}");
				BackgroundMonitorRuntime.Note($"air:delivered {prefabName} x{deliverNow}; continuing same flight manifest with {nextCargoPrefab} x{nextCargoAmount}", writeLog: false);
				return;
			}
			zdo.Set("vw_state", 5);
			zdo.Set("vw_bg_status", "air:remaining cargo; destination restore pending");
			return;
		}
		WorkerZdo.ClearCargo(zdo);
		WorkzoneLogisticsFlowRuntime.ClearCargoSource(zdo);
		if (string.Equals(targetKind2, "airhaul_base_deposit", StringComparison.Ordinal))
		{
			Vector3 val4 = WorkerZdo.ReadHome(zdo, position6);
			if (WorkerLogisticsRuntime.TryFindPersistentAirDepot(val4, ownerId2, AirDepotMode.Outpost, out var depotZdo3, out var position7) && Vector3.Distance(position7, val4) <= 250f && AirHaulerWorkRuntime.TryFindPersistentBaseStagedStockForOutpostDemand(ownerId2, depotZdo3, position7, out var prefab2, out baseHubZdo, out var position8, out var available2) && available2 > 0 && !string.IsNullOrEmpty(prefab2))
			{
				string text = prefab2;
				int persistentOutpostNeedForAirHub = AirHaulerWorkRuntime.GetPersistentOutpostNeedForAirHub(ownerId2, position7, text);
				Vector3 val5 = position8;
				WorkerZdo.SetCargoRoute(zdo, "airhaul_outbound", position7);
				WorkerZdo.WriteTarget(zdo, text, val5, "airhaul_base_source");
				SetAirEmptyPickupClaim(zdo, text, Mathf.Min(20, Mathf.Min(persistentOutpostNeedForAirHub, available2)), baseHubZdo != null ? baseHubZdo.m_uid : ZDOID.None);
				zdo.Set("vw_state", 2);
				zdo.Set("vw_bg_status", $"air:delivered {prefabName} x{amount}; backhaul {text} x{persistentOutpostNeedForAirHub}");
				BackgroundMonitorRuntime.Note($"air:delivered {prefabName} x{amount}; backhaul queued {text} x{persistentOutpostNeedForAirHub}", writeLog: false);
				TickAirPickupTransit(record, nowTicks, text, val5);
			}
			else
			{
				WorkerZdo.WriteTarget(zdo, "__airhaul_home__", val4, "airhaul_return_home");
				zdo.Set("vw_state", 6);
				zdo.Set("vw_bg_status", $"air:delivered {prefabName} x{amount}; return-home queued");
				BackgroundMonitorRuntime.Note($"air:delivered {prefabName} x{amount}; simulated return-home", writeLog: false);
				TickAirReturnHomeTransit(record, nowTicks, val4);
			}
		}
		else
		{
			WorkerZdo.ClearTarget(zdo);
			zdo.Set("vw_state", 1);
			zdo.Set("vw_bg_status", $"air:delivered {prefabName} x{amount}");
			BackgroundMonitorRuntime.Note($"air:delivered {prefabName} x{amount}", writeLog: false);
		}
	}

	private static void TickAirReturnHomeTransit(WorkerRecord record, long nowTicks, Vector3 homeTarget)
	{
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_021f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_0124: Unknown result type (might be due to invalid IL or missing references)
		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
		//IL_0170: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ff: Unknown result type (might be due to invalid IL or missing references)
		ZDO val = record?.Zdo;
		if (val == null)
		{
			return;
		}
		Vector3 val2 = WorkerZdo.ReadHome(val, homeTarget);
		if (HorizontalDistance(val2, homeTarget) > 8f)
		{
			homeTarget = val2;
		}
		val.Set("vw_air_return_transit_authority", true);
		EnsureAirHybridRoute(val, record.LastPosition, "airhaul_return_home", homeTarget, physicalStart: false);
		if (HoldOrResumeHybridArrival(record, nowTicks, homeTarget, "return-home"))
		{
			return;
		}
		long num = val.GetLong("vw_bg_transit_eta_ticks", 0L);
		if (!val.GetBool("vw_bg_transit_active", false) || num <= 0)
		{
			float distance = Vector3.Distance(record.LastPosition, homeTarget);
			double airTransitSeconds = GetAirTransitSeconds(val, distance);
			num = DateTime.UtcNow.AddSeconds(airTransitSeconds).Ticks;
			val.Set("vw_bg_transit_eta_ticks", num);
			val.Set("vw_bg_transit_active", true);
			val.Set("vw_bg_status", $"air:empty-return-home eta={airTransitSeconds:0}s");
			BackgroundMonitorRuntime.Note($"air:empty return-home started; eta={airTransitSeconds:0}s", writeLog: false);
			return;
		}
		double num2 = Math.Max(0.0, TimeSpan.FromTicks(num - nowTicks).TotalSeconds);
		if (nowTicks < num)
		{
			if (!TryStageHybridArrival(record, nowTicks, "airhaul_return_home", homeTarget, "return-home"))
			{
				SyncPersistentAirTransitPosition(record, nowTicks);
				val.Set("vw_bg_status", $"air:empty-return-home eta={num2:0}s");
			}
			return;
		}
		Character loadedCharacter = record.LoadedCharacter;
		bool flag = (Object)(object)loadedCharacter != (Object)null && !loadedCharacter.IsDead() && IsPlayerPhysicalAreaReady(homeTarget, 180f);
		MovePersistentWorker(val, record, homeTarget);
		ResetAirHybridRoute(val);
		WorkerZdo.ClearTarget(val);
		val.Set("vw_state", 1);
		val.Set("vw_bg_transit_active", false);
		val.Set("vw_bg_transit_eta_ticks", 0L);
		val.Set("vw_air_return_transit_authority", false);
		val.Set("vw_bg_physical_handoff_pending", false);
		val.Set("vw_bg_physical_handoff_since_ticks", 0L);
		if ((Object)(object)loadedCharacter != (Object)null && !loadedCharacter.IsDead())
		{
			try
			{
				((Component)loadedCharacter).transform.position = homeTarget;
			}
			catch
			{
			}
			try
			{
				ZdoSetPositionMethod?.Invoke(val, new object[1] { homeTarget });
			}
			catch
			{
			}
			if (flag)
			{
				WorkerNavigationRuntime.SetAirHaulerPhysicalPresentation(loadedCharacter, visible: true);
				val.Set("vw_bg_mode", "physical");
				record.LastSeenRealtime = Time.realtimeSinceStartup;
				try
				{
					WorkerNavigationRuntime.TrackAirHauler(loadedCharacter);
				}
				catch
				{
				}
			}
			else
			{
				WorkerNavigationRuntime.SetAirHaulerPhysicalPresentation(loadedCharacter, visible: false);
				val.Set("vw_bg_mode", "background");
				record.LastSeenRealtime = Time.realtimeSinceStartup - 4f - 1f;
			}
		}
		else
		{
			val.Set("vw_bg_mode", "background");
			record.LastSeenRealtime = Time.realtimeSinceStartup - 4f - 1f;
		}
		val.Set("vw_bg_status", flag ? "air:return-home arrived; physical restored" : "air:return-home arrived; persisted at Home");
		BackgroundMonitorRuntime.Note(flag ? "air:empty return-home arrived; physical restored at Home" : "air:empty return-home arrived; persisted at Home", writeLog: false);
	}

	private static void TickAirPickupTransit(WorkerRecord record, long nowTicks, string prefab, Vector3 target)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_0141: Unknown result type (might be due to invalid IL or missing references)
		//IL_017b: Unknown result type (might be due to invalid IL or missing references)
		//IL_019f: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_0211: Unknown result type (might be due to invalid IL or missing references)
		//IL_021a: Unknown result type (might be due to invalid IL or missing references)
		//IL_022e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0233: Unknown result type (might be due to invalid IL or missing references)
		//IL_0238: Unknown result type (might be due to invalid IL or missing references)
		//IL_0252: Unknown result type (might be due to invalid IL or missing references)
		//IL_03e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_033e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0343: Unknown result type (might be due to invalid IL or missing references)
		//IL_0348: Unknown result type (might be due to invalid IL or missing references)
		//IL_0362: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c8: Unknown result type (might be due to invalid IL or missing references)
		ZDO zdo = record.Zdo;
		if (zdo == null || string.IsNullOrEmpty(prefab))
		{
			return;
		}
		long ownerId = zdo.GetLong("vw_owner_player_id", 0L);
		if (!WorkerLogisticsRuntime.TryFindPersistentAirDepot(target, ownerId, AirDepotMode.BaseHub, out var depotZdo, out var position))
		{
			zdo.Set("vw_bg_status", "air:pickup-base-unresolved");
			return;
		}
		EnsureAirHybridRoute(zdo, record.LastPosition, "airhaul_base_source", position, physicalStart: false);
		if (HoldOrResumeHybridArrival(record, nowTicks, position, "pickup"))
		{
			return;
		}
		long num = zdo.GetLong("vw_bg_transit_eta_ticks", 0L);
		if (!zdo.GetBool("vw_bg_transit_active", false) || num <= 0)
		{
			float distance = Vector3.Distance(record.LastPosition, position);
			double airTransitSeconds = GetAirTransitSeconds(zdo, distance);
			num = DateTime.UtcNow.AddSeconds(airTransitSeconds).Ticks;
			zdo.Set("vw_bg_transit_eta_ticks", num);
			zdo.Set("vw_bg_transit_active", true);
			zdo.Set("vw_air_return_transit_authority", false);
			zdo.Set("vw_bg_status", $"air:empty-to-base eta={airTransitSeconds:0}s; pickup={prefab}");
			BackgroundMonitorRuntime.Note($"air:empty-to-base; pickup={prefab}; eta={airTransitSeconds:0}s", writeLog: false);
			return;
		}
		double num2 = Math.Max(0.0, TimeSpan.FromTicks(num - nowTicks).TotalSeconds);
		if (TryStageHybridArrival(record, nowTicks, "airhaul_base_source", position, "pickup"))
		{
			return;
		}
		if (nowTicks < num)
		{
			SyncPersistentAirTransitPosition(record, nowTicks);
			zdo.Set("vw_bg_status", $"air:empty-to-base eta={num2:0}s; pickup={prefab}");
			return;
		}
		MovePersistentWorker(zdo, record, position);
		if (!WorkerZdo.TryReadCargoRoute(zdo, out var route, out var target2) || !string.Equals(route, "airhaul_outbound", StringComparison.Ordinal))
		{
			Vector3 val = WorkerZdo.ReadHome(zdo, record.LastPosition);
			ClearAirEmptyPickupClaim(zdo);
			WorkerZdo.ClearCargoRoute(zdo);
			ResetAirHybridRoute(zdo);
			WorkerZdo.WriteTarget(zdo, "__airhaul_home__", val, "airhaul_return_home");
			zdo.Set("vw_state", 6);
			zdo.Set("vw_bg_transit_active", false);
			zdo.Set("vw_bg_transit_eta_ticks", 0L);
			zdo.Set("vw_bg_status", "air:pickup route missing at Base; return-home");
			BackgroundMonitorRuntime.Note("air:pickup route missing at Base; simulated return-home", writeLog: false);
			TickAirReturnHomeTransit(record, nowTicks, val);
			return;
		}
		int persistentOutpostNeedForAirHub = AirHaulerWorkRuntime.GetPersistentOutpostNeedForAirHub(ownerId, target2, prefab);
		if (persistentOutpostNeedForAirHub <= 0)
		{
			Vector3 position2 = WorkerZdo.ReadHome(zdo, record.LastPosition);
			ClearAirEmptyPickupClaim(zdo);
			WorkerZdo.ClearCargoRoute(zdo);
			ResetAirHybridRoute(zdo);
			WorkerZdo.WriteTarget(zdo, "__airhaul_home__", position2, "airhaul_return_home");
			zdo.Set("vw_state", 6);
			zdo.Set("vw_bg_transit_active", false);
			zdo.Set("vw_bg_transit_eta_ticks", 0L);
			zdo.Set("vw_bg_status", "air:backhaul no-longer-needed " + prefab + "; return-home");
			BackgroundMonitorRuntime.Note("air:backhaul request cleared for " + prefab + "; return-home", writeLog: false);
			return;
		}
		int num3 = Mathf.Min(20, persistentOutpostNeedForAirHub);
		int taken = 0;
		if ((!AdvancedFieldDepotRuntime.TryTakePendingBackgroundDelivery(depotZdo, prefab, num3, out taken) || taken <= 0) && ContainerLedgerRuntime.TryGetAvailable(depotZdo, prefab, 0, out var available) && available > 0)
		{
			string transactionId = ContainerLedgerRuntime.CreateTransactionId();
			if (ContainerLedgerRuntime.TryReserveRemoval(depotZdo, transactionId, prefab, Mathf.Min(num3, available), 0, "background-air-pickup", out var reservedAmount, out var status) && reservedAmount > 0 && ContainerLedgerRuntime.TryCommitRemoval(depotZdo, transactionId, out var transaction, out status) && transaction != null)
			{
				taken = transaction.Amount;
				ContainerLedgerRuntime.CompleteTransaction(depotZdo, transactionId);
			}
		}
		if (taken <= 0)
		{
			Vector3 val2 = WorkerZdo.ReadHome(zdo, record.LastPosition);
			ClearAirEmptyPickupClaim(zdo);
			WorkerZdo.ClearCargoRoute(zdo);
			ResetAirHybridRoute(zdo);
			WorkerZdo.WriteTarget(zdo, "__airhaul_home__", val2, "airhaul_return_home");
			zdo.Set("vw_state", 6);
			zdo.Set("vw_bg_transit_active", false);
			zdo.Set("vw_bg_transit_eta_ticks", 0L);
			zdo.Set("vw_bg_status", $"air:backhaul unavailable {prefab} x{persistentOutpostNeedForAirHub}; return-home");
			BackgroundMonitorRuntime.Note("air:backhaul " + prefab + " no longer available at Base; simulated return-home", writeLog: false);
			TickAirReturnHomeTransit(record, nowTicks, val2);
		}
		else
		{
			WorkerZdo.SetCargo(zdo, prefab, taken);
			int extraOutbound = AirHaulerWorkRuntime.TryTopUpPersistentOutboundCargo(zdo, ownerId, depotZdo, target2, prefab);
			WorkzoneLogisticsFlowRuntime.TagCargoSource(zdo, depotZdo, position, "base");
			ClearAirEmptyPickupClaim(zdo);
			ResetAirHybridRoute(zdo);
			WorkerZdo.WriteTarget(zdo, prefab, target2, "airhaul_outpost_deposit");
			zdo.Set("vw_state", 5);
			zdo.Set("vw_bg_transit_active", false);
			zdo.Set("vw_bg_transit_eta_ticks", 0L);
			zdo.Set("vw_bg_status", $"air:bg-picked-up {prefab} x{taken}; extra={extraOutbound}; returning outpost");
			BackgroundMonitorRuntime.Note($"air:bg-picked-up {prefab} x{taken}; extra={extraOutbound}; returning outpost", writeLog: false);
		}
	}

	private static void TickGroundTransport(WorkerRecord record, long nowTicks)
	{
		//IL_00b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_0175: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f4: Unknown result type (might be due to invalid IL or missing references)
		ZDO zdo = record.Zdo;
		if (!WorkerZdo.TryReadCargo(zdo, out var prefabName, out var amount) || amount <= 0)
		{
			zdo.Set("vw_bg_transit_active", false);
			zdo.Set("vw_bg_transit_eta_ticks", 0L);
			if (BackgroundGroundPickupRuntime.TryStartBackgroundPickup(zdo, out var status) && WorkerZdo.TryReadCargo(zdo, out prefabName, out amount))
			{
				_ = 0;
				zdo.Set("vw_bg_status", status);
			}
			else
			{
				zdo.Set("vw_bg_status", status);
			}
			return;
		}
		if (!WorkerZdo.TryReadTarget(zdo, out var _, out var targetKind, out var position) || !IsGroundDeliveryTarget(targetKind))
		{
			zdo.Set("vw_bg_status", "ground:no-delivery-target");
			return;
		}
		long num = zdo.GetLong("vw_bg_transit_eta_ticks", 0L);
		if (!zdo.GetBool("vw_bg_transit_active", false) || num <= 0)
		{
			float num2 = Vector3.Distance(record.LastPosition, position);
			double num3 = Math.Max(4.0, num2 / GetGroundSpeed(record.Species));
			num = DateTime.UtcNow.AddSeconds(num3).Ticks;
			zdo.Set("vw_bg_transit_eta_ticks", num);
			zdo.Set("vw_bg_transit_active", true);
			zdo.Set("vw_bg_status", $"ground:in-transit eta={num3:0}s");
			return;
		}
		double num4 = Math.Max(0.0, TimeSpan.FromTicks(num - nowTicks).TotalSeconds);
		if (nowTicks < num)
		{
			zdo.Set("vw_bg_status", $"ground:in-transit eta={num4:0}s");
			return;
		}
		if (!TryResolvePersistentGroundDestination(zdo, targetKind, position, out var destinationZdo, out var destinationPosition))
		{
			zdo.Set("vw_bg_transit_active", false);
			zdo.Set("vw_bg_transit_eta_ticks", 0L);
			zdo.Set("vw_bg_status", $"ground:destination-unresolved {prefabName} x{amount}");
			return;
		}
		int deliverAmount = amount;
		if (string.Equals(targetKind, WorkerStorageRuntime.ImportDepositTargetKind, StringComparison.Ordinal))
		{
			long importOwner = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
			int accept = ContainerStockLimitRuntime.GetPersistentAcceptCapacity(destinationZdo, prefabName);
			if (accept <= 0 && ContainerStockLimitRuntime.TryFindPersistentImportWithCapacity(importOwner, position, prefabName, out ZDO alternateImport, out Vector3 alternatePosition, out int alternateAccept))
			{
				destinationZdo = alternateImport;
				destinationPosition = alternatePosition;
				accept = alternateAccept;
			}
			if (accept <= 0)
			{
				zdo.Set("vw_bg_transit_active", false);
				zdo.Set("vw_bg_transit_eta_ticks", 0L);
				MovePersistentWorker(zdo, record, destinationPosition);
				zdo.Set("vw_bg_status", $"ground:waiting-import-buffer {prefabName} x{amount}");
				return;
			}
			deliverAmount = Math.Min(amount, accept);
		}

		AdvancedFieldDepotRuntime.QueueBackgroundDelivery(destinationZdo, prefabName, deliverAmount);
		string groundFlowFallback = string.Equals(targetKind, WorkerStorageRuntime.ImportDepositTargetKind, StringComparison.Ordinal)
			? WorkerZdo.ReadWorkAreaId(zdo)
			: "base";
		WorkzoneLogisticsFlowRuntime.RecordTaggedExport(zdo, prefabName, deliverAmount, groundFlowFallback);
		WorkerZdo.RemoveCargoAmount(zdo, prefabName, deliverAmount);
		if (WorkerZdo.HasCargo(zdo))
		{
			zdo.Set("vw_bg_transit_active", false);
			zdo.Set("vw_bg_transit_eta_ticks", 0L);
			MovePersistentWorker(zdo, record, destinationPosition);
			zdo.Set("vw_bg_status", $"ground:partial-bg {prefabName} x{deliverAmount}; remainder waits for Import Min/Max buffer");
			return;
		}

		WorkerZdo.ClearCargo(zdo);
		WorkzoneLogisticsFlowRuntime.ClearCargoSource(zdo);
		WorkerZdo.ClearTarget(zdo);
		zdo.Set("vw_state", 1);
		zdo.Set("vw_bg_transit_active", false);
		zdo.Set("vw_bg_transit_eta_ticks", 0L);
		MovePersistentWorker(zdo, record, destinationPosition);
		zdo.Set("vw_bg_status", $"ground:delivered-bg {prefabName} x{deliverAmount}");
	}

	private static bool TryResolvePersistentGroundDestination(ZDO workerZdo, string targetKind, Vector3 target, out ZDO destinationZdo, out Vector3 destinationPosition)
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		destinationZdo = null;
		destinationPosition = default(Vector3);
		if (workerZdo == null || string.IsNullOrEmpty(targetKind))
		{
			return false;
		}
		WorkerContainerRole role;
		if (string.Equals(targetKind, "storage_import_deposit", StringComparison.Ordinal))
		{
			role = WorkerContainerRole.Import;
		}
		else if (string.Equals(targetKind, "storage_export_deposit", StringComparison.Ordinal))
		{
			role = WorkerContainerRole.Export;
		}
		else if (string.Equals(targetKind, "storage_field_depot_deposit", StringComparison.Ordinal) || string.Equals(targetKind, "storage_supply_field_depot_deposit", StringComparison.Ordinal))
		{
			role = WorkerContainerRole.FieldDepot;
		}
		else
		{
			if (!string.Equals(targetKind, "storage_sort_base_deposit", StringComparison.Ordinal))
			{
				return false;
			}
			role = WorkerContainerRole.Normal;
		}
		long ownerId = workerZdo.GetLong("vw_owner_player_id", 0L);
		float bestSqr = 900f;
		ZDO found = null;
		Vector3 foundPosition = default(Vector3);
		PersistentContainerRegistryRuntime.ForEachPersistent(delegate(ZDO candidate, WorkerContainerRole candidateRole, long owner, Vector3 p)
		{
			//IL_0022: Unknown result type (might be due to invalid IL or missing references)
			//IL_0025: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Unknown result type (might be due to invalid IL or missing references)
			//IL_002f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0051: Unknown result type (might be due to invalid IL or missing references)
			//IL_0053: Unknown result type (might be due to invalid IL or missing references)
			if (candidate != null && candidateRole == role && (owner == 0L || ownerId == 0L || owner == ownerId))
			{
				Vector3 val = p - target;
				float sqrMagnitude = val.sqrMagnitude;
				if (!(sqrMagnitude > bestSqr))
				{
					bestSqr = sqrMagnitude;
					found = candidate;
					foundPosition = p;
				}
			}
		});
		destinationZdo = found;
		destinationPosition = foundPosition;
		return found != null;
	}

	private static bool IsGroundDeliveryTarget(string targetKind)
	{
		if (!string.Equals(targetKind, "storage_import_deposit", StringComparison.Ordinal) && !string.Equals(targetKind, "storage_supply_field_depot_deposit", StringComparison.Ordinal) && !string.Equals(targetKind, "storage_field_depot_deposit", StringComparison.Ordinal) && !string.Equals(targetKind, "storage_export_deposit", StringComparison.Ordinal))
		{
			return string.Equals(targetKind, "storage_sort_base_deposit", StringComparison.Ordinal);
		}
		return true;
	}

	internal static float GetEstimatedTransportSpeed(string species)
	{
		if (string.Equals(species, "Drake", StringComparison.OrdinalIgnoreCase))
		{
			return 14f;
		}
		if (string.Equals(species, "Lox", StringComparison.OrdinalIgnoreCase))
		{
			return 3.8f;
		}
		return 4.5f;
	}

	private static float GetGroundSpeed(string species)
	{
		if (string.Equals(species, "Lox", StringComparison.OrdinalIgnoreCase))
		{
			return 3.8f;
		}
		return 4.5f;
	}

	private static bool IsDestinationActive(Vector3 destination)
	{
		return IsPlayerPhysicalAreaReady(destination, 180f);
	}

	private static Vector3 BuildHandoffPoint(Vector3 origin, Vector3 destination, float distanceOut, bool air)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		Vector3 val = origin - destination;
		val.y = 0f;
		if (val.sqrMagnitude < 0.01f)
		{
			val = Vector3.back;
		}
		val.Normalize();
		Vector3 result = destination + val * distanceOut;
		result.y = (air ? Mathf.Max(destination.y + 24f, origin.y) : (destination.y + 1f));
		return result;
	}

	// Adjacent Drake platforms can be only a few metres apart. Their simulated routes therefore
	// converge on almost the same final-100 handoff point even though the actual homes are unique.
	// Give every HomeAnchor a stable lateral approach lane; physical steering still targets the
	// exact platform position, so this only separates the incoming flight corridors.
	private static Vector3 ApplyAirApproachLane(ZDO workerZdo, Vector3 handoffPoint, Vector3 destination)
	{
		if (workerZdo == null)
		{
			return handoffPoint;
		}
		string identity = WorkerZdo.ReadHomeAnchorZdoId(workerZdo).ToString();
		if (string.IsNullOrEmpty(identity))
		{
			identity = workerZdo.m_uid.ToString();
		}
		uint hash = 2166136261u;
		for (int i = 0; i < identity.Length; i++)
		{
			hash ^= identity[i];
			hash *= 16777619u;
		}
		int lane = (int)(hash % 7u) - 3;
		if (lane == 0)
		{
			return handoffPoint;
		}
		Vector3 direction = destination - handoffPoint;
		direction.y = 0f;
		if (direction.sqrMagnitude < 0.01f)
		{
			direction = Vector3.forward;
		}
		direction.Normalize();
		Vector3 lateral = new Vector3(-direction.z, 0f, direction.x);
		return handoffPoint + lateral * (lane * 4f);
	}

	private static void SyncPersistentAirTransitPosition(WorkerRecord record, long nowTicks)
	{
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ac: Unknown result type (might be due to invalid IL or missing references)
		if (record?.Zdo == null || !TryBuildTransportTelemetry(record, nowTicks, out var position, out var _, out var _, out var targetName))
		{
			return;
		}
		ZDO zdo = record.Zdo;
		zdo.Set("vw_bg_virtual_x", position.x);
		zdo.Set("vw_bg_virtual_y", position.y);
		zdo.Set("vw_bg_virtual_z", position.z);
		Character loadedCharacter = record.LoadedCharacter;
		bool num = IsReturnHomeShadowTransit(zdo);
		bool flag = false;
		if (num && TryReadPersistedAirHybridRoute(zdo, out targetName, out var _, out var target))
		{
			flag = IsDestinationActive(target);
		}
		if ((Object)(object)loadedCharacter != (Object)null && !loadedCharacter.IsDead())
		{
			if (flag)
			{
				WorkerNavigationRuntime.SetAirHaulerPhysicalPresentation(loadedCharacter, visible: true);
			}
			else
			{
				WorkerNavigationRuntime.SetAirHaulerPhysicalPresentation(loadedCharacter, visible: false);
				try
				{
					((Component)loadedCharacter).transform.position = position;
				}
				catch
				{
				}
			}
		}
		zdo.Set("vw_bg_virtual_pending", false);
		if (flag && !((Object)(object)loadedCharacter == (Object)null) && !loadedCharacter.IsDead())
		{
			return;
		}
		try
		{
			ZdoSetPositionMethod?.Invoke(zdo, new object[1] { position });
		}
		catch
		{
		}
	}

	private static void MovePersistentWorker(ZDO zdo, WorkerRecord record, Vector3 position, bool physicalHandoff = false)
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		zdo.Set("vw_bg_virtual_x", position.x);
		zdo.Set("vw_bg_virtual_y", position.y);
		zdo.Set("vw_bg_virtual_z", position.z);
		zdo.Set("vw_bg_virtual_pending", true);
		if (physicalHandoff)
		{
			zdo.Set("vw_bg_physical_handoff_pending", true);
		}
		record.LastPosition = position;
		Character loadedCharacter = record.LoadedCharacter;
		if ((Object)(object)loadedCharacter != (Object)null && !loadedCharacter.IsDead())
		{
			try
			{
				((Component)loadedCharacter).transform.position = position;
			}
			catch
			{
			}
		}
		try
		{
			ZdoSetPositionMethod?.Invoke(zdo, new object[1] { position });
		}
		catch
		{
		}
	}

	internal static void GetTransportMapSnapshots(List<TransportMapSnapshot> destination)
	{
		//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		if (destination == null)
		{
			return;
		}
		destination.Clear();
		long ticks = DateTime.UtcNow.Ticks;
		float realtimeSinceStartup = Time.realtimeSinceStartup;
		foreach (KeyValuePair<ZDOID, WorkerRecord> record in Records)
		{
			WorkerRecord value = record.Value;
			ZDO val = value?.Zdo;
			if (val != null && WorkerZdo.IsWorker(val))
			{
				bool num = val.GetInt("vw_air_hybrid_phase", 0) == 2;
				bool flag = IsAuthoritativeReturnHomeTransit(val);
				if ((num || flag || !(realtimeSinceStartup - value.LastSeenRealtime < 4f)) && TryBuildTransportTelemetry(value, ticks, out var position, out var progress, out var etaSeconds, out var mode))
				{
					WorkerRole role = (WorkerRole)val.GetInt("vw_role", 0);
					destination.Add(new TransportMapSnapshot(record.Key, val, value.Species, role, position, progress, etaSeconds, mode));
				}
			}
		}
	}

	private static bool TryBuildTransportTelemetry(WorkerRecord record, long nowTicks, out Vector3 position, out float progress01, out int etaSeconds, out string mode)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0004: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_017d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		position = record?.LastPosition ?? Vector3.zero;
		progress01 = 0f;
		etaSeconds = 0;
		mode = string.Empty;
		ZDO val = record?.Zdo;
		if (val == null || !val.GetBool("vw_bg_transit_active", false))
		{
			return false;
		}
		if (!WorkerZdo.TryReadWorkArea(val, out var order, out var _, out var _) || order != WorkOrder.Haul)
		{
			return false;
		}
		if (!WorkerZdo.TryReadTarget(val, out var targetName, out var targetKind, out var position2))
		{
			if (!TryReadPersistedAirHybridRoute(val, out targetName, out targetKind, out position2))
			{
				return false;
			}
			RestoreAirHybridTargetIfMissing(val);
		}
		long num = val.GetLong("vw_bg_transit_eta_ticks", 0L);
		if (num <= 0)
		{
			return false;
		}
		double num2 = Math.Max(0.0, TimeSpan.FromTicks(Math.Max(0L, num - nowTicks)).TotalSeconds);
		float val2 = (string.Equals(record.Species, "Drake", StringComparison.OrdinalIgnoreCase) ? 14f : GetGroundSpeed(record.Species));
		float num3 = Vector3.Distance(record.LastPosition, position2);
		double num4 = (string.Equals(record.Species, "Drake", StringComparison.OrdinalIgnoreCase) ? GetAirTransitSeconds(val, num3) : Math.Max(4.0, num3 / Math.Max(0.1f, val2)));
		double num5 = Math.Max(0.0, num4 - num2);
		progress01 = ((num4 <= 0.01) ? 1f : Mathf.Clamp01((float)(num5 / num4)));
		etaSeconds = Mathf.Max(0, Mathf.CeilToInt((float)num2));
		position = Vector3.Lerp(record.LastPosition, position2, progress01);
		mode = (string.Equals(record.Species, "Drake", StringComparison.OrdinalIgnoreCase) ? "AirTransport" : "GroundTransport");
		return true;
	}

	internal static void SetAirEmptyPickupClaim(ZDO zdo, string prefab, int amount)
	{
		SetAirEmptyPickupClaim(zdo, prefab, amount, ZDOID.None);
	}

	internal static void SetAirEmptyPickupClaim(ZDO zdo, string prefab, int amount, ZDOID sourceId)
	{
		if (zdo != null)
		{
			zdo.Set(AirEmptyPickupClaimPrefabKey, prefab ?? string.Empty);
			zdo.Set(AirEmptyPickupClaimAmountKey, Mathf.Max(0, amount));
			zdo.Set(AirEmptyPickupClaimSourceKey, sourceId);
		}
	}

	internal static void ClearAirEmptyPickupClaim(ZDO zdo)
	{
		if (zdo != null)
		{
			zdo.Set(AirEmptyPickupClaimPrefabKey, string.Empty);
			zdo.Set(AirEmptyPickupClaimAmountKey, 0);
			zdo.Set(AirEmptyPickupClaimSourceKey, ZDOID.None);
		}
	}

	internal static int GetAirEmptyPickupClaimAmountForWorker(ZDO zdo, string prefab = null)
	{
		if (zdo == null || WorkerZdo.HasCargo(zdo)) return 0;
		string claimedPrefab = zdo.GetString(AirEmptyPickupClaimPrefabKey, string.Empty);
		if (string.IsNullOrEmpty(claimedPrefab)) return 0;
		if (!string.IsNullOrWhiteSpace(prefab) && !string.Equals(claimedPrefab, prefab, StringComparison.OrdinalIgnoreCase)) return 0;
		return Mathf.Max(0, zdo.GetInt(AirEmptyPickupClaimAmountKey, 0));
	}

	internal static ZDOID GetAirEmptyPickupClaimSourceForWorker(ZDO zdo)
	{
		if (zdo == null || WorkerZdo.HasCargo(zdo)) return ZDOID.None;
		return zdo.GetZDOID(AirEmptyPickupClaimSourceKey);
	}

	internal static int GetAirEmptyPickupClaimAmount(long ownerId, string prefab, Vector3 outpostTarget, float targetRadius = 40f)
	{
		return GetAirEmptyPickupClaimAmountInternal(ownerId, prefab, outpostTarget, ZDOID.None, targetRadius);
	}

	internal static int GetAirEmptyPickupClaimAmountExceptWorker(long ownerId, string prefab, Vector3 outpostTarget, ZDOID excludeWorker, float targetRadius = 40f)
	{
		return GetAirEmptyPickupClaimAmountInternal(ownerId, prefab, outpostTarget, excludeWorker, targetRadius);
	}

	private static int GetAirEmptyPickupClaimAmountInternal(long ownerId, string prefab, Vector3 outpostTarget, ZDOID excludeWorker, float targetRadius)
	{
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		if (string.IsNullOrEmpty(prefab))
		{
			return 0;
		}
		float num = Mathf.Max(1f, targetRadius);
		float num2 = num * num;
		long num3 = 0L;
		foreach (KeyValuePair<ZDOID, WorkerRecord> record in Records)
		{
			if (!excludeWorker.Equals(ZDOID.None) && record.Key.Equals(excludeWorker)) continue;
			ZDO val = record.Value?.Zdo;
			if (val == null || !WorkerZdo.IsWorker(val) || WorkerZdo.HasCargo(val))
			{
				continue;
			}
			long num4 = val.GetLong("vw_owner_player_id", 0L);
			if ((num4 != 0L && ownerId != 0L && num4 != ownerId) || !WorkerZdo.TryReadTarget(val, out var _, out var targetKind, out var _) || !string.Equals(targetKind, "airhaul_base_source", StringComparison.Ordinal) || !WorkerZdo.TryReadCargoRoute(val, out var route, out var target) || !string.Equals(route, "airhaul_outbound", StringComparison.Ordinal))
			{
				continue;
			}
			float num5 = target.x - outpostTarget.x;
			float num6 = target.z - outpostTarget.z;
			if (num5 * num5 + num6 * num6 > num2 || !string.Equals(val.GetString(AirEmptyPickupClaimPrefabKey, string.Empty), prefab, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			int num7 = val.GetInt(AirEmptyPickupClaimAmountKey, 0);
			if (num7 > 0)
			{
				num3 += num7;
				if (num3 >= int.MaxValue)
				{
					return int.MaxValue;
				}
			}
		}
		return (int)num3;
	}


	internal static int GetAirInboundPickupClaimAmountForSource(long ownerId, string prefab, ZDOID sourceId, ZDOID excludeWorker)
	{
		if (string.IsNullOrEmpty(prefab) || sourceId.Equals(ZDOID.None)) return 0;
		long total = 0L;
		foreach (KeyValuePair<ZDOID, WorkerRecord> record in Records)
		{
			if (!excludeWorker.Equals(ZDOID.None) && record.Key.Equals(excludeWorker)) continue;
			ZDO val = record.Value?.Zdo;
			if (val == null || !WorkerZdo.IsWorker(val) || WorkerZdo.HasCargo(val)) continue;
			if (!WorkerZdo.TryReadTarget(val, out _, out string targetKind, out _) || !string.Equals(targetKind, AirHaulerWorkRuntime.OutpostSourceTargetKind, StringComparison.Ordinal)) continue;
			if (!WorkerZdo.TryReadCargoRoute(val, out string route, out _) || !string.Equals(route, AirHaulerWorkRuntime.RouteInbound, StringComparison.Ordinal)) continue;
			long workerOwner = val.GetLong(WorkerZdo.OwnerPlayerId, 0L);
			if (workerOwner != 0L && ownerId != 0L && workerOwner != ownerId) continue;
			if (!string.Equals(val.GetString(AirEmptyPickupClaimPrefabKey, string.Empty), prefab, StringComparison.OrdinalIgnoreCase)) continue;
			if (!val.GetZDOID(AirEmptyPickupClaimSourceKey).Equals(sourceId)) continue;
			int amount = val.GetInt(AirEmptyPickupClaimAmountKey, 0);
			if (amount <= 0) continue;
			total += amount;
			if (total >= int.MaxValue) return int.MaxValue;
		}
		return (int)Math.Max(0L, total);
	}

	internal static int GetAirEmptyPickupClaimAmountForSource(long ownerId, string prefab, ZDOID sourceId, ZDOID excludeWorker)
	{
		if (string.IsNullOrEmpty(prefab) || sourceId.Equals(ZDOID.None)) return 0;
		long total = 0L;
		foreach (KeyValuePair<ZDOID, WorkerRecord> record in Records)
		{
			if (!excludeWorker.Equals(ZDOID.None) && record.Key.Equals(excludeWorker)) continue;
			ZDO val = record.Value?.Zdo;
			if (val == null || !WorkerZdo.IsWorker(val) || WorkerZdo.HasCargo(val)) continue;
			if (!WorkerZdo.TryReadTarget(val, out _, out string targetKind, out _) || !string.Equals(targetKind, AirHaulerWorkRuntime.BaseSourceTargetKind, StringComparison.Ordinal)) continue;
			if (!WorkerZdo.TryReadCargoRoute(val, out string route, out _) || !string.Equals(route, AirHaulerWorkRuntime.RouteOutbound, StringComparison.Ordinal)) continue;
			long workerOwner = val.GetLong(WorkerZdo.OwnerPlayerId, 0L);
			if (workerOwner != 0L && ownerId != 0L && workerOwner != ownerId) continue;
			if (!string.Equals(val.GetString(AirEmptyPickupClaimPrefabKey, string.Empty), prefab, StringComparison.OrdinalIgnoreCase)) continue;
			if (!val.GetZDOID(AirEmptyPickupClaimSourceKey).Equals(sourceId)) continue;
			int amount = val.GetInt(AirEmptyPickupClaimAmountKey, 0);
			if (amount <= 0) continue;
			total += amount;
			if (total >= int.MaxValue) return int.MaxValue;
		}
		return (int)Math.Max(0L, total);
	}

	internal static int GetAirOutboundInTransitAmount(long ownerId, string prefab, Vector3 outpostTarget, float targetRadius = 40f)
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		return GetOutboundCargoReservation(ownerId, prefab, "airhaul_outbound", outpostTarget, targetRadius);
	}

	internal static int GetGroundOutboundInTransitAmount(long ownerId, string prefab, Vector3 fieldDepotTarget, float targetRadius = 24f)
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		return GetOutboundCargoReservation(ownerId, prefab, "haul_outbound", fieldDepotTarget, targetRadius);
	}

	internal static int GetGroundOutboundInTransitAmount(long ownerId, string prefab, string workAreaId, Vector3 fallbackTarget, float fallbackRadius = 24f)
	{
		if (string.IsNullOrEmpty(workAreaId))
		{
			return GetGroundOutboundInTransitAmount(ownerId, prefab, fallbackTarget, fallbackRadius);
		}
		if (string.IsNullOrEmpty(prefab)) return 0;
		long total = 0L;
		float radius = Mathf.Max(1f, fallbackRadius);
		float radiusSqr = radius * radius;
		foreach (KeyValuePair<ZDOID, WorkerRecord> record in Records)
		{
			ZDO zdo = record.Value?.Zdo;
			if (zdo == null || !WorkerZdo.IsWorker(zdo)) continue;
			long workerOwner = zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L);
			if (workerOwner != 0L && ownerId != 0L && workerOwner != ownerId) continue;
			if (!WorkerZdo.TryReadCargoRoute(zdo, out string route, out Vector3 routeTarget) ||
				!string.Equals(route, WorkerLogisticsRuntime.RouteHaulOutbound, StringComparison.Ordinal)) continue;
			string routeWorkAreaId = WorkerZdo.ReadCargoRouteTargetWorkAreaId(zdo);
			if (!string.IsNullOrEmpty(routeWorkAreaId))
			{
				if (!string.Equals(routeWorkAreaId, workAreaId, StringComparison.Ordinal)) continue;
			}
			else
			{
				// Legacy in-flight cargo from pre-v0.5.342 saves had no Workzone id. Count it
				// only when its old concrete target still lies inside the requested zone area.
				float dx = routeTarget.x - fallbackTarget.x;
				float dz = routeTarget.z - fallbackTarget.z;
				if (dx * dx + dz * dz > radiusSqr) continue;
			}
			int amount = WorkerZdo.GetCargoAmount(zdo, prefab);
			if (amount <= 0) continue;
			total += amount;
			if (total >= int.MaxValue) return int.MaxValue;
		}
		return (int)total;
	}

	private static int GetOutboundCargoReservation(long ownerId, string prefab, string expectedRoute, Vector3 target, float targetRadius)
	{
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ad: Unknown result type (might be due to invalid IL or missing references)
		if (string.IsNullOrEmpty(prefab) || string.IsNullOrEmpty(expectedRoute))
		{
			return 0;
		}
		float num = Mathf.Max(1f, targetRadius);
		float num2 = num * num;
		long num3 = 0L;
		foreach (KeyValuePair<ZDOID, WorkerRecord> record in Records)
		{
			ZDO val = record.Value?.Zdo;
			if (val == null || !WorkerZdo.IsWorker(val))
			{
				continue;
			}
			long num4 = val.GetLong("vw_owner_player_id", 0L);
			if ((num4 != 0L && ownerId != 0L && num4 != ownerId) || !WorkerZdo.TryReadCargoRoute(val, out var route, out var target2) || !string.Equals(route, expectedRoute, StringComparison.Ordinal))
			{
				continue;
			}
			float num5 = target2.x - target.x;
			float num6 = target2.z - target.z;
			if (num5 * num5 + num6 * num6 > num2)
			{
				continue;
			}
			int cargoAmount = WorkerZdo.GetCargoAmount(val, prefab);
			if (cargoAmount > 0)
			{
				num3 += cargoAmount;
				if (num3 >= int.MaxValue)
				{
					return int.MaxValue;
				}
			}
		}
		return (int)num3;
	}

	internal static string GetDebugSummary(ZDO zdo)
	{
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		if (zdo == null)
		{
			return "n/a";
		}
		string text = zdo.GetString("vw_bg_mode", "physical");
		WorkOrder workOrder = (WorkOrder)zdo.GetInt("vw_bg_job", 0);
		string text2 = zdo.GetString("vw_bg_status", "not-started");
		string text3 = ((workOrder == WorkOrder.None) ? "none" : BackgroundJobCatalog.Describe(workOrder));
		string text4 = string.Empty;
		if (Records.TryGetValue(zdo.m_uid, out var value) && TryBuildTransportTelemetry(value, DateTime.UtcNow.Ticks, out var _, out var progress, out var etaSeconds, out var mode))
		{
			int num = Mathf.RoundToInt(progress * 100f);
			int num2 = etaSeconds / 60;
			int num3 = etaSeconds % 60;
			text4 = $" | route={mode} {num}% ETA={num2}:{num3:00}";
		}
		return $"{text} | job={workOrder} | {text2}{text4} | support={text3}";
	}

	internal static void Clear()
	{
		Records.Clear();
		WorkzoneEconomyBackgroundRuntime.Clear();
		BackgroundMonitorRuntime.Clear();
		Cleanup.Clear();
		_nextTick = 0f;
		_persistentProductionBootstrapComplete = false;
		_persistentProductionBootstrapPasses = 0;
		_nextPersistentBootstrapAttempt = 0f;
		PlayerProximityCache.Clear();
		_nextPlayerProximityCacheRefresh = 0f;
		_nextDemandDrivenBootstrapRequest = 0f;
	}

	internal static bool TryRecallBoundAirHauler(long ownerPlayerId, string species, string bindingZdoKey, string bindingValue, Vector3 target, out string message)
	{
		message = string.Empty;
		if (string.IsNullOrWhiteSpace(species) || string.IsNullOrWhiteSpace(bindingZdoKey) || string.IsNullOrWhiteSpace(bindingValue))
		{
			message = "Transportbindung ist ungueltig.";
			return false;
		}

		foreach (KeyValuePair<ZDOID, WorkerRecord> pair in Records)
		{
			WorkerRecord record = pair.Value;
			if (record == null || record.Zdo == null) continue;
			if (!string.Equals(record.Species, species, StringComparison.OrdinalIgnoreCase)) continue;
			if (record.Zdo.GetLong(WorkerZdo.OwnerPlayerId, 0L) != ownerPlayerId) continue;
			if (!string.Equals(record.Zdo.GetString(bindingZdoKey, string.Empty), bindingValue, StringComparison.Ordinal)) continue;
			if (WorkerZdo.HasCargo(record.Zdo))
			{
				message = $"{species} transportiert gerade Fracht und kann nicht gerufen werden.";
				return false;
			}

			WorkerZdo.ClearTarget(record.Zdo);
			record.Zdo.Set(WorkerZdo.State, (int)WorkerState.Idle);
			Vector3 safeTarget = target + Vector3.up * 2f;
			try { ZdoSetPositionMethod?.Invoke(record.Zdo, new object[] { safeTarget }); } catch { }
			if (record.LoadedCharacter != null)
			{
				record.LoadedCharacter.transform.position = safeTarget;
				WorkerNavigationRuntime.SetAirHaulerPhysicalPresentation(record.LoadedCharacter, visible: true);
			}
			record.LastPosition = safeTarget;
			message = $"{species} gerufen. Er kommt bei dir an.";
			return true;
		}

		message = $"Gebundener {species} ist derzeit nicht im Worker-Register verfuegbar.";
		return false;
	}

}
