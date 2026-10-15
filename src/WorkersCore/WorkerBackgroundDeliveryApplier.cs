using System.Collections.Generic;
using UnityEngine;

namespace Valheim_Workers
{
    /// <summary>
    /// Materializes conserved background deliveries when a logistics container becomes physical.
    /// The background side stores only a prefab/amount manifest on the target ZDO; no loaded
    /// Inventory is mutated remotely. Once the container is authoritative and its inventory is not
    /// inside an external-mutation quiet window, the exact queued amount is applied. Remaining items
    /// stay queued if the chest is full.
    /// </summary>
    internal sealed class WorkerBackgroundDeliveryApplier : MonoBehaviour
    {
        // v0.5.356: Background deliveries used to be checked by one Update component on every
        // loaded container. Because every component started with the same 3-second retry cadence,
        // a base with ~100 indexed containers woke all of them on the same frame and produced the
        // characteristic recurring hitch. Keep loaded appliers indexed by their ZDO instead and
        // wake only the container whose manifest actually changed.
        private static readonly Dictionary<ZDOID, WorkerBackgroundDeliveryApplier> LoadedByZdo =
            new Dictionary<ZDOID, WorkerBackgroundDeliveryApplier>();

        private Container _container;
        private ZNetView _view;
        private ZDO _zdo;
        private ZDOID _registeredId = ZDOID.None;
        private float _nextAttempt;
        private bool _wakeRequested;

        internal static void Ensure(Container container)
        {
            if (container == null) return;
            GameObject go = container.gameObject;
            if (go == null) return;
            WorkerBackgroundDeliveryApplier applier = go.GetComponent<WorkerBackgroundDeliveryApplier>();
            if (applier == null) applier = go.AddComponent<WorkerBackgroundDeliveryApplier>();
            applier.Initialize(container);
        }

        internal static void NotifyPending(ZDO zdo)
        {
            if (zdo == null) return;
            WorkerBackgroundDeliveryApplier applier;
            if (!LoadedByZdo.TryGetValue(zdo.m_uid, out applier) || applier == null) return;
            applier._wakeRequested = true;
            applier._nextAttempt = 0f;
            applier.enabled = true;
        }

        private void Initialize(Container container)
        {
            _container = container;
            ResolveNetworkState();
            RegisterLoaded();

            _wakeRequested = _zdo != null && AdvancedFieldDepotRuntime.HasPendingBackgroundDelivery(_zdo);
            _nextAttempt = 0f;
            enabled = _wakeRequested;
        }

        private void OnEnable()
        {
            if (_container != null)
            {
                ResolveNetworkState();
                RegisterLoaded();

                if (_zdo != null && AdvancedFieldDepotRuntime.HasPendingBackgroundDelivery(_zdo))
                {
                    _wakeRequested = true;
                    _nextAttempt = 0f;
                }
            }
        }

        private void OnDisable()
        {
            // v0.5.455: remain indexed while idle/disabled so NotifyPending()
            // can wake this exact chest without any discovery scan.
        }

        private void OnDestroy()
        {
            UnregisterLoaded();
        }

        private void ResolveNetworkState()
        {
            if (_container == null) _container = GetComponent<Container>() ?? GetComponentInChildren<Container>(true);
            if (_container == null) return;
            if (_view == null) _view = WorkerContainerRoleRuntime.GetNetworkView(_container);
            if (_view != null && _view.IsValid()) _zdo = _view.GetZDO();
        }

        private void RegisterLoaded()
        {
            if (_zdo == null) return;
            ZDOID id = _zdo.m_uid;
            if (!_registeredId.Equals(ZDOID.None) && !_registeredId.Equals(id))
                LoadedByZdo.Remove(_registeredId);
            _registeredId = id;
            LoadedByZdo[id] = this;
        }

        private void UnregisterLoaded()
        {
            if (_registeredId.Equals(ZDOID.None)) return;
            WorkerBackgroundDeliveryApplier current;
            if (LoadedByZdo.TryGetValue(_registeredId, out current) && current == this)
                LoadedByZdo.Remove(_registeredId);
            _registeredId = ZDOID.None;
        }

        private void Update()
        {
            if (!_wakeRequested)
            {
                enabled = false;
                return;
            }

            // A queue write calls NotifyPending(), and a container that loads with an already
            // persisted manifest requests one immediate pass in Initialize().
            if (Time.unscaledTime < _nextAttempt) return;

            ResolveNetworkState();
            RegisterLoaded();
            if (_container == null || _view == null || !_view.IsValid() || !_view.IsOwner() || _zdo == null)
            {
                // Retry only this container while it really has pending work. This is intentionally
                // not synchronized with every other chest anymore.
                _nextAttempt = Time.unscaledTime + 0.75f;
                return;
            }

            if (!AdvancedFieldDepotRuntime.HasPendingBackgroundDelivery(_zdo))
            {
                _wakeRequested = false;
                enabled = false;
                return;
            }

            Inventory inventory = _container.GetInventory();
            if (inventory == null || !WorkerInventoryMutationGuard.CanWorkersMutate(inventory))
            {
                _nextAttempt = Time.unscaledTime + 0.5f;
                return;
            }

            AdvancedFieldDepotRuntime.ApplyPendingBackgroundDeliveries(_container, _zdo);
            if (AdvancedFieldDepotRuntime.HasPendingBackgroundDelivery(_zdo))
            {
                _nextAttempt = Time.unscaledTime + 0.5f;
            }
            else
            {
                _wakeRequested = false;
                enabled = false;
            }
        }
    }
}
