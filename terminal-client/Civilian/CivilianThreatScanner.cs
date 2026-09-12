using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.Terminal.Civilian
{
    // nearest entity a civilian should flee from. two channels:
    //  1. VISUAL — non-civilian with a firearm drawn, in radius, with LoS
    //     (direction-agnostic; geometry is the only gate)
    //  2. AUDIO — a recent gunshot stamped by GunshotHearingPatch; bypasses LoS
    // visual wins over audio when both are present.
    internal static class CivilianThreatScanner
    {
        public struct ThreatResult
        {
            // null when the trigger was audio-only — use ThreatPosition
            public IPlayer Threat;
            public Vector3 ThreatPosition;
            public float Distance;
            public bool HasThreat;
        }

        public static ThreatResult FindNearestThreat(BotOwner civilian, float radius)
        {
            var result = default(ThreatResult);
            if (civilian == null) return result;

            var world = Singleton<GameWorld>.Instance;
            if (world == null) return result;

            var civilianPos = civilian.Position;
            var bestDist = float.MaxValue;
            IPlayer bestThreat = null;

            var players = world.AllAlivePlayersList;
            if (players == null) return result;

            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player == null || !player.HealthController.IsAlive) continue;
                if (ReferenceEquals(player, civilian.GetPlayer)) continue;
                if (CivilianConstants.IsCivilian(player.Profile.Info.Settings.Role)) continue;

                if (!HasRangedWeaponDrawn(player)) continue;

                var dist = Vector3.Distance(civilianPos, player.Position);
                if (dist > radius || dist >= bestDist) continue;

                if (!HasLineOfSight(civilian, player)) continue;

                bestDist = dist;
                bestThreat = player;
            }

            if (bestThreat != null)
            {
                result.Threat = bestThreat;
                result.ThreatPosition = bestThreat.Position;
                result.Distance = bestDist;
                result.HasThreat = true;
                return result;
            }

            // audio fallback: position is where the shot was FIRED, not where the
            // shooter is now — restamped per shot, so the civilian adapts
            var state = CivilianFleeState.Get(civilian);
            if (Time.time - state.LastGunshotAt < CivilianConstants.GunshotMemorySeconds)
            {
                result.ThreatPosition = state.LastGunshotPos;
                result.Distance = Vector3.Distance(civilianPos, state.LastGunshotPos);
                result.HasThreat = true;
            }

            return result;
        }

        private static bool HasRangedWeaponDrawn(IPlayer player)
        {
            // only concrete Player exposes HandsController
            if (player is Player concrete)
            {
                var hc = concrete.HandsController;
                return hc is Player.FirearmController;
            }
            return false;
        }

        // same layers EFT's own AI visibility uses; cast from weapon root to the
        // threat's torso (feet poke under railings). null root = fail OPEN.
        private static bool HasLineOfSight(BotOwner civilian, IPlayer threat)
        {
            var weaponRoot = civilian.WeaponRoot;
            if (weaponRoot == null) return true;

            var from = weaponRoot.position;
            var to = threat.Position + Vector3.up * 1.3f;

            return !Physics.Linecast(from, to, LayersMaskController.HighPolyWithTerrainMask);
        }
    }
}
