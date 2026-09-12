using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using UnityEngine;

[assembly: InternalsVisibleTo("ManimalTerminalFika")]

namespace Manimal.Terminal
{
    // No Fika types may enter this assembly: solo installs must remain loadable.
    internal enum TerminalEvent : byte
    {
        PumpLayout = 1, PumpRepair, PumpDrain, GateReserve, GatePlant, GateOpen,
        Trigger, GearCabinet, GearDone, IntroStart, IntroDone, AttackStart,
        AttackDone, Evac, Door, Wave, GateCancel, GearPrepared
    }

    internal static class TerminalCoop
    {
        internal static Func<bool> IsAuthority;
        internal static Action<TerminalEvent, string, string, int> Send;
        internal static Action<Player> Plant;
        internal static event Action RaidReset;
        internal static bool Applying;
        internal static bool Replaying;
        internal static bool RaidStarted;
        internal static bool SyncReady;
        internal static double IntroStarted = -1;
        internal static bool IntroReleased;
        internal static bool AttackReleased;
        internal static double AttackStarted = -1;
        internal static Func<double> SharedClock;
        // Set explicitly by the optional addon from CoopGame.Create. During Fika's
        // pre-raid loading pause Singleton<AbstractGame> is not consistently ready,
        // so type-name detection alone can misclassify the raid as solo and launch
        // Terminal's intro over the Start Raid screen.
        private static bool _fikaRaidActive;
        internal static double Now => SharedClock?.Invoke() ?? Time.realtimeSinceStartupAsDouble;
        internal static bool Active => _fikaRaidActive
            || Singleton<AbstractGame>.Instance?.GetType().Name.IndexOf("Coop", StringComparison.OrdinalIgnoreCase) >= 0;
        internal static bool Authority => !Active || (IsAuthority?.Invoke() ?? false);
        internal static bool LocalHuman => IsHuman(Singleton<GameWorld>.Instance?.MainPlayer);

        internal static bool IsHuman(Player player) => player != null && !player.IsAI
            && player.HealthController != null && player.HealthController.IsAlive
            && !(player.Profile?.Info?.Nickname ?? "").StartsWith("headless_", StringComparison.OrdinalIgnoreCase);

        internal static void CollectHumans(List<Player> into)
        {
            into.Clear();
            var world = Singleton<GameWorld>.Instance;
            if (world?.RegisteredPlayers != null)
                foreach (var p in world.RegisteredPlayers)
                    if (p is Player human && IsHuman(human) && !into.Contains(human)) into.Add(human);
            if (IsHuman(world?.MainPlayer) && !into.Contains(world.MainPlayer)) into.Add(world.MainPlayer);
        }

        private static readonly List<Player> Nearby = new List<Player>();
        internal static Player NearestHuman(Vector3 position)
        {
            CollectHumans(Nearby);
            Player nearest = null;
            float distance = float.MaxValue;
            foreach (var player in Nearby)
            {
                float sq = (player.Position - position).sqrMagnitude;
                if (sq < distance) { distance = sq; nearest = player; }
            }
            return nearest;
        }

        internal static void Request(TerminalEvent kind, string key = "", string actor = "", int number = 0)
        {
            if (!Active || (Applying && kind != TerminalEvent.IntroDone && kind != TerminalEvent.AttackDone)) return;
            if (Send == null)
            {
                Plugin.Log.LogError("[TerminalCoop] Install the matching Terminal Fika addon on every peer, including the headless host.");
                return;
            }
            Send(kind, key, actor, number);
        }

        internal static void MarkFikaRaid() => _fikaRaidActive = true;

        internal static void Reset()
        {
            _fikaRaidActive = false;
            Applying = Replaying = RaidStarted = SyncReady = IntroReleased = AttackReleased = false;
            IntroStarted = -1;
            AttackStarted = -1;
            RaidReset?.Invoke();
        }
    }
}
