using System.Reflection;
using MoreBotsServer;
using MoreBotsServer.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;

namespace Manimal.Terminal.Server;

// TERMINAL CIVILIANS (ported from MitsuruMod 2026-08-18, recruitment stripped):
// registers WildSpawnType 776701 'civilian' with MoreBots — bot data ships in
// db/bots/sharedTypes/civilian.json (unarmed except a holstered pistol the
// client brain never draws; scav-tier fragile health per the live dumps). the
// prepatcher injects the enum client-side; the 3 live BossLocationSpawn rows in
// base.json place them at the ship/pump/gate zones.
[Injectable(InjectionType = InjectionType.Singleton,
            TypePriority = MoreBotsLoadOrder.LoadBots)]
public sealed class TerminalCivilianRegistration(
    MoreBotsAPI moreBotsApi,
    MoreBotsCustomBotTypeService customBotTypeService,
    FactionService factionService
) : IOnLoad
{
    public const int WildSpawnTypeValue = 776701;
    public const string BotTypeName = "civilian";
    public const string FactionName = "civilian";

    // neutral to everyone that roams terminal — PMCs, scavs, BD and RUAF should
    // scare civilians, not hunt them
    private static readonly string[] FriendlyFactions =
    [
        "savage",
        "usec",
        "bear",
    ];

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assembly = Assembly.GetExecutingAssembly();
        var typeList = new List<string> { BotTypeName };

        await moreBotsApi.LoadBotsShared(assembly, BotTypeName, typeList);
        cancellationToken.ThrowIfCancellationRequested();

        customBotTypeService.AddCustomWildSpawnTypeNames(new Dictionary<int, string>
        {
            { WildSpawnTypeValue, BotTypeName },
        });

        foreach (var factionName in FriendlyFactions)
        {
            if (!factionService.Factions.ContainsKey(factionName)) continue;
            factionService.AddFriendlyByFaction(typeList, factionName);
            factionService.AddFriendlyByFaction(factionName, FactionName);
        }

        await Task.CompletedTask;
    }
}

// faction entry must exist before bot registration wires relations into it
[Injectable(InjectionType = InjectionType.Singleton,
            TypePriority = MoreBotsLoadOrder.LoadFactions)]
public sealed class TerminalCivilianFaction(FactionService factionService) : IOnLoad
{
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!factionService.Factions.ContainsKey(TerminalCivilianRegistration.FactionName))
        {
            factionService.Factions.Add(TerminalCivilianRegistration.FactionName, new Faction
            {
                Name = TerminalCivilianRegistration.FactionName,
                BotTypes =
                {
                    (WildSpawnType)TerminalCivilianRegistration.WildSpawnTypeValue,
                }
            });
        }

        await Task.CompletedTask;
    }
}
