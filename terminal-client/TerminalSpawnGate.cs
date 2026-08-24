using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EFT;
using EFT.Game.Spawning;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // hold ALL bot waves until the attack cutscene has played (user call 2026-08-10:
    // distant firefights during the cutscene — the port shouldnt be at war before the
    // attack happens). every spawn path funnels through BotsController.ActivateBotsByWave
    // (LocalGame hands both scenario spawn actions to it), so gating the two overloads
    // holds waves, bosses and triggered events alike; held waves queue and flush the
    // moment the cutscene ends. escape hatches: gate never holds when the cutscene is
    // unavailable (missing bundle piece) and never past a hard 8min ceiling.
    internal static class TerminalSpawnGate
    {
        private static float _raidStart = -1f;
        private static float _attackStartedAt = -1f;
        private static bool _holdLogged;
        private static bool _botlessBlockLogged;
        private static bool _ignoreMaxLogged;
        private static bool _lifetimeCapLogged;
        private static bool _lifetimeBudgetClosed;
        private static bool _releasingQueuedWave;
        private static int _placementsCommittedThisRaid;
        private static float _lastPopLog;
        private sealed class ScavReservation
        {
            internal int Remaining;
            internal float ExpiresAt;
        }
        private static readonly List<ScavReservation> _scavReservations = new();
        private static readonly List<ScavReservation> _totalReservations = new();
        private static readonly List<(BotsController c, BotWaveDataClass w)> _waves = new();
        private static readonly List<(BotsController c, BossLocationSpawn w)> _bosses = new();

        // reset at raid CREATION, not OnGameStarted: raid 2 in one session held
        // raid 1's _raidStart through the load window, so the 480s failsafe read
        // "raid is old, release" and early waves slipped the gate ungated
        // (2026-08-19: bots fighting in the background during the intro)
        internal static void ResetForRaid()
        {
            _raidStart = -1f;
            _attackStartedAt = -1f;
            _holdLogged = false;
            _botlessBlockLogged = false;
            _ignoreMaxLogged = false;
            _lifetimeCapLogged = false;
            _lifetimeBudgetClosed = false;
            _releasingQueuedWave = false;
            _placementsCommittedThisRaid = 0;
            _lastPopLog = 0f;
            _scavReservations.Clear();
            _totalReservations.Clear();
            _waves.Clear();
            _bosses.Clear();
        }

        // ORDINARY-SCAV ADMISSION CEILING.  This gate acts at BotsController before
        // BotCreationDataClass.Create generates profiles.  The former low-level choke
        // acted after profile generation and left 14-15 ActivateBot jobs permanently
        // counted in InSpawnProcess during witness raids.  Do not cap there again.
        //
        // Retail's ForceSpawn/IgnoreMaxBots flag is choreography, not an accidental cap
        // hole: Black Division, RUAF, civilians and bosses must arrive when their event
        // fires. Those special placements bypass the ceiling. Only living ordinary
        // scav roles consume the scav target; special population is reported separately.
        private static int AliveScavCount() => TerminalPopulationDirector.LivingOrdinaryScavCount;

        // Zero disables the corresponding safeguard.
        private static bool CapEnabled => Plugin.MaxAliveScavs.Value > 0;
        private static bool TotalCapEnabled => Plugin.MaxAliveBots.Value > 0;
        private static bool ScavResidentCapEnabled => Plugin.MaxResidentScavs.Value > 0;
        private static bool LifetimeCapEnabled
            => Plugin.MaxBotsCreatedPerRaid != null && Plugin.MaxBotsCreatedPerRaid.Value > 0;
        internal static bool BotsDisabled => Plugin.DisableAllBots != null && Plugin.DisableAllBots.Value;

        private static void LogBotlessBlock(string path)
        {
            if (_botlessBlockLogged) return;
            _botlessBlockLogged = true;
            Plugin.Log.LogWarning($"[BotControl] blocked runtime bot spawn path '{path}' — botless diagnostic is active");
        }

        private static int ScavHeadroom
            => CapEnabled ? Math.Max(0, Plugin.MaxAliveScavs.Value - AliveScavCount() - PendingScavAdmissions) : int.MaxValue;

        private static int ScavResidentHeadroom
            => ScavResidentCapEnabled
                ? Math.Max(0, Plugin.MaxResidentScavs.Value - TerminalPopulationDirector.ResidentOrdinaryScavCount - PendingScavAdmissions)
                : int.MaxValue;

        private static int TotalHeadroom
            => TotalCapEnabled
                ? Math.Max(0, Plugin.MaxAliveBots.Value - TerminalPopulationDirector.LivingCount - PendingTotalAdmissions)
                : int.MaxValue;

        private static int PendingScavAdmissions
        {
            get
            {
                ExpireScavReservations();
                int count = 0;
                foreach (var r in _scavReservations) count += r.Remaining;
                return count;
            }
        }

        internal static int PendingScavCount => PendingScavAdmissions;

        private static int PendingTotalAdmissions
        {
            get
            {
                ExpireReservations(_totalReservations);
                int count = 0;
                foreach (var r in _totalReservations) count += r.Remaining;
                return count;
            }
        }

        private static void ExpireScavReservations()
        {
            ExpireReservations(_scavReservations);
        }

        private static void ExpireReservations(List<ScavReservation> reservations)
        {
            float now = Time.realtimeSinceStartup;
            for (int i = reservations.Count - 1; i >= 0; i--)
                if (reservations[i].Remaining <= 0 || now >= reservations[i].ExpiresAt)
                    reservations.RemoveAt(i);
        }

        private static void ReserveScavAdmissions(int count)
        {
            if (count <= 0) return;
            ExpireScavReservations();
            _scavReservations.Add(new ScavReservation
            {
                Remaining = count,
                // Profile generation normally completes in seconds. A failed custom-AI
                // activation must not starve the scav pool for the rest of the raid.
                ExpiresAt = Time.realtimeSinceStartup + 30f,
            });
        }

        private static void ReserveTotalAdmissions(int count)
        {
            if (count <= 0 || !TotalCapEnabled) return;
            ExpireReservations(_totalReservations);
            _totalReservations.Add(new ScavReservation
            {
                Remaining = count,
                ExpiresAt = Time.realtimeSinceStartup + 30f,
            });
        }

        internal static void NoteBotCreated(bool ordinaryScav)
        {
            ConsumeReservation(_totalReservations);
            if (ordinaryScav) ConsumeReservation(_scavReservations);
        }

        private static void ConsumeReservation(List<ScavReservation> reservations)
        {
            ExpireReservations(reservations);
            for (int i = 0; i < reservations.Count; i++)
            {
                if (reservations[i].Remaining <= 0) continue;
                reservations[i].Remaining--;
                if (reservations[i].Remaining <= 0) reservations.RemoveAt(i);
                return;
            }
        }

        private static bool LifetimeBudgetOpen
            => !LifetimeCapEnabled || (!_lifetimeBudgetClosed
                && _placementsCommittedThisRaid < Plugin.MaxBotsCreatedPerRaid.Value);

        private static void LogLifetimeHold()
        {
            if (_lifetimeCapLogged) return;
            _lifetimeCapLogged = true;
            Plugin.Log.LogWarning($"[SpawnGate] lifetime budget closed ({_placementsCommittedThisRaid} bot placement(s) accepted; limit {Plugin.MaxBotsCreatedPerRaid.Value}) — deaths will not spawn replacements");
        }

        private static void LogPopHold()
        {
            if (Time.realtimeSinceStartup - _lastPopLog < 20f) return;
            _lastPopLog = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo($"[SpawnGate] scav admission waiting (aliveScavs={AliveScavCount()}/{Plugin.MaxAliveScavs.Value}, "
                + $"aliveTotal={TerminalPopulationDirector.LivingCount}/{(TotalCapEnabled ? Plugin.MaxAliveBots.Value.ToString() : "unlimited")}, pendingTotal={PendingTotalAdmissions}, "
                + $"residentScavs={TerminalPopulationDirector.ResidentOrdinaryScavCount}/{(ScavResidentCapEnabled ? Plugin.MaxResidentScavs.Value.ToString() : "unlimited")}, "
                + $"lifetimeCreatedScavs={TerminalPopulationDirector.OrdinaryScavsCreatedCount}, "
                + $"pendingScavs={PendingScavAdmissions}, nativePipelineBusy={PlacementPipelineBusy()}) — {_waves.Count + _bosses.Count} wave(s) queued");
        }

        private static bool IsOrdinaryScavProfile(IGetProfileData data)
        {
            try
            {
                if (data == null || !data.TryGetRole(out WildSpawnType role, out _)) return false;
                return TerminalPopulationDirector.IsOrdinaryScav(role);
            }
            catch { return false; }
        }

        private static bool IsOrdinaryScavWave(BotWaveDataClass wave)
            => wave != null && TerminalPopulationDirector.IsOrdinaryScav(wave.WildSpawnType);

        private static bool IsOrdinaryScavWave(BossLocationSpawn wave)
            => wave != null && TerminalPopulationDirector.IsOrdinaryScav(wave.BossType);

        private static int ScavDemand(BotWaveDataClass wave) => Math.Max(0, wave?.BotsCount ?? 0);
        private static int ScavDemand(BossLocationSpawn wave)
            => wave == null ? 0 : 1 + Math.Max(0, wave.EscortCount);

        private static bool CanAdmitScavs(int wanted)
        {
            if (wanted <= 0) return true;
            // One ordinary-scav batch at a time, tracked independently from special
            // NPC activations. The old global InSpawnProcess check let one stuck RUAF
            // activation suppress every scav wave forever.
            if (PendingScavAdmissions > 0) return false;
            return wanted <= ScavHeadroom
                && wanted <= ScavResidentHeadroom;
        }

        private static bool CanAdmitTotal(int wanted)
            => wanted <= 0 || !TotalCapEnabled || wanted <= TotalHeadroom;

        // Direct authored helpers (currently only the keycard-hangar shortfall
        // topper) cannot use ActivateBotsByWave. Reserve their slots before profile
        // creation so they still participate in the same all-role ceiling.
        internal static bool TryReserveDirectAdmissions(int wanted)
        {
            if (!CanAdmitTotal(wanted)) return false;
            ReserveTotalAdmissions(wanted);
            return true;
        }

        private static bool PlacementPipelineBusy()
        {
            try
            {
                if (BotCreationDataClass.ProfilesLoadingProcess > 0) return true;
                var bc = Comfort.Common.Singleton<IBotGame>.Instantiated
                    ? Comfort.Common.Singleton<IBotGame>.Instance.BotsController : null;
                if (bc?.BotSpawner == null) return false;
                return bc.BotSpawner.InSpawnProcess > 0 || bc.BotSpawner.BotCreator.BotsLoading > 0;
            }
            catch { return true; }
        }

        private static void LogIgnoreMax(BossLocationSpawn wave)
        {
            if (_ignoreMaxLogged) return;
            _ignoreMaxLogged = true;
            Plugin.Log.LogInfo($"[SpawnGate] authored IgnoreMaxBots preserved for '{wave?.BossName}' — bypasses EFT's native/scav ceiling after Terminal's total-AI admission check");
        }

        internal static bool Open
        {
            get
            {
                if (!Plugin.HoldBotsForCutscene.Value) return true;
                // 480s failsafe releases NO MATTER WHAT — caps every hold below
                if (_raidStart > 0f && Time.realtimeSinceStartup - _raidStart > 480f) return true;
                // release in the attack cutscene's FINAL stretch (2026-08-17, user
                // design): at-end = visible pop-in after control returns; at-start
                // = a 48s window where early firefights bleed through the combat
                // audio path (the cutscene mute covers ambience only). the last
                // ~10s + the fade-out cover the staggered placement with a fight
                // window of seconds. SPACE-skip still releases instantly via
                // FinishedAt.
                if (TerminalAttackCutscene.PlayingNow && _attackStartedAt < 0f)
                    _attackStartedAt = Time.realtimeSinceStartup;
                const float AttackLen = 47.9f, ReleaseLead = 10f;
                bool cutsceneDone = TerminalAttackCutscene.FinishedAt > 0f
                    || !TerminalAttackCutscene.Available
                    || (_attackStartedAt > 0f && Time.realtimeSinceStartup - _attackStartedAt > AttackLen - ReleaseLead);
                if (!cutsceneDone) return false;
                // ALSO wait for the preset profiles (kept from the shelved tushonka
                // work — witness raids proved released waves die on BossSpawnerClass.
                // method_2's FIRST line `if (!IsProfilesLoaded) return false` and
                // crawl BSG's retry queue: the 'ruaf missing at start' symptom, and
                // it exists on the OLD bundle too). release into a loaded spawner
                // and the first volley actually places.
                try
                {
                    var bc = Comfort.Common.Singleton<IBotGame>.Instantiated
                        ? Comfort.Common.Singleton<IBotGame>.Instance.BotsController : null;
                    var sp = bc != null ? bc.BotSpawner : null;
                    if (sp != null && !sp.IsProfilesLoaded) return false;
                }
                catch { }
                return true;
            }
        }

        private static void LogHold(string what)
        {
            if (_holdLogged) return;
            _holdLogged = true;
            Plugin.Log.LogInfo($"[SpawnGate] holding bot spawns until the attack cutscene ends (first held: {what})");
        }

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_ArmGate
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On) return;
                _raidStart = Time.realtimeSinceStartup;
                _attackStartedAt = -1f;
                _holdLogged = false;
                _botlessBlockLogged = false;
                _ignoreMaxLogged = false;
                _lifetimeCapLogged = false;
                _lifetimeBudgetClosed = false;
                _releasingQueuedWave = false;
                _placementsCommittedThisRaid = 0;
                _lastPopLog = 0f;
                _scavReservations.Clear();
                _totalReservations.Clear();
                _waves.Clear();
                _bosses.Clear();
                if (BotsDisabled)
                {
                    Plugin.Log.LogWarning("[BotControl] Terminal botless diagnostic armed — all runtime spawn paths blocked");
                    return;
                }
                if (CapEnabled)
                {
                    Plugin.Log.LogWarning($"[SpawnGate] early ordinary-scav admission armed at {Plugin.MaxAliveScavs.Value} living scavs — profile creation is serialized");
                }
                if (TotalCapEnabled)
                    Plugin.Log.LogWarning($"[SpawnGate] total living-AI ceiling armed at {Plugin.MaxAliveBots.Value}; authored special waves wait intact and faction recyclers get first refusal");
                if (ScavResidentCapEnabled)
                    Plugin.Log.LogWarning($"[SpawnGate] ordinary-scav resident-resource ceiling armed at {Plugin.MaxResidentScavs.Value} living + uncleaned bodies; corpse retirement refunds capacity");
                if (LifetimeCapEnabled)
                    Plugin.Log.LogWarning($"[SpawnGate] lifetime budget armed at {Plugin.MaxBotsCreatedPerRaid.Value} total bot placements — deaths do not refund slots");
                // EITHER feature needs the host: with the ceiling on but the cutscene
                // hold off, no host means nothing ever drains the queue and the map
                // stays empty for the whole raid
                if (Plugin.HoldBotsForCutscene.Value || CapEnabled || TotalCapEnabled || ScavResidentCapEnabled || LifetimeCapEnabled)
                    new GameObject("Terminal_SpawnGate").AddComponent<FlushHost>();
            }
        }

        [HarmonyPatch(typeof(BotsController), nameof(BotsController.ActivateBotsByWave), typeof(BotWaveDataClass))]
        internal static class Patch_GateWaves
        {
            [HarmonyPrefix]
            private static bool Prefix(BotsController __instance, ref BotWaveDataClass wave, ref Task __result)
            {
                if (!TerminalGate.On) return true;
                if (BotsDisabled)
                {
                    LogBotlessBlock("assault wave");
                    __result = Task.CompletedTask;
                    return false;
                }
                if (_releasingQueuedWave)
                    return true;
                if (!LifetimeBudgetOpen)
                {
                    LogLifetimeHold();
                    __result = Task.CompletedTask;
                    return false;
                }

                // With no all-role ceiling, non-scav plain waves retain their authored
                // behavior. With it enabled they join the same early queue so several
                // callbacks cannot all observe the same free slots in one frame.
                if (Open && !IsOrdinaryScavWave(wave) && !TotalCapEnabled) return true;

                // Try recycling before queuing. A complete reuse creates no profile;
                // partial reuse shrinks the queued wave to its real shortfall.
                if (Open && TerminalPopulationDirector.TryPreparePlainWave(__instance, ref wave))
                {
                    __result = Task.CompletedTask;
                    return false;
                }

                // Every ordinary scav wave enters our queue, even when headroom exists.
                // FlushHost serializes profile generation, preventing a frame of many
                // simultaneous event callbacks from all observing the same empty map.
                _waves.Add((__instance, wave));
                if (!Open) LogHold("assault wave");
                else LogPopHold();
                __result = Task.CompletedTask;
                return false;
            }
        }

        [HarmonyPatch(typeof(BotsController), nameof(BotsController.ActivateBotsByWave), typeof(BossLocationSpawn))]
        internal static class Patch_GateBosses
        {
            [HarmonyPrefix]
            private static bool Prefix(BotsController __instance, ref BossLocationSpawn wave)
            {
                if (!TerminalGate.On) return true;
                if (BotsDisabled)
                {
                    LogBotlessBlock("boss/event wave");
                    return false;
                }
                if (_releasingQueuedWave)
                    return true;
                if (Open && wave != null && !IsOrdinaryScavWave(wave) && LifetimeBudgetOpen)
                {
                    if (TerminalPopulationDirector.IsRuaf(wave.BossType)
                        && TerminalPopulationDirector.TryPrepareRuafWave(__instance, ref wave))
                        return false;
                    if (TerminalPopulationDirector.IsBlackDivision(wave.BossType)
                        && TerminalPopulationDirector.TryPrepareBlackDivisionWave(__instance, ref wave))
                        return false;

                    // IgnoreMaxBots still bypasses EFT's role-blind native ceiling, but
                    // not Terminal's all-role safety ceiling. Queue the intact authored
                    // squad until enough living slots exist.
                    if (!TotalCapEnabled)
                    {
                        if (wave.IgnoreMaxBots) LogIgnoreMax(wave);
                        return true;
                    }
                    _bosses.Add((__instance, wave));
                    LogPopHold();
                    return false;
                }
                if (!LifetimeBudgetOpen)
                {
                    LogLifetimeHold();
                    return false;
                }

                // With the total ceiling disabled, non-scav event waves remain outside
                // the scav-only queue. Otherwise they wait and drain through the path
                // above once the cutscene gate opens.
                if (Open && !IsOrdinaryScavWave(wave) && !TotalCapEnabled) return true;

                if (Open && TerminalPopulationDirector.TryPrepareBossWave(__instance, ref wave)) return false;
                _bosses.Add((__instance, wave));
                if (!Open) LogHold("boss/event wave");
                else LogPopHold();
                return false;
            }
        }

        // The optional all-role lifetime diagnostic remains a final placement budget.
        // Ordinary-scav population control must never happen here: profiles and gear
        // already exist at this point, so refusing placement creates churn and can strand
        // the engine's loading bookkeeping.
        [HarmonyPatch(typeof(BotSpawner), nameof(BotSpawner.SpawnBotsInZoneOnPositions))]
        internal static class Patch_FinalLifetimeCap
        {
            [HarmonyPrefix]
            private static bool Prefix(BotSpawner __instance, List<ISpawnPoint> openedPositions,
                BotCreationDataClass data)
            {
                if (!TerminalGate.On) return true;
                if (BotsDisabled)
                {
                    LogBotlessBlock("final placement");
                    return false;
                }
                if (openedPositions == null || openedPositions.Count == 0) return true;

                if (LifetimeCapEnabled
                    && _placementsCommittedThisRaid + openedPositions.Count > Plugin.MaxBotsCreatedPerRaid.Value)
                {
                    // Do not repeatedly build profiles/gear for batches that can never
                    // fit the remaining lifetime slots. Closing may leave the raid one
                    // short when the next authored group is larger than the remainder;
                    // that is preferable to loading and discarding unlimited groups.
                    _lifetimeBudgetClosed = true;
                    LogLifetimeHold();
                    return false;
                }

                // BotSpawner.method_7 increments InSpawnProcess synchronously as soon
                // as this prefix returns. Count the accepted batch now so a death or a
                // second async wave cannot refund/race the lifetime reservation.
                if (LifetimeCapEnabled)
                {
                    _placementsCommittedThisRaid += openedPositions.Count;
                    if (_placementsCommittedThisRaid >= Plugin.MaxBotsCreatedPerRaid.Value)
                        _lifetimeBudgetClosed = true;
                }
                return true;
            }
        }

        // Non-wave scav replacement is the map's infinite churn path. Intercept it at
        // BotsController, before BotCreationDataClass.Create. NonWavesSpawnScenario
        // retries on its own schedule, so a rejected request needs no queue.
        [HarmonyPatch(typeof(BotsController), nameof(BotsController.ActivateBotsWithoutWave))]
        internal static class Patch_AdmitWithoutWave
        {
            [HarmonyPrefix]
            private static bool Prefix(int count, IGetProfileData data)
            {
                if (!TerminalGate.On) return true;
                if (BotsDisabled)
                {
                    LogBotlessBlock("non-wave profile admission");
                    return false;
                }
                if (!Open || !LifetimeBudgetOpen) return false;
                int wanted = Math.Max(1, count);
                if (!IsOrdinaryScavProfile(data))
                {
                    if (!CanAdmitTotal(wanted)) { LogPopHold(); return false; }
                    ReserveTotalAdmissions(wanted);
                    return true;
                }
                if (CanAdmitScavs(wanted) && CanAdmitTotal(wanted))
                {
                    ReserveScavAdmissions(wanted);
                    ReserveTotalAdmissions(wanted);
                    return true;
                }
                LogPopHold();
                return false;
            }
        }

        // Once an ordinary scav request has passed the early role-aware gate, do not
        // send it through EFT's second MaxBots check. That native check counts every
        // special NPC too and would retain the already-generated scav profiles in
        // SpawnDelaysService. This patch never rejects a placement or touches counters.
        [HarmonyPatch(typeof(BotSpawner), nameof(BotSpawner.TryToSpawnInZoneInner))]
        internal static class Patch_BypassNativeCapForAdmittedScavs
        {
            [HarmonyPrefix]
            private static void Prefix(BotCreationDataClass data, ref bool withCheckMinMax)
            {
                if (!TerminalGate.On || data == null) return;
                if (IsOrdinaryScavProfile(data._profileData)) withCheckMinMax = false;
            }
        }

        // the non-wave scenario spawns through its own Update loop, not the wave
        // choke — freeze it only while the cutscene/lifetime gate is closed. Its
        // role-aware requests are admitted or rejected by Patch_AdmitWithoutWave.
        [HarmonyPatch(typeof(NonWavesSpawnScenario), nameof(NonWavesSpawnScenario.Update))]
        internal static class Patch_GateNonWaves
        {
            // no queue here — the scenario drives its own Update, so skipping the
            // tick IS the hold; it retries next frame and flows again under the cap
            [HarmonyPrefix]
            private static bool Prefix()
            {
                if (!TerminalGate.On) return true;
                if (BotsDisabled)
                {
                    LogBotlessBlock("non-wave scenario");
                    return false;
                }
                if (!LifetimeBudgetOpen)
                {
                    LogLifetimeHold();
                    return false;
                }
                return Open;
            }
        }

        internal class FlushHost : MonoBehaviour
        {
            private float _next;

            private void Update()
            {
                if (BotsDisabled)
                {
                    _waves.Clear();
                    _bosses.Clear();
                    Destroy(gameObject);
                    return;
                }
                if (Time.realtimeSinceStartup < _next) return;
                bool cap = CapEnabled || TotalCapEnabled || ScavResidentCapEnabled;
                // faster tick with the ceiling on — the queue drains one wave per
                // tick, so the cadence sets how quickly kills make room
                _next = Time.realtimeSinceStartup + (cap ? 0.5f : 1f);
                if (!Open) return;

                if (!LifetimeBudgetOpen)
                {
                    LogLifetimeHold();
                    _waves.Clear();
                    _bosses.Clear();
                    return;
                }

                if (!cap)
                {
                    // legacy path: cutscene gate only, release the backlog wholesale
                    if (_waves.Count + _bosses.Count > 0)
                    {
                        Plugin.Log.LogInfo($"[SpawnGate] cutscene done — releasing {_waves.Count} wave(s) + {_bosses.Count} boss wave(s)");
                        foreach (var (c, w) in _waves)
                            try { _ = c.ActivateBotsByWave(w); } catch (Exception e) { Plugin.Log.LogWarning($"[SpawnGate] wave release failed: {e.Message}"); }
                        foreach (var (c, w) in _bosses)
                            try { c.ActivateBotsByWave(w); } catch (Exception e) { Plugin.Log.LogWarning($"[SpawnGate] boss release failed: {e.Message}"); }
                        _waves.Clear();
                        _bosses.Clear();
                    }
                    Destroy(gameObject); // gate is open — everything new passes straight through
                    return;
                }

                // Ceiling on: the host lives for the whole raid and releases one queued
                // request per tick. Ordinary scav requests are serialized until the
                // entire profile/activation pipeline is idle; special waves bypass it.
                if (_waves.Count + _bosses.Count == 0) return;
                DrainOne();
            }

            private static void DrainOne()
            {
                try
                {
                    // Cutscene-held special waves must never starve behind the scav
                    // budget. Release the first non-scav/IgnoreMaxBots boss request,
                    // then a non-scav plain request, before considering scav demand.
                    int specialBoss = -1;
                    for (int i = 0; i < _bosses.Count; i++)
                    {
                        if (!IsOrdinaryScavWave(_bosses[i].w))
                        {
                            specialBoss = i;
                            break;
                        }
                    }
                    if (specialBoss >= 0)
                    {
                        var (c, original) = _bosses[specialBoss];
                        var w = original;
                        if (TerminalPopulationDirector.IsRuaf(w.BossType)
                            && TerminalPopulationDirector.TryPrepareRuafWave(c, ref w))
                        {
                            _bosses.RemoveAt(specialBoss);
                            LogReleased("recycled RUAF wave");
                            return;
                        }
                        if (TerminalPopulationDirector.IsBlackDivision(w.BossType)
                            && TerminalPopulationDirector.TryPrepareBlackDivisionWave(c, ref w))
                        {
                            _bosses.RemoveAt(specialBoss);
                            LogReleased("recycled Black Division wave");
                            return;
                        }
                        _bosses[specialBoss] = (c, w);
                        int wanted = ScavDemand(w);
                        if (!CanAdmitTotal(wanted)) { LogPopHold(); return; }
                        _bosses.RemoveAt(specialBoss);
                        if (w.IgnoreMaxBots) LogIgnoreMax(w);
                        ReserveTotalAdmissions(wanted);
                        _releasingQueuedWave = true;
                        try { c.ActivateBotsByWave(w); }
                        finally { _releasingQueuedWave = false; }
                        LogReleased(w.BossName);
                        return;
                    }

                    int specialPlain = -1;
                    for (int i = 0; i < _waves.Count; i++)
                    {
                        if (!IsOrdinaryScavWave(_waves[i].w))
                        {
                            specialPlain = i;
                            break;
                        }
                    }
                    if (specialPlain >= 0)
                    {
                        var (c, w) = _waves[specialPlain];
                        int wanted = ScavDemand(w);
                        if (!CanAdmitTotal(wanted)) { LogPopHold(); return; }
                        _waves.RemoveAt(specialPlain);
                        ReserveTotalAdmissions(wanted);
                        _releasingQueuedWave = true;
                        try { _ = c.ActivateBotsByWave(w); }
                        finally { _releasingQueuedWave = false; }
                        LogReleased("non-scav wave");
                        return;
                    }

                    // Recycler gets first refusal on the oldest queued scav request.
                    // It can satisfy demand even at the live/profile ceilings.
                    if (_bosses.Count > 0)
                    {
                        var (c, original) = _bosses[0];
                        var w = original;
                        if (TerminalPopulationDirector.TryPrepareBossWave(c, ref w))
                        {
                            _bosses.RemoveAt(0);
                            LogReleased("recycled scav boss-wave");
                            return;
                        }
                        _bosses[0] = (c, w);
                        if (!CanAdmitScavs(ScavDemand(w)) || !CanAdmitTotal(ScavDemand(w))) { LogPopHold(); return; }
                        _bosses.RemoveAt(0);
                        // This flag bypasses EFT's role-blind total MaxBots check only
                        // after the ordinary group passed our live/profile budgets.
                        w.IgnoreMaxBots = true;
                        ReserveScavAdmissions(ScavDemand(w));
                        ReserveTotalAdmissions(ScavDemand(w));
                        _releasingQueuedWave = true;
                        try { c.ActivateBotsByWave(w); }
                        finally { _releasingQueuedWave = false; }
                        LogReleased(w.BossName);
                        return;
                    }

                    if (_waves.Count > 0)
                    {
                        var (c, original) = _waves[0];
                        var w = original;
                        if (TerminalPopulationDirector.TryPreparePlainWave(c, ref w))
                        {
                            _waves.RemoveAt(0);
                            LogReleased("recycled scav wave");
                            return;
                        }
                        _waves[0] = (c, w);
                        if (!CanAdmitScavs(ScavDemand(w)) || !CanAdmitTotal(ScavDemand(w))) { LogPopHold(); return; }
                        _waves.RemoveAt(0);
                        w.WithCheckMinMax = false;
                        ReserveScavAdmissions(ScavDemand(w));
                        ReserveTotalAdmissions(ScavDemand(w));
                        _releasingQueuedWave = true;
                        try { _ = c.ActivateBotsByWave(w); }
                        finally { _releasingQueuedWave = false; }
                        LogReleased("scav wave");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[SpawnGate] queued release failed: {e.Message}"); }
            }

            private static void LogReleased(string what)
            {
                Plugin.Log.LogDebug($"[SpawnGate] released queued '{what}' (aliveScavs={AliveScavCount()}/{(CapEnabled ? Plugin.MaxAliveScavs.Value.ToString() : "unlimited")}, "
                    + $"aliveTotal={TerminalPopulationDirector.LivingCount}/{(TotalCapEnabled ? Plugin.MaxAliveBots.Value.ToString() : "unlimited")}, pendingTotal={PendingTotalAdmissions}, "
                    + $"residentScavs={TerminalPopulationDirector.ResidentOrdinaryScavCount}/{(ScavResidentCapEnabled ? Plugin.MaxResidentScavs.Value.ToString() : "unlimited")}, "
                    + $"lifetimeCreatedScavs={TerminalPopulationDirector.OrdinaryScavsCreatedCount}, "
                    + $"pendingScavs={PendingScavAdmissions}, {_waves.Count + _bosses.Count} still queued)");
            }
        }
    }
}
