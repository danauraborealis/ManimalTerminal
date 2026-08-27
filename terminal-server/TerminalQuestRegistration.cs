using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;

namespace Manimal.Terminal.Server;

// Falling Skies is represented as a conventional seven-part Prapor sidequest.
// Content Backport already owns the four retail quest-item templates/bundles, so
// this registration deliberately loads only quests, zones and forced spawns.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 3)]
public class TerminalQuestRegistration(
    WTTServerCommonLib.WTTServerCommonLib wttCommon,
    ISptLogger<TerminalQuestRegistration> logger) : IOnLoad
{
    public async Task OnLoad()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        await wttCommon.CustomQuestService.CreateCustomQuests(assembly);
        await wttCommon.CustomQuestZoneService.CreateCustomQuestZones(assembly);
        await wttCommon.CustomLootspawnService.CreateCustomLootSpawns(assembly);
        logger.Info("[Terminal] Falling Skies quests and retail quest-item spawns registered");
    }
}
