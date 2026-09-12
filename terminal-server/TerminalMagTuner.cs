using System.Linq;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Common.Models.Logging;

namespace Manimal.Terminal.Server;

// which map the active raid generates loot for — latched at GenerateLocationLoot,
// same trick as icebreaker's raid context (loot generates at raid start, and thats
// the only request that carries the location id)
internal static class TerminalRaidContext
{
    internal static bool OnTerminal;
}

// LOOSE MAGAZINES ARRIVE LOADED — the live-1.0 dumps author floor mags with random
// cartridge amounts, but SPT's CreateDynamicLootItem has a special MAGAZINE branch
// that discards authored cartridges and rerolls from staticMagazineLootHasAmmoChancePercent,
// which ships 0 — every loose mag on every map spawns empty (weapons keep their
// children via the generic branch, which is why rifles come up loaded). terminal-only
// heal: after SPT builds the bare mag, re-attach the authored cartridges from the
// spawn template. harmony on the BASE generator instead of a DI override on purpose —
// icebreaker's loot firewall owns the generator slot when both maps are installed,
// and two subclasses would fight it for last-wins.
//
// the guard is shape-based, no baseclass lookups needed: SPT returned exactly one
// item AND the template authored cartridges children for the chosen root. weapons
// (full tree), ammo boxes (SPT adds stacks), money/ammo (no cartridges children)
// all fall through untouched.
[Injectable(TypePriority = OnLoadOrder.Preload + 90001)]
public class TerminalMagTuner(ISptLogger<TerminalMagTuner> logger) : IOnLoad
{
    private static bool _patched;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_patched) return Task.CompletedTask;
        _patched = true;
        var h = new Harmony("com.manimal.terminal.magtuner");
        h.Patch(AccessTools.Method(typeof(LocationLootGenerator), nameof(LocationLootGenerator.GenerateLocationLoot)),
            prefix: new HarmonyMethod(typeof(TerminalMagTuner), nameof(LatchPrefix)));
        h.Patch(AccessTools.Method(typeof(LocationLootGenerator), "CreateDynamicLootItem"),
            postfix: new HarmonyMethod(typeof(TerminalMagTuner), nameof(MagPostfix)));
        h.Patch(AccessTools.Method(typeof(LocationLootGenerator), "AddLootToContainer"),
            postfix: new HarmonyMethod(typeof(TerminalMagTuner), nameof(CorpsePostfix)));
        logger.Info("[Terminal] loot tuner armed — loose magazines keep their authored live-dump ammo, RUAF corpse carries the armory key");
        return Task.CompletedTask;
    }

    // scripted containers with EXACT contents (user calls 2026-08-10): the dead RUAF
    // carries only the armory key; the design-stuff suitcase carries the bd keycard +
    // the MSGL that spawns there in retail. pool+staticForced cant express exact
    // contents: the generator regenerates the root id (pre-authored children orphan)
    // and a 0-count roll returns BEFORE forced items are added. so rebuild these
    // containers' contents outright after generation. grid: MSGL is 4x1, keycard 1x1
    // — both fit the weapon box 5x2 on row 0.
    private sealed record ScriptedItem(string Tpl, int X, int Y, (string tpl, string slot)[]? Mods = null);

    // MSGL ships ASSEMBLED (2026-08-10: the bare tpl rendered as a broken red pile) —
    // mods copied from the game's MGL_default preset (globals 629744d002667c48a467e9f9)
    private static readonly System.Collections.Generic.Dictionary<string, ScriptedItem[]> ScriptedContainers = new()
    {
        ["container_Terminal_Area_03_Substation_Courtyard_00000"] = new[]
        {
            new ScriptedItem("68a4b745b63e97cc5a056a07", 0, 0),  // armory key
        },
        ["container_Terminal_Design_Stuff_00186"] = new[]
        {
            new ScriptedItem("6275303a9f372d6ea97f9ec7", 0, 0, new[]
            {
                ("571659bb2459771fb2755a12", "mod_pistol_grip"),
                ("55d4ae6c4bdc2d8b2f8b456e", "mod_stock"),
                ("627bce33f21bc425b06ab967", "mod_magazine"),
                ("6284bd5f95250a29bc628a30", "mod_scope"),
            }),
            new ScriptedItem("6866ad3853330f9b83064cf9", 4, 0),  // bd keycard
        },
    };

    private static void CorpsePostfix(StaticContainerData __result)
    {
        if (!TerminalRaidContext.OnTerminal) return;
        var id = __result?.Template?.Id;
        if (id is null || __result.Template.Items is null) return;
        if (!ScriptedContainers.TryGetValue(id, out var contents)) return;
        var root = __result.Template.Items.FirstOrDefault();
        if (root is null) return;
        var items = new System.Collections.Generic.List<SptLootItem> { root };
        foreach (var spec in contents)
        {
            var it = new Item
            {
                Id = new MongoId(),
                Template = spec.Tpl,
                ParentId = root.Id,
                SlotId = "main",
                Location = new SPTarkov.Server.Core.Models.Eft.Common.Tables.ItemLocation
                {
                    X = spec.X, Y = spec.Y,
                    R = SPTarkov.Server.Core.Models.Eft.Common.Tables.ItemRotation.Horizontal,
                },
            };
            items.Add(it.ToLootItem());
            foreach (var (mtpl, slot) in spec.Mods ?? System.Array.Empty<(string, string)>())
                items.Add(new Item
                {
                    Id = new MongoId(),
                    Template = mtpl,
                    ParentId = it.Id,
                    SlotId = slot,
                }.ToLootItem());
        }
        __result.Template.Items = items;
    }

    private static void LatchPrefix(string locationId)
    {
        TerminalRaidContext.OnTerminal = string.Equals(locationId, "terminal", System.StringComparison.OrdinalIgnoreCase);
    }

    private static void MagPostfix(SptLootItem chosenItem, System.Collections.Generic.IEnumerable<SptLootItem> lootItems, ContainerItem __result)
    {
        if (!TerminalRaidContext.OnTerminal || __result?.Items is null) return;
        var items = __result.Items.ToList();
        if (items.Count != 1) return;
        var root = items[0];
        var authored = lootItems.Where(i => i.ParentId == chosenItem.Id && i.SlotId == "cartridges").ToList();
        if (authored.Count == 0) return;
        foreach (var c in authored)
        {
            items.Add(new Item
            {
                Id = new MongoId(),
                Template = c.Template,
                ParentId = root.Id,
                SlotId = "cartridges",
                Location = c.Location,
                Upd = new Upd { StackObjectsCount = c.Upd?.StackObjectsCount ?? 1 },
            });
        }
        __result.Items = items;
    }
}
