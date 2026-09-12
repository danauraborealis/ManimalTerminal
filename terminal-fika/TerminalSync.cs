using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.GameTriggers;
using EFT.Interactive;
using EFT.InventoryLogic;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using UnityEngine;

namespace Manimal.Terminal.Fika
{
    internal sealed class TerminalSync : IDisposable
    {
        internal static readonly HashSet<string> SharedTriggers = new HashSet<string>(StringComparer.Ordinal)
        {
            TerminalGatesExplosion.ExplosionTrigger,
            "Switch_crane_enter", "Fall_01_2944538615", "Play_Crane_Explosive",
            "To_Play_Contusion_Effect_01", "Play_Contusion_Effect_01", "Mortar_Crane_Play", "Switch_crane"
        };
        private readonly TerminalStateLedger _ledger = new TerminalStateLedger();
        private readonly Queue<(TerminalPacket packet, NetPeer peer)> _inbound = new Queue<(TerminalPacket, NetPeer)>();
        private readonly List<TerminalPacket> _pending = new List<TerminalPacket>();
        private readonly TerminalAppliedState _applied = new TerminalAppliedState();
        private readonly HashSet<string> _ready = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _introDone = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _attackDone = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _peerProfiles = new Dictionary<int, string>();
        private readonly Dictionary<string, (EDoorState state, string key)> _doors = new Dictionary<string, (EDoorState, string)>();
        private readonly List<Player> _humans = new List<Player>();
        private IFikaNetworkManager _manager;
        private SyncDriver _driver;
        private WorldInteractiveObject[] _interactables = Array.Empty<WorldInteractiveObject>();
        private string _raid;
        private string _planter;
        private string _plantToken;
        private readonly HashSet<string> _usedPlantTokens = new HashSet<string>();
        private double _clockOffset, _nextHello, _nextScan, _nextRoster, _nextStage, _introFinished = -1;
        private int _startedFrame;
        private int _pumpLayoutFrame = -1;
        private double _evacDeadline = -1;
        private bool _snapshotReady, _warnedVersion, _clockReady;
        private bool _checkpointUnlocked;
        private double _nextWarning;
        private bool Server => FikaBackendUtils.IsServer;
        private static string Me => Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId ?? "";
        private static double LocalNow => Time.realtimeSinceStartupAsDouble;

        internal TerminalSync()
        {
            TerminalCoop.IsAuthority = () => Server;
            TerminalCoop.Send = Request;
            TerminalCoop.Plant = p => { if (TerminalCoop.IsHuman(p)) Request(TerminalEvent.GateReserve, "", p.ProfileId, 0); };
            TerminalCoop.SharedClock = () => LocalNow + (Server ? 0 : _clockOffset);
            TerminalCoop.RaidReset += Reset;
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManager);
            FikaEventDispatcher.SubscribeEvent<FikaRaidStartedEvent>(OnRaidStarted);
            if (Singleton<IFikaNetworkManager>.Instantiated) Register(Singleton<IFikaNetworkManager>.Instance);
        }

        private void OnManager(FikaNetworkManagerCreatedEvent e) => Register(e.Manager);
        private void OnRaidStarted(FikaRaidStartedEvent e)
        {
            if (!TerminalGate.On) return;
            TerminalCoop.MarkFikaRaid();
            if (!_driver) StartRaid();
            TerminalCoop.RaidStarted = true;
            _startedFrame = Time.frameCount;
            _nextHello = 0;
            FikaAddonPlugin.Log.LogInfo($"[TerminalSync] Fika raid-start event received — story/input systems released (authority={e.IsServer})");
        }
        private void Register(IFikaNetworkManager manager)
        {
            if (ReferenceEquals(manager, _manager)) return;
            if (_manager != null) { try { _manager.UnregisterPacket<TerminalPacket>(); } catch { } }
            _manager = manager;
            manager.RegisterPacket<TerminalPacket, NetPeer>((p, peer) => _inbound.Enqueue((p, peer)));
            FikaAddonPlugin.Log.LogInfo("[TerminalSync] reliable packet handler registered");
        }

