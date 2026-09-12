using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace Manimal.Terminal.Server;

internal static class TerminalContainerCatalog
{
    internal static void Register(Dictionary<MongoId, TemplateItem> items, Dictionary<MongoId, TemplateItem> templates)
    {
        foreach (var (id, template) in templates)
        {
            if (id != template.Id || !items.ContainsKey(template.Parent))
                throw new InvalidDataException($"Terminal container {id} has an invalid identity or missing parent");
            int grids = 0;
            foreach (var grid in template.Properties?.Grids ?? [])
            {
                if (!string.Equals(grid.Parent, id.ToString(), StringComparison.Ordinal)
                    || grid.Properties?.CellsH is not > 0 || grid.Properties.CellsV is not > 0)
                    throw new InvalidDataException($"Terminal container {id} has an invalid grid");
                grids++;
            }
            if (grids == 0) throw new InvalidDataException($"Terminal container {id} has no grid");
        }
        // Validate the complete set before modifying the shared item catalog.
        foreach (var (id, template) in templates) items[id] = template;
    }

    internal static void ValidateReferences(Dictionary<MongoId, TemplateItem> items,
        Dictionary<MongoId, StaticLootDetails> pools, Dictionary<string, IEnumerable<StaticAmmoDetails>> ammo,
        StaticContainerDetails containers)
    {
        var missing = new HashSet<string>();
        foreach (var (id, pool) in pools)
        {
            if (!items.ContainsKey(id)) missing.Add(id.ToString());
            foreach (var entry in pool.ItemDistribution)
                if (!items.ContainsKey(entry.Tpl)) missing.Add(entry.Tpl.ToString());
        }
        foreach (var entries in ammo.Values)
            foreach (var entry in entries)
                if (entry.Tpl is not { } id || !items.ContainsKey(id)) missing.Add(entry.Tpl?.ToString() ?? "null ammo tpl");
        foreach (var container in containers.StaticContainers)
            foreach (var item in container.Template?.Items ?? [])
                if (!items.ContainsKey(item.Template)) missing.Add(item.Template.ToString());
        foreach (var forced in containers.StaticForced)
            if (!items.ContainsKey(forced.ItemTpl)) missing.Add(forced.ItemTpl.ToString());
        if (missing.Count > 0)
            throw new InvalidDataException("Terminal loot references unavailable item templates: " + string.Join(", ", missing));
    }
}
