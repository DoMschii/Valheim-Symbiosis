using System;
using UnityEngine;

namespace Valheim_Workers
{
    internal static class WorkFlagZdo
    {
        internal const string FlagId = "vw_workflag_id";
        internal const string OwnerPlayerId = "vw_workflag_owner";
        // Legacy single-order field kept for backwards migration and older saves.
        internal const string Order = "vw_workflag_order";
        internal const string OrdersMask = "vw_workflag_orders_mask";
        internal const string Radius = "vw_workflag_radius";
        internal const string Enabled = "vw_workflag_enabled";
        internal const string DisplayName = "vw_workflag_name";

        internal static void InitializeIfNeeded(ZDO zdo, float defaultRadius)
        {
            if (zdo == null) return;
            if (zdo.GetFloat(Radius, 0f) <= 0f)
                zdo.Set(Radius, Mathf.Clamp(defaultRadius, 10f, 100f));

            // v0.5.106: A WorkFlag defines only a geographic Workzone. Jobs are configured per
            // worker at its Home/Rest station. Keep a universal implemented mask persisted for
            // backwards compatibility with older runtimes that still expect an order mask.
            int universalMask = WorkOrderCatalog.ImplementedMask;
            if (zdo.GetInt(OrdersMask, 0) != universalMask)
                zdo.Set(OrdersMask, universalMask);
            zdo.Set(Order, (int)WorkOrderCatalog.FirstEnabled(universalMask));
            zdo.Set(Enabled, true);
        }

        internal static string EnsureId(ZDO zdo)
        {
            if (zdo == null) return string.Empty;
            string id = zdo.GetString(FlagId, string.Empty);
            if (!string.IsNullOrEmpty(id)) return id;
            id = Guid.NewGuid().ToString("N");
            zdo.Set(FlagId, id);
            return id;
        }

        internal static string ReadId(ZDO zdo) => zdo?.GetString(FlagId, string.Empty) ?? string.Empty;
        internal static int ReadOrdersMask(ZDO zdo) => zdo == null ? 0 : WorkOrderCatalog.ImplementedMask;
        internal static WorkOrder ReadOrder(ZDO zdo) => WorkOrderCatalog.FirstEnabled(ReadOrdersMask(zdo));
        internal static float ReadRadius(ZDO zdo, float fallback) => zdo == null ? fallback : Mathf.Clamp(zdo.GetFloat(Radius, fallback), 10f, 100f);
        internal static long ReadOwner(ZDO zdo) => zdo?.GetLong(OwnerPlayerId, 0L) ?? 0L;
        internal static string ReadDisplayName(ZDO zdo) => zdo?.GetString(DisplayName, string.Empty) ?? string.Empty;

        internal static string EnsureDisplayName(ZDO zdo, int suggestedIndex)
        {
            if (zdo == null) return string.Empty;
            string current = ReadDisplayName(zdo);
            if (!string.IsNullOrWhiteSpace(current)) return current;
            current = $"Workzone{Mathf.Max(1, suggestedIndex)}";
            zdo.Set(DisplayName, current);
            return current;
        }

        internal static void SetDisplayName(ZDO zdo, string value)
        {
            if (zdo == null) return;
            string clean = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (clean.Length > 32) clean = clean.Substring(0, 32);
            if (string.IsNullOrWhiteSpace(clean)) return;
            zdo.Set(DisplayName, clean);
        }
        internal static bool IsEnabled(ZDO zdo) => zdo != null;
        internal static bool HasOrder(ZDO zdo, WorkOrder order) => WorkOrderCatalog.Contains(ReadOrdersMask(zdo), order);

        internal static bool TryResolveOrderForRole(ZDO zdo, WorkerRole role, out WorkOrder order)
        {
            order = WorkOrder.None;
            return zdo != null && WorkOrderCatalog.TryResolveForRole(ReadOrdersMask(zdo), role, out order);
        }

        // Legacy compatibility helpers. Since v0.5.106 a flag no longer owns jobs; any old
        // internal caller simply reasserts the universal Workzone compatibility mask.
        internal static void SetOrderEnabled(ZDO zdo, long ownerPlayerId, WorkOrder order, bool enabled)
        {
            if (zdo == null) return;
            EnsureId(zdo);
            zdo.Set(OwnerPlayerId, ownerPlayerId);
            int mask = WorkOrderCatalog.ImplementedMask;
            zdo.Set(OrdersMask, mask);
            zdo.Set(Order, (int)WorkOrderCatalog.FirstEnabled(mask));
            zdo.Set(Enabled, true);
        }

        internal static void ClearOrders(ZDO zdo, long ownerPlayerId)
        {
            if (zdo == null) return;
            EnsureId(zdo);
            zdo.Set(OwnerPlayerId, ownerPlayerId);
            int mask = WorkOrderCatalog.ImplementedMask;
            zdo.Set(OrdersMask, mask);
            zdo.Set(Order, (int)WorkOrderCatalog.FirstEnabled(mask));
            zdo.Set(Enabled, true);
        }

        internal static void SetOrder(ZDO zdo, long ownerPlayerId, WorkOrder order)
        {
            SetOrderEnabled(zdo, ownerPlayerId, order, true);
        }

        internal static void SetRadius(ZDO zdo, float radius)
        {
            if (zdo == null) return;
            zdo.Set(Radius, Mathf.Clamp(radius, 10f, 100f));
        }
    }
}