        private void Reset()
        {
            if (_driver) UnityEngine.Object.Destroy(_driver);
            _driver = null;
            _ledger.Clear(); _pending.Clear(); _inbound.Clear(); _applied.Clear();
            _ready.Clear(); _introDone.Clear(); _attackDone.Clear(); _peerProfiles.Clear(); _doors.Clear();
            _usedPlantTokens.Clear(); _planter = _plantToken = null;
            _interactables = Array.Empty<WorldInteractiveObject>();
            _raid = null; _clockOffset = _nextHello = _nextScan = _nextRoster = _nextStage = 0; _introFinished = -1;
            _snapshotReady = _warnedVersion = _clockReady = false;
            _checkpointUnlocked = false;
            _nextWarning = LocalNow + 30;
            _pumpLayoutFrame = -1; _evacDeadline = -1;
            TerminalGearTax.CoopCabinets.Clear(); TerminalGearTax.CoopFinished.Clear(); TerminalGearTax.CoopPrepared.Clear();
        }

        internal void StartRaid()
        {
            if (_driver) return;
            _raid = FikaBackendUtils.ServerGuid + "|" + FikaBackendUtils.RaidCode;
            // GameWorld.OnGameStarted happens during Fika's ~30% pre-raid pause.
            // Initialize local scene state here, but do not start story/input work
            // until FikaRaidStartedEvent fires after the deployment countdown.
            TerminalCoop.RaidStarted = false;
            TerminalCoop.SyncReady = Server;
            _startedFrame = Time.frameCount;
            _driver = Singleton<GameWorld>.Instance.gameObject.AddComponent<SyncDriver>();
            _driver.Sync = this;
            HealDoors();
            FikaAddonPlugin.Log.LogInfo($"[TerminalSync] raid started: authority={Server}, localHuman={TerminalCoop.LocalHuman}");
        }

        internal void Request(TerminalEvent kind, string key, string actor, int number)
        {
            if (!TerminalCoop.Active || !TerminalGate.On) return;
            var packet = New(0);
            packet.Kind = (byte)kind; packet.Key = key ?? "";
            packet.Actor = string.IsNullOrEmpty(actor) ? Me : actor; packet.Number = number;
            if (Server) _inbound.Enqueue((packet, null));
            else Send(packet); // requests are NEVER auto-relayed; the host validates and commits
        }

        private TerminalPacket New(byte op) => new TerminalPacket
        {
            Version = TerminalPacket.Protocol, Operation = op, Raid = _raid ?? (FikaBackendUtils.ServerGuid + "|" + FikaBackendUtils.RaidCode),
            Sent = LocalNow, Key = "", Actor = Me
        };
        private void Send(TerminalPacket packet, NetPeer peer = null)
        {
            if (_manager == null) return;
            packet.Sent = LocalNow;
            if (peer != null) _manager.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
            else _manager.SendData(ref packet, DeliveryMethod.ReliableOrdered, false);
        }

