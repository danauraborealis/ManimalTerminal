using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Manimal.Terminal
{
    // THE REAL FINAL EXIT (FinalExitZone + Gate3OpenTrigger, level628): retail's
    // endgame is 1.0-only wholesale (FinallExitZone ZUBR evac w/ seats, cutscenes,
    // FinalExitZoneTimerPanel UI — none of it exists in 4.0), so this is a faithful
    // reimplementation of the offline-relevant core, decoded from the retail bytes:
    //  - our Terminal_Zubr_Exit exfil snaps onto the AUTHORED FinalExitZone box
    //    (it was hand-placed before — the retail zone is the real spot)
    //  - retail's FinalGateOpenHandler watches gate 3's door; when it opens the
    //    evac timer starts: raid time left drops to 3:00 (FinallExitZone
    //    evacuateTimer = 180.0 in the authored bytes — user intel confirmed)
    internal static class TerminalFinalExit
    {
        private const float EvacSeconds = 180f;
        internal const string ExitName = "Terminal_Zubr_Exit";

        private static bool _staged;
        private static bool _timerCut;
        private static Door _door;
        private static EFT.Interactive.WorldInteractiveObjectInteract _handler;

        internal static void ResetForRaid()
        {
            if (_door != null && _handler != null) _door.OnDoorStateChanged -= _handler;
            _staged = false;
            _timerCut = false;
            _door = null;
            _handler = null;
        }

        internal static void TryStage()
        {
            if (_staged || !Plugin.FinalExit.Value) return;
            if (!TerminalGate.On) return;
            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (gw == null) return;

            var zone = TerminalRigFill.FindRootNamed("FinalExitZone");
            if (!zone) return; // scenes not up yet

            try
            {
                // authored doorID from the sidecar (FinalGateOpenHandler row)
                string doorId = "door_plastic2_door_plastic_prefab_10";
                try
                {
                    var path = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                        "plugin-data", "terminal_endgame.json");
                    var sc = JObject.Parse(System.IO.File.ReadAllText(path));
                    foreach (var row in (sc["rows"] as JArray) ?? new JArray())
                        if (row.Value<string>("cls") == "FinalGateOpenHandler" && row["fields"]?["doorID"] != null)
                            doorId = row["fields"].Value<string>("doorID");
                }
                catch { }

                // snap our exfil onto the authored zone box (no-op when the point was
                // runtime-built — it is born there)
                var zoneBox = zone.GetComponent<BoxCollider>();
                ExfiltrationPoint target = null;
                foreach (var ep in UnityEngine.Object.FindObjectsOfType<ExfiltrationPoint>(true))
                    if (ep.Settings != null && ep.Settings.Name == ExitName) { target = ep; break; }
                if (target && zoneBox)
                {
                    target.transform.SetPositionAndRotation(zone.position, zone.rotation);
                    var epBox = target.GetComponent<BoxCollider>();
                    if (epBox)
                    {
                        epBox.center = zoneBox.center;
                        epBox.size = Vector3.Scale(zoneBox.size, zone.lossyScale);
                    }
                    Plugin.Log.LogInfo($"[FinalExit] {ExitName} snapped to the authored FinalExitZone box at {zone.position}");
                }
                else Plugin.Log.LogWarning($"[FinalExit] snap incomplete (exfil={(bool)target} zoneBox={(bool)zoneBox}) — exfil stays where authored");

                // watch gate 3's door for the evac timer
                foreach (var d in UnityEngine.Object.FindObjectsOfType<Door>(true))
                {
                    if (d.Id != doorId) continue;
                    _door = d;
                    _handler = new EFT.Interactive.WorldInteractiveObjectInteract(OnDoorStateChanged);
                    d.OnDoorStateChanged += _handler;
                    break;
                }
                if (_door == null) Plugin.Log.LogWarning($"[FinalExit] gate 3 door '{doorId}' not found — no evac timer");
                else Plugin.Log.LogInfo($"[FinalExit] watching gate 3 door '{doorId}' — opening it cuts raid time to {EvacSeconds / 60f:0} min");

                _staged = true;
                if (_door != null && _door.DoorState == EDoorState.Open)
                    OnDoorStateChanged(_door, EDoorState.Shut, EDoorState.Open);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[FinalExit] staging failed: {e}");
                _staged = true;
            }
        }

        // the scene no longer ships a Terminal_Zubr_Exit point (user deleted the
        // hand-placed one after the authored-zone restore) and discovery is strictly
        // LocationScene's serialized registries — a runtime GO is invisible to it. so
        // build the point OURSELVES right after the controller enumerates: born on the
        // authored FinalExitZone box, LoadSettings from the same server exits row the
        // scene point would have gotten, appended to the controller's array. works
        // whether or not a scene copy ever comes back (skips if the name exists).
        [HarmonyLib.HarmonyPatch(typeof(CommonAssets.Scripts.Game.ExfiltrationController), nameof(CommonAssets.Scripts.Game.ExfiltrationController.InitAllExfiltrationPoints))]
        internal static class Patch_BuildZubrExit
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(CommonAssets.Scripts.Game.ExfiltrationController __instance, MongoID locationId, JsonType.BackendExitTriggerSettings[] settings, bool giveAuthority)
            {
                try
                {
                    if (!TerminalGate.On || !Plugin.FinalExit.Value || __instance == null) return;
                    var pts = __instance.ExfiltrationPoints ?? new ExfiltrationPoint[0];
                    foreach (var p in pts)
                        if (p != null && p.Settings != null && p.Settings.Name == ExitName) return; // scene copy survived

                    JsonType.BackendExitTriggerSettings row = null;
                    foreach (var s in settings ?? new JsonType.BackendExitTriggerSettings[0])
                        if (s != null && s.Name == ExitName) { row = s; break; }
                    if (row == null) { Plugin.Log.LogWarning($"[FinalExit] no '{ExitName}' exits row from the server — cannot build the point"); return; }

                    var zone = TerminalRigFill.FindRootNamed("FinalExitZone");
                    var zoneBox = zone ? zone.GetComponent<BoxCollider>() : null;
                    if (!zoneBox) { Plugin.Log.LogWarning("[FinalExit] authored FinalExitZone box not found — cannot build the point"); return; }

                    // inactive-build so Awake runs with everything wired (Awake scales the
                    // box by localScale — keep scale 1 and bake world size directly)
                    var go = new GameObject(ExitName);
                    go.SetActive(false);
                    go.layer = zone.gameObject.layer;
                    go.transform.SetPositionAndRotation(zone.position, zone.rotation);
                    var col = go.AddComponent<BoxCollider>();
                    col.center = zoneBox.center;
                    col.size = Vector3.Scale(zoneBox.size, zone.lossyScale);
                    col.isTrigger = true;
                    var point = go.AddComponent<ExfiltrationPoint>();
                    point.Settings.Name = ExitName;
                    go.SetActive(true);

                    point.LoadSettings(locationId.Add(pts.Length + 2), row, giveAuthority);

                    var merged = new ExfiltrationPoint[pts.Length + 1];
                    pts.CopyTo(merged, 0);
                    merged[pts.Length] = point;
                    __instance.ExfiltrationPoints = merged;
                    Plugin.Log.LogInfo($"[FinalExit] '{ExitName}' point BUILT at the authored zone ({zone.position}) — status {point.Status}");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[FinalExit] exfil build failed: {e}"); }
            }
        }

        private static void OnDoorStateChanged(WorldInteractiveObject obj, EDoorState prevState, EDoorState nextState)
        {
            if (TerminalCoop.Active)
            {
                if (!_timerCut && nextState == EDoorState.Open && TerminalCoop.Authority)
                    TerminalCoop.Request(TerminalEvent.Evac, obj.Id);
                return;
            }
            if (_timerCut || nextState != EDoorState.Open) return;
            ApplyCountdown(EvacSeconds, true);
        }

        internal static bool ApplyCountdown(double seconds, bool announce)
        {
            var game = Singleton<AbstractGame>.Instance;
            var timer = game?.GameTimer;
            if (timer == null) return false;
            float duration = (float)Math.Max(0, Math.Min(EvacSeconds, seconds));
            bool first = !_timerCut;
            try
            {
                _timerCut = true;
                var remaining = (timer.SessionTime ?? TimeSpan.Zero) - timer.PastTime;
                if (remaining > TimeSpan.FromSeconds(duration + (first ? 0 : 0.25)))
                {
                    timer.ChangeSessionTime(timer.PastTime + TimeSpan.FromSeconds(duration));
                    // the HUD timer caches its escape DateTime at Show — poke it or the
                    // display keeps the old countdown (2026-08-18 report)
                    try
                    {
                        // 4.0 HUD wraps the battle timer: GameUI.TimerPanel is an
                        // ExtractionTimersPanel holding _mainTimerPanel (: TimerPanel)
                        // — poke the inner panel's cached escape time (2026-08-18)
                        object panel = MonoBehaviourSingleton<EFT.UI.GameUI>.Instance?.TimerPanel;
                        if (panel != null && !(panel is EFT.UI.BattleTimer.TimerPanel))
                            panel = HarmonyLib.AccessTools.Field(panel.GetType(), "_mainTimerPanel")?.GetValue(panel);
                        if (panel is EFT.UI.BattleTimer.TimerPanel tp)
                        {
                            HarmonyLib.AccessTools.Field(typeof(EFT.UI.BattleTimer.TimerPanel), "_dateTime")
                                .SetValue(tp, EFT.DateTimeExtensions.UtcNow.AddSeconds(duration));
                            if (first) Plugin.Log.LogInfo($"[FinalExit] HUD timer synchronized to {duration:F1}s remaining");
                        }
                        else if (first && TerminalCoop.LocalHuman) Plugin.Log.LogWarning("[FinalExit] no TimerPanel reachable — HUD keeps the old countdown");
                    }
                    catch (Exception te) { Plugin.Log.LogWarning($"[FinalExit] timer UI poke failed: {te.Message}"); }
                    if (first) Plugin.Log.LogInfo($"[FinalExit] GATE 3 OPEN — {duration:F1}s evacuation window");
                }
                else if (first) Plugin.Log.LogInfo($"[FinalExit] gate 3 open with {remaining:mm\\:ss} left — already inside the evac window");

                // retail's loudspeaker announcement (user screenshot): subtitle text +
                // audio through the authored AnnouncementSystem speakers. the real VO
                // is vsrf_evac_3min (sharedassets636 — user found it in the inventory
                // dump 2026-08-18); siren only if the fx bundle predates it
                try
                {
                    if (!first || !announce || !TerminalCoop.LocalHuman) return true;
                    // authored 1.0 loudspeaker subtitle (locale 6924d5327d890c5d7e3ae2c9);
                    // duration matches vsrf_evac_3min clip (7.1s) plus a small tail
                    TerminalSubtitles.ShowStandalone(
                        "<b>Loudspeaker:</b> The enemy has broken through to the loading zone! Evacuation will end in 3 minutes!",
                        8.0f);
                    var clip = TerminalFxBundle.FindClip("vsrf_evac_3min");
                    if (clip == null) clip = TerminalFxBundle.FindClip("amb_terminal_siren");
                    if (clip != null)
                    {
                        int speakers = 0;
                        float nearest = float.MaxValue;
                        var me = Singleton<GameWorld>.Instance?.MainPlayer;
                        var speakersRoot = TerminalRigFill.FindRootNamed("AnnouncementSystem");
                        if (speakersRoot)
                            foreach (var t in speakersRoot.GetComponentsInChildren<Transform>(true))
                                if (t.name.StartsWith("announcement_speaker"))
                                {
                                    TerminalGatesExplosion.PlayAt(clip, t.position, 90f);
                                    if (me != null) nearest = Mathf.Min(nearest, (t.position - me.Position).magnitude);
                                    speakers++;
                                }
                        if (speakers == 0 && _door) TerminalGatesExplosion.PlayAt(clip, _door.transform.position, 200f);
                        // flat 2D bed so the line reads wherever the player stands
                        // (2026-08-18: played over 8 speakers, user heard none — gate 3
                        // may sit outside every speaker's 90m rolloff)
                        TerminalGatesExplosion.PlayFlat(clip, 0.55f);
                        Plugin.Log.LogInfo($"[FinalExit] evac announcement over {speakers} speaker(s) + 2D bed"
                            + $" ('{clip.name}', nearest speaker {(nearest < float.MaxValue ? $"{nearest:F0}m" : "n/a")})");
                    }
                }
                catch (Exception ae) { Plugin.Log.LogWarning($"[FinalExit] announcement failed: {ae.Message}"); }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[FinalExit] timer cut failed: {e}"); }
            return true;
        }
    }
}
