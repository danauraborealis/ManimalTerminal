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
        private static bool _endingStopped;
        private static int _placementsCommittedThisRaid;
        private static float _lastPopLog;
        private static float _lastPacingLog;
        private static float _nextFreshProfileAdmission;
        private static float _pipelineActuallyIdleSince = -1f;
        private static int _duplicateBossCallbacks;
        private sealed class AdmissionReservation
        {
            internal int Remaining;
            internal float SoftExpiresAt;
            internal float HardExpiresAt;
        }
        private static readonly List<AdmissionReservation> _scavReservations = new();
        private static readonly List<AdmissionReservation> _totalReservations = new();
        private static readonly List<(BotsController c, EFT.SpawnWave w, float queuedAt)> _waves = new();
        private sealed class QueuedBoss
        {
            internal BotsController Controller;
            internal BossLocationSpawn Wave;
            // Scenario Update can call ActivateBotsByWave with the same authored object
            // every frame while our gate owns it. Keep that identity even if recycling
            // later replaces Wave with a resized copy.
            internal BossLocationSpawn Source;
            internal float QueuedAt;
        }
        private static readonly List<QueuedBoss> _bosses = new();

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
            _endingStopped = false;
            _placementsCommittedThisRaid = 0;
            _lastPopLog = 0f;
            _lastPacingLog = 0f;
            _nextFreshProfileAdmission = 0f;
            _pipelineActuallyIdleSince = -1f;
            _duplicateBossCallbacks = 0;
            _scavReservations.Clear();
            _totalReservations.Clear();
            _waves.Clear();
            _bosses.Clear();
        }

        internal static void StopForEnding()
        {
            if (_endingStopped) return;
            _endingStopped = true;
            int discarded = _waves.Count + _bosses.Count;
            _waves.Clear();
            _bosses.Clear();
            _scavReservations.Clear();
            _totalReservations.Clear();
            Plugin.Log.LogInfo($"[SpawnGate] ending started — spawn admission closed and {discarded} queued wave(s) discarded");
        }

        // ORDINARY-SCAV ADMISSION CEILING.  This gate acts at BotsController before
        // BotCreationData.Create generates profiles.  The former low-level choke
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
        private static bool FreshProfilePacingEnabled
            => Plugin.FreshProfileWaveInterval != null && Plugin.FreshProfileWaveInterval.Value > 0f;
        internal static bool BotsDisabled => Plugin.DisableAllBots != null && Plugin.DisableAllBots.Value;

        private static float FreshProfileInterval
        {
            get
            {
                if (!FreshProfilePacingEnabled) return 0f;
                // Count reserved placements as population before they finish loading.
                // Otherwise a slow profile pipeline still reads as an empty map and
                // repeatedly takes the two-second catch-up path.
                int effectivePopulation = TerminalPopulationDirector.LivingCount + PendingTotalAdmissions;
                if (Plugin.LowPopulationWaveThreshold != null
                    && Plugin.LowPopulationWaveInterval != null
                    && effectivePopulation <= Plugin.LowPopulationWaveThreshold.Value)
                    return Plugin.LowPopulationWaveInterval.Value;
                return Plugin.FreshProfileWaveInterval.Value;
            }
        }

        private static bool FreshProfileAdmissionOpen
            => !FreshProfilePacingEnabled || Time.realtimeSinceStartup >= _nextFreshProfileAdmission;

        private static void NoteFreshProfileAdmission()
        {
            if (!FreshProfilePacingEnabled) return;
            _nextFreshProfileAdmission = Time.realtimeSinceStartup + FreshProfileInterval;
        }

        private static void LogPacingHold()
        {
            if (!FreshProfilePacingEnabled || Time.realtimeSinceStartup - _lastPacingLog < 10f) return;
            _lastPacingLog = Time.realtimeSinceStartup;
            float remaining = Mathf.Max(0f, _nextFreshProfileAdmission - Time.realtimeSinceStartup);
            Plugin.Log.LogInfo($"[SpawnPacing] fresh-profile admission waiting {remaining:0.0}s "
                + $"(alive={TerminalPopulationDirector.LivingCount}, pending={PendingTotalAdmissions}, queued={_waves.Count + _bosses.Count}); recycled fulfillment remains immediate");
        }

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
                ExpireReservations(_totalReservations, "total");
                int count = 0;
                foreach (var r in _totalReservations) count += r.Remaining;
                return count;
            }
        }

        private static void ExpireScavReservations()
        {
            ExpireReservations(_scavReservations, "scav");
        }

        private static void ExpireReservations(List<AdmissionReservation> reservations, string label)
        {
            float now = Time.realtimeSinceStartup;
            bool pipelineChecked = false;
            bool pipelineBusy = false;
            for (int i = reservations.Count - 1; i >= 0; i--)
            {
                var reservation = reservations[i];
                if (reservation.Remaining <= 0)
                {
                    reservations.RemoveAt(i);
                    continue;
                }
                if (now < reservation.SoftExpiresAt) continue;

                if (!pipelineChecked)
                {
                    pipelineBusy = PlacementPipelineBusy();
                    pipelineChecked = true;
                }
                // Profile creation on this map routinely takes longer than 30s. Keep
                // its slots reserved while EFT still reports work, but retain a hard
                // three-minute escape hatch for an actually wedged custom-AI job.
                if (pipelineBusy && now < reservation.HardExpiresAt)
                {
                    reservation.SoftExpiresAt = Math.Min(reservation.HardExpiresAt, now + 15f);
                    continue;
                }

                string why = now >= reservation.HardExpiresAt ? "hard timeout" : "pipeline idle";
                Plugin.Log.LogWarning($"[SpawnGate] released stale {label} admission reservation ({reservation.Remaining} placement(s), {why})");
                reservations.RemoveAt(i);
            }
        }

        private static void ReserveScavAdmissions(int count)
        {
            if (count <= 0) return;
            ExpireScavReservations();
            float now = Time.realtimeSinceStartup;
            _scavReservations.Add(new AdmissionReservation
            {
                Remaining = count,
                SoftExpiresAt = now + 15f,
                HardExpiresAt = now + 90f,
            });
        }

        private static void ReserveTotalAdmissions(int count)
        {
            if (count <= 0 || !TotalCapEnabled) return;
            ExpireReservations(_totalReservations, "total");
            float now = Time.realtimeSinceStartup;
            _totalReservations.Add(new AdmissionReservation
            {
                Remaining = count,
                SoftExpiresAt = now + 15f,
                HardExpiresAt = now + 90f,
            });
        }

        internal static void NoteBotCreated(bool ordinaryScav)
        {
            ConsumeReservation(_totalReservations);
            if (ordinaryScav) ConsumeReservation(_scavReservations);
        }

        private static void ConsumeReservation(List<AdmissionReservation> reservations)
        {
            ExpireReservations(reservations,
                ReferenceEquals(reservations, _scavReservations) ? "scav" : "total");
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
                + $"pendingScavs={PendingScavAdmissions}, nativePipelineBusy={PlacementPipelineBusy()}, duplicateBossCallbacksSuppressed={_duplicateBossCallbacks}) "
                + $"— {_waves.Count + _bosses.Count} wave(s) queued");
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

        private static bool IsOrdinaryScavWave(EFT.SpawnWave wave)
            => wave != null && TerminalPopulationDirector.IsOrdinaryScav(wave.WildSpawnType);

        private static bool IsOrdinaryScavWave(BossLocationSpawn wave)
            => wave != null && TerminalPopulationDirector.IsOrdinaryScav(wave.BossType);

        private static int ScavDemand(EFT.SpawnWave wave) => Math.Max(0, wave?.BotsCount ?? 0);
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
            if (!FreshProfileAdmissionOpen) { LogPacingHold(); return false; }
            ReserveTotalAdmissions(wanted);
            NoteFreshProfileAdmission();
            return true;
        }

        private static bool PlacementPipelineBusy()
        {
            try
            {
                var bc = Comfort.Common.Singleton<IBotGame>.Instantiated
                    ? Comfort.Common.Singleton<IBotGame>.Instance.BotsController : null;
                if (bc?.BotSpawner == null) return false;
                var spawner = bc.BotSpawner;
                if (spawner._inSpawnProcess < 0)
                {
                    Plugin.Log.LogWarning($"[SpawnGate] clamped negative native activation counter ({spawner._inSpawnProcess} -> 0)");
                    spawner._inSpawnProcess = 0;
                }
                bool actualWork = BotCreationData.ProfilesLoadingProcess > 0
                    || spawner._botCreator.BotsLoading > 0
                    || spawner._botCreator.BundlesLoading > 0
                    || spawner.SpawnDelaysService.WaitCount > 0;
                if (actualWork)
                {
                    _pipelineActuallyIdleSince = -1f;
                    return true;
                }

                // InSpawnProcess is incremented before ActivateBot and normally
                // decremented by its completion callback. Custom-AI activation can
                // abandon that callback without leaving any real loader/task behind;
                // this raid reached 12 forever and starved every later event wave.
                // Give a legitimate callback ten quiet seconds, then repair only the
                // orphaned counter. All authoritative workload counters above are idle.
                if (spawner._inSpawnProcess > 0)
                {
                    float now = Time.realtimeSinceStartup;
                    if (_pipelineActuallyIdleSince < 0f)
                    {
                        _pipelineActuallyIdleSince = now;
                        return true;
                    }
                    if (now - _pipelineActuallyIdleSince < 10f) return true;
                    int stale = spawner._inSpawnProcess;
                    spawner._inSpawnProcess = 0;
                    _pipelineActuallyIdleSince = -1f;
                    Plugin.Log.LogWarning($"[SpawnGate] repaired stale native activation counter ({stale} orphaned placement(s); all real loaders idle)");
                }
                else _pipelineActuallyIdleSince = -1f;
                return false;
            }
            catch { return true; }
        }

        private static void QueueBoss(BotsController controller, BossLocationSpawn wave)
        {
            if (wave == null) return;
            foreach (var queued in _bosses)
                if (ReferenceEquals(queued.Source, wave))
                {
                    _duplicateBossCallbacks++;
                    return;
                }
            _bosses.Add(new QueuedBoss
            {
                Controller = controller,
                Wave = wave,
                Source = wave,
                QueuedAt = Time.realtimeSinceStartup,
            });
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
                if (!TerminalCoop.Authority) return false;
                if (!Plugin.HoldBotsForCutscene.Value) return true;
                if (TerminalCoop.Active) return TerminalCoop.AttackReleased || (TerminalCoop.AttackStarted >= 0 && TerminalCoop.Now - TerminalCoop.AttackStarted >= 37.9);
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
                // work — witness raids proved released waves die on BotBossSpawn.
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
                if (!TerminalGate.On || !TerminalCoop.Authority) return;
                TerminalAILimitFirewall.EnforceAtRaidStart();
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
                _lastPacingLog = 0f;
                _nextFreshProfileAdmission = 0f;
                _pipelineActuallyIdleSince = -1f;
                _duplicateBossCallbacks = 0;
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
                if (FreshProfilePacingEnabled)
                    Plugin.Log.LogWarning($"[SpawnPacing] fresh-profile waves paced at {Plugin.FreshProfileWaveInterval.Value:0.0}s, "
                        + $"catching up at {Plugin.LowPopulationWaveInterval.Value:0.0}s while living + admitted AI <= {Plugin.LowPopulationWaveThreshold.Value}; recycled waves bypass pacing");
                if (LifetimeCapEnabled)
                    Plugin.Log.LogWarning($"[SpawnGate] lifetime budget armed at {Plugin.MaxBotsCreatedPerRaid.Value} total bot placements — deaths do not refund slots");
                // EITHER feature needs the host: with the ceiling on but the cutscene
                // hold off, no host means nothing ever drains the queue and the map
                // stays empty for the whole raid
                if (Plugin.HoldBotsForCutscene.Value || CapEnabled || TotalCapEnabled || ScavResidentCapEnabled
                    || FreshProfilePacingEnabled || LifetimeCapEnabled)
                    new GameObject("Terminal_SpawnGate").AddComponent<FlushHost>();
            }
        }

        [HarmonyPatch(typeof(BotsController), nameof(BotsController.ActivateBotsByWave), typeof(EFT.SpawnWave))]
        internal static class Patch_GateWaves
        {
            [HarmonyPrefix]
            private static bool Prefix(BotsController __instance, ref EFT.SpawnWave wave, ref Task __result)
            {
                if (!TerminalGate.On) return true;
                if (!TerminalCoop.Authority || _endingStopped) { __result = Task.CompletedTask; return false; }
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
                if (Open && !IsOrdinaryScavWave(wave) && !TotalCapEnabled && !FreshProfilePacingEnabled) return true;

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
                _waves.Add((__instance, wave, Time.realtimeSinceStartup));
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
                if (!TerminalCoop.Authority || _endingStopped) return false;
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
                    if (!TotalCapEnabled && !FreshProfilePacingEnabled)
                    {
                        if (wave.IgnoreMaxBots) LogIgnoreMax(wave);
                        return true;
                    }
                    QueueBoss(__instance, wave);
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
                if (Open && !IsOrdinaryScavWave(wave) && !TotalCapEnabled && !FreshProfilePacingEnabled) return true;

                if (Open && TerminalPopulationDirector.TryPrepareBossWave(__instance, ref wave)) return false;
                QueueBoss(__instance, wave);
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
                BotCreationData data)
            {
                if (!TerminalGate.On) return true;
                if (!TerminalCoop.Authority || _endingStopped) return false;
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
        // BotsController, before BotCreationData.Create. NonWavesSpawnScenario
        // retries on its own schedule, so a rejected request needs no queue.
        [HarmonyPatch(typeof(BotsController), nameof(BotsController.ActivateBotsWithoutWave))]
        internal static class Patch_AdmitWithoutWave
        {
            [HarmonyPrefix]
            private static bool Prefix(int count, IGetProfileData data)
            {
                if (!TerminalGate.On) return true;
                if (!TerminalCoop.Authority || _endingStopped) return false;
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
                    if (!FreshProfileAdmissionOpen) { LogPacingHold(); return false; }
                    ReserveTotalAdmissions(wanted);
                    NoteFreshProfileAdmission();
                    return true;
                }
                if (CanAdmitScavs(wanted) && CanAdmitTotal(wanted))
                {
                    if (!FreshProfileAdmissionOpen) { LogPacingHold(); return false; }
                    ReserveScavAdmissions(wanted);
                    ReserveTotalAdmissions(wanted);
                    NoteFreshProfileAdmission();
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
            private static void Prefix(BotCreationData data, ref bool withCheckMinMax)
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
                if (!TerminalCoop.Authority || _endingStopped) return false;
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
                if (_endingStopped)
                {
                    _waves.Clear();
                    _bosses.Clear();
                    Destroy(gameObject);
                    return;
                }
                if (BotsDisabled)
                {
                    _waves.Clear();
                    _bosses.Clear();
                    Destroy(gameObject);
                    return;
                }
                if (Time.realtimeSinceStartup < _next) return;
                bool managed = CapEnabled || TotalCapEnabled || ScavResidentCapEnabled || FreshProfilePacingEnabled;
                // faster tick with the ceiling on — the queue drains one wave per
                // tick, so the cadence sets how quickly kills make room
                _next = Time.realtimeSinceStartup + (managed ? 0.5f : 1f);
                if (!Open) return;

                if (!LifetimeBudgetOpen)
                {
                    LogLifetimeHold();
                    _waves.Clear();
                    _bosses.Clear();
                    return;
                }

                if (!managed)
                {
                    // legacy path: cutscene gate only, release the backlog wholesale
                    if (_waves.Count + _bosses.Count > 0)
                    {
                        Plugin.Log.LogInfo($"[SpawnGate] cutscene done — releasing {_waves.Count} wave(s) + {_bosses.Count} boss wave(s)");
                        foreach (var (c, w, _) in _waves)
                            try { _ = c.ActivateBotsByWave(w); } catch (Exception e) { Plugin.Log.LogWarning($"[SpawnGate] wave release failed: {e.Message}"); }
                        foreach (var queued in _bosses)
                            try { queued.Controller.ActivateBotsByWave(queued.Wave); } catch (Exception e) { Plugin.Log.LogWarning($"[SpawnGate] boss release failed: {e.Message}"); }
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
                    // budget. A special request which cannot fit must ALSO not pin all
                    // later special encounters: the 08-24 witness raid sat at 21/24
                    // with 24 waves queued because the oldest intact group needed four
                    // slots. Give its faction recycler first refusal, then rotate that
                    // still-blocked request to the back so another authored squad gets
                    // a chance on the next half-second drain tick.
                    bool blockedSpecial = false;
                    int specialBoss = -1;
                    for (int i = 0; i < _bosses.Count; i++)
                    {
                        if (!IsOrdinaryScavWave(_bosses[i].Wave))
                        {
                            specialBoss = i;
                            break;
                        }
                    }
                    if (specialBoss >= 0)
                    {
                        var queued = _bosses[specialBoss];
                        var c = queued.Controller;
                        var w = queued.Wave;
                        if (TerminalPopulationDirector.IsRuaf(w.BossType)
                            && TerminalPopulationDirector.TryPrepareRuafWave(c, ref w))
                        {
                            _bosses.RemoveAt(specialBoss);
                            LogReleased("recycled RUAF wave", queued.QueuedAt, false);
                            return;
                        }
                        if (TerminalPopulationDirector.IsBlackDivision(w.BossType)
                            && TerminalPopulationDirector.TryPrepareBlackDivisionWave(c, ref w))
                        {
                            _bosses.RemoveAt(specialBoss);
                            LogReleased("recycled Black Division wave", queued.QueuedAt, false);
                            return;
                        }
                        queued.Wave = w;
                        int wanted = ScavDemand(w);
                        if (!CanAdmitTotal(wanted))
                        {
                            _bosses.RemoveAt(specialBoss);
                            _bosses.Add(queued);
                            blockedSpecial = true;
                        }
                        else if (FreshProfileAdmissionOpen)
                        {
                            _bosses.RemoveAt(specialBoss);
                            if (w.IgnoreMaxBots) LogIgnoreMax(w);
                            ReserveTotalAdmissions(wanted);
                            NoteFreshProfileAdmission();
                            _releasingQueuedWave = true;
                            try { c.ActivateBotsByWave(w); }
                            finally { _releasingQueuedWave = false; }
                            LogReleased(w.BossName, queued.QueuedAt, true);
                            return;
                        }
                        else LogPacingHold();
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
                        var (c, w, queuedAt) = _waves[specialPlain];
                        int wanted = ScavDemand(w);
                        if (!CanAdmitTotal(wanted))
                        {
                            _waves.RemoveAt(specialPlain);
                            _waves.Add((c, w, queuedAt));
                            blockedSpecial = true;
                        }
                        else if (FreshProfileAdmissionOpen)
                        {
                            _waves.RemoveAt(specialPlain);
                            ReserveTotalAdmissions(wanted);
                            NoteFreshProfileAdmission();
                            _releasingQueuedWave = true;
                            try { _ = c.ActivateBotsByWave(w); }
                            finally { _releasingQueuedWave = false; }
                            LogReleased("non-scav wave", queuedAt, true);
                            return;
                        }
                        else LogPacingHold();
                    }

                    // Preserve special-wave priority. We rotated the blocked heads so
                    // the next tick tries different encounters, but do not spend their
                    // last few slots on replacement scavs in the meantime.
                    if (blockedSpecial) { LogPopHold(); return; }

                    // Recycler gets first refusal on the oldest queued scav request.
                    // It can satisfy demand even at the live/profile ceilings.
                    if (_bosses.Count > 0)
                    {
                        var queued = _bosses[0];
                        var c = queued.Controller;
                        var w = queued.Wave;
                        if (TerminalPopulationDirector.TryPrepareBossWave(c, ref w))
                        {
                            _bosses.RemoveAt(0);
                            LogReleased("recycled scav boss-wave", queued.QueuedAt, false);
                            return;
                        }
                        queued.Wave = w;
                        if (!CanAdmitScavs(ScavDemand(w)) || !CanAdmitTotal(ScavDemand(w))) { LogPopHold(); return; }
                        if (!FreshProfileAdmissionOpen) { LogPacingHold(); return; }
                        _bosses.RemoveAt(0);
                        // This flag bypasses EFT's role-blind total MaxBots check only
                        // after the ordinary group passed our live/profile budgets.
                        w.IgnoreMaxBots = true;
                        ReserveScavAdmissions(ScavDemand(w));
                        ReserveTotalAdmissions(ScavDemand(w));
                        NoteFreshProfileAdmission();
                        _releasingQueuedWave = true;
                        try { c.ActivateBotsByWave(w); }
                        finally { _releasingQueuedWave = false; }
                        LogReleased(w.BossName, queued.QueuedAt, true);
                        return;
                    }

                    if (_waves.Count > 0)
                    {
                        var (c, original, queuedAt) = _waves[0];
                        var w = original;
                        if (TerminalPopulationDirector.TryPreparePlainWave(c, ref w))
                        {
                            _waves.RemoveAt(0);
                            LogReleased("recycled scav wave", queuedAt, false);
                            return;
                        }
                        _waves[0] = (c, w, queuedAt);
                        if (!CanAdmitScavs(ScavDemand(w)) || !CanAdmitTotal(ScavDemand(w))) { LogPopHold(); return; }
                        if (!FreshProfileAdmissionOpen) { LogPacingHold(); return; }
                        _waves.RemoveAt(0);
                        w.WithCheckMinMax = false;
                        ReserveScavAdmissions(ScavDemand(w));
                        ReserveTotalAdmissions(ScavDemand(w));
                        NoteFreshProfileAdmission();
                        _releasingQueuedWave = true;
                        try { _ = c.ActivateBotsByWave(w); }
                        finally { _releasingQueuedWave = false; }
                        LogReleased("scav wave", queuedAt, true);
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[SpawnGate] queued release failed: {e.Message}"); }
            }

            private static void LogReleased(string what, float queuedAt, bool generatedProfiles)
            {
                float age = Mathf.Max(0f, Time.realtimeSinceStartup - queuedAt);
                float next = FreshProfilePacingEnabled
                    ? Mathf.Max(0f, _nextFreshProfileAdmission - Time.realtimeSinceStartup)
                    : 0f;
                Plugin.Log.LogDebug($"[SpawnGate] released queued '{what}' age={age:0.0}s profiles={(generatedProfiles ? "fresh" : "recycled")} nextFresh={next:0.0}s "
                    + $"(aliveScavs={AliveScavCount()}/{(CapEnabled ? Plugin.MaxAliveScavs.Value.ToString() : "unlimited")}, "
                    + $"aliveTotal={TerminalPopulationDirector.LivingCount}/{(TotalCapEnabled ? Plugin.MaxAliveBots.Value.ToString() : "unlimited")}, pendingTotal={PendingTotalAdmissions}, "
                    + $"residentScavs={TerminalPopulationDirector.ResidentOrdinaryScavCount}/{(ScavResidentCapEnabled ? Plugin.MaxResidentScavs.Value.ToString() : "unlimited")}, "
                    + $"lifetimeCreatedScavs={TerminalPopulationDirector.OrdinaryScavsCreatedCount}, "
                    + $"pendingScavs={PendingScavAdmissions}, {_waves.Count + _bosses.Count} still queued)");
            }
        }
    }
}