        internal void Tick()
        {
            if (!TerminalCoop.RaidStarted || !TerminalGate.On || _manager == null) return;
            double now = LocalNow;
            // Native handlers subscribe in Start(), one frame after staging. Scene
            // discovery is unnecessary every rendered frame and can be expensive on
            // Terminal's very large hierarchy, especially on a visual Fika host.
            if (now >= _nextStage)
            {
                _nextStage = now + 0.50;
                TerminalGatesExplosion.TryStage(); TerminalCraneFalling.TryStage();
                TerminalFinalExit.TryStage(); TerminalPumpStation.TryStage();
            }
            if (Time.frameCount <= _startedFrame + 2) return;
            bool rosterTick = now >= _nextRoster;
            if (rosterTick)
            {
                _nextRoster = now + 0.50;
                TerminalCoop.CollectHumans(_humans);
            }
            int count = _inbound.Count;
            for (int i = 0; i < count; i++)
            {
                var item = _inbound.Dequeue();
                try { Receive(item.packet, item.peer); }
                catch (Exception e) { FikaAddonPlugin.Log.LogError($"[TerminalSync] receive failed: {e}"); }
            }
            if (LocalNow >= _nextHello)
            {
                _nextHello = LocalNow + (_snapshotReady ? 10 : 2);
                if (!Server) { var hello = New(2); hello.Time = LocalNow; Send(hello); }
                else if (TerminalCoop.LocalHuman) _ready.Add(Me);
            }
            if (Server && rosterTick) AdvanceStory();
            // Ordered retries: a packet arriving before its scene/sidecar exists is
            // retained, not silently discarded. Layout must precede repair/drain.
            for (int i = 0; (Server || _clockReady) && i < _pending.Count;)
            {
                var packet = _pending[i];
                if (_applied.Contains(packet.Kind, packet.Key, packet.Revision)) { _pending.RemoveAt(i); continue; }
                bool applied;
                try { applied = Apply(packet); }
                catch (Exception e) { FikaAddonPlugin.Log.LogError($"[TerminalSync] apply kind={packet.Kind} failed: {e}"); applied = false; }
                if (!applied) { i++; continue; }
                _applied.Mark(packet.Kind, packet.Key, packet.Revision); _pending.RemoveAt(i);
            }
            if (_snapshotReady && _pending.Count == 0) TerminalCoop.SyncReady = true;
            if (LocalNow >= _nextWarning)
            {
                _nextWarning = LocalNow + 30;
                var waiting = new List<string>();
                foreach (var human in _humans)
                    if (!_ready.Contains(human.ProfileId)) waiting.Add(human.Profile.Nickname);
                if (Server && waiting.Count > 0)
                    FikaAddonPlugin.Log.LogWarning("[TerminalSync] waiting for matching addons on: " + string.Join(", ", waiting));
                else if (!Server && !_snapshotReady)
                    FikaAddonPlugin.Log.LogError("[TerminalSync] host has not answered — install this addon on the hosting/headless game too.");
                if (_pending.Count > 0)
                    FikaAddonPlugin.Log.LogWarning($"[TerminalSync] {_pending.Count} state(s) waiting for scene components; first kind={_pending[0].Kind} key='{_pending[0].Key}'");
            }
            if (LocalNow >= _nextScan)
            {
                _nextScan = LocalNow + 0.25;
                if (Server) { UnlockCheckpoint(); ScanDoors(); WatchCrane(); }
                if (_evacDeadline >= 0) TerminalFinalExit.ApplyCountdown(_evacDeadline - TerminalCoop.Now, false);
            }
        }

        private void Receive(TerminalPacket p, NetPeer peer)
        {
            if (p.Version != TerminalPacket.Protocol)
            {
                if (!_warnedVersion) FikaAddonPlugin.Log.LogError("[TerminalSync] incompatible protocol — install matching Terminal + addon versions on all players and the host.");
                _warnedVersion = true; return;
            }
            if (!string.Equals(p.Raid, _raid, StringComparison.Ordinal)) return;
            if (Server)
            {
                if (p.Operation == 2 && peer != null)
                {
                    if (!_humans.Exists(h => h.ProfileId == p.Actor)) return;
                    _peerProfiles[peer.Id] = p.Actor;
                    _ready.Add(p.Actor);
                    var reply = New(3); reply.Time = p.Time; Send(reply, peer);
                    foreach (var entry in _ledger.Snapshot()) Send(Packet(entry, true), peer);
                    Send(New(4), peer);
                    return;
                }
                if (p.Operation != 0) return; // clients cannot submit authoritative commits
                if (peer != null)
                {
                    if (!_peerProfiles.TryGetValue(peer.Id, out var actor) || actor != p.Actor) return;
                    if (!_humans.Exists(h => h.ProfileId == actor)) return;
                }
                HandleRequest(p, peer);
                return;
            }
            if (p.Operation == 3)
            {
                double roundTrip = Math.Max(0, LocalNow - p.Time);
                _clockOffset = p.Sent + roundTrip * 0.5 - LocalNow;
                _clockReady = true;
            }
            else if (p.Operation == 4) _snapshotReady = true;
            else if (p.Operation == 1) _pending.Add(p);
            else if (p.Operation == 5 && p.Actor == Me) ConsumeGrant(p.Key);
            else if (p.Operation == 6 && p.Actor == Me) TerminalPumpStation.ApplyCoopShock(p.Key);
        }

