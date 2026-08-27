using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;
using SysPath = System.IO.Path;

namespace Manimal.Terminal.Server;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.manimal.terminal";
    public override string Name { get; init; } = "ManimalTerminal";
    public override string Author { get; init; } = "Manimal";
    public override List<string>? Contributors { get; init; }
    // read from the assembly rather than repeated as a literal. forge requires every
    // version a mod declares to match exactly, and the csproj already feeds ModVersion
    // (Directory.Build.props) into <Version> — so this stays correct across a bump
    // instead of silently drifting from the client's BuildInfo.Version.
    public override SemanticVersioning.Version Version { get; init; } =
        new(typeof(ModMetadata).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.1.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0");
    public override List<string>? Incompatibilities { get; init; }
    // the map's bosses/items come from contentbackport + blackdiv — declared so a
    // missing install fails with the server's own dependency error instead of a
    // crash on unresolvable loot tpls / boss roles at raid start. guids + versions
    // read off the installed server dlls 2026-08-09, not guessed.
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        { "com.wtt.commonlib", new SemanticVersioning.Range("~2.0.23") },
        { "com.wtt.contentbackport", new SemanticVersioning.Range("~1.1.3") },
        { "com.blackdiv.tacticaltoaster", new SemanticVersioning.Range(">=0.0.1") },
        { "com.morebotsapi.tacticaltoaster", new SemanticVersioning.Range(">=0.0.1") },
        // RUAF Come Home carries the vsRF replacements (ruafRifleman/ruafMarksman in
        // our BossLocationSpawn) — guid read off the repo's Server/Mod.cs, v1.1.2
        { "com.ruafcomehome.tacticaltoaster", new SemanticVersioning.Range(">=1.1.0") },
    };
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; } = true; // will ship the scene + preset bundles
    public override string License { get; init; } = "MIT";
}

