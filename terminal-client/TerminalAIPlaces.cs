using System;
using System.Collections.Generic;
using SysIoPath = System.IO.Path;
using EFT;
using Comfort.Common;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // terminal port of icebreaker's spawn-trigger layer (resurrection #4): the T0-T5 /
    // TB1-TB8 trigger boxes recovered from level635 (extract_terminal_aiplaces.py).
    // mechanism: AIPlaceInfo trigger box -> logic raises BotEventHandler.AnyEvent(name)
    // on player entry -> BSG's BossSpawnScenario fires the base.json BossLocationSpawn
    // waves with TriggerName=botEvent + TriggerId=name. 55 of terminal's 65 live boss
    // waves are botEvent-gated — without this layer the map's spawn choreography never
    // fires. terminal has no 1.0-only group-size logic (all 16 are plain EventRaise),
    // and no crew bridge is needed: BSG's own scenario subscribes natively.
    internal static class TerminalAIPlaces
    {
        private static JObject _sidecar;
        private static bool _sidecarTried;
        private static GameObject _marker;

        internal static void ResetForNewRaid()
        {
            if (_marker != null) UnityEngine.Object.Destroy(_marker);
            _marker = null;
            TerminalTierEventLogic.ResetFireCounts();
        }

        private static string DataDir =>
            SysIoPath.Combine(SysIoPath.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                "plugin-data", "aiplaces");

        private static JObject Sidecar()
        {
            if (_sidecarTried) return _sidecar;
            _sidecarTried = true;
            try
            {
                var path = SysIoPath.Combine(DataDir, "terminal_aiplaces.json");
                if (System.IO.File.Exists(path))
                    _sidecar = JObject.Parse(System.IO.File.ReadAllText(path));
                else
                    Plugin.Log.LogDebug($"[AIPlaces] no sidecar at {path} — spawn triggers stay off");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[AIPlaces] sidecar parse failed: {e.Message}"); }
            return _sidecar;
        }

        // called from the BotsController.Init postfix — CoversData + holder exist, bots
        // haven't spawned yet
        [HarmonyPatch(typeof(BotsController), "Init")]
        internal static class Patch_BuildSpawnTriggers
        {
            [HarmonyPostfix]
            private static void Postfix(BotsController __instance)
            {
                if (!TerminalGate.On) return;
                try { TryBuild(__instance); }
                catch (Exception e) { Plugin.Log.LogWarning($"[AIPlaces] build failed: {e.Message}"); }
                // crew jobs: fresh book per raid, then watch every bot birth — tier-event
                // window bots get the push job (BotSpawner nulls its events on dispose,
                // so no cross-raid double-subscribe)
                try
                {
                    TerminalCrewJobs.Reset();
                    if (__instance.BotSpawner != null)
                        __instance.BotSpawner.OnBotCreated += TerminalCrewJobs.OnBotCreated;
                    TerminalHangarCrew.AugmentHangarZone(); // native fix: interior markers before any wave fires
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[AIPlaces] crew hook failed: {e.Message}"); }
            }
        }

        public static void TryBuild(BotsController controller)
        {
            if (_marker != null) return;
            var sc = Sidecar();
            if (sc == null || !TerminalGate.On) return;

            try
            {
                var holderGo = GameObject.Find("AIPlaceInfoHolder");
                if (holderGo == null) { Plugin.Log.LogDebug("[AIPlaces] AIPlaceInfoHolder GO not in scene — aborting"); return; }

                var index = new Dictionary<string, Transform>();
                void Walk(Transform t, string p)
                {
                    index[p] = t;
                    for (int i = 0; i < t.childCount; i++)
                    {
                        var c = t.GetChild(i);
                        Walk(c, p + "/" + c.name);
                    }
                }
                Walk(holderGo.transform, "AIPlaceInfoHolder");

                // pass 1: event-raise logic. NOT the engine's AIPlaceLogicEventRaise —
                // that one has zero player filter, so bots trip the boxes during their
                // spawn frame and burn the one-shot events at raid start (icebreaker
                // lesson). TierEventLogic is the same thing gated on the human player.
                var logicByRef = new Dictionary<long, AIPlaceInfoLogic>();
                foreach (var row in (sc["components"]?["AIPlaceLogicEventRaise"] as JArray) ?? new JArray())
                {
                    if (!index.TryGetValue(row.Value<string>("go") ?? "", out var t)) continue;
                    var stale = t.gameObject.GetComponent<AIPlaceLogicEventRaise>();
                    if (stale != null) UnityEngine.Object.Destroy(stale);
                    var lg = t.gameObject.GetComponent<TerminalTierEventLogic>() ?? t.gameObject.AddComponent<TerminalTierEventLogic>();
                    var f = row["fields"];
                    lg.EventName = f?.Value<string>("EventName") ?? "none";
                    lg.EnterRaise = (f?.Value<int?>("EnterRaise") ?? 1) != 0;
                    lg.ExitRaise = (f?.Value<int?>("ExitRaise") ?? 0) != 0;
                    logicByRef[row.Value<long>("path_id")] = lg;
                }

                // pass 2: the AIPlaceInfo boxes
                var holder = controller.CoversData != null ? controller.CoversData.AIPlaceInfoHolder : null;
                if (holder == null) holder = UnityEngine.Object.FindObjectOfType<AIPlaceInfoHolder>();
                if (holder != null && holder.Places == null) holder.Places = new List<AIPlaceInfo>();
                var doors = UnityEngine.Object.FindObjectsOfType<EFT.Interactive.Door>();
                int built = 0, evTriggers = 0;

                foreach (var row in (sc["components"]?["AIPlaceInfo"] as JArray) ?? new JArray())
                {
                    var goPath = row.Value<string>("go") ?? "";
                    if (!index.TryGetValue(goPath, out var t)) continue;
                    var go = t.gameObject;
                    var col = go.GetComponent<BoxCollider>();
                    if (col == null) continue; // trigger box died in rip
                    // the rip drops collider flags at random (Terminal_AI ships 30 solid +
                    // 4 disabled boxes) and a non-trigger box never raises OnTriggerEnter —
                    // these 17 are one-shot scripted spawn gates, force them live
                    if (!col.isTrigger || !col.enabled)
                    {
                        Plugin.Log.LogDebug($"[AIPlaces] '{go.name}' collider healed (isTrigger={col.isTrigger}, enabled={col.enabled})");
                        col.isTrigger = true;
                        col.enabled = true;
                    }
                    if (!go.activeInHierarchy)
                        Plugin.Log.LogWarning($"[AIPlaces] '{go.name}' is INACTIVE in hierarchy — trigger will not fire");

                    var place = go.GetComponent<AIPlaceInfo>() ?? go.AddComponent<AIPlaceInfo>();
                    var f = row["fields"];
                    place.AreaId = f?.Value<int?>("AreaId") ?? 0;
                    place.Id = f?.Value<string>("Id") ?? "";
                    place.IsOnTrigger = (f?.Value<int?>("IsOnTrigger") ?? 1) != 0;
                    place.BlockGrenade = (f?.Value<int?>("BlockGrenade") ?? 0) != 0;
                    place.IsDark = (f?.Value<int?>("IsDark") ?? 0) != 0;
                    place.IsMute = (f?.Value<int?>("IsMute") ?? 0) != 0;
                    place.IsInside = (f?.Value<int?>("IsInside") ?? 0) != 0;
                    place.AtrZone = (f?.Value<int?>("AtrZone") ?? 0) != 0;
                    place.CoversSpecial = (ECoverPointSpecial)(f?.Value<int?>("CoversSpecial") ?? 0);
                    place.UseAsCoverGroupId = (f?.Value<int?>("UseAsCoverGroupId") ?? 1) != 0;
                    place.GrenadePlaces = Array.Empty<ThrowGrenadePlace>();
                    place.Collider = go.GetComponent<BoxCollider>();

                    long logicRef = (f?["InfoLogicAllEnemy"] as JObject)?.Value<long?>("ref") ?? 0;
                    if (logicByRef.TryGetValue(logicRef, out var lg))
                    {
                        place.InfoLogicAllEnemy = lg;
                        evTriggers++;
                    }

                    if (holder != null && (holder.Places == null || !holder.Places.Contains(place)))
                        holder.AddPlace(place);
                    place.Init(doors, controller);
                    // Author-29 regression (2026-08-10): the places now sit in the
                    // LocationScene registry, so BotsController.method_2 Init'd them
                    // EMPTY before this fill ran — _isInited latched and the Init above
                    // no-ops, which is a silent event-logic death (raid log: 17 live
                    // triggers, player walked TB2, zero events). init the logic directly;
                    // TierEventLogic.Init is idempotent so the fresh path double-call is
                    // harmless
                    if (place.InfoLogicAllEnemy != null)
                        place.InfoLogicAllEnemy.Init(place, controller);
                    built++;
                    Plugin.Log.LogDebug($"[AIPlaces] trigger '{go.name}' live at {t.position} size {Vector3.Scale(col.size, t.lossyScale)}");
                }

                _marker = new GameObject("Terminal_AIPlacesStaged");
                var scn = SceneManager.GetSceneByName("Terminal_AI");
                if (scn.IsValid() && scn.isLoaded) SceneManager.MoveGameObjectToScene(_marker, scn);
                Plugin.Log.LogInfo($"[AIPlaces] SPAWN TRIGGERS REBUILT: {built} places, {evTriggers} event logics — botEvent waves armed");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[AIPlaces] rebuild failed: {e}");
            }
        }
    }

    // the engine's AIPlaceLogicEventRaise minus its blind spot: it raises for ANY
    // player (bots included), ours only for the human. fika teammates need a bridge
    // like icebreaker's — solo-correct for now.
    public class TerminalTierEventLogic : AIPlaceInfoLogic
    {
        public string EventName = "none";
        public bool EnterRaise = true;
        public bool ExitRaise;
        private AIPlaceInfo _place;

        public override void Init(AIPlaceInfo aiPlaceInfo, BotsController botsController)
        {
            if (_place == aiPlaceInfo) return; // already wired to this place
            base.Init(aiPlaceInfo, botsController);
            _place = aiPlaceInfo;
            _place.OnPlayerEnter += OnEnter;
            _place.OnPlayerExit += OnExit;
            Plugin.Log.LogDebug($"[AIPlaces] event logic wired: '{gameObject.name}' -> '{EventName}'");
        }

        public override void Dispose()
        {
            if (_place != null) { _place.OnPlayerEnter -= OnEnter; _place.OnPlayerExit -= OnExit; }
            base.Dispose();
        }

        // there is NO one-shot guard here — by design these mirror BSG's own
        // EventRaise, which re-raises on every entry. that makes re-entry churn a
        // real possibility (two separate boxes both raise 'T4'), so count the
        // fires per raid and log at Warning: if an event climbs past 1-2, the
        // player is crossing a boundary repeatedly and re-triggering waves.
        internal static readonly Dictionary<string, int> FireCounts = new Dictionary<string, int>();
        internal static void ResetFireCounts() => FireCounts.Clear();

        private void OnEnter(Player p)
        {
            if (EnterRaise && p != null && p.IsYourPlayer)
            {
                FireCounts.TryGetValue(EventName, out var n);
                FireCounts[EventName] = ++n;
                Plugin.Log.LogWarning($"[AIPlaces] ENTER '{gameObject.name}' -> raising event '{EventName}' (fire #{n} this raid)");
                TerminalCrewJobs.NoteEvent(EventName); // wave bots born off this event push the players
                Singleton<BotEventHandler>.Instance?.AnyEvent(EventName);
                TerminalPopulationDirector.EnsureProgressWavesActivated(EventName);
            }
        }

        private void OnExit(Player p)
        {
            if (p == null || !p.IsYourPlayer) return;
            // log the exit even when ExitRaise is off — enter/exit churn is exactly
            // what we're looking for and it's invisible otherwise
            Plugin.Log.LogWarning($"[AIPlaces] EXIT  '{gameObject.name}' ('{EventName}')"
                + (ExitRaise ? " -> raising on exit too" : ""));
            if (ExitRaise)
            {
                Singleton<BotEventHandler>.Instance?.AnyEvent(EventName);
                TerminalPopulationDirector.EnsureProgressWavesActivated(EventName);
            }
        }
    }
}
