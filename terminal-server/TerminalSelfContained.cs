using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Cloners;

namespace Manimal.Terminal.Server;

// SELF-CONTAINED TERMINAL (user 2026-08-19): the raid never happened, gear-wise.
// a PMC raid on terminal snapshots the full inventory at StartLocalRaid; at
// EndLocalRaid — death, extract, MIA or transit alike — the entry snapshot is
// re-applied from a Harmony finalizer after every normal raid-end postfix, then
// the profile re-saves. loot found in-raid is discarded with everything else;
// gear lost comes back.
// inspired by ScrewTSW's EquipmentIsEternal (death-only), extended to every
// outcome and scoped to this map.
//
// insurance: the insured-loss pipeline is suppressed for terminal entirely
// (HandleInsuredItemLostEvent prefix) — nothing is lost, nothing returns, no
// dupes. XP/skills/quest counters from the raid are kept (only the inventory
// reverts). scav runs untouched (and the map is DisabledForScav anyway).
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 90002)]
public class TerminalSelfContained(
    ISptLogger<TerminalSelfContained> logger,
    ProfileHelper profileHelper,
    ICloner cloner,
    SaveServer saveServer) : IOnLoad
{
    // Prapor's terminal-soldiers letter is configured as Terminal's native
    // AccessKey. The game consumes it when the accepted PMC raid begins. It is
    // intentionally excluded from the self-contained snapshot, so the normal
    // end-of-raid restore cannot resurrect the spent entry pass.
    private const string TerminalEntryNoteTpl = "68f213f8ea61d1803707cf7a";

    // hoisted: primary-ctor captures aren't reachable from the static patch bodies
    private readonly ISptLogger<TerminalSelfContained> _log = logger;
    private readonly ProfileHelper _profiles = profileHelper;
    private readonly ICloner _cloner = cloner;
    private readonly SaveServer _saves = saveServer;

    private static bool _patched;
    private static TerminalSelfContained? _instance;

    private sealed class Snapshot
    {
        public required BotBaseInventory Inventory;
        public List<InsuredItem>? Insured;
    }

    private static readonly Dictionary<string, Snapshot> _snapshots = new();

    public Task OnLoad()
    {
        _instance = this;
        if (_patched) return Task.CompletedTask;
        _patched = true;
        var h = new Harmony("com.manimal.terminal.selfcontained");
        h.Patch(AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.StartLocalRaid)),
            prefix: new HarmonyMethod(typeof(TerminalSelfContained), nameof(StartPrefix)));
        var locationFinalizer = new HarmonyMethod(typeof(TerminalSelfContained), nameof(EndFinalizer))
        {
            priority = Priority.Last,
        };
        h.Patch(AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.EndLocalRaid)),
            finalizer: locationFinalizer);
        var controllerFallback = new HarmonyMethod(typeof(TerminalSelfContained), nameof(EndControllerPostfix))
        {
            priority = Priority.Last,
        };
        h.Patch(AccessTools.Method(typeof(MatchController), nameof(MatchController.EndLocalRaid)),
            postfix: controllerFallback);
        // nothing is ever lost on a self-contained map, so the insurance-lost
        // pipeline must not fire at all — it queues return mail for "lost" gear
        // BEFORE the snapshot restore runs, which would deliver as dupes later
        h.Patch(AccessTools.Method(typeof(LocationLifecycleService), "HandleInsuredItemLostEvent"),
            prefix: new HarmonyMethod(typeof(TerminalSelfContained), nameof(InsurancePrefix)));
        logger.Info("[Terminal] self-contained raids armed — gear snapshots on entry, full restore on any exit");
        return Task.CompletedTask;
    }

    public static void StartPrefix(MongoId sessionId, StartLocalRaidRequestData request)
    {
        try
        {
            var self = _instance;
            if (self is null) return;
            var key = sessionId.ToString();
            _snapshots.Remove(key); // stale snapshot from a crashed raid dies here

            if (!string.Equals(request.Location, "Terminal", StringComparison.OrdinalIgnoreCase)) return;
            var side = request.PlayerSide ?? "";
            if (!side.Contains("pmc", StringComparison.OrdinalIgnoreCase))
            {
                self._log.Info("[Terminal] scav raid — self-contained snapshot skipped");
                return;
            }

            var pmc = self._profiles.GetFullProfile(sessionId)?.CharacterData?.PmcData;
            if (pmc?.Inventory is null) return;
            var snapshotInventory = self._cloner.Clone(pmc.Inventory)!;
            RemoveTemplateAndChildren(snapshotInventory, TerminalEntryNoteTpl);
            _snapshots[key] = new Snapshot
            {
                Inventory = snapshotInventory,
                Insured = self._cloner.Clone(pmc.InsuredItems),
            };
            self._log.Info($"[Terminal] gear snapshot taken ({pmc.Inventory.Items?.Count ?? 0} inventory item(s)); entry note excluded — this raid never happened");
        }
        catch (Exception e)
        {
            _instance?._log.Warning($"[Terminal] gear snapshot failed: {e.Message}");
        }
    }

    private static void RemoveTemplateAndChildren(BotBaseInventory inventory, string templateId)
    {
        if (inventory.Items is null) return;

        var removedIds = inventory.Items
            .Where(item => string.Equals(item.Template, templateId, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Id)
            .ToHashSet();

        // Keep the inventory structurally valid if the access item ever gains
        // attachments/contents in a later retail data update.
        while (true)
        {
            var childIds = inventory.Items
                .Where(item => item.ParentId is not null && removedIds.Contains(item.ParentId))
                .Select(item => item.Id)
                .Where(id => !removedIds.Contains(id))
                .ToList();
            if (childIds.Count == 0) break;
            foreach (var childId in childIds) removedIds.Add(childId);
        }

        inventory.Items.RemoveAll(item => removedIds.Contains(item.Id));
    }

    public static bool InsurancePrefix(string locationName)
    {
        if (string.Equals(locationName, "Terminal", StringComparison.OrdinalIgnoreCase))
        {
            _instance?._log.Info("[Terminal] insured-loss event suppressed — nothing is lost on a self-contained raid");
            return false;
        }
        return true;
    }

    /// <summary>
    /// A finalizer runs after every normal LocationLifecycleService postfix. This
    /// matters when another server mod also rewrites the profile at raid end.
    /// </summary>
    public static Exception? EndFinalizer(
        MongoId sessionId,
        EndLocalRaidRequestData request,
        Exception? __exception)
    {
        if (__exception is null)
        {
            FinalizeRestore(sessionId, request, "location finalizer");
        }
        else
        {
            _instance?._log.Warning(
                $"[Terminal] normal raid-end processing threw before final restore; snapshot retained: {__exception.Message}");
        }

        return __exception;
    }

    /// <summary>
    /// Direct wrapper around LocationLifecycleService.EndLocalRaid. Normally the
    /// finalizer has already consumed the snapshot; this is a second boundary in
    /// case a runtime patch prevents that finalizer from executing.
    /// </summary>
    public static void EndControllerPostfix(MongoId sessionId, EndLocalRaidRequestData request)
    {
        FinalizeRestore(sessionId, request, "controller fallback");
    }

    private static void FinalizeRestore(MongoId sessionId, EndLocalRaidRequestData request, string boundary)
    {
        var self = _instance;
        if (self is null) return;
        var key = sessionId.ToString();
        if (!_snapshots.TryGetValue(key, out var snap)) return;

        try
        {
            var pmc = self._profiles.GetFullProfile(sessionId)?.CharacterData?.PmcData;
            if (pmc is null)
            {
                self._log.Warning("[Terminal] final restore skipped: authoritative PMC profile was unavailable");
                return;
            }

            var processedCount = pmc.Inventory?.Items?.Count ?? 0;
            pmc.Inventory = self._cloner.Clone(snap.Inventory)!;
            if (snap.Insured is not null)
            {
                pmc.InsuredItems = self._cloner.Clone(snap.Insured);
            }

            RestoreFullHealth(pmc);

            // LocationLifecycleService has already saved the post-raid state.
            // Save again while the snapshot remains active, and only retire it
            // after the authoritative profile has been serialized successfully.
            self._saves.SaveProfileAsync(sessionId).GetAwaiter().GetResult();
            var restoredCount = pmc.Inventory?.Items?.Count ?? 0;
            _snapshots.Remove(key);
            self._log.Info(
                $"[Terminal] raid over ({request.Results?.Result}) — final authoritative restore complete " +
                $"({processedCount} post-raid item(s) -> {restoredCount} snapshot item(s)); " +
                $"health, hydration and energy restored to full via {boundary}");
        }
        catch (Exception e)
        {
            // Keep the snapshot until the next raid start rather than discarding
            // the only recoverable copy after a failed save.
            self._log.Warning($"[Terminal] final profile restore failed (snapshot retained): {e}");
        }
    }

    private static void RestoreFullHealth(PmcData pmc)
    {
        var health = pmc.Health;
        if (health is null) return;

        if (health.BodyParts is not null)
        {
            foreach (var bodyPart in health.BodyParts.Values)
            {
                if (bodyPart.Health is not null)
                {
                    bodyPart.Health.Current = bodyPart.Health.Maximum;
                }

                // Full recovery also means no persistent fracture, bleed, pain,
                // contusion, toxin, or other limb effects from Terminal.
                bodyPart.Effects?.Clear();
            }
        }

        if (health.Hydration is not null)
        {
            health.Hydration.Current = health.Hydration.Maximum;
        }

        if (health.Energy is not null)
        {
            health.Energy.Current = health.Energy.Maximum;
        }
    }
}
