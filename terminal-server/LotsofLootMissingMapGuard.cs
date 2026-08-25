using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace Manimal.Terminal.Server;

// Generic safety net for a server-side 500 on /client/match/local/start, first seen
// on Terminal in the 2026-08-20 server log:
//
//   The given key 'terminal' was not present in the dictionary.
//      at LotsofLoot.Generators.LotsofLootLocationLootGenerator.GenerateDynamicLoot(...)
//      at LotsofLoot.Overrides.Generators.GenerateDynamicLootOverride.Prefix(...)
//
// Root cause (confirmed by disassembling LotsofLoot.dll's IL directly — no .NET SDK
// available in the sandbox that wrote this, so dnfile/dncil in Python were used to
// read the PE/CIL metadata and resolve the exact crashing line): the third-party mod
// LotsofLoot overrides SPT core's LocationLootGenerator.GenerateDynamicLoot and keeps
// its own per-map config (Limits, LooseLootMultiplier, StaticLootMultiplier) keyed by
// location name in config.jsonc. Any custom map with no entry there hits an unguarded
// Dictionary<string,int> indexer lookup that throws a KeyNotFoundException, failing
// the whole raid-start request.
//
// The PRIMARY fix is still adding the map's entry directly to LotsofLoot's own
// config.jsonc (outside this repo) — that gets proper LotsofLoot-generated dynamic
// loot instead of none, and is already done for "terminal" and "suburbs" (Icebreaker).
// This guard is a SECONDARY, TERMINAL-ONLY safety net. If the config entry is missing
// (or lost in a LotsofLoot update), Terminal still starts, but with no dynamic loose
// loot until the config is fixed. Static containers remain available. Restricting the
// finalizer to Terminal is important: a KeyNotFoundException on another map may have a
// completely different cause and must not be hidden by this mod.
//
// Only Terminal's KeyNotFoundException is swallowed — every other map and exception
// type rethrows unchanged, so this doesn't mask unrelated loot-generation bugs.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 90003)]
public class LotsofLootMissingMapGuard(ISptLogger<LotsofLootMissingMapGuard> logger) : IOnLoad
{
    private static ISptLogger<LotsofLootMissingMapGuard>? _log;
    private static bool _patched;

    public Task OnLoad()
    {
        _log = logger;
        if (_patched) return Task.CompletedTask;
        _patched = true;

        var method = AccessTools.Method(
            typeof(LocationLootGenerator),
            nameof(LocationLootGenerator.GenerateDynamicLoot),
            new[] { typeof(LooseLoot), typeof(Dictionary<string, IEnumerable<StaticAmmoDetails>>), typeof(string) });
        if (method is null)
        {
            logger.Error(
                "[LotsofLootMissingMapGuard] could not resolve LocationLootGenerator." +
                "GenerateDynamicLoot (SPT core signature changed?) — custom maps are " +
                "UNGUARDED against missing-map dynamic-loot crashes.");
            return Task.CompletedTask;
        }

        var harmony = new Harmony("com.manimal.terminal.lotsoflootmissingmapguard");
        harmony.Patch(method, finalizer: new HarmonyMethod(typeof(LotsofLootMissingMapGuard), nameof(Finalizer)));
        logger.Info("[LotsofLootMissingMapGuard] guarding Terminal dynamic-loot generation " +
                    "against a missing map entry in loot-override mods.");
        return Task.CompletedTask;
    }

    private static Exception? Finalizer(
        Exception? __exception, string locationName, ref List<SpawnpointTemplate> __result)
    {
        if (__exception is null) return null;

        // only swallow the specific "map not configured" failure mode; anything else
        // (a real bug, a different crash) rethrows so it isn't masked.
        if (__exception is not KeyNotFoundException
            || !string.Equals(locationName, "terminal", StringComparison.OrdinalIgnoreCase))
            return __exception;

        __result = new List<SpawnpointTemplate>();
        _log?.Warning(
            $"[LotsofLootMissingMapGuard] dynamic loot generation threw KeyNotFoundException for " +
            $"location \"{locationName}\" — returning an empty dynamic-loot list instead of failing " +
            $"raid start. Root cause: {__exception.Message}. Add a \"{locationName.ToLowerInvariant()}\" " +
            "entry to the loot-override mod's config.jsonc (LooseLootMultiplier/StaticLootMultiplier/" +
            "Limits) for real LotsofLoot-generated loot on this map instead of this fallback.");
        return null;
    }
}
