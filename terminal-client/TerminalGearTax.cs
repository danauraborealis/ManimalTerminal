using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // PORT SECURITY GEAR CONFISCATION (user spec 2026-08-10): entering terminal takes
    // your headgear (helmet/headset/facecover/glasses), every weapon incl melee, your
    // backpack, and everything carried INSIDE the gear you keep (rig, pockets, and the
    // pack'n'strap belt when present). the rig, armor vest and belt stay equipped.
    // secured container is never touched.
    //
    // "perfect exact copies" by construction: the ORIGINAL Item objects are moved —
    // ids, durability, attachments and loaded ammo ride along untouched — into ONE
    // random equipment cabinet (gun safe), whose generated loot is cleared first.
    // safe is picked BEFORE anything is taken: no cabinet bound in the scene = no
    // confiscation, so gear can never be stranded in limbo.
    internal static class TerminalGearTax
    {
        private const string CabinetTpl = "68a4c0ffee0000000000cab1";
        private static readonly FieldInfo LastEquippedField =
            AccessTools.Field(typeof(Player), "_lastEquippedWeaponOrKnifeItem");

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_GearTax
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On || !Plugin.GearConfiscation.Value) return;
                new GameObject("Terminal_GearTax").AddComponent<TaxHost>();
            }
        }

        internal class TaxHost : MonoBehaviour
        {
            private void Start() => StartCoroutine(Run());

            private IEnumerator Run()
            {
                // let loot bind + the player settle; the intro cutscene covers this
                yield return new WaitForSeconds(2f);
                var player = Singleton<GameWorld>.Instance?.MainPlayer;
                float deadline = Time.time + 15f;
                while (player == null && Time.time < deadline)
                {
                    yield return new WaitForSeconds(0.5f);
                    player = Singleton<GameWorld>.Instance?.MainPlayer;
                }
                if (player == null) { Done("no main player"); yield break; }

                // the container bind can be DEFERRED behind the registry self-repair
                // (TerminalLootBind, 2026-08-18) — wait for a bound safe instead of
                // sampling once and losing the race. Pick and validate the destination
                // BEFORE touching the player's hands or inventory.
                LootableContainer safe = null;
                CompoundItem root = null;
                float bindDeadline = Time.time + 30f;
                while (Time.time < bindDeadline && !TryPickCabinet(out safe, out root))
                    yield return new WaitForSeconds(1f);
                if (safe == null || root == null)
                {
                    Done("no usable bound valberg safe in scene — gear and hands untouched");
                    yield break;
                }

                // SetEmptyHands is an asynchronous hands-controller operation. The old
                // fixed 0.6s delay could elapse during one long startup frame while the
                // holster animation/controller had not advanced at all. Removing the
                // held item at that point orphaned FirearmController and corrupted the
                // first-person camera. Await the real callback AND the native idle state.
                bool handsCallback = false;
                if (!HandsSettled(player))
                {
                    try
                    {
                        Plugin.Log.LogDebug($"[GearTax] empty-hands requested from {HandsState(player)}");
                        player.SetEmptyHands(new Callback<GInterface198>(_ => handsCallback = true));
                    }
                    catch (Exception e)
                    {
                        Done($"could not request empty hands ({e.Message}) — gear untouched");
                        yield break;
                    }

                    float handsStarted = Time.realtimeSinceStartup;
                    float handsDeadline = handsStarted + 30f;
                    while (Time.realtimeSinceStartup < handsDeadline
                        && (!handsCallback || !HandsSettled(player)))
                        yield return null; // animation/controller must receive real frames

                    if (!handsCallback || !HandsSettled(player))
                    {
                        Done($"empty-hands transition timed out after 30s ({HandsState(player)}, callback={handsCallback}) — gear untouched");
                        yield break;
                    }
                    Plugin.Log.LogDebug($"[GearTax] empty hands settled in {Time.realtimeSinceStartup - handsStarted:F2}s");
                }

                yield return StartCoroutine(Confiscate(player, safe, root));
                Destroy(gameObject);
            }

            private static bool TryPickCabinet(out LootableContainer safe, out CompoundItem root)
            {
                safe = null;
                root = null;
                try
                {
                    var candidates = UnityEngine.Object.FindObjectsOfType<LootableContainer>()
                        .Where(l => l != null && l.ItemOwner != null
                            && (l.name.IndexOf("valberg", StringComparison.OrdinalIgnoreCase) >= 0
                                || (l.transform.parent != null && l.transform.parent.name.IndexOf("valberg", StringComparison.OrdinalIgnoreCase) >= 0))
                            && l.ItemOwner.RootItem is CompoundItem c && c.Grids != null && c.Grids.Length > 0)
                        .ToList();
                    if (candidates.Count == 0) return false;
                    safe = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                    root = safe.ItemOwner.RootItem as CompoundItem;
                    return root?.Grids != null && root.Grids.Length > 0;
                }
                catch { }
                return false;
            }

            private static bool HandsSettled(Player player)
                => player != null
                    && player.HandsController is Player.EmptyHandsController
                    && player.ProcessStatus == Player.EProcessStatus.None;

            private static string HandsState(Player player)
            {
                try
                {
                    return $"controller={player?.HandsController?.GetType().Name ?? "<null>"}, process={player?.ProcessStatus.ToString() ?? "<null>"}";
                }
                catch { return "hands state unavailable"; }
            }

            private IEnumerator Confiscate(Player player, LootableContainer safe, CompoundItem root)
            {
                var eq = player.Profile?.Inventory?.Equipment;
                var controller = player.InventoryController;
                if (eq == null || controller == null) { Done("no equipment controller"); yield break; }

                var taken = new List<Item>();

                // First collect references without mutating anything. The old version
                // called Slot.RemoveItem/Grid.RemoveWithoutRestrictions directly and
                // then added the same object to the world container. That changed the
                // object graph but skipped Tarkov's inventory operation and event
                // pipeline; the next weapon pickup could consequently enter a broken
                // FirearmController with no exception in the log.
                EquipmentSlot[] slots =
                {
                    EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.FaceCover, EquipmentSlot.Eyewear,
                    EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster,
                    EquipmentSlot.Scabbard, EquipmentSlot.Backpack,
                };
                foreach (var sl in slots)
                {
                    var slot = eq.GetSlot(sl);
                    var it = slot?.ContainedItem;
                    if (it == null) continue;
                    if (!taken.Contains(it)) taken.Add(it);
                }

                // contents-only: rig + pockets + SECURE CONTAINER (user 2026-08-18)
                // stay equipped, their cargo doesnt. pack'n'strap belt found by slot id
                // — a modded slot, not in the enum.
                var hosts = new List<CompoundItem>();
                if (eq.GetSlot(EquipmentSlot.TacticalVest)?.ContainedItem is CompoundItem rig) hosts.Add(rig);
                if (eq.GetSlot(EquipmentSlot.Pockets)?.ContainedItem is CompoundItem pockets) hosts.Add(pockets);
                if (eq.GetSlot(EquipmentSlot.SecuredContainer)?.ContainedItem is CompoundItem secure) hosts.Add(secure);
                foreach (var s in eq.GetAllSlots())
                    if ((s.ID ?? "").IndexOf("belt", StringComparison.OrdinalIgnoreCase) >= 0
                        && s.ContainedItem is CompoundItem belt && !hosts.Contains(belt))
                        hosts.Add(belt);

                foreach (var host in hosts)
                {
                    if (host.Grids == null) continue;
                    foreach (var grid in host.Grids)
                        foreach (var it in grid.Items.ToList())
                            if (!taken.Contains(it)) taken.Add(it);
                }

                if (taken.Count == 0) { Done("nothing to confiscate"); yield break; }

                var last = player.LastEquippedWeaponOrKnifeItem;
                int boundBefore = CountTakenFastAccess(player, taken);

                // A world loot container is a different item owner. EFT deliberately
                // rejects moves into an owner that the player's search controller has
                // not observed yet ("Cannot transfer to unknown address"). The intro
                // confiscation is authoritative and happens before the player can open
                // this cabinet, so register its root as known/searched before creating
                // the same cross-owner Move operation the loot UI would use.
                var search = player.SearchController as IPlayerSearchController;
                if (search == null)
                {
                    Done("no player search controller — gear untouched");
                    yield break;
                }
                try
                {
                    if (!search.IsItemKnown(root, null))
                        search.SetItemAsKnown(root, false);
                    if (root is SearchableItemItemClass searchable && !search.IsSearched(searchable))
                        search.SetItemAsSearched(searchable);
                }
                catch (Exception e)
                {
                    Done($"could not authorize cabinet transfer ({e.Message}) — gear untouched");
                    yield break;
                }

                // out with the cabinet's generated loot, in with the player's gear —
                // big items first so the packer never strands a rifle behind a bandage.
                // InteractionsHandlerClass.Move + TryRunNetworkTransaction is the same
                // native path used by EFT's inventory UI and transfer requirements. It
                // owns remove/add events, slot caches, fast-access unbinding, operation
                // locking and rollback.
                foreach (var grid in root.Grids)
                    grid.RemoveAll();

                int placed = 0;
                int failed = 0;
                foreach (var it in taken.OrderByDescending(CellArea))
                {
                    GClass3393 destination = null;
                    foreach (var grid in root.Grids)
                    {
                        var loc = grid.FindFreeSpace(it);
                        if (loc == null) continue;
                        destination = grid.CreateItemAddress(loc);
                        break;
                    }
                    if (destination == null)
                    {
                        failed++;
                        Plugin.Log.LogError($"[GearTax] NO ROOM in cabinet for '{it.LocalizedName()}' — item remains on player");
                        continue;
                    }

                    GStruct154<GClass3411> move;
                    try { move = InteractionsHandlerClass.Move(it, destination, controller, true); }
                    catch (Exception e)
                    {
                        failed++;
                        Plugin.Log.LogWarning($"[GearTax] native move construction failed for '{it.LocalizedName()}': {e.Message}");
                        continue;
                    }
                    if (move.Failed)
                    {
                        failed++;
                        Plugin.Log.LogWarning($"[GearTax] native move rejected for '{it.LocalizedName()}': {move.Error}");
                        continue;
                    }

                    System.Threading.Tasks.Task<IResult> task;
                    try { task = controller.TryRunNetworkTransaction(move, null); }
                    catch (Exception e)
                    {
                        failed++;
                        Plugin.Log.LogWarning($"[GearTax] native transaction start failed for '{it.LocalizedName()}': {e.Message}");
                        continue;
                    }

                    float transactionDeadline = Time.realtimeSinceStartup + 30f;
                    while (!task.IsCompleted && Time.realtimeSinceStartup < transactionDeadline)
                        yield return null;
                    if (!task.IsCompleted)
                    {
                        failed++;
                        Plugin.Log.LogError($"[GearTax] native transaction timed out for '{it.LocalizedName()}' — confiscation stopped to avoid overlapping inventory operations");
                        break;
                    }
                    if (task.IsFaulted || task.IsCanceled)
                    {
                        failed++;
                        Plugin.Log.LogWarning($"[GearTax] native transaction faulted for '{it.LocalizedName()}': {task.Exception?.GetBaseException().Message ?? "cancelled"}");
                        continue;
                    }
                    var result = task.Result;
                    if (result == null || result.Failed)
                    {
                        failed++;
                        Plugin.Log.LogWarning($"[GearTax] native transaction failed for '{it.LocalizedName()}': {result?.Error?.ToString() ?? "no result"}");
                        continue;
                    }
                    placed++;
                }

                // LastEquippedWeaponOrKnifeItem's public setter ignores null. Clear its
                // backing field only if that exact item actually completed the move.
                bool clearedLast = last != null && IsOwnedBy(last, safe.ItemOwner);
                if (clearedLast)
                {
                    if (LastEquippedField != null) LastEquippedField.SetValue(player, null);
                    else Plugin.Log.LogWarning("[GearTax] could not clear stale last-equipped item field");
                }
                int boundAfter = CountTakenFastAccess(player, taken);

                Plugin.Log.LogInfo($"[GearTax] confiscated {placed}/{taken.Count} item(s) -> '{safe.name}' via native transactions "
                    + $"(failed={failed}, quickBindingsCleared={Math.Max(0, boundBefore - boundAfter)}, staleBindings={boundAfter}, "
                    + $"lastEquippedCleared={clearedLast}) at {safe.transform.position}");
                try
                {
                    // informational only — the MP Officer's authored lines play as
                    // timed subtitles during the intro (TerminalSubtitles)
                    NotificationManagerClass.DisplayMessageNotification(
                        "Port security has confiscated your equipment. It is locked in one of the terminal's equipment cabinets.",
                        ENotificationDurationType.Long, ENotificationIconType.Alert, Color.yellow);
                }
                catch { }
            }

            private static int CountTakenFastAccess(Player player, List<Item> taken)
            {
                try
                {
                    var controller = player.InventoryController;
                    if (controller?.Inventory?.FastAccess?.BoundItems == null) return 0;
                    return controller.Inventory.FastAccess.BoundItems.Values
                        .Where(item => item != null && taken.Contains(item))
                        .Distinct()
                        .Count();
                }
                catch { return 0; }
            }

            private static bool IsOwnedBy(Item item, IItemOwner owner)
            {
                try
                {
                    var currentOwner = item?.Parent?.GetOwner();
                    return currentOwner != null && owner != null
                        && (ReferenceEquals(currentOwner, owner) || currentOwner.ID == owner.ID);
                }
                catch { return false; }
            }

            private static int CellArea(Item it)
            {
                try { var s = it.CalculateCellSize(); return s.X * s.Y; }
                catch { return 1; }
            }

            private void Done(string why)
            {
                Plugin.Log.LogInfo($"[GearTax] {why}");
                Destroy(gameObject);
            }
        }
    }
}
