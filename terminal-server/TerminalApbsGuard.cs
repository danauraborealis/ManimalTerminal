using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Common.Models.Logging;

namespace Manimal.Terminal.Server;

// safety net for vanilla-role bots (assault, marksman, bossGluhar,
// followerGluharSecurity) failing to spawn on Terminal, seen in the 2026-08-20 server
// log:
//
//   Failed to generate bot #N (assault): Map 'Terminal' not found.
//
// Root cause (confirmed by cloning the third-party mod Acid's Progressive Bot System
// (APBS)'s own open-source repo, acidphantasm/progressivebotsystem-csharp): unlike
// LotsofLoot, APBS's Models/ApbsServerConfig.cs has a MapRangeWeights class with a
// string-keyed indexer backed by a FIXED C# switch statement over ~13 hardcoded map
// names (Bigmap, RezervBase, Woods, ...), with
// `_ => throw new KeyNotFoundException($"Map '{key}' not found.")` for anything else.
// This is read by CustomBotWeaponGenerator.cs as
// MapRangeWeighting[RaidInformation.RaidLocation] to weight long/short-range weapon
// selection per map. Unlike LotsofLoot, there is NO config file that can add Terminal
// support here — the map list is compiled into the DLL, so a code-level guard is the
// only option (no equivalent of the LotsofLoot config.jsonc fix exists for this one).
//
// This guards APBS's indexer with a Harmony finalizer that supplies a default
// LongShortRange (20% long-range / 80% short-range, matching Interchange/
// TarkovStreets — a reasonable fit for Terminal's mostly indoor/midrange layout with
// a few longer yard sightlines) for Terminal instead of letting the exception
// propagate. Every other map's lookup — known or unknown — is untouched. Custom bot
// roles (blackDivAssault, ruaf*, civilian) are unaffected — the same server log
// showed they generate fine on Terminal already; only vanilla roles go through this
// APBS code path.
//
// The APBS type/method is resolved via reflection (AccessTools), so this has no
// compile-time dependency on APBS and stays inert (logs a warning, does nothing) if
// APBS isn't installed or has changed its internals.
[Injectable(TypePriority = OnLoadOrder.Preload + 90004)]
public class TerminalApbsGuard(ISptLogger<TerminalApbsGuard> logger) : IOnLoad
{
    private const string TargetTypeName = "ProgressiveBotSystem.Models.MapRangeWeights";
    private const string FallbackTypeName = "ProgressiveBotSystem.Models.LongShortRange";
    private const string GuardedLocation = "Terminal";
    private const double FallbackLongRange = 20.0;
    private const double FallbackShortRange = 80.0;

    private static ISptLogger<TerminalApbsGuard>? _log;
    private static bool _patched;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _log = logger;
        if (_patched) return Task.CompletedTask;
        _patched = true;

        var type = AccessTools.TypeByName(TargetTypeName);
        if (type is null)
        {
            // expected if APBS isn't installed, or renamed the type in a newer
            // version — either way, log it and stay inert rather than fail silently.
            logger.Warning(
                $"[TerminalApbsGuard] could not resolve '{TargetTypeName}' — APBS not " +
                "installed, or its internals changed. Nothing to guard; if APBS IS " +
                "installed and vanilla-role bots (assault/marksman/bossGluhar) still " +
                "fail with \"Map 'Terminal' not found\", this guard needs updating.");
            return Task.CompletedTask;
        }

        var method = AccessTools.Method(type, "get_Item", new[] { typeof(string) });
        if (method is null)
        {
            logger.Error(
                $"[TerminalApbsGuard] resolved '{TargetTypeName}' but its indexer getter " +
                "wasn't found (APBS internals changed?) — Terminal is UNGUARDED against " +
                "this crash.");
            return Task.CompletedTask;
        }

        var fallbackType = AccessTools.TypeByName(FallbackTypeName);
        if (fallbackType is null)
        {
            logger.Error(
                $"[TerminalApbsGuard] could not resolve '{FallbackTypeName}' (APBS " +
                "internals changed?) — Terminal is UNGUARDED against this crash.");
            return Task.CompletedTask;
        }

        var harmony = new Harmony("com.manimal.terminal.apbsguard");
        harmony.Patch(method, finalizer: new HarmonyMethod(typeof(TerminalApbsGuard), nameof(Finalizer)));
        logger.Info($"[TerminalApbsGuard] guarding {TargetTypeName}[\"{GuardedLocation}\"]");
        return Task.CompletedTask;
    }

    // fires for every map lookup — only substitutes a fallback for exactly
    // "Terminal"; every other (known or unknown) key's exception propagates
    // unchanged, so this stays inert for anything it isn't specifically guarding.
    private static Exception? Finalizer(string key, ref object __result, Exception __exception)
    {
        if (__exception is null) return null;
        if (!string.Equals(key, GuardedLocation, StringComparison.OrdinalIgnoreCase)) return __exception;

        var fallbackType = AccessTools.TypeByName(FallbackTypeName);
        if (fallbackType is null) return __exception; // shouldn't happen — OnLoad already verified this

        var instance = Activator.CreateInstance(fallbackType);
        fallbackType.GetProperty("LongRange")?.SetValue(instance, FallbackLongRange);
        fallbackType.GetProperty("ShortRange")?.SetValue(instance, FallbackShortRange);
        __result = instance!;

        _log?.Warning(
            $"[TerminalApbsGuard] APBS has no native range-weighting for Terminal — " +
            $"substituted default ({FallbackLongRange}% long / {FallbackShortRange}% short), " +
            $"same split as Interchange/TarkovStreets. Root cause: {__exception.Message}");
        return null;
    }
}
