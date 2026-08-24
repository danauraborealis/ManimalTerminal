using System;
using System.Collections.Generic;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.Terminal
{
    // BIGBRAIN CREW LAYERS — icebreaker's IceCrewJobs port, trimmed to what terminal
    // needs today. vanilla patrol layers are passive wanderers: the TB-triggered
    // assault waves spawned and strolled their zone routes instead of storming the
    // tier line (2026-08-10 raid). retail's read is "the port pushes back" — so every
    // bot born off a tier event gets a HUNT job with a rush window: sprint at the
    // players until contact, then the normal combat AI owns the fight.
    //
    // priorities carry icebreaker's measured numbers: crew 68 owns the idle slot
    // (above vanilla idle <=65, below combat >=70, stands down on any live threat);
    // rush 110 clears AvoidDanger (ExUsec 100 / PMC 80) so a deploy order actually
    // executes, yielding only on a VISIBLE enemy. hold tier + speaker muting NOT
    // ported — terminal has no scripted ambush squads yet.
    internal static class TerminalCrewJobs
    {
        internal enum Job { None, Guard, Hunt, Siege }

        internal sealed class Rec
        {
            public Job Job;
            public Vector3? RushTo; // set = rush this point, not the player
            public Bounds Zone;
            public float RushUntil;
        }

        // internal: the stage director seeds migration jobs here directly (ByProfile
        // lookup precedes the blackdiv role gate, so seeded recs work for any role)
        internal static readonly Dictionary<string, Rec> ByProfile = new Dictionary<string, Rec>();

        // the last tier event: bots created inside the window get the push job. wave
        // spawn requests go through the server (profile gen takes a while), so the
        // window is generous — retail waves land inside a couple of minutes.
        private static float _eventAt = -1f;
        private const float EventWindow = 180f;

        private static bool _registered;

        internal static void Register()
        {
            if (_registered) return;
            _registered = true;
            // Assault = the scav brain the TB waves run; PMC-family = blackdiv/RUAF
            // (icebreaker's post-release diagnostic: faction mods run the LITERAL
            // 'PMC' brain; ExUsec included for any rogue-brained strays)
            var brains = new List<string> { "Assault", "CursedAssault", "PMC", "PmcBear", "PmcUsec", "ExUsec" };
            BrainManager.AddCustomLayer(typeof(TerminalCrewLayer), brains, 68);
            // 150, not 110 (2026-08-15: the armory squad spawned ~176m out and never
            // arrived — vanilla AvoidDangerLayer runs at 120-140 in every EFT brain
            // we've decompiled, and the spawn area is a scav battlefield, so danger
            // reactions owned the bots over a 110 rush and they shuffled near spawn.
            // a determined rush outranks danger flinching; it still yields the moment
            // an enemy is VISIBLE, so real fights are untouched)
            BrainManager.AddCustomLayer(typeof(TerminalRushLayer), brains, 150);
            // 72: above crew/patrol, below combat — and it self-yields to vanilla combat
            // whenever the enemy is visible, so gunfights never route through us
            BrainManager.AddCustomLayer(typeof(TerminalRuafDefenseLayer), brains, 72);
            Plugin.Log.LogInfo("[CrewLayer] bigbrain layers registered (Assault/PMC-family, crew 68 / ruaf-defense 72 / rush 150)");
        }

        internal static void NoteEvent(string name)
        {
            TerminalPopulationDirector.NoteProgressEvent(name);
            _eventAt = Time.time;
            Plugin.Log.LogDebug($"[CrewLayer] tier event '{name}' noted — bots spawning in the next {EventWindow:0}s push the players");
            if (string.Equals(name, "TB8", StringComparison.OrdinalIgnoreCase))
                TerminalHangarCrew.Arm(); // count the squad shortly after the wave, not on first-guard luck
        }

        // the BD_hold marker volume (retail AIPlaceInfo, the one with NO event logic):
        // black division born inside it guards the hangar — they camp the keycard
        // suitcase instead of joining the push (user call 2026-08-10). resolved lazily;
        // the box is a runtime-rebuilt trigger so it exists by the time bots spawn.
        private static Bounds? _bdHold;
        private static bool _bdHoldLooked;

        // remembered so the hangar top-up can still name the role after its boss dies
        internal static WildSpawnType? LastBlackDivRole;

        private static Bounds? BdHoldBounds()
        {
            if (_bdHoldLooked) return _bdHold;
            _bdHoldLooked = true;
            try
            {
                var go = GameObject.Find("BD_hold");
                var col = go != null ? go.GetComponent<BoxCollider>() : null;
                if (col != null) _bdHold = col.bounds;
            }
            catch { }
            if (_bdHold == null) Plugin.Log.LogWarning("[CrewLayer] BD_hold volume not found — hangar guard duty off");
            return _bdHold;
        }

        // called from BotSpawner.OnBotCreated — event-window bots hunt with a rush
        internal static void OnBotCreated(BotOwner bot)
        {
            try
            {
                if (!TerminalGate.On || !Plugin.EventWavesPush.Value || bot?.ProfileId == null) return;
                if (bot.GetPlayer != null && bot.GetPlayer.IsYourPlayer) return;
                // blackdiv is decided LAZILY in For() — at OnBotCreated the bot isnt at
                // its spawn point yet (2026-08-10 raid: every hangar BD read as outside
                // BD_hold and took the hunt job), the first brain-tick query runs with a
                // settled position
                var r0 = bot.Profile?.Info?.Settings?.Role.ToString() ?? "";
                if (r0.IndexOf("blackdiv", StringComparison.OrdinalIgnoreCase) >= 0) return;
                if (_eventAt < 0f || Time.time - _eventAt > EventWindow) return;
                // RUAF stays off the push list — they're authored NEUTRAL to the player
                // (2026-08-10: the hunt job marched them into the player's face and the
                // encounter turned into a firefight). only the hostile wave roles storm:
                // scav assault + blackdiv. role names resolve because the faction
                // prepatchers inject real enum members.
                var role = bot.Profile?.Info?.Settings?.Role.ToString() ?? "";
                bool hostileWaveRole = role.IndexOf("assault", StringComparison.OrdinalIgnoreCase) >= 0
                                    && role.IndexOf("ruaf", StringComparison.OrdinalIgnoreCase) < 0
                                    && role.IndexOf("vsrf", StringComparison.OrdinalIgnoreCase) < 0;
                if (!hostileWaveRole)
                {
                    Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: role '{role}' not a push role — left to its own brain");
                    return;
                }
                // NATIVE PUSH (user calls 2026-08-18, replaces our Hunt rush for scavs):
                // BSG ships dormant ForceAttack/ForcePersuit brain layers (501/502),
                // activated per-bot through BotsForceAttackEvent. STRICTLY BSG-design:
                // the bot's own Mind.ACTIVE_FORCE_ATTACK_EVENTS gate (from its server
                // bot config) decides eligibility — we never flip it, only start the
                // event for bots BSG authored to answer it.
                try
                {
                    var bc = Comfort.Common.Singleton<IBotGame>.Instance?.BotsController;
                    var ev = bc?.EventsController?.ForceAttackEvent;
                    if (ev == null)
                        Plugin.Log.LogWarning($"[CrewLayer] no ForceAttackEvent controller — {bot.name} left passive");
                    else if (ev.CanActivate(bot))
                    {
                        ev.ExternalStart();
                        ev.BotEventActive(bot);
                        Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: NATIVE FORCE ATTACK (event wave) role='{role}'");
                    }
                    else
                        Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: not force-attack eligible by BSG design — left to its own brain");
                }
                catch (Exception fe) { Plugin.Log.LogWarning($"[CrewLayer] force-attack activation failed for {bot.name}: {fe.Message}"); }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[CrewLayer] assign failed: {e.Message}"); }
        }

        // blackdiv placement check runs here, on the layer's own queries — the bot is
        // placed and ticking by then. 2s grace (icebreaker's FirstSeen pattern) so a
        // mid-teleport first tick cant mis-file them.
        private static readonly Dictionary<string, float> FirstSeen = new Dictionary<string, float>();

        internal static Rec For(BotOwner bot)
        {
            var id = bot?.ProfileId;
            if (id == null) return null;
            if (ByProfile.TryGetValue(id, out var rec)) return rec;

            var role = bot.Profile?.Info?.Settings?.Role.ToString() ?? "";
            if (role.IndexOf("blackdiv", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (LastBlackDivRole == null && bot.Profile?.Info?.Settings != null)
                    LastBlackDivRole = bot.Profile.Info.Settings.Role;
                if (!FirstSeen.TryGetValue(id, out var seen)) { FirstSeen[id] = Time.time; return null; }
                if (Time.time - seen < 2f) return null;
                // EXPLICIT PER-ZONE BEHAVIOR TABLE (retail script per user, 2026-08-11):
                //   Zone1BD1HangarAmbush11 — the TB4 armory push: ~8 BD in staggered
                //     waves CHARGE the player's position, brawl the armory scavs/ruaf.
                //     always hunt, no event-window requirement.
                //   Zone1BD1HangarBD11 — the keycard squad: camp BD_hold, guard the
                //     valbergs. (zone name beats the position race; topper spawns carry
                //     the same zone.)
                //   other *Ambush* zones (GateAmbush13's 950s pair) — hold their post.
                //   everything else — event-window push as before.
                var zname = bot.BotsGroup?.BotZone?.name ?? "";
                if (zname.IndexOf("HangarAmbush", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // RETAIL-SHAPED SIEGE (user 2026-08-15: "rush the armory and patrol
                    // around it while im gearing up" — and the retail 1.0 assembly
                    // confirms this IS the authored behavior: the BD brain ships
                    // BlackDivisionControlBuildingLayer + BlackDivisionPatrolDefenceLayer,
                    // patrol-hold with a per-bot RandomHorizontal(15m) offset around the
                    // defended point). our version: sprint to the TB4 trigger box (the
                    // armory area — no dedicated armory marker exists, but the armory
                    // push trigger sits on it), then roam the surrounding box on the
                    // Guard job. no expiry — they hold until combat pulls them off.
                    var armory = ArmoryBounds();
                    ByProfile[id] = new Rec
                    {
                        Job = Job.Siege, // cover-aware hold, not the box-roam Guard
                        Zone = armory,
                        RushTo = ArmoryNavAnchor(), // navmesh-projected — NEVER bounds.center
                        // 300s, not 90 (2026-08-15: the approach runs through scav
                        // country — every contact pauses the rush for a fight while the
                        // window keeps burning, so all 8 straggled in late. arrival
                        // still ends the rush early; this just survives the brawls)
                        RushUntil = Time.time + 300f,
                    };
                    Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: SIEGE armory (rush TB4 then patrol ring) role='{role}' zone='{zname}' pos={bot.Position}");
                    return ByProfile[id];
                }
                if (zname.IndexOf("HangarBD", StringComparison.OrdinalIgnoreCase) >= 0
                    && BdHoldBounds() is Bounds holdZ)
                {
                    ByProfile[id] = new Rec { Job = Job.Guard, Zone = holdZ };
                    Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: GUARD BD_hold (hangar camp) role='{role}' zone='{zname}' pos={bot.Position}");
                    TerminalHangarCrew.Arm();
                    return ByProfile[id];
                }
                if (zname.IndexOf("Ambush", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ByProfile[id] = new Rec { Job = Job.Guard, Zone = new Bounds(bot.Position, new Vector3(24f, 8f, 24f)) };
                    Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: GUARD ambush post role='{role}' zone='{zname}' pos={bot.Position}");
                    return ByProfile[id];
                }
                // zone-less fallback: position against the hold box with the spawn-mouth
                // reach margin (topper/odd spawn paths)
                if (BdHoldBounds() is Bounds hold)
                {
                    var reach = hold;
                    reach.Expand(new Vector3(30f, 12f, 30f));
                    if (reach.Contains(bot.Position))
                    {
                        ByProfile[id] = new Rec { Job = Job.Guard, Zone = hold };
                        Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: GUARD BD_hold (position fallback) role='{role}' pos={bot.Position}");
                        TerminalHangarCrew.Arm();
                        return ByProfile[id];
                    }
                }
                // DEFAULT BD POSTURE (user 2026-08-15): patrol the spawn area until they
                // see an enemy — combat layers take over on contact. no more event-window
                // player-hunts; the armory HangarAmbush squad is the ONLY siege crew.
                ByProfile[id] = new Rec { Job = Job.Guard, Zone = new Bounds(bot.Position, new Vector3(28f, 8f, 28f)) };
                Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: GUARD patrol post (BD default) role='{role}' pos={bot.Position}");
                return ByProfile[id];
            }
            ByProfile[id] = null;
            return null;
        }

        internal static Bounds? BdHoldBoundsPublic() => BdHoldBounds();

        // the armory is the Administration_03_small building (user 2026-08-15 — the TB4
        // trigger does NOT sit on the armory, it's tripped on the way there; it only
        // serves as a tiebreaker below). anchor + patrol box come from the building's
        // own renderer bounds, expanded so the ring runs AROUND the walls, not inside
        // them. building prefabs are reused across the map, so when several instances
        // share the name, take the one nearest the TB4 trigger (it's on the approach).
        private static Bounds? _armoryBounds;
        private static bool _armoryLooked;
        private static readonly Vector3 Tb4Pos = new Vector3(131.1f, -49.0f, -277.1f);

        // the rush destination must be ON THE NAVMESH (2026-08-15: RushTo was the
        // building's renderer-bounds center — mid-air at half the building's height.
        // GoToPoint rejected it silently every 3s and all 8 siege bots stood at spawn
        // logging identical distances). project the center to the ground, sample the
        // navmesh around it, fall back to the TB4 trigger ground point.
        private static Vector3? _armoryNavAnchor;

        internal static Vector3 ArmoryNavAnchor()
        {
            if (_armoryNavAnchor != null) return _armoryNavAnchor.Value;
            var b = ArmoryBounds();
            var ground = new Vector3(b.center.x, b.min.y + 1f, b.center.z);
            if (NavMesh.SamplePosition(ground, out var hit, 15f, NavMesh.AllAreas))
                _armoryNavAnchor = hit.position;
            else if (NavMesh.SamplePosition(Tb4Pos, out var hit2, 15f, NavMesh.AllAreas))
                _armoryNavAnchor = hit2.position;
            else
                _armoryNavAnchor = Tb4Pos;
            Plugin.Log.LogDebug($"[CrewLayer] armory nav anchor: {_armoryNavAnchor} (bounds center was {b.center})");
            return _armoryNavAnchor.Value;
        }

        internal static Bounds ArmoryBounds()
        {
            if (_armoryLooked && _armoryBounds != null) return _armoryBounds.Value;
            if (!_armoryLooked)
            {
                _armoryLooked = true;
                try
                {
                    Transform best = null;
                    float bestSq = float.MaxValue;
                    foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                    {
                        if (!t || t.name != "Administration_03_small") continue;
                        float sq = (t.position - Tb4Pos).sqrMagnitude;
                        if (sq < bestSq) { bestSq = sq; best = t; }
                    }
                    if (best)
                    {
                        var rends = best.GetComponentsInChildren<Renderer>();
                        if (rends.Length > 0)
                        {
                            var b = rends[0].bounds;
                            foreach (var r in rends) b.Encapsulate(r.bounds);
                            b.Expand(new Vector3(24f, 6f, 24f)); // ring width around the walls
                            _armoryBounds = b;
                            Plugin.Log.LogDebug($"[CrewLayer] armory = Administration_03_small at {best.position} "
                                + $"(dist to TB4 {Mathf.Sqrt(bestSq):0}m), patrol box {b.size}");
                        }
                    }
                    if (_armoryBounds == null)
                        Plugin.Log.LogWarning("[CrewLayer] Administration_03_small not found — armory siege falls back to the TB4 area");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[CrewLayer] armory lookup failed: {e.Message}"); }
            }
            return _armoryBounds ?? new Bounds(Tb4Pos, new Vector3(38f, 10f, 38f));
        }

        internal static void Reset()
        {
            ByProfile.Clear(); FirstSeen.Clear(); _eventAt = -1f; _bdHold = null; _bdHoldLooked = false;
            _armoryBounds = null; _armoryLooked = false; _armoryNavAnchor = null;
            LastBlackDivRole = null;
            TerminalHangarCrew.ResetForRaid();
        }
    }

    internal class TerminalCrewLayer : CustomLayer
    {
        public TerminalCrewLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "TerminalCrew";

        public override bool IsActive()
        {
            if (!TerminalGate.On) return false;
            var rec = TerminalCrewJobs.For(BotOwner);
            if (rec == null || rec.Job == TerminalCrewJobs.Job.None) return false;
            var p = BotOwner.GetPlayer;
            if (!p || p.HealthController == null || !p.HealthController.IsAlive) return false;
            try
            {
                if (BotOwner.Memory != null)
                {
                    if (BotOwner.Memory.IsUnderFire) return false; // rounds landing = real fight, always
                    var ge = BotOwner.Memory.GoalEnemy;
                    if (ge != null)
                    {
                        // SIEGE is LOS-driven (2026-08-15 armory raid: the player holed
                        // up in the no-navmesh gear room; a blanket GoalEnemy yield let
                        // vanilla assault path to the nearest reachable point — the
                        // doorway — and 8 BD stacked it single-file to be gunned down.
                        // retail BD fight you when you PEEK and patrol when you hide):
                        // yield to combat only while the enemy is visible or was seen
                        // in the last 8s; a stale enemy memory hands the bot back to
                        // the patrol ring.
                        if (TerminalCrewJobs.For(BotOwner)?.Job == TerminalCrewJobs.Job.Siege)
                        {
                            if (ge.IsVisible || Time.time - ge.TimeLastSeenReal < 8f) return false;
                        }
                        else return false; // other jobs keep the instant stand-down
                    }
                }
            }
            catch { return false; }
            return true;
        }

        public override Action GetNextAction()
        {
            switch (TerminalCrewJobs.For(BotOwner)?.Job)
            {
                case TerminalCrewJobs.Job.Hunt: return new Action(typeof(TerminalHuntLogic), "hunt the players");
                case TerminalCrewJobs.Job.Siege: return new Action(typeof(TerminalSiegeLogic), "hold the armory");
                default: return new Action(typeof(TerminalGuardLogic), "guard the zone");
            }
        }

        public override bool IsCurrentActionEnding()
        {
            Type want;
            switch (TerminalCrewJobs.For(BotOwner)?.Job)
            {
                case TerminalCrewJobs.Job.Hunt: want = typeof(TerminalHuntLogic); break;
                case TerminalCrewJobs.Job.Siege: want = typeof(TerminalSiegeLogic); break;
                default: want = typeof(TerminalGuardLogic); break;
            }
            return CurrentAction == null || CurrentAction.Type != want;
        }
    }

    // deployment override: while the rush window is open the bot MOVES — no
    // enemy-memory gate (noise must not hold the wave in its spawn zone), yields
    // only on a visible enemy so combat takes over at contact
    internal class TerminalRushLayer : CustomLayer
    {
        public TerminalRushLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "TerminalCrewRush";

        public override bool IsActive()
        {
            if (!TerminalGate.On) return false;
            var rec = TerminalCrewJobs.For(BotOwner);
            if (rec == null || rec.Job == TerminalCrewJobs.Job.None || Time.time >= rec.RushUntil) return false;
            var p = BotOwner.GetPlayer;
            if (!p || p.HealthController == null || !p.HealthController.IsAlive) return false;
            try
            {
                // sight ends the charge — but only CLOSE sight (2026-08-15: a visible
                // scav 100m off through a fence held bots at spawn; a determined rush
                // ignores distant targets and brawls whoever's actually in the way)
                var ge = BotOwner.Memory?.GoalEnemy;
                if (ge != null && ge.IsVisible
                    && (ge.CurrPosition - BotOwner.Position).sqrMagnitude < 40f * 40f) return false;
            }
            catch { }
            return true;
        }

        public override Action GetNextAction()
            => new Action(typeof(TerminalHuntLogic), "rush the players");

        public override bool IsCurrentActionEnding()
            => CurrentAction == null || CurrentAction.Type != typeof(TerminalHuntLogic);
    }

    // roam the assigned box at a watchful walk (unused until something assigns Guard,
    // but the layer contract needs it)
    internal class TerminalGuardLogic : CustomLogic
    {
        private float _next;

        public TerminalGuardLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Update(CustomLayer.ActionData data)
        {
            if (Time.time < _next) return;
            _next = Time.time + UnityEngine.Random.Range(6f, 12f);
            var rec = TerminalCrewJobs.For(BotOwner);
            if (rec == null) return;
            var z = rec.Zone;
            var want = new Vector3(
                UnityEngine.Random.Range(z.min.x, z.max.x),
                z.center.y,
                UnityEngine.Random.Range(z.min.z, z.max.z));
            if (!NavMesh.SamplePosition(want, out var hit, 4f, NavMesh.AllAreas)) return;
            BotOwner.Mover?.SetTargetMoveSpeed(0.5f);
            BotOwner.Mover?.SetPose(1f);
            BotOwner.Mover?.GoToPoint(hit.position, true, 0.6f);
        }

        public override void Stop()
        {
            base.Stop();
            _next = 0f;
        }
    }

    // RUAF SQUAD DEFENSE — VSRFDefence from the retail 1.0 dump, ported to its felt
    // effect. retail's layer (priority 100 in VSRFAssaultLayersStrategy) keeps every
    // squaddie tethered to the boss (MAX_SDIST_FROM_BOSS=900 sq = 30m), holding the
    // GROUP's central cover ("cover in the middle") with heal breaks (PERIOD_TO_HEAL=25,
    // Medecine first-aid/surgery checks in the target layer).
    //
    // scope discipline: we do NOT replace gunfights. this layer activates only in the
    // fight's dead air — the group knows an enemy but THIS bot has no visible target —
    // which is exactly when retail's defense positioning shows (squad collapses onto
    // boss-anchored cover instead of scattering or blind-pursuing). the instant the
    // enemy is visible, IsActive yields and vanilla combat owns the bot.
    internal class TerminalRuafDefenseLayer : CustomLayer
    {
        public TerminalRuafDefenseLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "RuafDefense";

        public override bool IsActive()
        {
            try
            {
                if (!TerminalGate.On || !Plugin.RuafDefense.Value) return false;
                var role = BotOwner.Profile?.Info?.Settings?.Role.ToString() ?? "";
                if (role.IndexOf("ruaf", StringComparison.OrdinalIgnoreCase) < 0
                    && role.IndexOf("vsrf", StringComparison.OrdinalIgnoreCase) < 0) return false;
                var p = BotOwner.GetPlayer;
                if (!p || p.HealthController == null || !p.HealthController.IsAlive) return false;
                var ge = BotOwner.Memory?.GoalEnemy;
                if (ge == null) return false;          // no fight — patrol/crew layers own the bot
                if (ge.IsVisible) return false;        // eyes on target — vanilla combat owns the bot
                return true;                            // fight on, target lost: defensive posture
            }
            catch { return false; }
        }

        public override Action GetNextAction() => new Action(typeof(TerminalRuafDefenseLogic), "squad defense");

        public override bool IsCurrentActionEnding()
            => CurrentAction == null || CurrentAction.Type != typeof(TerminalRuafDefenseLogic);
    }

    internal class TerminalRuafDefenseLogic : CustomLogic
    {
        private float _next;
        private float _nextHeal;
        private CustomNavigationPoint _claim;

        public TerminalRuafDefenseLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Update(CustomLayer.ActionData data)
        {
            if (Time.time < _next) return;
            _next = Time.time + 3f;
            try
            {
                // anchor = the boss (retail tether); the boss himself anchors on his own
                // position, exactly like the decompile's HaveBoss branch
                var boss = BotOwner.BotFollower?.BossToFollow;
                var anchor = boss != null ? boss.Position : BotOwner.Position;

                var enemyPos = BotOwner.Memory?.GoalEnemy?.EnemyLastPosition ?? anchor;

                // group-central cover: closest free point to the boss, evaluated against
                // the enemy's last position — our stand-in for GetCoverPointMain
                if (_claim == null || (_claim.Position - anchor).sqrMagnitude > 900f) // retail MAX_SDIST_FROM_BOSS
                {
                    Release();
                    var p = BotOwner.Covers?.FindClosestPoint(anchor, 3f, enemyPos, false,
                        gp => gp.IsFreeById(BotOwner.Id) && (gp.Position - anchor).sqrMagnitude <= 900f);
                    if (p != null && p.IsFreeById(BotOwner.Id))
                    {
                        p.SetOwner(BotOwner);
                        _claim = p;
                    }
                }

                var goal = _claim != null ? _claim.Position : anchor;
                float sq = (goal - BotOwner.Position).sqrMagnitude;
                if (sq > 2.25f)
                {
                    BotOwner.Mover?.SetPose(1f);
                    BotOwner.Mover?.SetTargetMoveSpeed(0.9f); // urgent but not sprint-blind
                    BotOwner.Mover?.GoToPoint(goal, true, 1f);
                }
                else
                {
                    // in position: crouch behind the cover, watch the threat axis
                    BotOwner.Mover?.SetPose(0.6f);
                    BotOwner.Steering?.LookToPoint(enemyPos + Vector3.up * 1.5f);

                    // retail's heal breaks: patch up while holding, on a cooldown
                    // (PERIOD_TO_HEAL=25), never while rounds are landing
                    if (Time.time >= _nextHeal && BotOwner.Memory != null && !BotOwner.Memory.IsUnderFire)
                    {
                        var aid = BotOwner.Medecine?.FirstAid;
                        if (aid != null && aid.Have2Do)
                        {
                            _nextHeal = Time.time + 25f;
                            aid.TryApplyToCurrentPart();
                        }
                    }
                }
            }
            catch { }
        }

        private void Release()
        {
            try { _claim?.SetFree(); } catch { }
            _claim = null;
        }

        public override void Stop()
        {
            base.Stop();
            Release();
            _next = 0f;
        }
    }

    // COVER-AWARE ARMORY HOLD — the retail 1.0 BD brain, imitated from the dump's
    // blueprint with 4.0's own cover machinery:
    //   BlackDivisionPatrolDefenceLayer: each bot holds a personal RandomHorizontal
    //     offset (<=15m) around the defended point, re-rolled periodically.
    //   BlackDivisionControlBuildingLayer: cover points are picked INSIDE the target
    //     building's environment via Covers.FindClosestPoint + an IsGoodPoint filter.
    // our split: ~1/3 of the squad holds interior cover (FindHidePoint with
    // onlyWithInsideCover — the ControlBuilding half), the rest ring the building on
    // claimed cover points near their personal offset (the PatrolDefence half). points
    // are claimed via SetOwner/IsFreeById so two bots never share one, and dwell timers
    // walk them cover-to-cover so it reads as a patrol, not statues.
    internal class TerminalSiegeLogic : CustomLogic
    {
        private float _next;
        private float _reroll;
        private Vector3 _offset;
        private CustomNavigationPoint _claim;
        private bool _arrived;

        public TerminalSiegeLogic(BotOwner botOwner) : base(botOwner)
        {
            RollOffset();
        }

        private void RollOffset()
        {
            var r = UnityEngine.Random.insideUnitCircle * 15f; // retail's RandomHorizontal(15)
            _offset = new Vector3(r.x, 0f, r.y);
            _reroll = Time.time + UnityEngine.Random.Range(45f, 90f); // EndHoldPosition re-roll flavor
        }

        private bool InteriorBot => (BotOwner.Id % 3) == 0;

        public override void Update(CustomLayer.ActionData data)
        {
            if (Time.time < _next) return;
            _next = Time.time + 4f;

            var rec = TerminalCrewJobs.For(BotOwner);
            if (rec == null) return;
            var armory = rec.Zone;

            try
            {
                if (Time.time >= _reroll)
                {
                    RollOffset();
                    Release(); // next block claims a fresh point — cover-to-cover patrol
                }

                if (_claim == null)
                {
                    _arrived = false;
                    var covers = BotOwner.Covers;
                    CustomNavigationPoint p = null;
                    if (covers != null)
                    {
                        if (InteriorBot)
                            p = covers.FindHidePoint(armory.center, 0f, null, true);
                        if (p == null || !armory.Contains(p.Position) || !p.IsFreeById(BotOwner.Id))
                            p = covers.FindClosestPoint(armory.center + _offset, 0f, armory.center, false,
                                gp => armory.Contains(gp.Position) && gp.IsFreeById(BotOwner.Id));
                    }
                    if (p != null && armory.Contains(p.Position) && p.IsFreeById(BotOwner.Id))
                    {
                        p.SetOwner(BotOwner);
                        _claim = p;
                    }
                }

                if (_claim != null)
                {
                    float sq = (_claim.Position - BotOwner.Position).sqrMagnitude;
                    if (sq > 2.25f)
                    {
                        _arrived = false;
                        BotOwner.Mover?.SetPose(1f);
                        BotOwner.Mover?.SetTargetMoveSpeed(0.55f);
                        BotOwner.Mover?.GoToPoint(_claim.Position, true, 1f);
                    }
                    else if (!_arrived)
                    {
                        _arrived = true;
                        // watch OUTWARD from the building, like a cordon — not at a wall
                        var outward = _claim.Position - armory.center; outward.y = 0f;
                        if (outward.sqrMagnitude < 1f) outward = BotOwner.LookDirection;
                        BotOwner.Steering?.LookToPoint(_claim.Position + outward.normalized * 20f + Vector3.up * 1.5f);
                    }
                    return;
                }

                // no cover point qualified (bake gap in this box) — legacy box roam so
                // the squad still patrols rather than idling in place
                var want = new Vector3(
                    UnityEngine.Random.Range(armory.min.x, armory.max.x),
                    armory.center.y,
                    UnityEngine.Random.Range(armory.min.z, armory.max.z));
                if (NavMesh.SamplePosition(want, out var hit, 4f, NavMesh.AllAreas))
                {
                    BotOwner.Mover?.SetPose(1f);
                    BotOwner.Mover?.SetTargetMoveSpeed(0.5f);
                    BotOwner.Mover?.GoToPoint(hit.position, true, 0.6f);
                }
            }
            catch (Exception e)
            {
                if (Time.time > _quietUntil)
                {
                    _quietUntil = Time.time + 60f;
                    Plugin.Log.LogWarning($"[CrewLayer] siege logic error: {e.Message}");
                }
            }
        }

        private static float _quietUntil;

        private void Release()
        {
            try { _claim?.SetFree(); } catch { }
            _claim = null;
            _arrived = false;
        }

        public override void Stop()
        {
            base.Stop();
            Release();
            _next = 0f;
        }
    }

    // push toward the player: repath every few seconds, sprint when far, slow to a
    // hunting walk close-in so vision/hearing acquire before contact. 4m reach —
    // the goal is contact, not a hug. solo-correct (main player only), fika bridge
    // stays deferred with the rest of terminal's fika gating.
    internal class TerminalHuntLogic : CustomLogic
    {
        private float _next;
        private float _nextRushLog;

        public TerminalHuntLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Update(CustomLayer.ActionData data)
        {
            if (Time.time < _next) return;
            _next = Time.time + 3f;

            // point-rush variant (armory siege): run the fixed anchor, and once close
            // enough hand over to the guard ring by closing the rush window
            var rec = TerminalCrewJobs.For(BotOwner);
            if (rec?.RushTo is Vector3 anchor)
            {
                float asq = (anchor - BotOwner.Position).sqrMagnitude;
                if (asq < 14f * 14f)
                {
                    rec.RushUntil = 0f; // arrived — rush layer stands down, Guard patrols
                    try { BotOwner.Sprint(false, true); } catch { }
                    return;
                }
                // progress telemetry — "some stayed at spawn" needs per-bot evidence
                if (Time.time >= _nextRushLog)
                {
                    _nextRushLog = Time.time + 20f;
                    Plugin.Log.LogDebug($"[CrewLayer] {BotOwner.name}: rushing, {Mathf.Sqrt(asq):0}m to anchor");
                }
                // FULL SPRINT the whole way (user: "in retail theyre already at the
                // armory before youre finished gearing") — only ease off inside 10m
                bool afar = asq > 10f * 10f;
                BotOwner.Mover?.SetPose(1f);
                BotOwner.Mover?.SetTargetMoveSpeed(1f);
                try { BotOwner.Sprint(afar, true); } catch { }
                BotOwner.Mover?.GoToPoint(anchor, true, 6f);
                return;
            }

            var target = Singleton<GameWorld>.Instance?.MainPlayer;
            if (!target || target.HealthController == null || !target.HealthController.IsAlive) return;

            float sq = (target.Position - BotOwner.Position).sqrMagnitude;
            bool far = sq > 20f * 20f;
            BotOwner.Mover?.SetPose(1f);
            BotOwner.Mover?.SetTargetMoveSpeed(far ? 1f : 0.7f);
            try { BotOwner.Sprint(far, true); } catch { }
            BotOwner.Mover?.GoToPoint(target.Position, true, 4f);
        }

        public override void Stop()
        {
            base.Stop();
            try { BotOwner.Sprint(false, true); } catch { }
            _next = 0f;
        }
    }
}