        private bool Has(TerminalEvent kind, string key = "") => _ledger.Get((byte)kind, key) != null;
        private void HandleRequest(TerminalPacket p, NetPeer peer)
        {
            var kind = (TerminalEvent)p.Kind;
            switch (kind)
            {
                case TerminalEvent.IntroDone: _introDone.Add(p.Actor); return;
                case TerminalEvent.AttackDone: _attackDone.Add(p.Actor); return;
                case TerminalEvent.GearCabinet:
                    if (!Plugin.GearConfiscation.Value) return;
                    ReserveCabinet(p.Actor); return;
                case TerminalEvent.GearDone:
                    if (Has(TerminalEvent.GearCabinet, p.Actor)) Commit(kind, p.Actor);
                    return;
                case TerminalEvent.GearPrepared:
                    return; // host-owned phase; clients cannot declare a cabinet cleared
                case TerminalEvent.GateReserve:
                    if (!Plugin.GatesExplosion.Value || Has(TerminalEvent.GatePlant) || Has(TerminalEvent.GateOpen)) return;
                    if (_planter != null && _planter != p.Actor) return;
                    var planter = _humans.Find(h => h.ProfileId == p.Actor);
                    if (planter == null || TerminalGatesExplosion.FindCharge(planter) == null) return;
                    if (_planter == null)
                    {
                        _planter = p.Actor; _plantToken = Guid.NewGuid().ToString("N");
                    }
                    var grant = New(5); grant.Actor = p.Actor; grant.Key = _plantToken;
                    if (peer == null) ConsumeGrant(_plantToken); else Send(grant, peer);
                    return;
                case TerminalEvent.GateCancel:
                    if (_planter == p.Actor && _plantToken == p.Key) _planter = _plantToken = null;
                    return;
                case TerminalEvent.GatePlant:
                    if (_planter != p.Actor || _plantToken != p.Key || Has(TerminalEvent.GateOpen)) return;
                    Commit(kind, "", p.Actor); return;
                case TerminalEvent.GateOpen:
                    if (_planter != null || Has(TerminalEvent.GatePlant)) return;
                    var opener = _humans.Find(h => h.ProfileId == p.Actor);
                    if (opener == null || !opener.Skills.Strength.IsEliteLevel) return;
                    Commit(kind, "", p.Actor); return;
                case TerminalEvent.PumpRepair:
                    if (!Plugin.PumpStation.Value || !TerminalPumpStation.Ready) return;
                    if (Has(kind, p.Key) || TerminalPumpStation.FindBroken(p.Key) == null) return;
                    var repairer = _humans.Find(h => h.ProfileId == p.Actor);
                    if (repairer == null) return;
                    if (!TerminalPumpStation.HasMultitool(repairer) && UnityEngine.Random.value >= 0.1f)
                    {
                        var shock = New(6); shock.Actor = p.Actor; shock.Key = p.Key;
                        if (peer == null) TerminalPumpStation.ApplyCoopShock(p.Key); else Send(shock, peer);
                        return;
                    }
                    Commit(kind, p.Key, p.Actor); return;
                case TerminalEvent.PumpDrain:
                    if (!TerminalPumpStation.PowerFixed) return;
                    Commit(kind, "", p.Actor); return;
                case TerminalEvent.Trigger:
                    if (peer != null || !SharedTriggers.Contains(p.Key)) return;
                    if (p.Key == "Switch_crane_enter" && !_humans.Exists(h => h.ProfileId == p.Actor)) return;
                    if (p.Key == TerminalGatesExplosion.ExplosionTrigger && !Has(TerminalEvent.GatePlant)) return;
                    Commit(kind, p.Key, p.Actor); return;
                case TerminalEvent.Wave:
                    if (peer == null) Commit(kind, p.Key);
                    return;
                case TerminalEvent.Evac:
                    if (peer != null) return;
                    var timer = Singleton<AbstractGame>.Instance?.GameTimer;
                    if (timer == null) return;
                    double remaining = Math.Max(0, ((timer.SessionTime ?? TimeSpan.Zero) - timer.PastTime).TotalSeconds);
                    Commit(kind, "", "", 0, LocalNow + Math.Min(180, remaining)); return;
            }
        }

