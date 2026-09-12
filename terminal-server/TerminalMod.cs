using SPTarkov.Server.Core.Models.Spt.Tables;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;
using SysPath = System.IO.Path;

namespace Manimal.Terminal.Server;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = BuildInfo.ModGuid;
    public string Name { get; init; } = BuildInfo.Name;
    public string Author { get; init; } = BuildInfo.Author;
    public List<string>? Contributors { get; init; }
    // Generated from the same properties as the client and project version.
    public SemanticVersioning.Version Version { get; init; } = new(BuildInfo.Version);
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    // the map's bosses/items come from contentbackport + blackdiv — declared so a
    // missing install fails with the server's own dependency error instead of a
    // crash on unresolvable loot tpls / boss roles at raid start. guids + versions
    // read off the installed server dlls 2026-08-09, not guessed.
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        { "com.wtt.commonlib", new SemanticVersioning.Range("^3.0.6") },
        { "com.wtt.contentbackport", new SemanticVersioning.Range("^2.0.1") },
        { "com.blackdiv.tacticaltoaster", new SemanticVersioning.Range(">=0.0.1") },
        { "com.morebotsapi.tacticaltoaster", new SemanticVersioning.Range(">=2.1.1") },
        // RUAF Come Home carries the vsRF replacements (ruafRifleman/ruafMarksman in
        // our BossLocationSpawn) — guid read off the repo's Server/Mod.cs, v1.1.2
        { "com.ruafcomehome.tacticaltoaster", new SemanticVersioning.Range(">=1.1.0") },
    };
    public string? Url { get; init; } = BuildInfo.SourceUrl;
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}

