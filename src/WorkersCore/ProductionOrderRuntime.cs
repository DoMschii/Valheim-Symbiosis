using System;
using System.Collections.Generic;
using System.Globalization;

namespace Valheim_Workers
{
    internal enum ProductionOrderMode
    {
        MakeAmount = 0,
        UntilStock = 1,
        MaintainStock = 2
    }

    internal sealed class ProductionOrderState
    {
        internal string Output = string.Empty;
        internal ProductionOrderMode Mode;
        internal int Target = 10;
        internal int Remaining = 10;
        internal bool Completed;

        internal ProductionOrderState Clone()
        {
            return new ProductionOrderState
            {
                Output = Output,
                Mode = Mode,
                Target = Target,
                Remaining = Remaining,
                Completed = Completed
            };
        }
    }

    internal readonly struct ProductionDemand
    {
        internal readonly string Output;
        internal readonly int Needed;
        internal readonly ProductionOrderMode Mode;

        internal ProductionDemand(string output, int needed, ProductionOrderMode mode)
        {
            Output = output ?? string.Empty;
            Needed = Math.Max(0, needed);
            Mode = mode;
        }
    }

    /// <summary>
    /// RimWorld-style production bills persisted on the owning production station ZDO. The first
    /// consumer is the Fuling Baker, but the storage format is deliberately generic so later
    /// cooks/smelters/crafters can use the same per-station orders without worker-specific state.
    /// </summary>
    internal static class ProductionOrderRuntime
    {
        private const string OrdersKey = "vw_production_orders_v1";

        internal const string BarleyFlour = "BarleyFlour";
        internal const string BreadDough = "BreadDough";
        internal const string Bread = "Bread";

        internal static readonly string[] BakerProducts = { BarleyFlour, BreadDough, Bread };

        internal static string DisplayProduct(string output)
        {
            if (string.Equals(output, BarleyFlour, StringComparison.OrdinalIgnoreCase)) return "Gerstenmehl";
            if (string.Equals(output, BreadDough, StringComparison.OrdinalIgnoreCase)) return "Brotteig";
            if (string.Equals(output, Bread, StringComparison.OrdinalIgnoreCase)) return "Brot";
            if (CookingRecipeRuntime.TryGetDisplayName(output, out string cookedName)) return cookedName;
            string itemName = CookingRecipeRuntime.DisplayIngredient(output);
            if (!string.IsNullOrWhiteSpace(itemName)) return itemName;
            return output ?? string.Empty;
        }

        internal static string DisplayMode(ProductionOrderMode mode)
        {
            switch (mode)
            {
                case ProductionOrderMode.MakeAmount: return "X Stueck herstellen";
                case ProductionOrderMode.UntilStock: return "Bis X erreicht";
                case ProductionOrderMode.MaintainStock: return "Bestand X halten";
                default: return "Unbekannt";
            }
        }