// binds the backported Terminal map into SPT's native Terminal location slot.
// unlike icebreaker (which hijacks Suburbs + locale-rebrands), Terminal is a
// first-class dormant stub on the Locations record with its own id — every native
// lookup already resolves, and shoreline's vanilla transit SHO_TRANSIT_25 already
// targets it. we only have to supply Base + loot data.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 90000)]
public class TerminalMod(
    DatabaseService databaseService,
    ConfigServer configServer,
    ICloner cloner,
    JsonUtil jsonUtil,
    ImageRouter imageRouter,
    ISptLogger<TerminalMod> logger)
    : IOnLoad
{
    private const string TerminalBannerId = "6901f52709598eae190be134";

    private const string TerminalBlurb =
        "The southern port Terminal has been under Russian Armed Forces control since the beginning of the Tarkov conflict. "
        + "After the city was sealed off, the terminal was fortified and became the primary evacuation point, sought by both "
        + "civilian refugees and PMC units. According to unconfirmed reports, TerraGroup personnel were also evacuated through this port.";

    // Retail has used several ids for Terminal across the location card, loading
    // banner and related data. Supplying the same copy for every known id keeps the
    // description intact across client builds instead of exposing a raw locale key.
    private static readonly string[] TerminalLocaleIds =
    {
        "5704e5a4d2720bb45b8b4567",
        "65cc8f81a9aac3e77d0cfd3e",
        "6925a2c38bdebd9e2302692e",
        TerminalBannerId,
    };

    public async Task OnLoad()
    {
        var modDir = SysPath.GetDirectoryName(typeof(TerminalMod).Assembly.Location)!;
        var basePath = SysPath.Combine(modDir, "db", "base.json");
        var newBase = await jsonUtil.DeserializeFromFileAsync<LocationBase>(basePath);
        if (newBase is null)
        {
            // expected state until phase 6 authors db/base.json — server stays healthy
            logger.Warning($"[Terminal] no {basePath} yet — Terminal slot left dormant");
            return;
        }

        var terminal = databaseService.GetLocations().Terminal;
        if (terminal is null)
        {
            logger.Error("[Terminal] Terminal location slot missing from database — aborting");
            return;
        }

        terminal.Base = newBase;
        // scavs never cross: same lever labs uses; map screen greys it natively
        newBase.DisabledForScav = true;

        // MAP CARD + LOADING BANNER COPY. The map-selection panel localizes the
        // location's Mongo id, while the loading screen localizes the banner id.
        // Retail uses the same Terminal blurb for both.
        try
        {
            foreach (var kv in databaseService.GetLocales().Global)
                kv.Value.AddTransformer(locale =>
                {
                    foreach (var id in TerminalLocaleIds)
                    {
                        locale[$"{id} Name"] = "Terminal";
                        locale[$"{id} Description"] = TerminalBlurb;
                    }
                    return locale;
                });

            logger.Info("[Terminal] map card and loading-banner locale restored");
        }
        catch (Exception e)
        {
            logger.Warning($"[Terminal] map/banner locale setup failed: {e.Message}");
        }

        // The Sherpa and Emissary cards in the retail three-card rotation are stock
        // SPT banners and already have image routes. Only Terminal's cover is owned
        // by this mod and needs an explicit /files/banners route.
        try
        {
            const string bannerFile = "6901f4be499c695f6e03247c.png";
            var bannerPath = SysPath.Combine(modDir, "db", "banners", bannerFile);
            if (System.IO.File.Exists(bannerPath))
            {
                imageRouter.AddRoute($"/files/banners/{SysPath.GetFileNameWithoutExtension(bannerFile)}", bannerPath);
                logger.Info("[Terminal] custom loading-screen banner wired (plus stock Sherpa/Emissary rotation)");
            }
            else
            {
                logger.Warning($"[Terminal] custom loading-screen banner missing: {bannerPath}");
            }
        }
        catch (Exception e)
        {
            logger.Warning($"[Terminal] loading-screen banner setup failed: {e.Message}");
        }

        // the dormant stub ships base.json ONLY — no loot files — so raid-start loot
        // generation NREs on null LooseLoot/StaticLoot/StaticContainers. same guarded
        // loader chain as icebreaker: authored db files win, coherent fallback pair
        // otherwise (never mix fallback containers with our pools — KeyNotFound at
        // raid start).
        var factory = databaseService.GetLocations().Factory4Day;
        var labs = databaseService.GetLocations().Laboratory;
        terminal.StaticAmmo = labs.StaticAmmo;
        terminal.AllExtracts = []; // scav extract list — v1 is PMC-only

        // EQUIPMENT CABINET — our own container tpl for the gunsafe valberg doors
        // (user call 2026-08-10: no vanilla twin, build one). clone of the airdrop
        // common supply crate (already 10 wide) re-gridded to 10x20; the client learns
        // the tpl automatically because SPT serves the item db to it, and the scene
        // visual is the safe mesh — no client asset needed for a container.
        // staticLoot.json carries a pool under this tpl; gen_terminal_static_containers
        // + the client remap table reference it — keep all three in sync.
        const string cabinetTpl = "68a4c0ffee0000000000cab1";
        try
        {
            var itemsDb = databaseService.GetTemplates().Items;
            if (itemsDb is not null && itemsDb.TryGetValue(new MongoId("6223349b3136504a544d1608"), out var crate))
            {
                var cab = cloner.Clone(crate);
                cab.Id = new MongoId(cabinetTpl);
                cab.Name = "container_equipment_cabinet";
                var grid = cab.Properties?.Grids?.FirstOrDefault();
                if (grid is not null)
                {
                    grid.Id = "68a4c0ffee0000000000cab2";
                    grid.Parent = cabinetTpl;
                    if (grid.Properties is not null)
                    {
                        grid.Properties.CellsH = 10;
                        grid.Properties.CellsV = 20;
                    }
                }
                itemsDb[cab.Id] = cab;
                foreach (var kv in databaseService.GetLocales().Global)
                    kv.Value.AddTransformer(locale =>
                    {
                        locale[$"{cabinetTpl} Name"] = "Equipment Cabinet";
                        locale[$"{cabinetTpl} ShortName"] = "EqCabinet";
                        locale[$"{cabinetTpl} Description"] = "A tall port-authority equipment cabinet. Whatever the terminal crews locked away is still inside.";
                        return locale;
                    });
                logger.Info("[Terminal] Equipment Cabinet registered (10x20 container, clone of airdrop supply crate)");
            }
            else logger.Warning("[Terminal] airdrop crate template missing — equipment cabinet not registered");
        }
        catch (Exception e) { logger.Warning($"[Terminal] equipment cabinet failed: {e.Message}"); }

        // NOTE these properties are LazyLoad<T> — deserialize the INNER model and
        // wrap, or the load silently fails (playbook phase 6.3)
        var staticLootPath = SysPath.Combine(modDir, "db", "staticLoot.json");
        Dictionary<MongoId, StaticLootDetails>? ourStaticLoot = null;
        if (System.IO.File.Exists(staticLootPath))
        {
            try { ourStaticLoot = await jsonUtil.DeserializeFromFileAsync<Dictionary<MongoId, StaticLootDetails>>(staticLootPath); }
            catch (Exception e) { logger.Warning($"[Terminal] db/staticLoot.json unreadable — falling back: {e.Message}"); }
        }
        if (ourStaticLoot is not null)
        {
            terminal.StaticLoot = new LazyLoad<Dictionary<MongoId, StaticLootDetails>>(() => ourStaticLoot);
            logger.Info($"[Terminal] container loot pools loaded ({ourStaticLoot.Count} container types)");
        }
        else
        {
            terminal.StaticLoot = labs.StaticLoot;
        }

        // loose loot: retail positions are server-generated per raid — authored file
        // wins (marker workflow, gen_loose_loot.py), else empty set so raids run
        // clean on container loot only
        var loosePath = SysPath.Combine(modDir, "db", "looseLoot.json");
        string? looseJson = null;
        if (System.IO.File.Exists(loosePath))
        {
            try
            {
                looseJson = System.IO.File.ReadAllText(loosePath);
                if (jsonUtil.Deserialize<LooseLoot>(looseJson) is null) looseJson = null;
            }
            catch (Exception e)
            {
                looseJson = null;
                logger.Warning($"[Terminal] db/looseLoot.json unreadable — running loose-loot-free: {e.Message}");
            }
        }
        if (looseJson is not null)
        {
            // LazyLoad.Value re-invokes the factory EVERY access and SPT's generator
            // mutates spawnpoint templates — return a FRESH deserialization each
            // raid; the fresh copy is also where group-position picks + forced-
            // probability rolls happen (two generator gaps, verified on icebreaker)
            var json = looseJson;
            terminal.LooseLoot = new LazyLoad<LooseLoot>(() => RandomiseLooseLoot(jsonUtil.Deserialize<LooseLoot>(json)));
            logger.Info("[Terminal] authored loose loot loaded (per-raid group positions + forced-spawn rolls)");
        }
        else
        {
            terminal.LooseLoot = new LazyLoad<LooseLoot>(() => new LooseLoot
            {
                SpawnpointCount = new SpawnpointCount { Mean = 0, Std = 0 },
                Spawnpoints = [],
                SpawnpointsForced = [],
            });
        }

        // staticContainers.json must come from the BUILT bundle (ids regenerate every
        // SDK rebake — playbook phase 6.4)
        var containersPath = SysPath.Combine(modDir, "db", "staticContainers.json");
        StaticContainerDetails? ourContainers = null;
        if (System.IO.File.Exists(containersPath))
        {
            try { ourContainers = await jsonUtil.DeserializeFromFileAsync<StaticContainerDetails>(containersPath); }
            catch (Exception e) { logger.Warning($"[Terminal] db/staticContainers.json unreadable: {e.Message}"); }
        }
        if (ourContainers is not null)
        {
            terminal.StaticContainers = new LazyLoad<StaticContainerDetails>(() => ourContainers);
            logger.Info("[Terminal] container set loaded from bundle scan");
        }
        else
        {
            // coherent fallback PAIR — containers and pools from the same map
            terminal.StaticContainers = factory.StaticContainers;
            terminal.StaticLoot = factory.StaticLoot;
            logger.Warning($"[Terminal] {containersPath} missing — factory loot this run, container ids wont match the map");
        }

        // scav raid time settings keyed by map id — clone a real map's so lookups
        // resolve. key case unverified: icebreaker's slot id was lowercase "suburbs",
        // Terminal's record id is "Terminal" — set both, harmless if one is unused.
        var locationConfig = configServer.GetConfig<LocationConfig>();
        if (locationConfig.ScavRaidTimeSettings.Maps.TryGetValue("factory4_day", out var factorySettings))
        {
            locationConfig.ScavRaidTimeSettings.Maps["terminal"] = cloner.Clone(factorySettings);
            locationConfig.ScavRaidTimeSettings.Maps["Terminal"] = cloner.Clone(factorySettings);
        }

        logger.Success("[Manimal-Terminal] Terminal slot bound — native id, no rebrand needed");
    }

    private static readonly Random LootRng = new();

    // per-raid loose loot post-processing on the fresh LazyLoad copy:
    //  1. GROUPS — SPT's generator never reads GroupPositions; bake one pick per raid
    //  2. FORCED — forced points are added unconditionally; roll sub-100% here
    private static LooseLoot? RandomiseLooseLoot(LooseLoot? loose)
    {
        if (loose is null) return null;

        var all = (loose.Spawnpoints ?? []).Concat(loose.SpawnpointsForced ?? []);
        foreach (var sp in all)
        {
            var t = sp.Template;
            if (t?.IsGroupPosition != true) continue;
            var poses = t.GroupPositions?.ToList();
            if (poses is null || poses.Count == 0) continue;
            var pick = poses[LootRng.Next(poses.Count)];
            t.Position = pick.Position;
            t.Rotation = pick.Rotation;
            t.IsGroupPosition = false; // pose is baked now — nothing downstream needs the group
            t.GroupPositions = [];
        }

        loose.SpawnpointsForced = (loose.SpawnpointsForced ?? [])
            .Where(p => (p.Probability ?? 1) >= 1 || LootRng.NextDouble() < p.Probability!.Value)
            .ToList();

        return loose;
    }
}