        private async void ConsumeGrant(string token)
        {
            if (!_usedPlantTokens.Add(token)) return;
            string raid = _raid;
            try
            {
                bool success = await TerminalGatesExplosion.ConsumeChargeCoop(Singleton<GameWorld>.Instance.MainPlayer);
                if (_raid == raid && TerminalGate.On)
                    Request(success ? TerminalEvent.GatePlant : TerminalEvent.GateCancel, token, Me, 0);
            }
            catch (Exception e)
            {
                FikaAddonPlugin.Log.LogError($"[TerminalSync] charge transaction failed: {e}");
                if (_raid == raid) Request(TerminalEvent.GateCancel, token, Me, 0);
            }
        }

        private void ReserveCabinet(string profile)
        {
            if (Has(TerminalEvent.GearCabinet, profile)) return;
            var reserved = new HashSet<string>();
            foreach (var entry in _ledger.Snapshot())
                if (entry.Kind == (byte)TerminalEvent.GearCabinet) reserved.Add(entry.Actor);
            var available = new List<LootableContainer>();
            foreach (var c in UnityEngine.Object.FindObjectsOfType<LootableContainer>())
                if (c.ItemOwner?.RootItem is CompoundItem root && root.Grids?.Length > 0
                    && (c.name.IndexOf("valberg", StringComparison.OrdinalIgnoreCase) >= 0
                        || (c.transform.parent?.name ?? "").IndexOf("valberg", StringComparison.OrdinalIgnoreCase) >= 0)
                    && !reserved.Contains(c.ItemOwner.ID)) available.Add(c);
            available.Sort((a, b) => StringComparer.Ordinal.Compare(a.ItemOwner.ID, b.ItemOwner.ID));
            if (available.Count == 0) return; // never clear a teammate's cabinet or remove gear without a destination
            var cabinet = available[UnityEngine.Random.Range(0, available.Count)];
            Commit(TerminalEvent.GearCabinet, profile, cabinet.ItemOwner.ID);
            // Clear only the cabinet reserved for this profile. This is a Terminal
            // state event, applied identically on every live peer, so it never sends
            // Fika a cross-owner inventory descriptor. Unreserved cabinets retain
            // their generated loot. Replays preserve gear already deposited there.
            Commit(TerminalEvent.GearPrepared, profile);
        }

