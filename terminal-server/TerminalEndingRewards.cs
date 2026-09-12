using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Commerce;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.InRaid;
using WTTServerCommonLib.Services;

namespace Manimal.Terminal.Server;

// SURVIVOR-ENDING REWARDS: grants the retail 1.0 Terminal storyline unlocks the
// moment the player survives-and-extracts at Terminal_Zubr_Exit (the Zubr evac).
// implementation is one line of intent — mark the achievement complete and let
// SPT's RewardHelper cascade its Rewards[] onto the profile (customization
// unlocks + the Item mail via the built-in ApplyRewards path).
//
// achievement + rewards are authored in db/CustomAchievements/Achievements/
// terminal_survivor.json and loaded on OnLoad via WTT's CustomAchievementService.
// idempotency comes from the achievement itself — Achievements dict is a
// TryAdd, and ProfileHelper.AddHideoutCustomisationUnlock dedupes on target id.
[Injectable(TypePriority = OnLoadOrder.Preload + 90003)]
public class TerminalEndingRewards(
    ISptLogger<TerminalEndingRewards> logger,
    ProfileHelper profileHelper,
    RewardHelper rewardHelper,
    SaveServer saveServer,
    WTTCustomAchievementService achievementService) : IOnLoad
{
    // hoisted for the static Harmony body
    private readonly ISptLogger<TerminalEndingRewards> _log = logger;
    private readonly ProfileHelper _profiles = profileHelper;
    private readonly RewardHelper _rewards = rewardHelper;
    private readonly SaveServer _saves = saveServer;

    // matches db/CustomAchievements/Achievements/terminal_survivor.json id +
    // TerminalFinalExit.ExitName on the client
    internal static readonly MongoId SurvivorAchievementId = new("68a67d00000000000000a501");
    internal const string ZubrExitName = "Terminal_Zubr_Exit";

    private static bool _patched;
    private static TerminalEndingRewards? _instance;

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _instance = this;

        // register the achievement templates + locale + icon from db/CustomAchievements/
        // the service walks that folder, so the survivor entry is picked up automatically
        try
        {
            await achievementService.CreateCustomAchievements(typeof(TerminalEndingRewards).Assembly);
            cancellationToken.ThrowIfCancellationRequested();
            _log.Info("[TerminalRewards] custom Survivor achievement registered");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _log.Error($"[TerminalRewards] achievement registration failed — rewards will not grant: {e.Message}");
        }

        if (_patched) return;
        _patched = true;
        var h = new Harmony("com.manimal.terminal.endingrewards");
        h.Patch(AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.EndLocalRaidAsync)),
            postfix: new HarmonyMethod(typeof(TerminalEndingRewards), nameof(EndPostfix)));
        _log.Info("[TerminalRewards] armed — survivor ending grants unlock at Zubr extraction");
    }

    public static void EndPostfix(MongoId sessionId, EndLocalRaidRequestData request,
        CancellationToken cancellationToken, ref Task __result)
    {
        __result = TerminalRaidCompletion.AfterAsync(__result,
            () => GrantRewardsAsync(sessionId, request, cancellationToken));
    }

    private static async Task GrantRewardsAsync(MongoId sessionId, EndLocalRaidRequestData request,
        CancellationToken cancellationToken)
    {
        try
        {
            var self = _instance;
            if (self is null) return;

            var results = request.Results;
            if (results is null) return;
            if (results.Result != ExitStatus.SURVIVED) return;
            if (!string.Equals(results.ExitName, ZubrExitName, StringComparison.OrdinalIgnoreCase)) return;

            var fullProfile = self._profiles.GetFullProfile(sessionId);
            var pmc = fullProfile?.CharacterData?.PmcData;
            if (fullProfile is null || pmc is null) return;

            // idempotency — one grant per profile, EVER. TryAdd would also handle this
            // but the guard lets us skip the log spam on repeat survivals
            pmc.Achievements ??= new Dictionary<MongoId, long>();
            if (pmc.Achievements.ContainsKey(SurvivorAchievementId))
            {
                self._log.Info("[TerminalRewards] player already has the Survivor achievement — no re-grant");
                return;
            }

            self._rewards.AddAchievementToProfile(fullProfile, SurvivorAchievementId);
            await self._saves.SaveProfileAsync(sessionId, cancellationToken);
            self._log.Info("[TerminalRewards] Survivor ending completed — achievement + 7 unlocks granted");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _instance?._log.Warning($"[TerminalRewards] survivor grant failed: {e.Message}");
        }
    }
}
