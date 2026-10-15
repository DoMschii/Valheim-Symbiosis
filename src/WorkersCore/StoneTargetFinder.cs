using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Valheim_Workers
{
    internal sealed class StoneTarget
    {
        internal const string MineRock5Kind = "StoneMineRock5";
        internal const string MineRockKind = "StoneMineRock";
        internal const string DestructibleKind = "StoneDestructible";

        internal readonly MineRock5 Rock5;
        internal readonly MineRock Rock;
        internal readonly Destructible Destructible;
        internal readonly Collider AnchorCollider;
        internal readonly Vector3 AnchorPoint;

        internal StoneTarget(MineRock5 rock, Collider anchorCollider, Vector3 anchorPoint)
        {
            Rock5 = rock;
            Rock = null;
            Destructible = null;
            AnchorCollider = anchorCollider;
            AnchorPoint = anchorPoint;
        }

        internal StoneTarget(MineRock rock, Collider anchorCollider, Vector3 anchorPoint)
        {
            Rock = rock;
            Rock5 = null;
            Destructible = null;
            AnchorCollider = anchorCollider;
            AnchorPoint = anchorPoint;
        }

        internal StoneTarget(Destructible destructible, Collider anchorCollider, Vector3 anchorPoint)
        {
            Destructible = destructible;
            Rock5 = null;
            Rock = null;
            AnchorCollider = anchorCollider;
            AnchorPoint = anchorPoint;
        }

        internal GameObject GameObject => Rock5 != null ? Rock5.gameObject : Rock != null ? Rock.gameObject : Destructible != null ? Destructible.gameObject : null;
        // MineRock5 can expose many independent quarry patches under one root. Use the concrete
        // anchor collider as the per-worker retry identity so skipping one blocked face does not
        // blacklist the whole boulder for every approach.
        internal int IdentityKey => AnchorCollider != null ? AnchorCollider.GetInstanceID() : GameObject != null ? GameObject.GetInstanceID() : 0;
        internal string KindKey => Rock5 != null ? MineRock5Kind : Rock != null ? MineRockKind : DestructibleKind;
        internal int MinToolTier => Rock5 != null ? Rock5.m_minToolTier : Rock != null ? Rock.m_minToolTier : StoneTargetFinder.GetDestructibleMinToolTier(Destructible);
        // IMPORTANT: MineRock5 roots can cover a surprisingly large area. Persist the exact
        // collider surface point chosen by the quarry scan instead of the root transform.
        internal Vector3 Position => AnchorPoint;

        internal Collider ResolveCollider(Vector3 from)
        {
            if (AnchorCollider != null && AnchorCollider.enabled && AnchorCollider.gameObject.activeInHierarchy)
                return AnchorCollider;

            GameObject go = GameObject;
            if (go == null) return null;

            Collider[] colliders = go.GetComponentsInChildren<Collider>();
            Collider best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider c = colliders[i];
                if (c == null || !c.enabled) continue;
                float sqr = (ColliderGeometryRuntime.ClosestPointSafe(c, AnchorPoint) - AnchorPoint).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = c;
                }
            }
            return best;
        }

        internal Vector3 ClosestPoint(Vector3 from)
        {
            // Keep working the selected visible patch instead of allowing a huge MineRock5
            // collider hierarchy to move the interaction point to another side of the rock.
            Collider c = ResolveCollider(from);
            if (c != null)
            {
                Vector3 p = ColliderGeometryRuntime.ClosestPointSafe(c, AnchorPoint);
                if ((p - AnchorPoint).sqrMagnitude <= 0.25f) return AnchorPoint;
            }
            return AnchorPoint;
        }

        internal void Damage(HitData hit)
        {
            if (Rock5 != null) Rock5.Damage(hit);
            else if (Rock != null) Rock.Damage(hit);
            else if (Destructible != null) Destructible.Damage(hit);
        }
    }

    internal sealed class StoneTargetResult
    {
        internal StoneTarget Target;
        internal int Candidates;
        internal int PhysicsHits;
        internal bool BufferFull;
    }

    /// <summary>
    /// Local, throttled-by-WorkerAiController quarry target search. Stonework accepts real
    /// MineRock/MineRock5 nodes plus mineral Destructibles, including ore-bearing deposits.
    /// Capability/tool tier remains the hard gate for what a Stone Golem can actually damage.
    /// </summary>
    internal static class StoneTargetFinder
    {
        private const int ColliderBufferSize = 2048;
        private static readonly Collider[] ColliderBuffer = new Collider[ColliderBufferSize];
        private static readonly HashSet<int> SeenTargets = new HashSet<int>();
        private static readonly List<string> LastSamples = new List<string>(8);

        internal static int LastPhysicsHits { get; private set; }
        internal static int LastRawMineRock5Colliders { get; private set; }
        internal static int LastRawMineRockColliders { get; private set; }
        internal static int LastRawDestructibleColliders { get; private set; }
        internal static int LastResolvedStoneTargets { get; private set; }
        internal static int LastTierRejected { get; private set; }
        internal static int LastCandidates { get; private set; }
        internal static bool LastBufferFull { get; private set; }

        internal static string GetLastDebugSummary()
        {
            string samples = LastSamples.Count > 0 ? string.Join(", ", LastSamples.ToArray()) : "none";
            return $"hits={LastPhysicsHits}/{ColliderBufferSize}, raw R5={LastRawMineRock5Colliders}, R={LastRawMineRockColliders}, D={LastRawDestructibleColliders}, stone={LastResolvedStoneTargets}, tierReject={LastTierRejected}, usable={LastCandidates}, full={LastBufferFull}, samples=[{samples}]";
        }

        // Some small world rocks are ordinary Destructible prefabs instead of MineRock/MineRock5.
        // Resolve their vanilla fields defensively once and cache the FieldInfo; no reflection scan per frame.
        private static readonly FieldInfo DestructibleDropTableField = ResolveDestructibleField(typeof(DropTable), "m_dropItems");
        private static readonly FieldInfo DestructibleMinToolTierField = ResolveDestructibleField(typeof(int), "m_minToolTier");

        internal static StoneTargetResult FindNearest(Vector3 center, float radius, Vector3 workerPosition, int maxToolTier, Func<StoneTarget, bool> availability = null)
        {
            StoneTargetResult result = new StoneTargetResult();
            SeenTargets.Clear();
            LastPhysicsHits = 0;
            LastRawMineRock5Colliders = 0;
            LastRawMineRockColliders = 0;
            LastRawDestructibleColliders = 0;
            LastResolvedStoneTargets = 0;
            LastTierRejected = 0;
            LastCandidates = 0;
            LastBufferFull = false;
            LastSamples.Clear();

            int hits = Physics.OverlapSphereNonAlloc(center, radius, ColliderBuffer, ~0, QueryTriggerInteraction.Ignore);
            result.PhysicsHits = hits;
            result.BufferFull = hits >= ColliderBuffer.Length;
            LastPhysicsHits = hits;
            LastBufferFull = result.BufferFull;

            float bestSqr = float.MaxValue;
            for (int i = 0; i < hits; i++)
            {
                Collider collider = ColliderBuffer[i];
                ColliderBuffer[i] = null;
                if (collider == null) continue;

                MineRock5 rawRock5 = collider.GetComponentInParent<MineRock5>();
                MineRock rawRock = collider.GetComponentInParent<MineRock>();
                Destructible rawDestructible = collider.GetComponentInParent<Destructible>();
                if (rawRock5 != null) LastRawMineRock5Colliders++;
                if (rawRock != null) LastRawMineRockColliders++;
                if (rawDestructible != null) LastRawDestructibleColliders++;

                if (LastSamples.Count < 8 && (rawRock5 != null || rawRock != null || rawDestructible != null))
                {
                    GameObject sampleGo = rawRock5 != null ? rawRock5.gameObject : rawRock != null ? rawRock.gameObject : rawDestructible.gameObject;
                    string tag = rawRock5 != null ? "R5" : rawRock != null ? "R" : "D";
                    string sample = tag + ":" + NormalizePrefabName(sampleGo != null ? sampleGo.name : "null");
                    if (!LastSamples.Contains(sample)) LastSamples.Add(sample);
                }

                StoneTarget candidate = Resolve(collider, workerPosition);
                if (candidate == null || candidate.GameObject == null) continue;

                // A single MineRock5 root may contain many spatially separated colliders.
                // Treat the concrete collider as the selectable quarry patch.
                int instanceId = candidate.AnchorCollider != null ? candidate.AnchorCollider.GetInstanceID() : candidate.GameObject.GetInstanceID();
                if (!SeenTargets.Add(instanceId)) continue;
                LastResolvedStoneTargets++;
                if (candidate.MinToolTier > maxToolTier)
                {
                    LastTierRejected++;
                    continue;
                }
                if (!IsUsable(candidate, center, radius, maxToolTier)) continue;
                if (availability != null && !availability(candidate)) continue;

                result.Candidates++;
                LastCandidates = result.Candidates;
                Vector3 point = ColliderGeometryRuntime.ClosestPointSafe(candidate, workerPosition);
                float sqr = (point - workerPosition).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                result.Target = candidate;
            }

            SeenTargets.Clear();
            return result;
        }

        internal static StoneTarget ResolveNear(Vector3 position, string targetName, string kind, float radius, int maxToolTier)
        {
            int hits = Physics.OverlapSphereNonAlloc(position, radius, ColliderBuffer, ~0, QueryTriggerInteraction.Ignore);
            float bestSqr = float.MaxValue;
            StoneTarget best = null;
            SeenTargets.Clear();

            for (int i = 0; i < hits; i++)
            {
                Collider collider = ColliderBuffer[i];
                if (collider == null) continue;
                StoneTarget candidate = Resolve(collider, position);
                if (candidate == null || candidate.GameObject == null) continue;
                if (!string.Equals(candidate.KindKey, kind, StringComparison.Ordinal)) continue;

                int instanceId = candidate.AnchorCollider != null ? candidate.AnchorCollider.GetInstanceID() : candidate.GameObject.GetInstanceID();
                if (!SeenTargets.Add(instanceId)) continue;
                if (!string.IsNullOrEmpty(targetName) &&
                    !string.Equals(NormalizePrefabName(candidate.GameObject.name), targetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsMineableRockOrOre(candidate) || candidate.MinToolTier > maxToolTier) continue;

                float sqr = (candidate.Position - position).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = candidate;
            }

            return best;
        }

        internal static bool IsUsable(StoneTarget target, Vector3 center, float radius, int maxToolTier)
        {
            if (target == null || target.GameObject == null || !target.GameObject.activeInHierarchy) return false;
            if (target.MinToolTier > maxToolTier) return false;
            if (!IsMineableRockOrOre(target)) return false;

            Vector3 delta = target.Position - center;
            delta.y = 0f;
            return delta.sqrMagnitude <= radius * radius;
        }

        private static StoneTarget Resolve(Collider collider, Vector3 referencePoint)
        {
            // MineRock/MineRock5 are already authoritative mineral nodes in Valheim. Accept both
            // ordinary stone and ore deposits; required tool tier is checked by the caller.
            Vector3 anchor = collider != null ? ColliderGeometryRuntime.ClosestPointSafe(collider, referencePoint) : referencePoint;

            MineRock5 rock5 = collider.GetComponentInParent<MineRock5>();
            if (rock5 != null && rock5.gameObject != null)
                return new StoneTarget(rock5, collider, anchor);

            MineRock rock = collider.GetComponentInParent<MineRock>();
            if (rock != null && rock.gameObject != null)
                return new StoneTarget(rock, collider, anchor);

            Destructible destructible = collider.GetComponentInParent<Destructible>();
            if (IsMineableRockOrOreDestructible(destructible))
                return new StoneTarget(destructible, collider, anchor);

            return null;
        }

        private static bool IsStoneOnly(StoneTarget target)
        {
            if (target == null) return false;
            if (target.Rock5 != null) return IsStoneOnly(target.Rock5.m_dropItems);
            if (target.Rock != null) return IsStoneOnly(target.Rock.m_dropItems);
            if (target.Destructible != null) return IsStoneOnlyDestructible(target.Destructible);
            return false;
        }

        private static bool IsStoneOnly(DropTable table)
        {
            if (table == null || table.m_drops == null || table.m_drops.Count == 0) return false;

            bool foundStone = false;
            for (int i = 0; i < table.m_drops.Count; i++)
            {
                GameObject item = table.m_drops[i].m_item;
                if (item == null) return false;

                string prefabName = NormalizePrefabName(item.name);
                ItemDrop itemDrop = item.GetComponent<ItemDrop>();
                string sharedName = itemDrop != null && itemDrop.m_itemData != null && itemDrop.m_itemData.m_shared != null
                    ? itemDrop.m_itemData.m_shared.m_name
                    : string.Empty;

                bool isStone = string.Equals(prefabName, "Stone", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(sharedName, "$item_stone", StringComparison.OrdinalIgnoreCase);
                if (!isStone) return false;
                foundStone = true;
            }

            return foundStone;
        }


        private static bool IsMineableRockOrOre(StoneTarget target)
        {
            if (target == null || target.GameObject == null) return false;
            if (target.Rock5 != null || target.Rock != null) return true;
            if (target.Destructible != null) return IsMineableRockOrOreDestructible(target.Destructible);
            return false;
        }

        private static bool IsMineableRockOrOreDestructible(Destructible destructible)
        {
            if (destructible == null || destructible.gameObject == null) return false;
            if (destructible.GetComponent<TreeBase>() != null || destructible.GetComponent<TreeLog>() != null) return false;
            if (TreeTargetFinder.IsTreeStump(destructible)) return false;

            string prefab = NormalizePrefabName(destructible.gameObject.name);
            DropTable table = null;
            try
            {
                if (DestructibleDropTableField != null)
                    table = DestructibleDropTableField.GetValue(destructible) as DropTable;
            }
            catch { }

            // Ordinary Destructibles are accepted only when their identity or drops clearly look
            // mineral, so buildings/furniture never become quarry targets.
            if (LooksLikeRockName(prefab) || LooksLikeOreName(prefab)) return true;
            return DropTableContainsMineableResource(table);
        }

        private static bool LooksLikeOreSource(GameObject go, DropTable table)
        {
            if (go != null && LooksLikeOreName(NormalizePrefabName(go.name))) return true;
            if (table == null || table.m_drops == null) return false;
            for (int i = 0; i < table.m_drops.Count; i++)
            {
                GameObject item = table.m_drops[i].m_item;
                if (item == null) continue;
                string prefab = NormalizePrefabName(item.name);
                ItemDrop drop = item.GetComponent<ItemDrop>();
                string shared = drop != null && drop.m_itemData != null && drop.m_itemData.m_shared != null
                    ? drop.m_itemData.m_shared.m_name
                    : string.Empty;
                if (LooksLikeOreName(prefab) || LooksLikeOreName(shared)) return true;
            }
            return false;
        }

        private static bool DropTableContainsMineableResource(DropTable table)
        {
            if (table == null || table.m_drops == null) return false;
            for (int i = 0; i < table.m_drops.Count; i++)
            {
                GameObject item = table.m_drops[i].m_item;
                if (item == null) continue;
                string prefab = NormalizePrefabName(item.name);
                ItemDrop drop = item.GetComponent<ItemDrop>();
                string shared = drop != null && drop.m_itemData != null && drop.m_itemData.m_shared != null
                    ? drop.m_itemData.m_shared.m_name
                    : string.Empty;
                if (IsQuarryResourceName(prefab) || IsQuarryResourceName(shared)) return true;
            }
            return false;
        }

        internal static bool IsQuarryDrop(ItemDrop drop)
        {
            if (drop == null || drop.m_itemData == null) return false;
            string prefab = NormalizePrefabName(drop.gameObject != null ? drop.gameObject.name : string.Empty);
            string shared = drop.m_itemData.m_shared != null ? drop.m_itemData.m_shared.m_name : string.Empty;
            return IsQuarryResourceName(prefab) || IsQuarryResourceName(shared);
        }

        private static bool IsQuarryResourceName(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string n = value.ToLowerInvariant();
            if (n == "stone" || n == "$item_stone" || n.Contains("blackmarble") || n.Contains("black marble")) return true;
            return LooksLikeOreName(n);
        }

        private static bool LooksLikeRockName(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string n = value.ToLowerInvariant();
            return n.Contains("rock") || n.Contains("stone") || n.Contains("boulder");
        }

        private static bool LooksLikeOreName(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string n = value.ToLowerInvariant();
            return n.Contains("copper") || n.Contains("tin") || n.Contains("silver") ||
                   n.Contains("iron") || n.Contains("flametal") || n.Contains("ore") ||
                   n.Contains("meteor") || n.Contains("obsidian");
        }


        internal static bool IsConfigurableRockOrOre(MineRock5 rock)
        {
            return rock != null && rock.gameObject != null;
        }

        internal static bool IsConfigurableRockOrOre(MineRock rock)
        {
            return rock != null && rock.gameObject != null;
        }

        internal static bool IsConfigurableRockOrOre(Destructible destructible)
        {
            return IsMineableRockOrOreDestructible(destructible);
        }

        internal static DropTable GetDestructibleDropTable(Destructible destructible)
        {
            if (destructible == null || DestructibleDropTableField == null) return null;
            try
            {
                return DestructibleDropTableField.GetValue(destructible) as DropTable;
            }
            catch { return null; }
        }

        internal static int GetDestructibleMinToolTier(Destructible destructible)
        {
            if (destructible == null || DestructibleMinToolTierField == null) return 0;
            try
            {
                object value = DestructibleMinToolTierField.GetValue(destructible);
                return value is int tier ? tier : 0;
            }
            catch { return 0; }
        }

        private static bool IsStoneOnlyDestructible(Destructible destructible)
        {
            if (destructible == null) return false;
            if (destructible.GetComponent<TreeBase>() != null || destructible.GetComponent<TreeLog>() != null) return false;
            if (TreeTargetFinder.IsTreeStump(destructible)) return false;
            if (DestructibleDropTableField == null) return false;

            try
            {
                DropTable table = DestructibleDropTableField.GetValue(destructible) as DropTable;
                return IsStoneOnly(table);
            }
            catch { return false; }
        }

        private static FieldInfo ResolveDestructibleField(Type fieldType, string preferredName)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo preferred = typeof(Destructible).GetField(preferredName, flags);
            if (preferred != null && fieldType.IsAssignableFrom(preferred.FieldType)) return preferred;

            FieldInfo[] fields = typeof(Destructible).GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fieldType.IsAssignableFrom(fields[i].FieldType)) return fields[i];
            }
            return null;
        }

        internal static string NormalizePrefabName(string name) => SpeciesRoleRegistry.NormalizePrefabName(name);
    }
}