        private void AdvanceStory()
        {
            if (!Has(TerminalEvent.PumpLayout) && _humans.Count > 0 && _humans.TrueForAll(h => _ready.Contains(h.ProfileId)))
                Commit(TerminalEvent.PumpLayout, "", Guid.NewGuid().GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture), _humans.Count);
            if (_humans.Count == 0) return; // headless dummy never starts story or counts toward a barrier
            if (!Has(TerminalEvent.IntroStart) && _humans.TrueForAll(h => _ready.Contains(h.ProfileId)))
                Commit(TerminalEvent.IntroStart, "", "", Plugin.IntroCutscene.Value ? 0 : 1);
            if (Has(TerminalEvent.IntroStart) && !Has(TerminalEvent.IntroDone) && _humans.TrueForAll(h => _introDone.Contains(h.ProfileId)))
            {
                _introFinished = LocalNow;
                Commit(TerminalEvent.IntroDone);
            }
            if (_introFinished >= 0 && !Has(TerminalEvent.AttackStart) && LocalNow - _introFinished >= Plugin.AttackCutsceneDelay.Value)
                Commit(TerminalEvent.AttackStart, "", "", Plugin.AttackCutscene.Value ? 0 : 1);
            if (Has(TerminalEvent.AttackStart) && !Has(TerminalEvent.AttackDone) && _humans.TrueForAll(h => _attackDone.Contains(h.ProfileId)))
                Commit(TerminalEvent.AttackDone);
            // Gear may ask before the host has bound containers. Retry only unassigned
            // active humans; assignments survive disconnect/reconnect for this raid.
            if (Plugin.GearConfiscation.Value)
                foreach (var h in _humans)
                    if (_ready.Contains(h.ProfileId)) ReserveCabinet(h.ProfileId);
        }

        private void Commit(TerminalEvent kind, string key = "", string actor = "", int number = 0, double? time = null, bool replace = false)
        {
            var entry = _ledger.Put((byte)kind, key, actor, number, time ?? LocalNow, replace);
            if (entry == null) return;
            var packet = Packet(entry, false);
            _pending.Add(packet); Send(packet);
            FikaAddonPlugin.Log.LogInfo($"[TerminalSync] commit #{entry.Revision} {kind} '{key}'");
        }
        private TerminalPacket Packet(TerminalStateLedger.Entry e, bool replay)
        {
            var p = New(1); p.Revision = e.Revision; p.Kind = e.Kind; p.Key = e.Key;
            p.Actor = e.Actor; p.Number = e.Number; p.Time = e.Time; p.Replay = replay; return p;
        }

        private bool Apply(TerminalPacket p)
        {
            var kind = (TerminalEvent)p.Kind;
            bool priorApply = TerminalCoop.Applying, priorReplay = TerminalCoop.Replaying;
            TerminalCoop.Applying = true;
            TerminalCoop.Replaying = p.Replay || (kind == TerminalEvent.Trigger && TerminalCoop.Now - p.Time > 3);
            try
            {
                switch (kind)
                {
                    case TerminalEvent.PumpLayout:
                        TerminalPumpStation.ApplyCoopLayout(p.Number, int.Parse(p.Actor, System.Globalization.CultureInfo.InvariantCulture));
                        _pumpLayoutFrame = Time.frameCount;
                        return true;
                    case TerminalEvent.PumpRepair:
                        if (_pumpLayoutFrame < 0 || Time.frameCount <= _pumpLayoutFrame) return false;
                        return TerminalPumpStation.ApplyCoopRepair(p.Key);
                    case TerminalEvent.PumpDrain:
                        if (!TerminalPumpStation.Ready || !TerminalPumpStation.PowerFixed) return false;
                        TerminalPumpStation.Drain(); return true;
                    case TerminalEvent.GearCabinet: TerminalGearTax.CoopCabinets[p.Key] = p.Actor; return true;
                    case TerminalEvent.GearDone: TerminalGearTax.CoopFinished.Add(p.Key); return true;
                    case TerminalEvent.GearPrepared:
                        return TerminalGearTax.TaxHost.ApplyCoopPreparation(p.Key, !p.Replay);
                    case TerminalEvent.IntroStart:
                        TerminalCoop.IntroStarted = p.Time;
                        // A reconnect does not replay a completed introduction.
                        TerminalIntroCutscene.PlayCoop(p.Number != 0 || (p.Replay && TerminalCoop.Now - p.Time > 180)); return true;
                    case TerminalEvent.IntroDone: TerminalCoop.IntroReleased = true; return true;
                    case TerminalEvent.AttackStart:
                        TerminalCoop.AttackStarted = p.Time;
                        TerminalAttackCutscene.PlayCoop(p.Number != 0 || (p.Replay && TerminalCoop.Now - p.Time > 50)); return true;
                    case TerminalEvent.AttackDone: TerminalCoop.AttackReleased = true; return true;
                    case TerminalEvent.GatePlant:
                        if (!TerminalGatesExplosion.Ready) return false;
                        TerminalGatesExplosion.Planted = true;
                        Emit(TerminalGatesExplosion.PlaceTrigger, p.Actor);
                        if (!p.Replay) TerminalGatesExplosion.PlayAt(TerminalGatesExplosion.FindClip("amb_terminal_interactive_c6_timer"), TerminalGatesExplosion.RigPos(), 30);
                        return true;
                    case TerminalEvent.GateOpen:
                        if (!TerminalGatesExplosion.Ready) return false;
                        TerminalGatesExplosion.Opened = true; Emit(TerminalGatesExplosion.SoloOpenTrigger, p.Actor); return true;
                    case TerminalEvent.Trigger:
                        if (p.Replay && p.Key != "Switch_crane" && p.Key != TerminalGatesExplosion.ExplosionTrigger
                            && _pending.Exists(e => e.Kind == (byte)TerminalEvent.Trigger && e.Key == "Switch_crane")) return true;
                        if (p.Key == TerminalGatesExplosion.ExplosionTrigger)
                        {
                            if (!TerminalGatesExplosion.Ready) return false;
                            TerminalGatesExplosion.Opened = true;
                        }
                        else if (!TerminalCraneFalling.Ready) return false;
                        Emit(p.Key, p.Actor); return true;
                    case TerminalEvent.Evac:
                        _evacDeadline = p.Time;
                        return TerminalFinalExit.ApplyCountdown(p.Time - TerminalCoop.Now, !p.Replay || p.Time - TerminalCoop.Now > 172);
                    case TerminalEvent.Door:
                        var door = Singleton<GameWorld>.Instance.FindDoor(p.Key);
                        if (door == null) { HealDoors(); door = Singleton<GameWorld>.Instance.FindDoor(p.Key); }
                        if (door == null) return false;
                        if (Server) return true; // already committed by the host/native interaction
                        door.KeyId = p.Actor;
                        door.Snap |= (EDoorState)p.Number;
                        if (door.DoorState != (EDoorState)p.Number)
                        {
                            // This is a committed state, not a new local interaction.
                            // SetDoorState would emit triggers using a null InteractingPlayer.
                            if (door.DoorState == EDoorState.Interacting) return false;
                            door.SetInitialSyncState(new WorldInteractiveObject.InteractiveObjectStatusInfo { State = (byte)p.Number });
                        }
                        return true;
                    case TerminalEvent.Wave:
                        if (!Server) TerminalCrewJobs.NoteEvent(p.Key); // no client bot spawning
                        return true;
                }
                return true;
            }
            finally { TerminalCoop.Applying = priorApply; TerminalCoop.Replaying = priorReplay; }
        }

        private static void Emit(string trigger, string actor)
        {
            // Feed the local native graph without also asking Fika/BSG's stripped
            // trigger transport to send another copy. Keep the originating human.
            var ev = EFT.GlobalEvents.GlobalEventsController.Instance.CreateCommonEvent<TriggerEvent>();
            ev.TriggerId = TerminalRigFill.StableHash(trigger); ev.OriginProfileId = actor ?? ""; ev.Invoke();
        }

        private void HealDoors()
        {
            var world = Singleton<GameWorld>.Instance;
            if (world?.World == null) return;
            var interactables = new List<WorldInteractiveObject>();
            foreach (var d in UnityEngine.Object.FindObjectsOfType<WorldInteractiveObject>(true))
                if (d && (d.gameObject.scene.name ?? "").StartsWith("Terminal", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(d.Id)) interactables.Add(d);
            interactables.Sort((a, b) =>
            {
                int result = StringComparer.Ordinal.Compare(a.Id, b.Id);
                var pa = a.transform.position; var pb = b.transform.position;
                if (result == 0) result = pa.x.CompareTo(pb.x);
                if (result == 0) result = pa.y.CompareTo(pb.y);
                if (result == 0) result = pa.z.CompareTo(pb.z);
                return result;
            });
            _interactables = interactables.ToArray();
            int added = 0;
            foreach (var door in _interactables)
            {
                if (world.FindDoor(door.Id) == null) { world.World.RegisterWorldInteractionObject(door); added++; }
                if (!_doors.ContainsKey(door.Id)) _doors[door.Id] = (door.DoorState, door.KeyId ?? "");
            }
            FikaAddonPlugin.Log.LogInfo($"[TerminalSync] door registry: {added} missing interactables registered, {_interactables.Length} tracked");
        }
        private void ScanDoors()
        {
            foreach (var door in _interactables)
            {
                if (!door) continue;
                var state = door.DoorState;
                if (state != EDoorState.Shut && state != EDoorState.Open && state != EDoorState.Locked) continue;
                var current = (state, door.KeyId ?? "");
                if (_doors.TryGetValue(door.Id, out var previous) && previous == current) continue;
                _doors[door.Id] = current;
                Commit(TerminalEvent.Door, door.Id, current.Item2, (int)state, replace: true);
            }
        }
        private void UnlockCheckpoint()
        {
            if (_checkpointUnlocked || !Has(TerminalEvent.AttackDone)) return;
            string id = TerminalSoundRig.CheckpointDoorId();
            if (string.IsNullOrEmpty(id)) return;
            var door = Singleton<GameWorld>.Instance.FindDoor(id);
            if (door == null) { HealDoors(); return; }
            door.Snap |= EDoorState.Locked | EDoorState.Shut | EDoorState.Open;
            door.KeyId = "";
            if (door.DoorState == EDoorState.Locked) door.DoorState = EDoorState.Shut;
            _checkpointUnlocked = true;
        }
        private void WatchCrane()
        {
            if (!Plugin.CraneFalling.Value || Has(TerminalEvent.Trigger, "Switch_crane_enter")) return;
            var root = TerminalRigFill.FindRootNamed("Terminal_Crane_Falling");
            if (!root) return;
            foreach (var zone in root.GetComponentsInChildren<TriggerZone>(true))
            {
                bool entersCrane = false;
                foreach (var trigger in zone.OutputTriggerIds)
                    if (trigger == "Switch_crane_enter") { entersCrane = true; break; }
                if (!entersCrane) continue;
                var box = zone.GetComponent<BoxCollider>();
                if (!box || !box.enabled || !box.gameObject.activeInHierarchy) continue;
                foreach (var human in _humans)
                    if (new Bounds(box.center, box.size).Contains(box.transform.InverseTransformPoint(human.Position)))
                    { Commit(TerminalEvent.Trigger, "Switch_crane_enter", human.ProfileId); return; }
            }
        }

        public void Dispose()
        {
            FikaEventDispatcher.UnsubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManager);
            FikaEventDispatcher.UnsubscribeEvent<FikaRaidStartedEvent>(OnRaidStarted);
            TerminalCoop.RaidReset -= Reset;
            TerminalCoop.Send = null; TerminalCoop.Plant = null; TerminalCoop.IsAuthority = null; TerminalCoop.SharedClock = null;
            if (_manager != null) { try { _manager.UnregisterPacket<TerminalPacket>(); } catch { } }
            Reset(); _manager = null;
        }
    }

    internal sealed class SyncDriver : MonoBehaviour
    {
        internal TerminalSync Sync;
        private void Update()
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Sync?.Tick(); }
            catch (Exception e) { FikaAddonPlugin.Log.LogError($"[TerminalSync] update failed: {e}"); }
            finally { TerminalTickProfiler.Add("FikaSync", System.Diagnostics.Stopwatch.GetTimestamp() - started); }
        }
    }
}
