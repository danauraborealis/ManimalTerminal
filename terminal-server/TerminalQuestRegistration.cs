using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Servers;

namespace Manimal.Terminal.Server;

// Falling Skies and the Survivor branch of The Ticket use normal Prapor quests
// plus in-raid Lightkeeper dialogue. Content Backport owns the retail quest-item
// templates/bundles; Terminal owns their quests, dialogue, zones, and recipe.
[Injectable(TypePriority = OnLoadOrder.Preload + 3)]
public class TerminalQuestRegistration(
    WTTServerCommonLib.WTTServerCommonLib wttCommon,
    SaveServer saveServer,
    ISptLogger<TerminalQuestRegistration> logger) : IOnLoad
{
    private static readonly MongoId RetiredCaseUnlockQuestId = new("6a8f4d5e90206485fa1d2e07");
    private static readonly MongoId RetiredCardDiscoveryQuestId = new("6a8f4d5e90206485fa1d2e08");
    private static readonly MongoId RetiredShorelineTestQuestId = new("6a8f4d5e90206485fa1d2e09");
    private static readonly MongoId RetiredTerminalEscapeQuestId = new("6a8f4d5e90206485fa1d2e0b");
    private static readonly MongoId TicketJammerQuestId = new("6a8f4d5e90206485fa1d2e06");
    // This is the hideout-production _id, not the unlocked-case item template.
    // SPT stores production unlocks by recipe ID in the player profile.
    private static readonly MongoId TicketCaseProductionUnlockId = new("6a8f4d5e90206485fa1d2ec0");

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        await wttCommon.CustomQuestService.CreateCustomQuests(assembly);
        await wttCommon.CustomDialogueService.CreateCustomDialogues(assembly);
        await wttCommon.CustomQuestZoneService.CreateCustomQuestZones(assembly);
        await wttCommon.CustomLootspawnService.CreateCustomLootSpawns(assembly);
        await wttCommon.CustomHideoutRecipeService.CreateHideoutRecipes(assembly);
        await wttCommon.CustomAssortSchemeService.CreateCustomAssortSchemes(assembly);
        cancellationToken.ThrowIfCancellationRequested();
        logger.Info("[Terminal] Falling Skies + The Ticket quests, Lightkeeper dialogue, zones, spawns, and hideout recipes registered");
    }

    internal async Task MigrateProfilesAsync(CancellationToken cancellationToken)
    {
        await RemoveRetiredTicketFillerQuestsFromProfiles(cancellationToken);
        await GrantTicketCaseRecipeToCompletedProfiles(cancellationToken);
    }

    private async Task RemoveRetiredTicketFillerQuestsFromProfiles(CancellationToken cancellationToken)
    {
        var migrated = 0;
        foreach (var (sessionId, profile) in saveServer.GetProfiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quests = profile.CharacterData?.PmcData?.Quests;
            if (quests is null || quests.RemoveAll(quest => quest.QId == RetiredCaseUnlockQuestId || quest.QId == RetiredCardDiscoveryQuestId || quest.QId == RetiredShorelineTestQuestId || quest.QId == RetiredTerminalEscapeQuestId) == 0)
                continue;

            await saveServer.SaveProfileAsync(sessionId, cancellationToken);
            migrated++;
        }

        if (migrated > 0)
            logger.Info($"[Terminal] retired redundant armored-case filler quest(s) from {migrated} profile(s)");
    }

    // ProductionScheme rewards only run when a quest is completed. Existing saves can
    // already have Part 6 complete, so grant its identical production unlock once on
    // startup instead of forcing those players to redo the jammer step.
    private async Task GrantTicketCaseRecipeToCompletedProfiles(CancellationToken cancellationToken)
    {
        var migrated = 0;
        foreach (var (sessionId, profile) in saveServer.GetProfiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pmc = profile.CharacterData?.PmcData;
            if (pmc?.Quests?.Any(quest => quest.QId == TicketJammerQuestId && quest.Status == QuestStatusEnum.Success) != true)
                continue;

            pmc.UnlockedInfo ??= new UnlockedInfo { UnlockedProductionRecipe = [] };
            if (!pmc.UnlockedInfo.UnlockedProductionRecipe.Add(TicketCaseProductionUnlockId))
                continue;

            await saveServer.SaveProfileAsync(sessionId, cancellationToken);
            migrated++;
        }

        if (migrated > 0)
            logger.Info($"[Terminal] granted the Part 6 armored-case recipe unlock to {migrated} existing profile(s)");
    }
}

// SaveCallbacks loads profiles after Preload. Register quest data early, but
// migrate existing saves only once the profile collection has been populated.
[Injectable(TypePriority = OnLoadOrder.PostLoad + 3)]
public sealed class TerminalQuestProfileMigration(TerminalQuestRegistration quests) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken) => quests.MigrateProfilesAsync(cancellationToken);
}