        internal static List<ProductionOrderState> Read(ZDO flagZdo)
        {
            var result = new List<ProductionOrderState>(8);
            if (flagZdo == null) return result;
            string raw = flagZdo.GetString(OrdersKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return result;

            string[] rows = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < rows.Length; i++)
            {
                string[] p = rows[i].Split('|');
                if (p.Length < 5 || string.IsNullOrEmpty(p[0])) continue;
                int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int modeRaw);
                int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int target);
                int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int remaining);
                bool completed = p[4] == "1";
                result.Add(new ProductionOrderState
                {
                    Output = p[0],
                    Mode = (ProductionOrderMode)Math.Max(0, Math.Min(2, modeRaw)),
                    Target = Math.Max(1, target),
                    Remaining = Math.Max(0, remaining),
                    Completed = completed
                });
            }
            return result;
        }

        internal static void Write(ZDO flagZdo, List<ProductionOrderState> orders)
        {
            if (flagZdo == null) return;
            if (orders == null || orders.Count == 0)
            {
                flagZdo.Set(OrdersKey, string.Empty);
                return;
            }

            var rows = new List<string>(orders.Count);
            for (int i = 0; i < orders.Count; i++)
            {
                ProductionOrderState o = orders[i];
                if (o == null || string.IsNullOrEmpty(o.Output)) continue;
                rows.Add(string.Join("|", new[]
                {
                    o.Output,
                    ((int)o.Mode).ToString(CultureInfo.InvariantCulture),
                    Math.Max(1, o.Target).ToString(CultureInfo.InvariantCulture),
                    Math.Max(0, o.Remaining).ToString(CultureInfo.InvariantCulture),
                    o.Completed ? "1" : "0"
                }));
            }
            flagZdo.Set(OrdersKey, string.Join(";", rows));
        }

        internal static bool HasOrderFor(ZDO flagZdo, string output)
        {
            List<ProductionOrderState> orders = Read(flagZdo);
            for (int i = 0; i < orders.Count; i++)
                if (string.Equals(orders[i].Output, output, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static void EnsureOrder(ZDO flagZdo, string output, int defaultTarget)
        {
            if (flagZdo == null || string.IsNullOrEmpty(output)) return;
            List<ProductionOrderState> orders = Read(flagZdo);
            for (int i = 0; i < orders.Count; i++)
            {
                if (!string.Equals(orders[i].Output, output, StringComparison.OrdinalIgnoreCase)) continue;
                orders[i].Completed = false;
                if (orders[i].Mode == ProductionOrderMode.MakeAmount && orders[i].Remaining <= 0)
                    orders[i].Remaining = Math.Max(1, orders[i].Target);
                Write(flagZdo, orders);
                return;
            }

            int target = Math.Max(1, defaultTarget);
            orders.Add(new ProductionOrderState
            {
                Output = output,
                Mode = ProductionOrderMode.MaintainStock,
                Target = target,
                Remaining = target,
                Completed = false
            });
            Write(flagZdo, orders);
        }

        internal static void SetMaintainStock(ZDO flagZdo, string output, int target)
        {
            if (flagZdo == null || string.IsNullOrEmpty(output)) return;
            int safeTarget = Math.Max(1, Math.Min(9999, target));
            List<ProductionOrderState> orders = Read(flagZdo);
            for (int i = 0; i < orders.Count; i++)
            {
                ProductionOrderState o = orders[i];
                if (!string.Equals(o.Output, output, StringComparison.OrdinalIgnoreCase)) continue;
                o.Mode = ProductionOrderMode.MaintainStock;
                o.Target = safeTarget;
                o.Remaining = safeTarget;
                o.Completed = false;
                Write(flagZdo, orders);
                return;
            }

            orders.Add(new ProductionOrderState
            {
                Output = output,
                Mode = ProductionOrderMode.MaintainStock,
                Target = safeTarget,
                Remaining = safeTarget,
                Completed = false
            });
            Write(flagZdo, orders);
        }

        internal static void RemoveOrder(ZDO flagZdo, string output)
        {
            if (flagZdo == null || string.IsNullOrEmpty(output)) return;
            List<ProductionOrderState> orders = Read(flagZdo);
            orders.RemoveAll(o => o != null && string.Equals(o.Output, output, StringComparison.OrdinalIgnoreCase));
            Write(flagZdo, orders);
        }

        internal static void CycleMode(ZDO flagZdo, string output)
        {
            if (flagZdo == null || string.IsNullOrEmpty(output)) return;
            List<ProductionOrderState> orders = Read(flagZdo);
            for (int i = 0; i < orders.Count; i++)
            {
                ProductionOrderState o = orders[i];
                if (!string.Equals(o.Output, output, StringComparison.OrdinalIgnoreCase)) continue;
                o.Mode = (ProductionOrderMode)(((int)o.Mode + 1) % 3);
                o.Completed = false;
                if (o.Mode == ProductionOrderMode.MakeAmount) o.Remaining = Math.Max(1, o.Target);
                Write(flagZdo, orders);
                return;
            }
        }

        internal static void AdjustTarget(ZDO flagZdo, string output, int delta)
        {
            if (flagZdo == null || string.IsNullOrEmpty(output) || delta == 0) return;
            List<ProductionOrderState> orders = Read(flagZdo);
            for (int i = 0; i < orders.Count; i++)
            {
                ProductionOrderState o = orders[i];
                if (!string.Equals(o.Output, output, StringComparison.OrdinalIgnoreCase)) continue;
                int oldTarget = Math.Max(1, o.Target);
                o.Target = Math.Max(1, Math.Min(9999, oldTarget + delta));
                o.Completed = false;
                if (o.Mode == ProductionOrderMode.MakeAmount)
                {
                    int produced = Math.Max(0, oldTarget - o.Remaining);
                    o.Remaining = Math.Max(0, o.Target - produced);
                }
                Write(flagZdo, orders);
                return;
            }
        }

        internal static bool TryGetOrder(ZDO flagZdo, string output, out ProductionOrderState order)
        {
            order = null;
            List<ProductionOrderState> orders = Read(flagZdo);
            for (int i = 0; i < orders.Count; i++)
            {
                if (!string.Equals(orders[i].Output, output, StringComparison.OrdinalIgnoreCase)) continue;
                order = orders[i].Clone();
                return true;
            }
            return false;
        }

        internal static bool TryGetNextDemand(ZDO flagZdo, Func<string, int> stockCounter, out ProductionDemand demand)
        {
            return TryGetNextDemandInternal(flagZdo, stockCounter, null, null, out demand);
        }

        /// <summary>
        /// Returns the first active bill that currently needs output and satisfies the optional
        /// runtime predicate. A temporarily blocked bill (missing ingredients, insufficient worker
        /// tier, unsupported live recipe, etc.) must not block every later bill on the same station.
        /// The previous station search asked for the first demand and only afterwards tested whether
        /// it was runnable, which could make a worker idle forever even though a later bill was ready.
        /// </summary>
        internal static bool TryGetNextDemand(ZDO flagZdo, Func<string, int> stockCounter, Func<string, bool> canRunOutput, out ProductionDemand demand)
        {
            return TryGetNextDemandInternal(flagZdo, stockCounter, canRunOutput, null, out demand);
        }

        /// <summary>
        /// Same demand evaluation, but ignores a claim already owned by the evaluating worker.
        /// This is used by long-running physical jobs (for example Smith ore pickup -> furnace) so
        /// their own reservation does not make the station appear satisfied while they are still
        /// carrying the reserved input. Claims from every other worker still reduce demand.
        /// </summary>
        internal static bool TryGetNextDemandForWorker(ZDO flagZdo, Func<string, int> stockCounter, Func<string, bool> canRunOutput, ZDO workerZdo, out ProductionDemand demand)
        {
            return TryGetNextDemandInternal(flagZdo, stockCounter, canRunOutput, workerZdo, out demand);
        }

        private static bool TryGetNextDemandInternal(ZDO flagZdo, Func<string, int> stockCounter, Func<string, bool> canRunOutput, ZDO excludedWorkerZdo, out ProductionDemand demand)
        {
            demand = default;
            if (flagZdo == null || stockCounter == null) return false;
            List<ProductionOrderState> orders = Read(flagZdo);
            bool changed = false;

            for (int i = 0; i < orders.Count; i++)
            {
                ProductionOrderState o = orders[i];
                if (o == null || string.IsNullOrEmpty(o.Output) || o.Completed) continue;

                int rawNeeded;
                if (o.Mode == ProductionOrderMode.MakeAmount)
                {
                    rawNeeded = Math.Max(0, o.Remaining);
                    if (rawNeeded <= 0)
                    {
                        o.Completed = true;
                        changed = true;
                        continue;
                    }
                }
                else
                {
                    int stock = Math.Max(0, stockCounter(o.Output));
                    rawNeeded = Math.Max(0, o.Target - stock);
                    if (rawNeeded <= 0)
                    {
                        if (o.Mode == ProductionOrderMode.UntilStock)
                        {
                            o.Completed = true;
                            changed = true;
                        }
                        continue;
                    }
                }

                // v0.5.351: subtract output that is already owned by an in-flight physical or
                // background recipe batch. This prevents two cooks from both seeing the same
                // MaintainStock deficit and independently fetching a full set of ingredients.
                int claimed = excludedWorkerZdo == null
                    ? ProductionOrderClaimRuntime.GetClaimedAmount(flagZdo, o.Output)
                    : ProductionOrderClaimRuntime.GetClaimedAmountExceptWorker(flagZdo, o.Output, excludedWorkerZdo);
                int needed = Math.Max(0, rawNeeded - claimed);
                if (needed <= 0) continue;

                if (canRunOutput != null && !canRunOutput(o.Output))
                    continue;

                if (changed) Write(flagZdo, orders);
                demand = new ProductionDemand(o.Output, needed, o.Mode);
                return true;
            }

            if (changed) Write(flagZdo, orders);
            return false;
        }

        internal static void RegisterProduced(ZDO flagZdo, string orderedOutput, int amount)
        {
            if (flagZdo == null || string.IsNullOrEmpty(orderedOutput) || amount <= 0) return;
            List<ProductionOrderState> orders = Read(flagZdo);
            for (int i = 0; i < orders.Count; i++)
            {
                ProductionOrderState o = orders[i];
                if (o == null || o.Completed || !string.Equals(o.Output, orderedOutput, StringComparison.OrdinalIgnoreCase)) continue;
                if (o.Mode == ProductionOrderMode.MakeAmount)
                {
                    o.Remaining = Math.Max(0, o.Remaining - amount);
                    if (o.Remaining <= 0) o.Completed = true;
                    Write(flagZdo, orders);
                }
                return;
            }
        }

        internal static string Describe(ProductionOrderState order)
        {
            if (order == null) return string.Empty;
            if (order.Completed) return "Fertig";
            if (order.Mode == ProductionOrderMode.MakeAmount) return $"Noch {Math.Max(0, order.Remaining)} / {Math.Max(1, order.Target)}";
            return $"Zielbestand {Math.Max(1, order.Target)}";
        }
    }
}
