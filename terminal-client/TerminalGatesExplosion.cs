using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // THE GATES_EXPLOSION RESTORE (decoded 2026-08-17, playbook 'GATES_EXPLOSION
    // DECODED'): retail's gate is a data-authored TRIGGER NETWORK. the engine
    // (GClass3592 emitter, created offline by ClientLocalGameWorld) and most
    // handler classes are ALIVE in 4.0 — we resurrect those from the sidecar
    // (terminal_gates.json, full retail values) and re-implement only the
    // 1.0-only INTERACTION layer as action-menu sessions that EMIT triggers
    // into the native graph:
    //   SwitchBoard 'Open'  -> hold -> gates_opened_solo_*  (ELITE STRENGTH 51
    //                          required — the retail solo requirement, user-known;
    //                          the duo path is fika-era work)
    //   Switch_bomb 'Plant' -> hold + SZ-1 consume -> place_bomb_*
    // then retail takes over: charge prop shows, switches retire, HandlerDelay
    // runs the 10s fuse, HandlerExplosion deals the REAL blast (frag settings,
    // Effects 'Fire'), HandlerAnimator plays the gate. both SZ-1 tpls accepted
    // (BSG shipped one per map; either works, same as icebreaker's door).
    internal static class TerminalGatesExplosion
    {
        internal const string Suffix = "3037225082";
        internal const string PlaceTrigger = "place_bomb_" + Suffix;
        internal const string ExplosionTrigger = "bomb_explosion_" + Suffix;
        internal const string SoloOpenTrigger = "gates_opened_solo_" + Suffix;
        internal const float PlantSeconds = 5f;
        internal const float OpenSeconds = 5f;

        internal static readonly string[] ChargeTpls =
        {
            "69a0174087a75d2cbd0842e8",
            "6819f8df28294ec0730db6b4",
        };

        internal static bool Planted;
        internal static bool Opened;
        internal static bool _boomSeen;

        private static bool _staged;
        private static Transform _root;

        // native 4.0 classes resurrected verbatim from the sidecar. NOT in the set,
        // deliberately: HandlerInteractable (its job is binding vanilla Switch use to
        // triggers — our action patch owns that layer, and both would double-fire),
        // TriggersPrefab (_processed=1: ids were suffix-baked at authoring),
        // AnimatorSync/GameObjectStateSync (MP shims), and every 1.0-only class.
        // HandlerDelay is deliberately OUT: SPT 4.0 ships it gutted (wait with no
        // emit) — authored delays run through TerminalRigFill.StageDelayShims
        private static readonly HashSet<string> NativeSet = new HashSet<string>
        {
            "HandlerAnimator", "HandlerExplosion",
            "HandlerGameObjectState", "HandlerGOState",
        };

        internal static void ResetForRaid()
        {
            _staged = false;
            _root = null;
            Planted = false;
            Opened = false;
            _boomSeen = false;
        }

        internal static void TryStage()
        {
            if (_staged || !Plugin.GatesExplosion.Value) return;
            if (!TerminalGate.On) return;
            // handlers Subscribe via GClass3592.Instance in their Start — stage only
            // once the emitter exists or every handler dies on a silent NRE
            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (gw == null || gw.TriggersEmitter == null) return;

            var root = FindRoot();
            if (!root) return; // scenes not up yet
            JObject sc;
            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_gates.json");
                if (!System.IO.File.Exists(path))
                {
                    Plugin.Log.LogWarning("[Gates] terminal_gates.json missing — gate stays inert");
                    _staged = true;
                    return;
                }
                sc = JObject.Parse(System.IO.File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Gates] sidecar parse failed: {e.Message}");
                _staged = true;
                return;
            }

            try
            {
                _root = root;
                var index = new Dictionary<string, Transform>();
                TerminalRigFill.IndexTree(root, "Gates_explosion", index);
                TerminalRigFill.StageRows(sc["rows"] as JArray, NativeSet, index, "Gates");
                TerminalRigFill.StageDelayShims(sc["rows"] as JArray, "Gates");

                // the charge prop must START hidden no matter how the scene ships
                // (user-authored intent: invisible until planted, gone after the
                // blast — the resurrected HandlerGameObjectState pair owns the
                // transitions, this owns the initial state)
                if (index.TryGetValue("Gates_explosion/Triggers/ExpoldeTriggers/item_spec_sz_1", out var prop))
                    prop.gameObject.SetActive(false);

                // interaction surfaces must exist for the prompt raycast — the old
                // bundle ships them WTT-stub-alive; warn loudly if not
                foreach (var swPath in new[]
                {
                    "Gates_explosion/Triggers/ExpoldeTriggers/Switch_bomb",
                    "Gates_explosion/Triggers/OpeningTriggers/Switch/SwitchBoard",
                })
                {
                    if (!index.TryGetValue(swPath, out var swT))
                    {
                        Plugin.Log.LogWarning($"[Gates] '{swPath}' GO not found — its prompt cannot appear");
                        continue;
                    }
                    // the switch GO (and its parents inside the rig) may ship inactive
                    for (var up = swT; up != null && up != root.parent; up = up.parent)
                        if (!up.gameObject.activeSelf) up.gameObject.SetActive(true);
                    var sw = swT.GetComponent<EFT.Interactive.Switch>();
                    if (!sw) Plugin.Log.LogWarning($"[Gates] no Switch component on '{swPath}' — prompt dead (bundle husk?)");
                    else sw.enabled = true;
                }

                // belt-and-braces VFX: retail wires the C4_Explosion rig through its
                // own graph, but if that authoring rode a class we skipped, this
                // listener guarantees the show (idempotent — Play on a playing
                // system is skipped)
                try
                {
                    GClass3592.Instance.Subscribe(ExplosionTrigger, new Action(OnExplosionVfx));
                    // FUSE WATCHDOG (raid report 2026-08-18: plant worked, timer ran,
                    // no boom — the resurrected HandlerDelay never fired). track the
                    // native explosion, and if 10.5s pass after the plant with no boom
                    // seen, emit it ourselves — the rest of the native graph (blast,
                    // anim, prop hide) hangs off the trigger either way.
                    GClass3592.Instance.Subscribe(ExplosionTrigger, new Action(() => _boomSeen = true));
                    GClass3592.Instance.Subscribe(PlaceTrigger, new Action(() =>
                    {
                        var host = new GameObject("Terminal_GateFuseWatchdog");
                        host.AddComponent<FuseWatchdog>();
                    }));
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Gates] vfx listener subscribe failed: {e.Message}"); }

                _staged = true;
                // 3s later (handler Starts run a frame after AddComponent) prove or
                // disprove the native subscriptions — 2026-08-18 raid: fuse silent
                TerminalRigFill.ScheduleSubscriberDump("Gates", PlaceTrigger, ExplosionTrigger, SoloOpenTrigger);
                Plugin.Log.LogInfo("[Gates] GATES_EXPLOSION RESTORED — retail fuse/blast/anim armed, interaction layer ours");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Gates] staging failed: {e}");
                _staged = true;
            }
        }

        private static void OnExplosionVfx()
        {
            try
            {
                if (!_root) return;
                var at = _root.position;
                var vfx = TerminalRigFill.FindChildNamed(_root, "C4_Explosion");
                if (vfx != null)
                {
                    at = vfx.position;
                    if (!vfx.gameObject.activeSelf) vfx.gameObject.SetActive(true);
                    foreach (var ps in vfx.GetComponentsInChildren<ParticleSystem>(true))
                        if (!ps.isPlaying) ps.Play();
                }
                // authored boom (level629 sound row: bomb_explosion -> c6_detonate);
                // icebreaker chain-door pair only as fallback until the fx bundle rebuild
                var boom = FindClip("amb_terminal_interactive_c6_detonate");
                if (boom != null) PlayAt(boom, at, 120f);
                else
                {
                    PlayAt(FindClip("IB_chain_door_explosion"), at, 120f);
                    PlayAt(FindClip("IB_chain_door_explosion_reaction"), at, 60f, 1f);
                }
                Plugin.Log.LogInfo($"[Gates] C4_Explosion VFX + boom foley fired ('{(boom != null ? boom.name : "IB fallback pair")}')");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Gates] vfx failed: {e.Message}"); }
        }

        private static Transform FindRoot() => TerminalRigFill.FindRootNamed("Gates_explosion");

        // -------------------------------------------------------------- triggers
        internal static void Emit(string trigger)
        {
            try
            {
                TerminalWorldDiff.RecordEvent("map trigger", trigger);
                var player = Singleton<GameWorld>.Instance?.MainPlayer;
                GClass3592.Instance.Emit(trigger, player != null ? player.ProfileId : "");
                Plugin.Log.LogInfo($"[Gates] emitted '{trigger}'");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Gates] emit '{trigger}' failed: {e}"); }
        }

        // ---------------------------------------------------------------- charge
        internal static bool IsCharge(Item it)
        {
            if (it == null) return false;
            foreach (var t in ChargeTpls) if (it.TemplateId == t) return true;
            return false;
        }

        internal static Item FindCharge(Player player)
        {
            if (player?.Profile?.Inventory == null) return null;
            foreach (var it in player.Profile.Inventory.AllRealPlayerItems)
                if (IsCharge(it)) return it;
            return null;
        }

        // icebreaker-hardened consume, ported verbatim (unbind quickbinds first;
        // template-matched in-hands check; swap-then-remove; simulate-validate then
        // one network transaction — the exact sequence that survived the ghost-hands
        // and flashing-item eras)
        private static void DispatchRemove(Player player, Item item)
        {
            try
            {
                var unbind = player.InventoryController.UnbindItemDirect(item, true);
                if (!unbind.Failed)
                {
                    player.InventoryController.TryRunNetworkTransaction(unbind, r =>
                    { if (!r.Succeed) Plugin.Log.LogWarning($"[Gates] charge unbind failed post-validation: {r.Error}"); });
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Gates] unbind attempt threw (continuing): {e.Message}"); }

            var op = InteractionsHandlerClass.Remove(item, player.InventoryController, true);
            if (op.Failed)
            {
                Plugin.Log.LogWarning($"[Gates] charge remove validation failed: {op.Error}");
                return;
            }
            player.InventoryController.TryRunNetworkTransaction(op, r =>
            { if (!r.Succeed) Plugin.Log.LogWarning($"[Gates] charge remove failed post-validation: {r.Error}"); });
            Plugin.Log.LogInfo($"[Gates] consumed charge '{item.Name.Localized()}'");
        }

        internal static bool ConsumeCharge(Player player)
        {
            var item = FindCharge(player);
            if (item == null) return false;
            var handsItem = player.HandsController != null ? player.HandsController.Item : null;
            bool inHands = handsItem != null
                && (ReferenceEquals(handsItem, item) || handsItem.Id == item.Id || IsCharge(handsItem));
            if (!inHands)
            {
                DispatchRemove(player, item);
                return true;
            }
            Plugin.Log.LogInfo("[Gates] charge IN HANDS — swapping to a weapon before consuming");
            player.SetFirstAvailableItem(new Callback<IHandsController>(r =>
            {
                try
                {
                    if (player.HandsController != null && IsCharge(player.HandsController.Item))
                    {
                        Plugin.Log.LogWarning("[Gates] hands still hold a charge after swap — NOT consumed");
                        return;
                    }
                    DispatchRemove(player, item);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Gates] post-swap consume failed: {e.Message}"); }
            }));
            return true;
        }

        // ----------------------------------------------------------------- foley
        // the plant/timer/boom clips are retail TERMINAL sounds that only survived in
        // icebreaker's bundle — extracted from there and shipped in terminal_fx.bundle
        internal static AudioClip FindClip(string name) => TerminalFxBundle.FindClip(name);

        internal static void PlayAt(AudioClip clip, Vector3 pos, float maxDist, float delay = 0f)
        {
            if (clip == null) return;
            var go = new GameObject("Terminal_GatesSnd");
            go.transform.position = pos;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.spatialBlend = 1f;
            src.maxDistance = maxDist;
            src.rolloffMode = AudioRolloffMode.Linear;
            if (delay > 0f) src.PlayDelayed(delay); else src.Play();
            UnityEngine.Object.Destroy(go, clip.length + delay + 0.5f);
        }

        // flat 2D one-shot — for broadcast moments that must read regardless of
        // player position (loudspeaker lines)
        internal static void PlayFlat(AudioClip clip, float volume)
        {
            if (clip == null) return;
            var go = new GameObject("Terminal_FlatSnd");
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.spatialBlend = 0f;
            src.volume = volume;
            src.Play();
            UnityEngine.Object.Destroy(go, clip.length + 0.5f);
        }

        internal static Vector3 RigPos()
        {
            return _root ? _root.position : Vector3.zero;
        }
    }

    // watches the native 10s fuse after a plant; emits the boom itself if the
    // resurrected HandlerDelay never delivers (the trigger drives the whole rest
    // of the graph, so a watchdog boom is indistinguishable from a native one)
    internal class FuseWatchdog : MonoBehaviour
    {
        private float _armed;

        private void Start() => _armed = Time.time;

        private void Update()
        {
            if (TerminalGatesExplosion._boomSeen) { Destroy(gameObject); return; }
            if (Time.time - _armed < 10.5f) return;
            Plugin.Log.LogWarning("[Gates] native fuse silent 10.5s after the plant — watchdog detonating");
            TerminalGatesExplosion.Emit(TerminalGatesExplosion.ExplosionTrigger);
            Destroy(gameObject);
        }
    }

    // hold-to-interact session — the icebreaker PlantSession pattern generalized:
    // objectives-panel countdown, firearms locked, release/Escape/walk-away cancels
    internal class GateHoldSession : MonoBehaviour
    {
        public GamePlayerOwner Owner;
        public Transform Anchor;
        public float Seconds = 5f;
        public string PanelText = "Planting charge {0:F1}";
        public string FoleyClip;
        public Action OnSuccess;

        private float _start;
        private bool _started, _ending, _handsLocked, _panelShown;
        private AudioSource _snd;

        private void Update()
        {
            try
            {
                if (_ending) return;
                var player = Singleton<GameWorld>.Instance?.MainPlayer;
                if (player == null || Anchor == null) { End(false); return; }

                if (!_started)
                {
                    _started = true;
                    _start = Time.time;
                    if (Owner != null)
                    {
                        Owner.ShowObjectivesPanel(PanelText, Seconds);
                        _panelShown = true;
                    }
                    var mc = player.MovementContext;
                    if (mc != null) { mc.BlockFirearms = true; _handsLocked = true; TerminalHoldLock.Acquire(); }
                    transform.position = Anchor.position;
                    var clip = string.IsNullOrEmpty(FoleyClip) ? null : TerminalGatesExplosion.FindClip(FoleyClip);
                    if (clip != null)
                    {
                        _snd = gameObject.AddComponent<AudioSource>();
                        _snd.clip = clip;
                        _snd.spatialBlend = 1f;
                        _snd.maxDistance = 25f;
                        _snd.rolloffMode = AudioRolloffMode.Linear;
                        _snd.Play();
                    }
                }

                if (Input.GetKeyDown(KeyCode.Escape)) { End(false); return; }
                if ((player.Position - Anchor.position).sqrMagnitude > 25f) { End(false); return; }
                if (!TerminalInteractKey.Held()) { End(false); return; }
                if (Time.time - _start >= Seconds) { End(true); return; }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Gates] hold session threw: {e.Message}");
                End(false);
            }
        }

        private void End(bool success)
        {
            if (_ending) return;
            _ending = true;
            if (_snd != null) _snd.Stop();
            try
            {
                if (success && OnSuccess != null) OnSuccess();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Gates] hold completion threw: {e}"); }
            finally { Destroy(gameObject); }
        }

        private void OnDestroy()
        {
            if (_handsLocked)
            {
                var mc = Singleton<GameWorld>.Instance?.MainPlayer?.MovementContext;
                if (mc != null) mc.BlockFirearms = false;
                TerminalHoldLock.Release();
            }
            if (_panelShown && Owner != null) Owner.CloseObjectivesPanel();
        }
    }

    // own the gate switches' action menus (the icebreaker chain-door pattern) —
    // matched by GO NAME in a Terminal scene, not by switch Id (id fields are
    // drift-suspect in the rip; names survived)
    [HarmonyPatch(typeof(GetActionsClass), "smethod_11")]
    internal static class Patch_GateSwitchActions
    {
        private static void Replace(ref ActionsReturnClass result, ActionsTypesClass act)
        {
            if (result == null) result = new ActionsReturnClass { Actions = new List<ActionsTypesClass> { act } };
            else { result.Actions.Clear(); result.Actions.Add(act); }
        }

        private static bool Ours(EFT.Interactive.Switch sw)
        {
            try
            {
                var sc = sw.gameObject.scene.name;
                return sc != null && sc.StartsWith("Terminal", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void Postfix(ref ActionsReturnClass __result, GamePlayerOwner owner, EFT.Interactive.Switch interactiveSwitch)
        {
            try
            {
                if (!Plugin.GatesExplosion.Value || interactiveSwitch == null || !Ours(interactiveSwitch)) return;
                var goName = interactiveSwitch.gameObject.name;

                if (goName == "Switch_bomb")
                {
                    if (TerminalGatesExplosion.Planted || TerminalGatesExplosion.Opened)
                    {
                        if (__result != null) __result.Actions.Clear();
                        __result = null;
                        return;
                    }
                    var player = Singleton<GameWorld>.Instance?.MainPlayer;
                    bool hasCharge = player != null && TerminalGatesExplosion.FindCharge(player) != null;
                    var sw = interactiveSwitch;
                    Replace(ref __result, new ActionsTypesClass
                    {
                        Name = "Plant",
                        Disabled = !hasCharge,
                        Action = () =>
                        {
                            var go = new GameObject("Terminal_GatePlant");
                            var s = go.AddComponent<GateHoldSession>();
                            s.Owner = owner;
                            s.Anchor = sw.transform;
                            s.Seconds = TerminalGatesExplosion.PlantSeconds;
                            s.PanelText = "Planting charge {0:F1}";
                            s.FoleyClip = "amb_terminal_interactive_c6_plant_activate";
                            s.OnSuccess = () =>
                            {
                                var p = Singleton<GameWorld>.Instance?.MainPlayer;
                                if (p == null || !TerminalGatesExplosion.ConsumeCharge(p))
                                {
                                    Plugin.Log.LogDebug("[Gates] hold finished but no charge — cancelled");
                                    return;
                                }
                                TerminalGatesExplosion.Planted = true;
                                // the 10.5s timer bed runs the fuse's length — the native
                                // HandlerDelay boom lands over its tail
                                TerminalGatesExplosion.PlayAt(
                                    TerminalGatesExplosion.FindClip("amb_terminal_interactive_c6_timer"),
                                    sw.transform.position, 30f);
                                TerminalGatesExplosion.Emit(TerminalGatesExplosion.PlaceTrigger);
                                try { owner?.ClearInteractionState(); } catch { }
                            };
                        },
                    });
                }
                else if (goName == "SwitchBoard")
                {
                    if (TerminalGatesExplosion.Opened || TerminalGatesExplosion.Planted)
                    {
                        // planted = the gate's fate is sealed; opened = nothing left
                        if (__result != null) __result.Actions.Clear();
                        __result = null;
                        return;
                    }
                    var player = Singleton<GameWorld>.Instance?.MainPlayer;
                    bool elite = false;
                    try { elite = player != null && player.Skills.Strength.IsEliteLevel; } catch { }
                    var sw = interactiveSwitch;
                    Replace(ref __result, new ActionsTypesClass
                    {
                        Name = elite ? "Open" : "Open (requires Elite Strength)",
                        Disabled = !elite,
                        Action = () =>
                        {
                            var go = new GameObject("Terminal_GateOpen");
                            var s = go.AddComponent<GateHoldSession>();
                            s.Owner = owner;
                            s.Anchor = sw.transform;
                            s.Seconds = TerminalGatesExplosion.OpenSeconds;
                            s.PanelText = "Opening gate {0:F1}";
                            // authored: solo/duo open triggers both play the handle clip
                            s.FoleyClip = "amb_terminal_interactive_gates_02_handle_open";
                            s.OnSuccess = () =>
                            {
                                TerminalGatesExplosion.Opened = true;
                                TerminalGatesExplosion.Emit(TerminalGatesExplosion.SoloOpenTrigger);
                                try { owner?.ClearInteractionState(); } catch { }
                            };
                        },
                    });
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Gates] actions patch threw: {e.Message}"); }
        }
    }
}
