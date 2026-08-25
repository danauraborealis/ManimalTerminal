using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using Systems.Effects;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.Terminal
{
    // One raid-scoped source of truth for Terminal's bot lifecycle.  The old cap,
    // stage director and diagnostics each counted a different collection; most
    // importantly GameWorld.AllAlivePlayersList deliberately retains dead players
    // while their lootable corpse exists.  That made corpses look alive and let the
    // systems disagree about whether a wave had room to spawn.
    //
    // All AI corpses may eventually be retired, but ordinary scavs are the disposable
    // population: they become eligible sooner, at a shorter distance, and are always
    // processed before Black Division/RUAF/civilians/bosses/followers/PMCs/custom roles.
    internal static class TerminalPopulationDirector
    {
        internal sealed class Entry
        {
            internal string ProfileId;
            internal BotOwner Bot;
            internal Player Player;
            internal WildSpawnType Role;
            internal bool OrdinaryScav;
            internal string OriginalZone;
            internal string LogicalZone;
            internal int OriginalStage;
            internal int LogicalStage;
            internal int LogicalTier;
            internal bool Alive;
            internal bool Removed;
            internal float SpawnAt;
            internal float DeathAt = -1f;
            internal float LastRecycledAt = -1f;
            internal int RecycleCount;
            internal float VisualRepairUntil = -1f;
        }

        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
        private static GameObject _host;
        private static int _highestProgressTier = -1;
        private static int _created;
        private static int _died;
        private static int _retiredCorpses;
        private static int _recycledPlacements;
        private static int _avoidedCreations;
        private static int _recoveredBirths;
        private static float _nextNativeReconcile;
        private static readonly Dictionary<string, float> RecycleDiagnosticTimes = new Dictionary<string, float>();

        // Retail stage 2 contains every Zone2 AI place except its three finale places.
        private static readonly HashSet<string> Stage1Zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Zone1BD1HangarAmbush11","Zone1BD1HangarBD11","Zone1BD1PortAmbush1","Zone1BDGateAmbush13",
            "Zone1ScavEnterStorm4","Zone1ScavEnterStorm7","Zone1ScavHangarStorm14","Zone1ScavMiddleAmbush5",
            "Zone1ScavMiddleAmbush8","Zone1ScavPortStorm6","Zone1ScavStoreStorm9","Zone1VSRF1Spawn3",
            "Zone1VSRFSnipeRoofPort2","Zone1VSRFStoreAmbush10",
        };

        private static readonly Dictionary<string, int> Stage2Tiers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Zone2VSRFPassMiddle15"] = 0,
            ["Zone2ScavsWarehouse16"] = 0,
            ["Zone2ScavContainers17"] = 0,
            ["Zone2BDWarehouse18"] = 0,
            ["Zone2BDWarehouse19"] = 0,
            ["Zone2CivGate36"] = 0,
            ["Zone2ScavsColumns20"] = 1,
            ["Zone2ScavsColumns21"] = 1,
            ["Zone2VSRFColumns22"] = 1,
            ["Zone2CivPump35"] = 1,
            ["Zone2VSRFContainers24"] = 2,
            ["Zone2ScavContainers25"] = 2,
            ["Zone2ScavContainers26"] = 2,
            ["Zone2BDColumns23"] = 3,
            ["Zone2BDPort28"] = 4,
            ["Zone2ScavPort29"] = 4,
            ["Zone2BDPort30"] = 4,
            ["Zone2BDPort31"] = 4,
        };

        private static readonly HashSet<string> Stage3Zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Zone2BDPort32", "Zone2VSRFExit33", "Zone2CivShip34",
        };

        internal static void ResetForRaid()
        {
            Entries.Clear();
            _highestProgressTier = -1;
            _created = _died = _retiredCorpses = _recycledPlacements = _avoidedCreations = _recoveredBirths = 0;
            _nextNativeReconcile = 0f;
            RecycleDiagnosticTimes.Clear();
            if (_host) UnityEngine.Object.Destroy(_host);
            _host = null;
        }

        internal static bool IsOrdinaryScav(WildSpawnType role)
        {
            return role == WildSpawnType.assault
                || role == WildSpawnType.assaultGroup
                || role == WildSpawnType.cursedAssault
                || role == WildSpawnType.marksman;
        }

        internal static bool IsRuaf(WildSpawnType role)
        {
            var name = role.ToString();
            return name.IndexOf("ruaf", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("vsrf", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsBlackDivision(WildSpawnType role)
        {
            var name = role.ToString();
            return name.IndexOf("blackdiv", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ZoneName(BotOwner bot)
        {
            try
            {
                if (!bot) return "";
                var z = bot.BotsGroup?.BotZone;
                return z ? z.name : "";
            }
            catch { return ""; }
        }

        internal static int StageOf(string zone)
        {
            if (string.IsNullOrEmpty(zone)) return 0;
            if (Stage1Zones.Contains(zone)) return 1;
            if (Stage3Zones.Contains(zone)) return 3;
            if (Stage2Tiers.ContainsKey(zone)) return 2;
            return 0;
        }

        internal static int TierOf(string zone)
        {
            if (Stage1Zones.Contains(zone)) return -1;
            if (Stage2Tiers.TryGetValue(zone ?? "", out var tier)) return tier;
            if (Stage3Zones.Contains(zone ?? "")) return 5;
            return int.MinValue;
        }

        internal static void NoteProgressEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName) || eventName.Length < 2) return;
            if (char.ToUpperInvariant(eventName[0]) != 'T' || eventName.StartsWith("TB", StringComparison.OrdinalIgnoreCase)) return;
            int tier = eventName[1] - '0';
            if (tier < 0 || tier > 5) return;
            if (tier > _highestProgressTier)
            {
                _highestProgressTier = tier;
                Plugin.Log.LogInfo($"[Population] progression reached T{tier}; earlier idle scav tiers may now be recycled forward");
            }
        }

        internal static bool HasReachedTier(int tier) => _highestProgressTier >= tier;

        // BossSpawnScenario normally consumes BotEventHandler.OnEvent synchronously.
        // Terminal's reconstructed trigger layer proved that the event can still be
        // logged while the matching scenario rows remain dormant, leaving a long empty
        // middle raid.  Call this only AFTER AnyEvent: native gets first refusal, then
        // we activate exactly the still-dormant matching rows through its own method_5.
        internal static void EnsureProgressWavesActivated(string eventName)
        {
            if (!TryProgressTier(eventName, out var tier)) return;
            try
            {
                var game = Singleton<IBotGame>.Instantiated ? Singleton<IBotGame>.Instance : null;
                var scenario = game?.BossSpawnScenario;
                var waves = scenario?.BossSpawnWaves;
                if (scenario == null || waves == null) return;
                int repaired = 0;
                foreach (var wave in waves)
                {
                    if (wave == null || wave.Activated || !wave.ShallSpawn) continue;
                    if (wave.TriggerType != SpawnTriggerType.botEvent) continue;
                    if (!string.Equals(wave.TriggerId, "T" + tier, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        scenario.method_5(wave); // native activation; honors authored Delay
                        repaired++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"[StageDirector] T{tier} fallback could not activate '{wave.BossName}' in '{wave.BossZone}': {e.Message}");
                    }
                }
                if (repaired > 0)
                    Plugin.Log.LogWarning($"[StageDirector] native T{tier} event left {repaired} authored wave(s) dormant; activated them through BossSpawnScenario fallback");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[StageDirector] progression-wave audit failed: {e.Message}"); }
        }

        private static bool TryProgressTier(string eventName, out int tier)
        {
            tier = -1;
            if (string.IsNullOrEmpty(eventName) || eventName.Length < 2) return false;
            if (char.ToUpperInvariant(eventName[0]) != 'T' || eventName.StartsWith("TB", StringComparison.OrdinalIgnoreCase)) return false;
            tier = eventName[1] - '0';
            return tier >= 0 && tier <= 5;
        }

        [HarmonyPatch(typeof(BotsController), "Init")]
        internal static class Patch_Attach
        {
            [HarmonyPostfix]
            private static void Postfix(BotsController __instance)
            {
                if (!TerminalGate.On || __instance?.BotSpawner == null) return;
                ResetForRaid();
                __instance.BotSpawner.OnBotCreated += OnBotCreated;
                __instance.BotSpawner.OnBotRemoved += OnBotRemoved;
                _host = new GameObject("Terminal_PopulationDirector");
                _host.AddComponent<Host>();
                Plugin.Log.LogInfo("[Population] ledger armed (event-driven births/deaths, scav-priority AI corpse retirement and recycling)");
            }
        }

        // Defense in depth for both vanilla and third-party corpse removal. Unity's
        // destroyed Components compare equal to null, and AITaskManager interprets a
        // null Bot as an ownerless task that SHOULD run. Mark fake-null bot tasks for
        // cancellation before it executes their closures against a pooled transform.
        [HarmonyPatch(typeof(AITaskManager), "method_0")]
        internal static class Patch_DropDestroyedBotTasks
        {
            [HarmonyPrefix]
            private static void Prefix(AITaskManager __instance)
            {
                if (!TerminalGate.On || __instance?.SimpleTasks == null) return;
                foreach (var task in __instance.SimpleTasks)
                    if (task != null && !ReferenceEquals(task.Bot, null) && !task.Bot)
                        task.IsCancelRequested = true;
            }
        }

        // EffectsCommutator retains bleeding players independently of GameWorld.
        // Scrub any fake-null entry before its unguarded Player.Position read. The
        // normal cleaner explicitly unregisters first; this catches races and other
        // cleanup mods without changing behavior on non-Terminal maps.
        [HarmonyPatch(typeof(EffectsCommutator), "UpdatePlayersBleedings")]
        internal static class Patch_DropDestroyedBleedingPlayers
        {
            private static readonly FieldInfo BleedingPlayers = AccessTools.Field(typeof(EffectsCommutator), "list_1");

            [HarmonyPrefix]
            private static void Prefix(EffectsCommutator __instance)
            {
                if (!TerminalGate.On || __instance == null) return;
                var list = BleedingPlayers?.GetValue(__instance) as System.Collections.IList;
                if (list == null) return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    object pair = list[i];
                    object player = pair?.GetType().GetProperty("Key")?.GetValue(pair);
                    if (player == null || (player is UnityEngine.Object unityPlayer && !unityPlayer))
                        list.RemoveAt(i);
                }
            }
        }

        private static void OnBotCreated(BotOwner bot)
        {
            ScrubPooledCorpseComponents(bot);
            RecordBot(bot, false);
        }

        // Corpse is added dynamically when a Player dies. Unlike the Player component,
        // it is not placed in PlayerPoolObject.RegisteredComponentsToClean. Calling
        // Corpse.Kill directly can therefore return the GO to the player pool with an
        // old LootItem/InventoryEquipment component still attached. The next bot can
        // look and behave correctly while interaction selects that older inventory.
        private static void ScrubPooledCorpseComponents(BotOwner bot)
        {
            try
            {
                var player = bot ? bot.GetPlayer : null;
                if (!player) return;
                var stale = player.gameObject.GetComponents<Corpse>();
                if (stale == null || stale.Length == 0) return;
                foreach (var corpse in stale)
                    if (corpse) UnityEngine.Object.DestroyImmediate(corpse);
                Plugin.Log.LogWarning($"[CorpseIdentity] removed {stale.Length} stale pooled corpse component(s) before activating {bot.ProfileId} role={bot.Profile?.Info?.Settings?.Role}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[CorpseIdentity] pooled corpse scrub failed: {e.Message}");
            }
        }

        [HarmonyPatch(typeof(Corpse), "method_17")]
        internal static class Patch_AuditCorpseIdentity
        {
            [HarmonyPostfix]
            private static void Postfix(Corpse __instance, string playerProfileID, InventoryEquipment equipment)
            {
                if (!TerminalGate.On || !__instance || string.IsNullOrEmpty(playerProfileID)) return;
                try
                {
                    Entries.TryGetValue(playerProfileID, out var entry);
                    bool equipmentMatch = entry != null && entry.Player
                        && ReferenceEquals(entry.Player.Equipment, equipment);
                    int components = __instance.gameObject.GetComponents<Corpse>().Length;
                    Plugin.Log.LogInfo($"[CorpseIdentity] created profile={playerProfileID} role={(entry != null ? entry.Role.ToString() : "untracked")}"
                        + $" equipment={(equipment != null ? equipment.Id.ToString() : "NULL")} playerEquipmentMatch={equipmentMatch}"
                        + $" corpseComponentsOnObject={components} recycled={(entry != null ? entry.RecycleCount : 0)}");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[CorpseIdentity] creation audit failed: {e.Message}"); }
            }
        }

        private static void RecordBot(BotOwner bot, bool recovered)
        {
            try
            {
                if (!bot) return;
                var id = bot.ProfileId;
                var player = bot.GetPlayer;
                var settings = bot.Profile?.Info?.Settings;
                if (string.IsNullOrEmpty(id) || !player || settings == null) return;
                if (Entries.TryGetValue(id, out var existing))
                {
                    existing.Bot = bot;
                    existing.Player = player;
                    return;
                }
                var zone = ZoneName(bot);
                var stage = StageOf(zone);
                Entries[id] = new Entry
                {
                    ProfileId = id,
                    Bot = bot,
                    Player = player,
                    Role = settings.Role,
                    OrdinaryScav = IsOrdinaryScav(settings.Role),
                    OriginalZone = zone,
                    LogicalZone = zone,
                    OriginalStage = stage,
                    LogicalStage = stage,
                    LogicalTier = TierOf(zone),
                    Alive = true,
                    SpawnAt = Time.time,
                };
                _created++;
                TerminalSpawnGate.NoteBotCreated(IsOrdinaryScav(settings.Role));
                if (recovered) _recoveredBirths++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Population] birth record failed: {e.Message}"); }
        }

        // OnBotCreated is not guaranteed for every custom/direct placement path.  The
        // witness raid ended at ledger=0/native=9: those nine living bots had entered
        // BotSpawner.Bots without an event record, which also made stage 1 clear early.
        // Reconcile against the authoritative native list at low frequency.
        private static void ReconcileNativeBots()
        {
            if (Time.time < _nextNativeReconcile) return;
            _nextNativeReconcile = Time.time + 1f;
            try
            {
                var game = Singleton<IBotGame>.Instantiated ? Singleton<IBotGame>.Instance : null;
                var bots = game?.BotsController?.BotSpawner?.Bots;
                if (bots == null) return;
                int before = _recoveredBirths;
                foreach (var bot in bots.BotOwners)
                {
                    if (!bot || string.IsNullOrEmpty(bot.ProfileId) || Entries.ContainsKey(bot.ProfileId)) continue;
                    RecordBot(bot, true);
                }
                int added = _recoveredBirths - before;
                if (added > 0)
                    Plugin.Log.LogWarning($"[Population] recovered {added} living bot(s) omitted by OnBotCreated (native-list reconciliation)");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Population] native reconciliation failed: {e.Message}"); }
        }

        private static void OnBotRemoved(BotOwner bot)
        {
            try
            {
                if (!bot) return;
                var id = bot.ProfileId;
                if (string.IsNullOrEmpty(id)) return;
                if (!Entries.TryGetValue(id, out var entry))
                {
                    var role = bot.Profile.Info.Settings.Role;
                    var zone = ZoneName(bot);
                    entry = new Entry
                    {
                        ProfileId = id,
                        Bot = bot,
                        Player = bot.GetPlayer,
                        Role = role,
                        OrdinaryScav = IsOrdinaryScav(role),
                        OriginalZone = zone,
                        LogicalZone = zone,
                        OriginalStage = StageOf(zone),
                        LogicalStage = StageOf(zone),
                        LogicalTier = TierOf(zone),
                        SpawnAt = Time.time,
                    };
                    Entries[id] = entry;
                }
                if (!entry.Alive) return;
                entry.Alive = false;
                entry.DeathAt = Time.time;
                _died++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Population] death record failed: {e.Message}"); }
        }

        internal static int LivingCount
        {
            get
            {
                ReconcileNativeBots();
                int n = 0;
                foreach (var e in Entries.Values)
                    if (e.Alive && !e.Removed && e.Player
                        && e.Player.HealthController != null && e.Player.HealthController.IsAlive) n++;
                // During very early spawn callbacks the native count may lead the
                // ledger by one.  Never under-report to the hard-cap gate.
                try
                {
                    var bc = Singleton<IBotGame>.Instantiated ? Singleton<IBotGame>.Instance.BotsController : null;
                    n = Math.Max(n, bc?.BotSpawner?.AllBotsCount ?? 0);
                }
                catch { }
                return n;
            }
        }

        internal static int DespawnAllForEnding()
        {
            int despawned = 0;
            var snapshot = new List<Entry>(Entries.Values);
            foreach (var entry in snapshot)
            {
                if (entry == null || !entry.Alive || entry.Removed || !entry.Bot) continue;
                try
                {
                    entry.Removed = true;
                    TerminalCrewJobs.ByProfile.Remove(entry.ProfileId);
                    entry.Bot.LeaveData.RemoveFromMap();
                    despawned++;
                }
                catch (Exception e)
                {
                    entry.Removed = false;
                    Plugin.Log.LogWarning($"[Ending] bot despawn failed for {entry.ProfileId} role={entry.Role}: {e.Message}");
                }
            }
            Plugin.Log.LogInfo($"[Ending] despawned {despawned} living bot(s) before the ending cutscene");
            return despawned;
        }

        internal static int LivingOrdinaryScavCount
        {
            get
            {
                ReconcileNativeBots();
                int n = 0;
                foreach (var e in Entries.Values)
                    if (e.OrdinaryScav && e.Alive && !e.Removed && e.Player
                        && e.Player.HealthController != null && e.Player.HealthController.IsAlive) n++;
                return n;
            }
        }

        internal static int OrdinaryScavsCreatedCount
        {
            get
            {
                ReconcileNativeBots();
                int n = 0;
                foreach (var e in Entries.Values)
                    if (e.OrdinaryScav) n++;
                return n;
            }
        }

        internal static int ResidentOrdinaryScavCount
        {
            get
            {
                ReconcileNativeBots();
                int n = 0;
                foreach (var e in Entries.Values)
                    if (e.OrdinaryScav && !e.Removed) n++;
                return n;
            }
        }

        internal static int SeenInStage(int stage)
        {
            ReconcileNativeBots();
            int count = 0;
            foreach (var e in Entries.Values)
                if (e.OriginalStage == stage || e.LogicalStage == stage) count++;
            return count;
        }

        internal static List<BotOwner> LivingInLogicalStage(int stage)
        {
            ReconcileNativeBots();
            var result = new List<BotOwner>();
            foreach (var e in Entries.Values)
                if (e.Alive && !e.Removed && e.LogicalStage == stage && e.Bot)
                    result.Add(e.Bot);
            return result;
        }

        internal static void MarkLogicalZone(BotOwner bot, string zone)
        {
            if (!bot || bot.ProfileId == null || !Entries.TryGetValue(bot.ProfileId, out var e)) return;
            e.LogicalZone = zone ?? "";
            e.LogicalStage = StageOf(e.LogicalZone);
            e.LogicalTier = TierOf(e.LogicalZone);
        }

        internal static bool TryPreparePlainWave(BotsController controller, ref BotWaveDataClass wave)
        {
            if (wave == null || !Plugin.ScavRecycler.Value || !IsOrdinaryScav(wave.WildSpawnType)) return false;
            var zone = FindZone(controller, wave.SpawnAreaName);
            if (!zone || wave.BotsCount <= 0) return false;
            int reused = RecycleInto(zone, wave.BotsCount, false, RecyclePool.Scav);
            if (reused <= 0) return false;
            if (reused >= wave.BotsCount)
            {
                _avoidedCreations += wave.BotsCount;
                Plugin.Log.LogInfo($"[Recycler] fulfilled plain scav wave {wave.BotsCount}/{wave.BotsCount} in '{zone.name}' from living survivors; no profiles created");
                return true;
            }
            var shortfall = wave.BotsCount - reused;
            _avoidedCreations += reused;
            wave = new BotWaveDataClass
            {
                BotsCount = shortfall,
                Side = wave.Side,
                SpawnAreaName = wave.SpawnAreaName,
                Time = wave.Time,
                WildSpawnType = wave.WildSpawnType,
                IsPlayers = wave.IsPlayers,
                Difficulty = wave.Difficulty,
                ChanceGroup = wave.ChanceGroup,
                WithCheckMinMax = wave.WithCheckMinMax,
                KeepZoneOnSpawn = wave.KeepZoneOnSpawn,
            };
            Plugin.Log.LogInfo($"[Recycler] fulfilled {reused} scav slot(s) in '{zone.name}' from survivors; spawning only {shortfall}");
            return false;
        }

        internal static bool TryPrepareBossWave(BotsController controller, ref BossLocationSpawn wave)
        {
            if (wave == null || !Plugin.ScavRecycler.Value || wave.Supports != null) return false;
            if (!IsOrdinaryScav(wave.BossType)) return false;
            if (wave.EscortCount > 0 && !IsOrdinaryScav(wave.EscortType)) return false;
            string zoneName = !string.IsNullOrEmpty(wave.BornZone)
                ? wave.BornZone : (wave.BossZone ?? "").Split(',')[0];
            var zone = FindZone(controller, zoneName);
            int total = 1 + Math.Max(0, wave.EscortCount);
            if (!zone || total <= 0) return false;
            bool push = wave.TriggerType == SpawnTriggerType.botEvent || !string.IsNullOrEmpty(wave.TriggerId);
            int reused = RecycleInto(zone, total, push, RecyclePool.Scav);
            if (reused <= 0) return false;
            if (reused >= total)
            {
                _avoidedCreations += total;
                Plugin.Log.LogInfo($"[Recycler] fulfilled scav wave {total}/{total} in '{zone.name}' from living survivors; no profiles created");
                return true;
            }

            int shortfall = total - reused;
            var copy = wave.Copy();
            copy.BossChance = 100f; // the scenario already rolled this wave; never reroll while resizing it
            copy.BossEscortAmount = Math.Max(0, shortfall - 1).ToString();
            copy.Supports = null;
            copy.PerfectPos = wave.PerfectPos;
            copy.Init();
            copy.ShallSpawn = true;
            copy.Activated = wave.Activated;
            wave = copy;
            _avoidedCreations += reused;
            Plugin.Log.LogInfo($"[Recycler] fulfilled {reused} scav slot(s) in '{zone.name}' from survivors; spawning only {shortfall}");
            return false;
        }

        internal static bool TryPrepareRuafWave(BotsController controller, ref BossLocationSpawn wave)
        {
            if (wave == null || !Plugin.RuafRecycler.Value || !IsRuaf(wave.BossType)) return false;
            string zoneName = !string.IsNullOrEmpty(wave.BornZone)
                ? wave.BornZone : (wave.BossZone ?? "").Split(',')[0];
            var zone = FindZone(controller, zoneName);
            int total = 1 + Math.Max(0, wave.EscortCount);
            if (!zone || total <= 0) return false;

            int reused = RecycleInto(zone, total, false, RecyclePool.Ruaf);
            if (reused <= 0) return false;
            if (reused >= total)
            {
                _avoidedCreations += total;
                Plugin.Log.LogInfo($"[Recycler] fulfilled RUAF wave {total}/{total} in '{zone.name}' from idle RUAF survivors; no profiles created");
                return true;
            }

            // Reused soldiers fill abstract squad slots. Preserve one authored leader
            // for the remaining shortfall and collapse its support table to the wave's
            // primary RUAF escort type so Init computes the exact reduced group size.
            int shortfall = total - reused;
            var copy = wave.Copy();
            copy.BossChance = 100f;
            copy.BossEscortAmount = Math.Max(0, shortfall - 1).ToString();
            copy.Supports = null;
            copy.PerfectPos = wave.PerfectPos;
            copy.Init();
            copy.ShallSpawn = true;
            copy.Activated = wave.Activated;
            wave = copy;
            _avoidedCreations += reused;
            Plugin.Log.LogInfo($"[Recycler] moved {reused} idle RUAF survivor(s) into '{zone.name}'; spawning only {shortfall} new RUAF instead of {total}");
            return false;
        }

        internal static bool TryPrepareBlackDivisionWave(BotsController controller, ref BossLocationSpawn wave)
        {
            if (wave == null || !Plugin.BlackDivisionRecycler.Value || !IsBlackDivision(wave.BossType)) return false;
            string zoneName = !string.IsNullOrEmpty(wave.BornZone)
                ? wave.BornZone : (wave.BossZone ?? "").Split(',')[0];
            var zone = FindZone(controller, zoneName);
            int total = 1 + Math.Max(0, wave.EscortCount);
            if (!zone || total <= 0) return false;

            int reused = RecycleInto(zone, total, false, RecyclePool.BlackDivision);
            if (reused <= 0) return false;
            if (reused >= total)
            {
                _avoidedCreations += total;
                Plugin.Log.LogInfo($"[Recycler] fulfilled Black Division wave {total}/{total} in '{zone.name}' from idle BD survivors; no profiles created");
                return true;
            }

            int shortfall = total - reused;
            var copy = wave.Copy();
            copy.BossChance = 100f;
            copy.BossEscortAmount = Math.Max(0, shortfall - 1).ToString();
            copy.Supports = null;
            copy.PerfectPos = wave.PerfectPos;
            copy.Init();
            copy.ShallSpawn = true;
            copy.Activated = wave.Activated;
            wave = copy;
            _avoidedCreations += reused;
            Plugin.Log.LogInfo($"[Recycler] moved {reused} idle Black Division survivor(s) into '{zone.name}'; spawning only {shortfall} new BD instead of {total}");
            return false;
        }

        private static BotZone FindZone(BotsController controller, string name)
        {
            if (controller?.BotSpawner == null || string.IsNullOrEmpty(name)) return null;
            try { return controller.BotSpawner.GetZoneByName(name); }
            catch { return null; }
        }

        private enum RecyclePool { Scav, Ruaf, BlackDivision }

        private static int RecycleInto(BotZone targetZone, int wanted, bool push, RecyclePool pool)
        {
            if (!targetZone || wanted <= 0) return 0;
            int targetStage = StageOf(targetZone.name);
            int targetTier = TierOf(targetZone.name);
            // Opening-area waves remain authored.  Recycling begins only once the
            // player reaches Zone2 and its corresponding progression tier has fired.
            // RUAF/BD are different: their trigger boxes can fire out of numerical
            // stage order (TB4 did so after T0 in the 08-24 witness raid). A requested
            // faction encounter is itself proof that the destination is now legal, so
            // remote survivors may answer it even when the zone number points back.
            if (pool == RecyclePool.Scav
                && (targetStage < 2 || targetTier < 0 || !HasReachedTier(targetTier))) return 0;

            var player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
            if (!player) return 0;
            float sourceMinDistSq = Plugin.ScavRecycleMinDistance.Value * Plugin.ScavRecycleMinDistance.Value;
            float targetMinDistSq = Plugin.ScavRecycleDestinationDistance.Value * Plugin.ScavRecycleDestinationDistance.Value;
            var positions = EligibleTargetPositions(targetZone, player.Position, targetMinDistSq);
            if (positions.Count == 0)
            {
                LogRecycleBlock(pool, targetZone.name, "no safe destination marker (all too close, visible, or off NavMesh)");
                return 0;
            }

            var candidates = new List<Entry>();
            var rejects = new int[9];
            foreach (var e in Entries.Values)
            {
                if (CanRecycle(e, targetStage, targetTier, targetZone.name, player.Position, sourceMinDistSq, pool, out var reason))
                    candidates.Add(e);
                else
                    rejects[(int)reason]++;
            }
            if (candidates.Count == 0)
            {
                LogRecycleBlock(pool, targetZone.name,
                    $"no eligible survivor among {Entries.Count}: faction={rejects[(int)RecycleReject.WrongFaction]}, invalid/dead={rejects[(int)RecycleReject.Invalid]}, protectedAssignment={rejects[(int)RecycleReject.Progress]}, young={rejects[(int)RecycleReject.TooYoung]}, nearOrVisible={rejects[(int)RecycleReject.NearOrVisible]}, inactive={rejects[(int)RecycleReject.Inactive]}, combat={rejects[(int)RecycleReject.Combat]}, error={rejects[(int)RecycleReject.Error]}");
            }
            candidates.Sort((a, b) => a.SpawnAt.CompareTo(b.SpawnAt));
            int candidateLimit = Math.Min(Math.Min(wanted, positions.Count), candidates.Count);

            int moved = 0;
            for (int candidateIndex = 0; candidateIndex < candidateLimit; candidateIndex++)
            {
                var e = candidates[candidateIndex];
                var pos = positions[moved % positions.Count];
                try
                {
                    var bot = e.Bot;
                    // External AI limiters may have left a perfectly reusable survivor
                    // inactive. The destination is safe and out of view, so wake it as
                    // part of the move instead of throwing away the recycler candidate.
                    if (!e.Player.gameObject.activeSelf) e.Player.gameObject.SetActive(true);
                    bot.BotState = EBotState.Active;
                    bot.StandBy?.Activate();
                    bot.Memory?.Spotted(false, null, null);
                    bot.Mover.Stop();
                    bot.Mover.Teleport(pos);
                    bot.Transform.position = pos;
                    bot.SpawnBotZone = targetZone;
                    bot.LookSensor?.UpdateZoneValue(targetZone);

                    // Observed/player-body culling evaluates the old position for at
                    // least one job cycle after a teleport. In the witness raid that
                    // state stuck on a recycled RUAF soldier: clothing/body renderers
                    // remained forceRenderingOff while independently owned equipment
                    // stayed visible. Clear the stale flag now and for a short settling
                    // window while the culling sphere catches the new transform.
                    e.VisualRepairUntil = Time.time + 3f;
                    RepairRecycledBodyVisibility(e, out int bodyRenderers, out int forcedBackOn);

                    TerminalCrewJobs.ByProfile[e.ProfileId] = new TerminalCrewJobs.Rec
                    {
                        Job = push ? TerminalCrewJobs.Job.Hunt : TerminalCrewJobs.Job.Guard,
                        Zone = new Bounds(pos, new Vector3(30f, 10f, 30f)),
                        RushUntil = 0f,
                    };
                    e.LogicalZone = targetZone.name;
                    e.LogicalStage = targetStage;
                    e.LogicalTier = targetTier;
                    e.LastRecycledAt = Time.time;
                    e.RecycleCount++;
                    moved++;
                    _recycledPlacements++;
                    Plugin.Log.LogDebug($"[Recycler] {PoolLabel(pool)} {e.ProfileId} '{e.Role}' -> {targetZone.name} at {pos} (reuse #{e.RecycleCount})");
                    Plugin.Log.LogDebug($"[Recycler] body visibility refreshed for {e.ProfileId}: {bodyRenderers} renderer(s), {forcedBackOn} stale force-off flag(s) cleared");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Recycler] failed moving {e.ProfileId} to '{targetZone.name}': {ex.Message}");
                }
            }
            return moved;
        }

        private static void RepairRecycledBodyVisibility(Entry entry, out int renderers, out int forcedBackOn)
        {
            renderers = 0;
            forcedBackOn = 0;
            try
            {
                if (entry == null || !entry.Player || !entry.Player.PlayerBody) return;
                var body = new List<Renderer>(128);
                entry.Player.PlayerBody.GetRenderersNonAlloc(body);
                renderers = body.Count;
                foreach (var renderer in body)
                {
                    if (!renderer) continue;
                    if (renderer.forceRenderingOff)
                    {
                        renderer.forceRenderingOff = false;
                        forcedBackOn++;
                    }
                    // A teleported skinned mesh can retain bounds/bones from its old
                    // culled position. Let it update offscreen thereafter; recycler
                    // populations are deliberately small, so this is bounded.
                    if (renderer is SkinnedMeshRenderer skinned)
                        skinned.updateWhenOffscreen = true;
                }
            }
            catch { }
        }

        private static string PoolLabel(RecyclePool pool)
            => pool == RecyclePool.Ruaf ? "RUAF" : pool == RecyclePool.BlackDivision ? "Black Division" : "scav";

        private enum RecycleReject
        {
            Invalid,
            WrongFaction,
            Progress,
            TooYoung,
            NearOrVisible,
            Inactive,
            Combat,
            Error,
            Reserved,
        }

        private static void LogRecycleBlock(RecyclePool pool, string targetZone, string reason)
        {
            string key = pool + ":" + targetZone;
            if (RecycleDiagnosticTimes.TryGetValue(key, out var last) && Time.realtimeSinceStartup - last < 30f) return;
            RecycleDiagnosticTimes[key] = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo($"[RecyclerDiag] {PoolLabel(pool)} -> '{targetZone}' blocked: {reason}");
        }

        private static bool CanRecycle(Entry e, int targetStage, int targetTier, string targetZone,
            Vector3 playerPos, float minDistSq, RecyclePool pool, out RecycleReject reason)
        {
            reason = RecycleReject.Invalid;
            if (e == null || !e.Alive || e.Removed || !e.Bot || !e.Player) return false;
            if (pool == RecyclePool.Ruaf && !IsRuaf(e.Role)) { reason = RecycleReject.WrongFaction; return false; }
            if (pool == RecyclePool.BlackDivision && !IsBlackDivision(e.Role)) { reason = RecycleReject.WrongFaction; return false; }
            if (pool == RecyclePool.Scav && !e.OrdinaryScav) { reason = RecycleReject.WrongFaction; return false; }
            // Never recycle a bot into the assignment it already owns. Ordinary
            // scavs remain forward-only so background population follows progression.
            // Triggered RUAF/BD encounters may arrive out of numeric stage order, and
            // their already-spawned remote survivors are exactly the resources those
            // later requests should reuse rather than strand behind the total cap.
            bool sameAssignment = string.Equals(e.LogicalZone, targetZone, StringComparison.OrdinalIgnoreCase);
            bool scavAheadOfTarget = pool == RecyclePool.Scav
                && (e.LogicalStage > targetStage || e.LogicalTier > targetTier);
            if (sameAssignment || scavAheadOfTarget)
            {
                reason = RecycleReject.Progress;
                return false;
            }
            float ageFromLastPlacement = Time.time - Math.Max(e.SpawnAt, e.LastRecycledAt);
            if (ageFromLastPlacement < Plugin.ScavRecycleMinAge.Value) { reason = RecycleReject.TooYoung; return false; }
            if ((e.Bot.Position - playerPos).sqrMagnitude < minDistSq || IsInCamera(e.Bot.Position))
            {
                reason = RecycleReject.NearOrVisible;
                return false;
            }
            try
            {
                if (e.Bot.Mover == null) { reason = RecycleReject.Inactive; return false; }
                if (e.Player.HealthController == null || !e.Player.HealthController.IsAlive) return false;
                if (e.Bot.Memory == null || e.Bot.Memory.IsUnderFire) { reason = RecycleReject.Combat; return false; }
                // GoalEnemy can remain assigned long after a fight has ended. Treat it
                // as active combat only while visible/shootable or seen in the last
                // 10s. Persistent patrol/guard requests are deliberately NOT a veto:
                // virtually every authored survivor owns one even while standing idle.
                var enemy = e.Bot.Memory.GoalEnemy;
                if (enemy != null && (enemy.IsVisible || enemy.CanShoot
                    || Time.time - enemy.PersonalLastSeenTime < 10f)) { reason = RecycleReject.Combat; return false; }
            }
            catch { reason = RecycleReject.Error; return false; }
            return true;
        }

        private static List<Vector3> EligibleTargetPositions(BotZone zone, Vector3 playerPos, float minDistSq)
        {
            var result = new List<Vector3>();
            var fallback = new List<Vector3>();
            try
            {
                if (zone.SpawnPointMarkers == null) return result;
                float fallbackMinDistSq = Mathf.Min(minDistSq, 20f * 20f);
                foreach (var marker in zone.SpawnPointMarkers)
                {
                    if (!marker) continue;
                    var p = marker.Position;
                    if (IsInCamera(p)) continue;
                    if (!NavMesh.SamplePosition(p, out var hit, 6f, NavMesh.AllAreas)) continue;
                    if (IsInCamera(hit.position)) continue;
                    float distSq = (hit.position - playerPos).sqrMagnitude;
                    if (distSq >= minDistSq) result.Add(hit.position);
                    else if (distSq >= fallbackMinDistSq) fallback.Add(hit.position);
                }
                // Do not strand a triggered wave merely because every authored point
                // is 20-39m away. Visibility remains a hard veto; only the conservative
                // distance buffer relaxes when a zone has no full-distance marker.
                if (result.Count == 0 && fallback.Count > 0)
                {
                    fallback.Sort((a, b) => (b - playerPos).sqrMagnitude.CompareTo((a - playerPos).sqrMagnitude));
                    result.AddRange(fallback);
                    Plugin.Log.LogDebug($"[Recycler] '{zone.name}' using out-of-view destination fallback ({Mathf.Sqrt((fallback[0] - playerPos).sqrMagnitude):F0}m)");
                }
            }
            catch { }
            return result;
        }

        private static bool IsInCamera(Vector3 position)
        {
            try
            {
                var cam = Camera.main;
                if (!cam) return false;
                var p = cam.WorldToViewportPoint(position + Vector3.up);
                return p.z > 0f && p.x >= 0f && p.x <= 1f && p.y >= 0f && p.y <= 1f;
            }
            catch { return false; }
        }

        private sealed class Host : MonoBehaviour
        {
            private float _nextCleanup;
            private float _nextHeartbeat;

            private void Update()
            {
                if (!TerminalGate.On) { Destroy(gameObject); return; }
                ReconcileNativeBots();
                foreach (var entry in Entries.Values)
                    if (entry.Alive && !entry.Removed && entry.VisualRepairUntil >= Time.time)
                        RepairRecycledBodyVisibility(entry, out _, out _);
                bool manual = Plugin.ScavCorpseCleanupKey.Value.IsDown();
                if (manual || (Plugin.ScavCorpseCleanup.Value && Time.time >= _nextCleanup))
                {
                    _nextCleanup = Time.time + 10f;
                    SweepCorpses(manual);
                }
                if (Time.time >= _nextHeartbeat)
                {
                    _nextHeartbeat = Time.time + 60f;
                    int living = 0, livingScavs = 0, scavCorpses = 0, specialCorpses = 0;
                    foreach (var e in Entries.Values)
                    {
                        if (e.Alive && !e.Removed)
                        {
                            living++;
                            if (e.OrdinaryScav) livingScavs++;
                        }
                        else if (!e.Removed && e.OrdinaryScav) scavCorpses++;
                        else if (!e.Removed) specialCorpses++;
                    }
                    int nativeAlive = 0, activating = 0, botCreatorLoading = 0, profilesLoading = 0, delayed = 0;
                    try
                    {
                        var spawner = Singleton<IBotGame>.Instantiated ? Singleton<IBotGame>.Instance.BotsController?.BotSpawner : null;
                        nativeAlive = spawner?.AllBotsCount ?? 0;
                        activating = spawner?.InSpawnProcess ?? 0;
                        botCreatorLoading = spawner?.BotCreator?.BotsLoading ?? 0;
                        profilesLoading = BotCreationDataClass.ProfilesLoadingProcess;
                        delayed = spawner?.SpawnDelaysService?.WaitCount ?? 0;
                    }
                    catch { }
                    Plugin.Log.LogInfo($"[Population] ledgerAlive={living} livingScavs={livingScavs} pendingScavs={TerminalSpawnGate.PendingScavCount} nativeAlive={nativeAlive} activating={activating} botCreatorLoading={botCreatorLoading} profilesLoading={profilesLoading} delayed={delayed} scavCorpses={scavCorpses} specialCorpses={specialCorpses} "
                        + $"created={_created} died={_died} recovered={_recoveredBirths} recycled={_recycledPlacements} avoided={_avoidedCreations} retired={_retiredCorpses}");
                }
            }

            private static void SweepCorpses(bool manual)
            {
                var world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
                var me = world ? world.MainPlayer : null;
                if (!me) return;
                int limit = manual ? int.MaxValue : Plugin.ScavCorpseCleanupPerSweep.Value;
                int removed = 0;
                var ordered = new List<Entry>(Entries.Values);
                ordered.Sort((a, b) =>
                {
                    // The cleanup budget belongs to cannon-fodder scavs first. Within
                    // each tier, retire the oldest corpse before newer loot opportunities.
                    int tier = b.OrdinaryScav.CompareTo(a.OrdinaryScav);
                    return tier != 0 ? tier : a.DeathAt.CompareTo(b.DeathAt);
                });
                foreach (var e in ordered)
                {
                    if (removed >= limit) break;
                    if (e.Alive || e.Removed || !e.Player) continue;
                    float lifetime = e.OrdinaryScav ? Plugin.ScavCorpseLifetime.Value : Plugin.SpecialCorpseLifetime.Value;
                    float distance = e.OrdinaryScav ? Plugin.ScavCorpseCleanupDistance.Value : Plugin.SpecialCorpseCleanupDistance.Value;
                    if (!manual && (e.DeathAt < 0f || Time.time - e.DeathAt < lifetime)) continue;
                    // Player.Corpse is internal in the public 4.0 metadata, but the
                    // corpse is an ordinary component attached to this same pooled GO.
                    var corpse = e.Player.GetComponent<Corpse>();
                    if (!corpse) continue; // Player.OnDead has not built it yet
                    // Protect proximity to EVERY human, not just the host/local player.
                    // This matters now that the retention radii are less conservative:
                    // a Fika client looting far from the host must never lose its body.
                    if (IsNearAnyHuman(world, corpse.transform.position, distance * distance)) continue;
                    if (RetireCorpse(world, e, corpse)) removed++;
                }
                if (manual)
                    Plugin.Log.LogWarning($"[CorpseCleanup] manual sweep retired {removed} distance-eligible AI corpse(s), scav priority first");
            }

            private static bool IsNearAnyHuman(GameWorld world, Vector3 corpsePosition, float distanceSq)
            {
                bool foundHuman = false;
                try
                {
                    var players = world.RegisteredPlayers;
                    for (int i = 0; i < players.Count; i++)
                    {
                        var player = players[i];
                        if (player == null || player.IsAI) continue;
                        foundHuman = true;
                        if ((player.Position - corpsePosition).sqrMagnitude < distanceSq) return true;
                    }
                }
                catch { }

                // Defensive fallback for unusual startup/teardown registry timing.
                var main = world ? world.MainPlayer : null;
                return !foundHuman && main
                    && (main.Position - corpsePosition).sqrMagnitude < distanceSq;
            }

            private static bool RetireCorpse(GameWorld world, Entry e, Corpse corpse)
            {
                var player = e.Player;
                int cancelledAiTasks = 0;

                // Corpse.Kill returns the entire pooled player GO and invalidates its
                // BifacialTransform. Several EFT registries live outside GameWorld and
                // are NOT cleaned by GameWorld.UnregisterPlayer. The old cleaner left
                // the dead player in EffectsCommutator (16,052 Position NREs in one
                // raid) and left delayed hearing work owned by the pooled BotOwner
                // (1,031 more Transform NREs). Detach those consumers before touching
                // the corpse object.
                try
                {
                    if (Singleton<Effects>.Instantiated)
                        Singleton<Effects>.Instance.EffectsCommutator?.StopBleedingForPlayer(player);
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[CorpseCleanup] bleeding unregister failed for {e.ProfileId}: {ex.Message}"); }
                try { cancelledAiTasks = CancelDelayedBotTasks(e.Bot); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[CorpseCleanup] delayed-AI purge failed for {e.ProfileId}: {ex.Message}"); }

                try
                {
                    // Remove the AI's dead-body search record before its Player leaves
                    // the world.  This list is independent from GameWorld's registry.
                    var dbc = e.Bot ? e.Bot.BotsGroup?.DeadBodiesController : null;
                    if (dbc != null)
                    {
                        for (int i = dbc.Bodies.Count - 1; i >= 0; i--)
                        {
                            var body = dbc.Bodies[i];
                            if (body != null && ReferenceEquals(body.Player, player)) dbc.RemoveBody(body);
                        }
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[CorpseCleanup] body-ledger removal failed for {e.ProfileId}: {ex.Message}"); }

                // Bot death removes BotOwner from the live count but deliberately
                // leaves its Player in BotSpawner.AllPlayers and every group's player
                // connector so the corpse can still be perceived. Once we are actually
                // retiring the corpse, use EFT's native metadata cleanup as well.
                try { e.Bot?.BotsController?.DestroyInfo(player); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[CorpseCleanup] bot metadata unregister failed for {e.ProfileId}: {ex.Message}"); }

                // LocalGame owns two private player dictionaries in addition to
                // GameWorld. If a pooled corpse remains in either one, raid teardown
                // calls Player.Dispose on the already-returned object and
                // FirearmController.Destroy repeatedly NREs. Remove only this exact
                // profile/player pair before Corpse.Kill returns the GO to its pool.
                try { RemoveFromLocalGameRegistries(e.ProfileId, player); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[CorpseCleanup] local-game unregister failed for {e.ProfileId}: {ex.Message}"); }
                try { if (world) world.UnregisterPlayer(player); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[CorpseCleanup] world unregister failed for {e.ProfileId}: {ex.Message}"); }
                try { RemoveFromGameWorldRetainedRegistries(world, e.ProfileId, player); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[CorpseCleanup] retained-world unregister failed for {e.ProfileId}: {ex.Message}"); }

                // Retiring the corpse bypasses Player.Dispose, but another retained
                // teardown path may still invoke it. Destroy the hands controller once
                // while its animator is valid; a later Dispose then sees null instead
                // of double-destroying a pooled firearm animator.
                try { player.method_118(); }
                catch (Exception ex) { Plugin.Log.LogWarning($"[CorpseCleanup] hands release failed for {e.ProfileId}: {ex.Message}"); }
                try
                {
                    e.Removed = true;
                    // Corpse.Kill -> LootItem.Kill disposes PlayerBody, unregisters
                    // loot/culling, and returns the pooled player object. Calling
                    // Player.Dispose first double-destroys its firearm animator.
                    corpse.Kill();
                    // LootItem.Kill returns the GO, but this dynamically added Corpse
                    // component is not one of the pool's registered cleanup components.
                    // Destroy it before the object can be checked out as another bot.
                    try { if (corpse) UnityEngine.Object.DestroyImmediate(corpse); }
                    catch (Exception componentError)
                    {
                        // Kill already completed and the object is pooled; never roll
                        // the ledger back to "present" if only this hardening step fails.
                        Plugin.Log.LogWarning($"[CorpseIdentity] retired component cleanup failed for {e.ProfileId}: {componentError.Message}");
                    }
                    _retiredCorpses++;
                    Plugin.Log.LogInfo($"[CorpseCleanup] safely retired {(e.OrdinaryScav ? "ordinary scav" : "remote special")} {e.ProfileId} role={e.Role} age={Time.time - e.DeathAt:0}s aiTasksCancelled={cancelledAiTasks}");
                    return true;
                }
                catch (Exception ex)
                {
                    e.Removed = false;
                    Plugin.Log.LogError($"[CorpseCleanup] Corpse.Kill failed for {e.ProfileId}; object left in place: {ex}");
                    return false;
                }
            }

            private static int CancelDelayedBotTasks(BotOwner bot)
            {
                if (!bot || bot.AITaskManager == null || bot.AITaskManager.SimpleTasks == null) return 0;
                int cancelled = 0;
                // Cancellation is applied by AITaskManager on its next tick; iterate a
                // snapshot so this remains safe if a mod cancels synchronously.
                var tasks = new List<AITaskManager.GClass607>(bot.AITaskManager.SimpleTasks);
                foreach (var task in tasks)
                {
                    if (task == null || !ReferenceEquals(task.Bot, bot)) continue;
                    bot.AITaskManager.CancelDelayedTask(task.Id);
                    cancelled++;
                }
                return cancelled;
            }

            private static void RemoveFromGameWorldRetainedRegistries(GameWorld world, string profileId, Player player)
            {
                if (!world || string.IsNullOrEmpty(profileId) || !player) return;
                for (Type type = world.GetType(); type != null; type = type.BaseType)
                {
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic
                        | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    {
                        object value = field.GetValue(world);
                        if (value is IDictionary<string, Player> players)
                        {
                            if (players.TryGetValue(profileId, out var registered)
                                && ReferenceEquals(registered, player))
                                players.Remove(profileId);
                        }
                        else if (value is IDictionary<string, IPlayerOwner> owners)
                        {
                            if (!owners.TryGetValue(profileId, out var owner) || owner == null
                                || !ReferenceEquals(owner.iPlayer, player)) continue;
                            try { owner.Dispose(); } catch { }
                            owners.Remove(profileId);
                        }
                    }
                }
            }

            private static void RemoveFromLocalGameRegistries(string profileId, Player player)
            {
                if (string.IsNullOrEmpty(profileId) || player == null
                    || !Singleton<AbstractGame>.Instantiated) return;
                object game = Singleton<AbstractGame>.Instance;
                for (Type type = game.GetType(); type != null; type = type.BaseType)
                {
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic
                        | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    {
                        if (!typeof(IDictionary<string, Player>).IsAssignableFrom(field.FieldType)) continue;
                        if (!(field.GetValue(game) is IDictionary<string, Player> players)) continue;
                        if (players.TryGetValue(profileId, out var registered)
                            && ReferenceEquals(registered, player))
                            players.Remove(profileId);
                    }
                }
            }
        }
    }
}
