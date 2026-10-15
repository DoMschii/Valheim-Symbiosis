using System;

namespace Valheim_Workers
{
    /// <summary>
    /// Small persistent per-worker filter for Firekeeper patrol duties.
    /// Directly assigned fires remain an explicit override; these switches only control workzone patrol.
    /// </summary>
    internal static class FirekeeperTaskFilterRuntime
    {
        private const string FiresKey = "vw_firekeeper_allow_fires";
        private const string OvensKey = "vw_firekeeper_allow_ovens";

        internal static bool AllowFires(ZDO zdo)
        {
            return zdo == null || zdo.GetInt(FiresKey, 1) != 0;
        }

        internal static bool AllowOvens(ZDO zdo)
        {
            return zdo == null || zdo.GetInt(OvensKey, 1) != 0;
        }

        internal static void SetAllowFires(ZDO zdo, bool value)
        {
            if (zdo == null) return;
            zdo.Set(FiresKey, value ? 1 : 0);
        }

        internal static void SetAllowOvens(ZDO zdo, bool value)
        {
            if (zdo == null) return;
            zdo.Set(OvensKey, value ? 1 : 0);
        }

        internal static bool HasCustomFilter(ZDO zdo)
        {
            if (zdo == null) return false;
            return zdo.GetInt(FiresKey, 1) == 0 || zdo.GetInt(OvensKey, 1) == 0;
        }
    }
}