// binds the backported Terminal map into SPT's native Terminal location slot.
// unlike icebreaker (which hijacks Suburbs + locale-rebrands), Terminal is a
// first-class dormant stub on the Locations record with its own id — every native
// lookup already resolves, and shoreline's vanilla transit SHO_TRANSIT_25 already
// targets it. we only have to supply Base + loot data.
[Injectable(TypePriority = OnLoadOrder.Preload + 90000)]
public class TerminalMod(
    LocationTable locationTable,
    TemplateTable templateTable,
    LocaleTable localeTable,
    LocationConfig locationConfig,
    InventoryConfig inventoryConfig,
    ICloner cloner,
    JsonUtil jsonUtil,
    ImageRouter imageRouter,
    ISptLogger<TerminalMod> logger)
    : IOnLoad
{
    private const string TerminalBannerId = "6901f52709598eae190be134";
    private const string TicketUnlockedCaseTpl = "68fa8e253666e2fd5b00a626";
    private const string TicketLockedCaseTpl = "67bde59edb4b617a6c0d69f8";
    private const string TerminalEntryNoteTpl = "68f213f8ea61d1803707cf7a";
    private const string TicketRfidKeycardTpl = "67bdea8667098e658f064695";
    private const string TerraGroupLabsKeycardTpl = "5c94bbff86f7747ee735c08f";
    private const string EurosTpl = "569668774bdc2da2298b4568";
    private const string RandomLootContainerTpl = "62f10b79e7ee985f386b2f47";
    private static RewardDetails? ticketCaseRewardDetails;
    private static bool ticketCaseLootPatched;

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

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modDir = SysPath.GetDirectoryName(typeof(TerminalMod).Assembly.Location)!;
        var basePath = SysPath.Combine(modDir, "db", "base.json");
        var newBase = await jsonUtil.DeserializeFromFileAsync<LocationBase>(basePath, cancellationToken);
        if (newBase is null)
        {
            // expected state until phase 6 authors db/base.json — server stays healthy
            logger.Warning($"[Terminal] no {basePath} yet — Terminal slot left dormant");
            return;
        }

        var terminal = locationTable.Terminal;
        if (terminal is null)
        {
            logger.Error("[Terminal] Terminal location slot missing from database — aborting");
            return;
        }

        ConfigureTicketArmoredCase();
        ConfigureTerminalEntryNoteSpecialSlot();

        var containerTemplates = await jsonUtil.DeserializeFromFileAsync<Dictionary<MongoId, TemplateItem>>(
            SysPath.Combine(modDir, "db", "containerTemplates.json"), cancellationToken)
            ?? throw new InvalidDataException("Terminal containerTemplates.json is missing or invalid");
        TerminalContainerCatalog.Register(templateTable.Items, containerTemplates);
        var containerLocales = await jsonUtil.DeserializeFromFileAsync<Dictionary<string, string>>(
            SysPath.Combine(modDir, "db", "containerLocales.json"), cancellationToken)
            ?? throw new InvalidDataException("Terminal containerLocales.json is missing or invalid");
        foreach (var globalLocale in localeTable.Global.Values)
            globalLocale.AddTransformer(locale =>
            {
                if (locale is not null)
                    foreach (var entry in containerLocales) locale[entry.Key] = entry.Value;
                return locale;
            });
        logger.Info($"[Terminal] {containerTemplates.Count} original container templates registered with authored grids and search sounds");

        terminal.Base = newBase;
        // scavs never cross: same lever labs uses; map screen greys it natively
        newBase.DisabledForScav = true;

        // MAP CARD + LOADING BANNER COPY. The map-selection panel localizes the
        // location's Mongo id, while the loading screen localizes the banner id.
        // Retail uses the same Terminal blurb for both.
        try
        {
            foreach (var kv in localeTable.Global)
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
        var factory = locationTable.Factory4Day;
        var labs = locationTable.Laboratory;
        terminal.StaticAmmo = await jsonUtil.DeserializeFromFileAsync<Dictionary<string, IEnumerable<StaticAmmoDetails>>>(
            SysPath.Combine(modDir, "db", "staticAmmo.json"), cancellationToken)
            ?? throw new InvalidDataException("Terminal staticAmmo.json is missing or invalid");
        logger.Info($"[Terminal] Terminal ammunition distributions loaded ({terminal.StaticAmmo.Count} calibers)");
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
            var itemsDb = templateTable.Items;
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
                foreach (var kv in localeTable.Global)
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
            try { ourStaticLoot = await jsonUtil.DeserializeFromFileAsync<Dictionary<MongoId, StaticLootDetails>>(staticLootPath, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { logger.Warning($"[Terminal] db/staticLoot.json unreadable — falling back: {e.Message}"); }
        }
        if (ourStaticLoot is not null)
        {
            terminal.StaticLoot = new LazyLoad<Dictionary<MongoId, StaticLootDetails>>(() => ourStaticLoot, cacheValue: false);
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
                looseJson = await System.IO.File.ReadAllTextAsync(loosePath, cancellationToken);
                if (jsonUtil.Deserialize<LooseLoot>(looseJson) is null) looseJson = null;
            }
            catch (OperationCanceledException) { throw; }
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
            terminal.LooseLoot = new LazyLoad<LooseLoot>(() => RandomiseLooseLoot(jsonUtil.Deserialize<LooseLoot>(json))!, cacheValue: false);
            logger.Info("[Terminal] authored loose loot loaded (per-raid group positions + forced-spawn rolls)");
        }
        else
        {
            terminal.LooseLoot = new LazyLoad<LooseLoot>(() => new LooseLoot
            {
                SpawnpointCount = new SpawnpointCount { Mean = 0, Std = 0 },
                Spawnpoints = [],
                SpawnpointsForced = [],
            }, cacheValue: false);
        }

        // staticContainers.json must come from the BUILT bundle (ids regenerate every
        // SDK rebake — playbook phase 6.4)
        var containersPath = SysPath.Combine(modDir, "db", "staticContainers.json");
        StaticContainerDetails? ourContainers = null;
        if (System.IO.File.Exists(containersPath))
        {
            try { ourContainers = await jsonUtil.DeserializeFromFileAsync<StaticContainerDetails>(containersPath, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { logger.Warning($"[Terminal] db/staticContainers.json unreadable: {e.Message}"); }
        }
        if (ourContainers is not null)
        {
            if (ourStaticLoot is not null)
                TerminalContainerCatalog.ValidateReferences(templateTable.Items,
                    ourStaticLoot, terminal.StaticAmmo, ourContainers);
            terminal.StaticContainers = new LazyLoad<StaticContainerDetails>(() => ourContainers, cacheValue: false);
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
        if (locationConfig.ScavRaidTimeSettings.Maps.TryGetValue("factory4_day", out var factorySettings))
        {
            locationConfig.ScavRaidTimeSettings.Maps["terminal"] = cloner.Clone(factorySettings);
            locationConfig.ScavRaidTimeSettings.Maps["Terminal"] = cloner.Clone(factorySettings);
        }

        logger.Success("[Manimal-Terminal] Terminal slot bound — native id, no rebrand needed");
    }

    // Content Backport marks the letter as a special-slot item but does not put
    // its template into the actual special-pocket filters. Add it directly after
    // all item registration has run, mirroring CommonLib's SpecialSlotsHelper.
    private void ConfigureTerminalEntryNoteSpecialSlot()
    {
        try
        {
            var items = templateTable.Items;
            if (!items.TryGetValue(new MongoId(TerminalEntryNoteTpl), out var letter)
                || letter.Properties is null)
            {
                logger.Warning("[Terminal] Prapor checkpoint letter template missing — special-slot compatibility skipped");
                return;
            }

            // The letter belongs exclusively in a Special Slot. Content Backport
            // already sets this correctly; its missing slot filters were the bug.
            letter.Properties.IsSpecialSlotOnly = true;

            var patchedSlots = 0;
            foreach (var pocketsId in new[]
                     {
                         "627a4e6b255f7527fb05a0f6", // standard pockets
                         "65e080be269cbd5c5005e529", // Unheard Edition pockets
                     })
            {
                if (!items.TryGetValue(new MongoId(pocketsId), out var pockets)
                    || pockets.Properties?.Slots is null)
                    continue;

                foreach (var slot in pockets.Properties.Slots)
                {
                    var filter = slot.Properties?.Filters?.FirstOrDefault()?.Filter;
                    if (filter is not null && filter.Add(TerminalEntryNoteTpl))
                        patchedSlots++;
                }
            }

            logger.Info($"[Terminal] Prapor checkpoint letter enabled for Special Slots ({patchedSlots} filter(s) updated)");
        }
        catch (Exception e)
        {
            logger.Warning($"[Terminal] Prapor checkpoint letter special-slot patch failed: {e.Message}");
        }
    }

    // Content Backport supplies the retail model and locale for the opened case, but
    // currently clones it from an SAS drive. That makes the item inert in 4.0: no
    // right-click Unpack action and therefore no destroyed-after-unpacking behavior.
    // Re-parent a clone of SPT's native random-loot box to the retail case tpl while
    // preserving the case's appearance/size. The Workbench produces ONLY this case.
    // Every reward is then generated by its native Unpack action: fixed story loot
    // plus two of the retail valuables, before the case is consumed.
    private void ConfigureTicketArmoredCase()
    {
        try
        {
            var items = templateTable.Items;
            var caseId = new MongoId(TicketUnlockedCaseTpl);
            var randomContainerId = new MongoId(RandomLootContainerTpl);
            if (!items.TryGetValue(caseId, out var caseTemplate)
                || !items.TryGetValue(randomContainerId, out var randomContainer)
                || caseTemplate.Properties is null
                || randomContainer.Properties is null)
            {
                logger.Warning("[Terminal] Ticket case unpack setup skipped: Content Backport case or SPT random-container template is missing");
                return;
            }

            var caseProps = caseTemplate.Properties;
            var unpackable = cloner.Clone(randomContainer);
            unpackable.Id = caseId;
            unpackable.Name = caseTemplate.Name;
            unpackable.Type = caseTemplate.Type;

            var props = unpackable.Properties!;
            props.Name = caseProps.Name;
            props.ShortName = caseProps.ShortName;
            props.Description = caseProps.Description;
            props.Prefab = cloner.Clone(caseProps.Prefab);
            props.BackgroundColor = caseProps.BackgroundColor;
            props.Width = caseProps.Width;
            props.Height = caseProps.Height;
            props.Weight = caseProps.Weight;
            props.ItemSound = caseProps.ItemSound;
            props.ExaminedByDefault = caseProps.ExaminedByDefault;
            props.ExamineTime = caseProps.ExamineTime;
            props.ExamineExperience = caseProps.ExamineExperience;
            props.LootExperience = caseProps.LootExperience;
            props.StackMaxSize = 1;
            props.StackObjectsCount = 1;
            props.HideEntrails = true;
            props.QuestItem = false;
            props.IsSpecialSlotOnly = caseProps.IsSpecialSlotOnly;
            props.IsUndiscardable = caseProps.IsUndiscardable;
            props.IsUngivable = caseProps.IsUngivable;
            props.IsUnsaleable = caseProps.IsUnsaleable;
            props.IsUnbuyable = caseProps.IsUnbuyable;
            props.Unlootable = caseProps.Unlootable;
            props.UnlootableFromSide = caseProps.UnlootableFromSide;
            props.UnlootableFromSlot = caseProps.UnlootableFromSlot;
            items[caseId] = unpackable;

            ticketCaseRewardDetails = new RewardDetails
            {
                Type = "Terminal unlocked armored case",
                RewardCount = 2,
                FoundInRaid = true,
                RewardTplPool = new Dictionary<MongoId, double>
                {
                    [new MongoId("62a09cfe4f842e1bd12da3e4")] = 1, // Golden egg
                    [new MongoId("5734758f24597738025ee253")] = 1, // Golden neck chain
                    [new MongoId("5d235a5986f77443f6329bc6")] = 1, // Gold skull ring
                    [new MongoId("59faf7ca86f7740dbe19f6c2")] = 1, // Roler
                },
                RewardTypePool = null,
            };
            inventoryConfig.RandomLootContainers[caseId] = ticketCaseRewardDetails;

            if (!ticketCaseLootPatched)
            {
                var target = AccessTools.Method(typeof(LootGenerator), nameof(LootGenerator.GetRandomLootContainerLoot));
                if (target is null)
                    logger.Warning("[Terminal] Ticket case fixed-loot patch unavailable — the case cannot unpack its story contents");
                else
                {
                    // Use the same Prefix/ref-result pattern as Mitsuru's working
                    // Synth Case patch. The previous postfix failed Harmony's IL
                    // compilation in installs where another random-container patch
                    // was present, leaving the Armored Case with vanilla-only loot.
                    new Harmony("com.manimal.terminal.ticketcase").Patch(
                        target,
                        prefix: new HarmonyMethod(typeof(TerminalMod), nameof(CreateTicketCaseContents)));
                    ticketCaseLootPatched = true;
                }
            }

            logger.Info("[Terminal] unlocked armored case registered as an unpackable container (fixed Ticket loot + two random valuables)");
        }
        catch (Exception e)
        {
            logger.Warning($"[Terminal] Ticket case unpack setup failed: {e.Message}");
        }
    }

    // The standard random-container config cannot express guaranteed companion
    // items or a currency stack. Return this case's complete contents ourselves;
    // every other vanilla/mod container continues into its normal generator.
    private static bool CreateTicketCaseContents(RewardDetails rewardContainerDetails, ref List<List<Item>> __result)
    {
        if (!ReferenceEquals(rewardContainerDetails, ticketCaseRewardDetails))
            return true;

        var contents = new List<List<Item>>
        {
            new() { CreateTicketCaseReward(TicketRfidKeycardTpl) },
            new() { CreateTicketCaseReward(TerraGroupLabsKeycardTpl) },
            new() { CreateTicketCaseReward(TerraGroupLabsKeycardTpl) },
            new() { CreateTicketCaseReward(EurosTpl, LootRng.Next(25_000, 100_001)) },
        };

        // Two different valuables, matching the retail case's "any two" reward.
        var valuables = ticketCaseRewardDetails!.RewardTplPool.Keys
            .OrderBy(_ => LootRng.Next())
            .Take(2);
        foreach (var valuable in valuables)
            contents.Add(new List<Item> { CreateTicketCaseReward(valuable.ToString()) });

        __result = contents;
        return false;
    }

    private static Item CreateTicketCaseReward(string templateId, int stackCount = 1) => new()
    {
        Id = new MongoId(),
        Template = new MongoId(templateId),
        Upd = new Upd { SpawnedInSession = true, StackObjectsCount = stackCount },
    };

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
