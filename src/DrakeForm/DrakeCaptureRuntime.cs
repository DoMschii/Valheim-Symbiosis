using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Valheim_Workers_DrakeForm
{
    /// <summary>
    /// Binding persistence/capture for all creature-specific necklaces. The class keeps its legacy
    /// name so old source references stay stable, but FormKey is now the authoritative discriminator.
    /// </summary>
    internal static class DrakeCaptureRuntime
    {
        private const string RecruitedKey = "vw_recruited";
        private const string OwnerKey = "vw_owner_player_id";
        private const string SpeciesKey = "vw_worker_species";
        private const string MorphEligibleKey = "vw_drake_morph_eligible";
        private const string MorphHatchOwnerKey = "vw_drake_morph_hatch_owner";
        private static float _nextSerpentWeakMessageAt;
        private static float _nextTrollWeakMessageAt;
        private static float _nextWerewolfWeakMessageAt;

        internal static bool TryCapture(Character target, HitData hit)
        {
            if (target == null || hit == null) return false;
            Player player = hit.GetAttacker() as Player;
            if (player == null || player != Player.m_localPlayer) return false;

            ItemDrop.ItemData medallion = FindEquippedMedallion(player, requireBound: false, requireUnbound: true);
            if (medallion == null) return false;

            string requestedForm = GetNecklaceFormByPrefab(medallion);
            if (string.Equals(requestedForm, SymbiosisFormCatalog.Drake, StringComparison.OrdinalIgnoreCase))
                return TryCaptureDrake(target, player, medallion);
            if (string.Equals(requestedForm, SymbiosisFormCatalog.Serpent, StringComparison.OrdinalIgnoreCase))
                return TryCaptureSerpent(target, player, medallion);
            if (string.Equals(requestedForm, SymbiosisFormCatalog.Troll, StringComparison.OrdinalIgnoreCase))
                return TryCaptureTroll(target, player, medallion);
            if (string.Equals(requestedForm, SymbiosisFormCatalog.Werewolf, StringComparison.OrdinalIgnoreCase))
                return TryCaptureWerewolf(target, player, medallion);
            return false;
        }

        private static bool TryCaptureDrake(Character target, Player player, ItemDrop.ItemData medallion)
        {
            ZNetView view = target.GetComponent<ZNetView>() ?? target.GetComponentInParent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || !zdo.GetBool(RecruitedKey, false)) return false;
            if (!string.Equals(zdo.GetString(SpeciesKey, string.Empty), "Drake", StringComparison.OrdinalIgnoreCase)) return false;
            if (!zdo.GetBool(MorphEligibleKey, false))
            {
                Notify(player, "Nur ein selbst ausgebrueteter Drake kann an eine Drachenhalskette gebunden werden.");
                return true;
            }

            long playerId = player.GetPlayerID();
            long owner = zdo.GetLong(OwnerKey, 0L);
            long hatchOwner = zdo.GetLong(MorphHatchOwnerKey, owner);
            if ((owner != 0L && owner != playerId) || (hatchOwner != 0L && hatchOwner != playerId))
            {
                Notify(player, "Dieser Drake gehoert nicht zu deiner Brut.");
                return true;
            }

            if (HasWorkerCargo(zdo))
            {
                Notify(player, "Der Drake traegt noch Fracht. Erst abladen, dann an die Halskette binden.");
                return true;
            }

            BindCreature(medallion, playerId, zdo, target, SymbiosisFormCatalog.Drake, "Drake");
            DestroyNetworkObject(target.gameObject);
            Notify(player, "Der Drake wurde an deine Drachenhalskette gebunden. Benutze die Halskette direkt oder ueber die Schnellleiste, um die Drachenform anzunehmen.");
            Plugin.Log?.LogInfo("[Symbiosis] Hatched Drake captured into necklace for player=" + playerId);
            return true;
        }

        private static bool TryCaptureSerpent(Character target, Player player, ItemDrop.ItemData medallion)
        {
            string prefab = Normalize(target.gameObject.name);
            if (!string.Equals(prefab, "Serpent", StringComparison.OrdinalIgnoreCase)) return false;
            if (target.IsDead()) return false;

            float maxHealth = Mathf.Max(1f, target.GetMaxHealth());
            float healthFraction = Mathf.Clamp01(target.GetHealth() / maxHealth);
            if (healthFraction > 0.30f)
            {
                // Let this attack hit normally so the player can actually weaken the Serpent. Only
                // the binding hit itself is consumed. Throttle the center message while fighting.
                if (Time.time >= _nextSerpentWeakMessageAt)
                {
                    _nextSerpentWeakMessageAt = Time.time + 1.25f;
                    Notify(player, $"Seeschlange erst schwaechen: {Mathf.CeilToInt(healthFraction * 100f)}% Leben (Bindung ab 30%).");
                }
                return false;
            }

            ZNetView view = target.GetComponent<ZNetView>() ?? target.GetComponentInParent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null) return false;

            long playerId = player.GetPlayerID();
            BindCreature(medallion, playerId, zdo, target, SymbiosisFormCatalog.Serpent, "Serpent");
            DestroyNetworkObject(target.gameObject);
            Notify(player, "Die Seeschlange wurde an deine Seeschlangenhalskette gebunden. Benutze die gebundene Halskette im Wasser, um ihre Gestalt anzunehmen.");
            Plugin.Log?.LogInfo("[Symbiosis] Wild Serpent captured into necklace for player=" + playerId);
            return true;
        }

        private static bool TryCaptureTroll(Character target, Player player, ItemDrop.ItemData medallion)
        {
            string prefab = Normalize(target.gameObject.name);
            if (!string.Equals(prefab, "Troll", StringComparison.OrdinalIgnoreCase)) return false;
            if (target.IsDead()) return false;

            float maxHealth = Mathf.Max(1f, target.GetMaxHealth());
            float healthFraction = Mathf.Clamp01(target.GetHealth() / maxHealth);
            if (healthFraction > 0.30f)
            {
                if (Time.time >= _nextTrollWeakMessageAt)
                {
                    _nextTrollWeakMessageAt = Time.time + 1.25f;
                    Notify(player, $"Troll erst schwaechen: {Mathf.CeilToInt(healthFraction * 100f)}% Leben (Bindung ab 30%).");
                }
                return false;
            }

            ZNetView view = target.GetComponent<ZNetView>() ?? target.GetComponentInParent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null) return false;

            long playerId = player.GetPlayerID();
            BindCreature(medallion, playerId, zdo, target, SymbiosisFormCatalog.Troll, "Troll");
            DestroyNetworkObject(target.gameObject);
            Notify(player, "Der Troll wurde an deine Trollhalskette gebunden. Benutze die gebundene Halskette an Land, um seine Gestalt anzunehmen.");
            Plugin.Log?.LogInfo("[Symbiosis] Wild Troll captured into necklace for player=" + playerId);
            return true;
        }

        private static bool TryCaptureWerewolf(Character target, Player player, ItemDrop.ItemData medallion)
        {
            string prefab = Normalize(target.gameObject.name);
            if (!string.Equals(prefab, "Fenring", StringComparison.OrdinalIgnoreCase)) return false;
            if (target.IsDead()) return false;

            float maxHealth = Mathf.Max(1f, target.GetMaxHealth());
            float healthFraction = Mathf.Clamp01(target.GetHealth() / maxHealth);
            if (healthFraction > 0.30f)
            {
                if (Time.time >= _nextWerewolfWeakMessageAt)
                {
                    _nextWerewolfWeakMessageAt = Time.time + 1.25f;
                    Notify(player, $"Fenring erst schwaechen: {Mathf.CeilToInt(healthFraction * 100f)}% Leben (Bindung ab 30%).");
                }
                return false;
            }

            ZNetView view = target.GetComponent<ZNetView>() ?? target.GetComponentInParent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null) return false;

            long playerId = player.GetPlayerID();
            BindCreature(medallion, playerId, zdo, target, SymbiosisFormCatalog.Werewolf, "Werewolf");
            DestroyNetworkObject(target.gameObject);
            Notify(player, "Der Fenring wurde an deine Werwolfhalskette gebunden. Benutze die Halskette an Land fuer die schnelle Werwolfform.");
            Plugin.Log?.LogInfo("[Symbiosis] Wild Fenring captured into Werewolf necklace for player=" + playerId);
            return true;
        }

        private static void BindCreature(ItemDrop.ItemData medallion, long playerId, ZDO source, Character target, string formId, string species)
        {
            EnsureCustomData(medallion);
            medallion.m_customData[DrakeMedallionItemRuntime.BoundKey] = "1";
            medallion.m_customData[DrakeMedallionItemRuntime.BoundOwnerKey] = playerId.ToString();
            medallion.m_customData[DrakeMedallionItemRuntime.BoundSourceKey] = source != null ? source.m_uid.ToString() : string.Empty;
            medallion.m_customData[DrakeMedallionItemRuntime.FormKey] = formId;
            medallion.m_customData[DrakeMedallionItemRuntime.BoundPrefabKey] = Normalize(target.gameObject.name);
            medallion.m_customData[DrakeMedallionItemRuntime.BoundLevelKey] = ReadCharacterLevel(target).ToString();
            medallion.m_customData[DrakeMedallionItemRuntime.BoundSpeciesKey] = species;
            medallion.m_customData[DrakeMedallionItemRuntime.BindingVersionKey] = DrakeMedallionItemRuntime.CurrentBindingVersion;
        }

        internal static bool IsBoundDrakeNecklace(ItemDrop.ItemData item)
        {
            return IsBoundFormNecklace(item, SymbiosisFormCatalog.Drake);
        }

        internal static bool IsBoundSymbiosisNecklace(ItemDrop.ItemData item)
        {
            return TryGetBoundForm(item, out _);
        }

        internal static bool IsBoundFormNecklace(ItemDrop.ItemData item, string formId)
        {
            return TryGetBoundForm(item, out string actual) && string.Equals(actual, formId, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryGetBoundForm(ItemDrop.ItemData item, out string formId)
        {
            formId = string.Empty;
            if (item == null || item.m_dropPrefab == null || !IsKnownNecklacePrefab(Normalize(item.m_dropPrefab.name))) return false;
            bool bound = item.m_customData != null &&
                         item.m_customData.TryGetValue(DrakeMedallionItemRuntime.BoundKey, out string value) &&
                         value == "1";
            if (!bound) return false;

            EnsureCustomData(item);
            if (!item.m_customData.TryGetValue(DrakeMedallionItemRuntime.FormKey, out formId) || string.IsNullOrWhiteSpace(formId))
            {
                formId = GetNecklaceFormByPrefab(item);
                item.m_customData[DrakeMedallionItemRuntime.FormKey] = formId;
            }
            return SymbiosisFormCatalog.IsKnown(formId);
        }

        internal static ItemDrop.ItemData FindBoundMedallionInInventory(Player player, string requiredForm = null)
        {
            Inventory inv = player?.GetInventory();
            List<ItemDrop.ItemData> items = inv?.GetAllItems();
            if (items == null) return null;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_dropPrefab == null) continue;
                if (!TryGetBoundForm(item, out string form)) continue;
                if (!string.IsNullOrWhiteSpace(requiredForm) &&
                    !string.Equals(form, requiredForm, StringComparison.OrdinalIgnoreCase)) continue;
                return item;
            }
            return null;
        }

        internal static ItemDrop.ItemData FindEquippedMedallion(Player player, bool requireBound, bool requireUnbound = false, string requiredForm = null)
        {
            Inventory inv = player?.GetInventory();
            List<ItemDrop.ItemData> items = inv?.GetAllItems();
            if (items == null) return null;
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || !item.m_equipped || item.m_dropPrefab == null) continue;
                string prefab = Normalize(item.m_dropPrefab.name);
                if (!IsKnownNecklacePrefab(prefab)) continue;

                bool bound = item.m_customData != null && item.m_customData.TryGetValue(DrakeMedallionItemRuntime.BoundKey, out string v) && v == "1";
                if (requireBound && !bound) continue;
                if (requireUnbound && bound) continue;

                string form = bound && TryGetBoundForm(item, out string savedForm) ? savedForm : GetNecklaceFormByPrefab(item);
                if (!string.IsNullOrWhiteSpace(requiredForm) && !string.Equals(form, requiredForm, StringComparison.OrdinalIgnoreCase)) continue;
                return item;
            }
            return null;
        }

        internal static int NormalizeSavedBindings(Player player)
        {
            Inventory inv = player?.GetInventory();
            List<ItemDrop.ItemData> items = inv?.GetAllItems();
            if (items == null) return 0;

            int repaired = 0;
            long playerId = player.GetPlayerID();
            for (int i = 0; i < items.Count; i++)
            {
                ItemDrop.ItemData item = items[i];
                if (item == null || item.m_dropPrefab == null) continue;
                if (!IsKnownNecklacePrefab(Normalize(item.m_dropPrefab.name))) continue;
                if (item.m_customData == null || !item.m_customData.TryGetValue(DrakeMedallionItemRuntime.BoundKey, out string bound) || bound != "1") continue;
                if (NormalizeBoundMetadata(item, playerId)) repaired++;
            }
            return repaired;
        }

        internal static bool NormalizeBoundMetadata(ItemDrop.ItemData item, long currentPlayerId)
        {
            if (item == null) return false;
            EnsureCustomData(item);
            bool changed = false;
            string form = GetNecklaceFormByPrefab(item);
            if (item.m_customData.TryGetValue(DrakeMedallionItemRuntime.FormKey, out string savedForm) && SymbiosisFormCatalog.IsKnown(savedForm))
                form = savedForm;

            if (!item.m_customData.TryGetValue(DrakeMedallionItemRuntime.BoundOwnerKey, out string ownerText) ||
                !long.TryParse(ownerText, out long owner) || owner == 0L)
            {
                if (currentPlayerId != 0L)
                {
                    item.m_customData[DrakeMedallionItemRuntime.BoundOwnerKey] = currentPlayerId.ToString();
                    changed = true;
                }
            }

            if (!item.m_customData.TryGetValue(DrakeMedallionItemRuntime.FormKey, out string existingForm) || !SymbiosisFormCatalog.IsKnown(existingForm))
            {
                item.m_customData[DrakeMedallionItemRuntime.FormKey] = form;
                changed = true;
            }

            string defaultPrefab = string.Equals(form, SymbiosisFormCatalog.Serpent, StringComparison.OrdinalIgnoreCase) ? "Serpent"
                : string.Equals(form, SymbiosisFormCatalog.Troll, StringComparison.OrdinalIgnoreCase) ? "Troll"
                : string.Equals(form, SymbiosisFormCatalog.Werewolf, StringComparison.OrdinalIgnoreCase) ? "Fenring"
                : "Hatchling";
            string defaultSpecies = string.Equals(form, SymbiosisFormCatalog.Serpent, StringComparison.OrdinalIgnoreCase) ? "Serpent"
                : string.Equals(form, SymbiosisFormCatalog.Troll, StringComparison.OrdinalIgnoreCase) ? "Troll"
                : string.Equals(form, SymbiosisFormCatalog.Werewolf, StringComparison.OrdinalIgnoreCase) ? "Werewolf"
                : "Drake";

            if (!item.m_customData.TryGetValue(DrakeMedallionItemRuntime.BoundPrefabKey, out string prefab) || string.IsNullOrWhiteSpace(prefab))
            {
                item.m_customData[DrakeMedallionItemRuntime.BoundPrefabKey] = defaultPrefab;
                changed = true;
            }
            if (!item.m_customData.TryGetValue(DrakeMedallionItemRuntime.BoundLevelKey, out string levelText) ||
                !int.TryParse(levelText, out int level) || level < 1)
            {
                item.m_customData[DrakeMedallionItemRuntime.BoundLevelKey] = "1";
                changed = true;
            }
            if (!item.m_customData.TryGetValue(DrakeMedallionItemRuntime.BoundSpeciesKey, out string species) ||
                !string.Equals(species, defaultSpecies, StringComparison.OrdinalIgnoreCase))
            {
                item.m_customData[DrakeMedallionItemRuntime.BoundSpeciesKey] = defaultSpecies;
                changed = true;
            }
            if (!item.m_customData.TryGetValue(DrakeMedallionItemRuntime.BindingVersionKey, out string version) ||
                !string.Equals(version, DrakeMedallionItemRuntime.CurrentBindingVersion, StringComparison.Ordinal))
            {
                item.m_customData[DrakeMedallionItemRuntime.BindingVersionKey] = DrakeMedallionItemRuntime.CurrentBindingVersion;
                changed = true;
            }
            return changed;
        }

        private static string GetNecklaceFormByPrefab(ItemDrop.ItemData item)
        {
            string prefab = item?.m_dropPrefab != null ? Normalize(item.m_dropPrefab.name) : string.Empty;
            if (string.Equals(prefab, SerpentMedallionItemRuntime.PrefabName, StringComparison.OrdinalIgnoreCase))
                return SymbiosisFormCatalog.Serpent;
            if (string.Equals(prefab, TrollMedallionItemRuntime.PrefabName, StringComparison.OrdinalIgnoreCase))
                return SymbiosisFormCatalog.Troll;
            if (string.Equals(prefab, WerewolfMedallionItemRuntime.PrefabName, StringComparison.OrdinalIgnoreCase))
                return SymbiosisFormCatalog.Werewolf;
            return SymbiosisFormCatalog.Drake;
        }

        private static bool IsKnownNecklacePrefab(string prefab)
        {
            return string.Equals(prefab, DrakeMedallionItemRuntime.PrefabName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(prefab, SerpentMedallionItemRuntime.PrefabName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(prefab, TrollMedallionItemRuntime.PrefabName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(prefab, WerewolfMedallionItemRuntime.PrefabName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasWorkerCargo(ZDO zdo)
        {
            if (zdo == null) return false;
            try
            {
                Type workerZdo = Type.GetType("Valheim_Workers.WorkerZdo, Valheim_Workers", false);
                MethodInfo method = workerZdo?.GetMethod("HasCargo", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(ZDO) }, null);
                object value = method?.Invoke(null, new object[] { zdo });
                return value is bool hasCargo && hasCargo;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Symbiosis] Worker cargo guard unavailable; capture continues safely: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static int ReadCharacterLevel(Character target)
        {
            if (target == null) return 1;
            try
            {
                MethodInfo method = target.GetType().GetMethod("GetLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                object value = method?.Invoke(target, null);
                if (value is int level) return Math.Max(1, level);
            }
            catch { }
            return 1;
        }

        private static void EnsureCustomData(ItemDrop.ItemData item)
        {
            if (item.m_customData == null) item.m_customData = new Dictionary<string, string>();
        }

        private static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            const string clone = "(Clone)";
            return name.EndsWith(clone, StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - clone.Length) : name;
        }

        private static void DestroyNetworkObject(GameObject target)
        {
            if (target == null) return;
            try
            {
                ZNetView view = target.GetComponent<ZNetView>() ?? target.GetComponentInParent<ZNetView>();
                if (view != null && view.IsValid() && !view.IsOwner())
                {
                    MethodInfo claim = typeof(ZNetView).GetMethod("ClaimOwnership", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    claim?.Invoke(view, null);
                }

                ZNetScene scene = ZNetScene.instance;
                MethodInfo destroy = scene?.GetType().GetMethod("Destroy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(GameObject) }, null);
                if (destroy != null) { destroy.Invoke(scene, new object[] { target }); return; }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Symbiosis] Network destroy/ownership claim failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }
            UnityEngine.Object.Destroy(target);
        }

        private static void Notify(Player p, string text)
        {
            try { p?.Message(MessageHud.MessageType.Center, text); } catch { }
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
    internal static class DrakeMedallionCaptureDamagePatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Character __instance, HitData hit)
        {
            return !DrakeCaptureRuntime.TryCapture(__instance, hit);
        }
    }
}
