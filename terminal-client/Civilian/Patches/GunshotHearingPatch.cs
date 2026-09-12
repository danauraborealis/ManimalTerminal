using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.Terminal.Civilian.Patches
{
    // stamps every civilian in hearing radius with a shot timestamp + position so
    // the flee layer panics off gunfire through walls. hooks the internal shoot
    // routine (method_58) instead of the public OnShot event — the event carries
    // no shooter argument.
    internal sealed class GunshotHearingPatch : ModulePatch
    {
        // declared on base ItemHandsController; AccessTools walks the chain
        private static readonly FieldInfo PlayerField =
            AccessTools.Field(typeof(Player.FirearmController), "_player");

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player.FirearmController), "Shot");

        [PatchPostfix]
        private static void PatchPostfix(Player.FirearmController __instance)
        {
            var shooter = PlayerField?.GetValue(__instance) as Player;
            if (shooter == null || !shooter.HealthController.IsAlive) return;

            if (CivilianConstants.IsCivilian(shooter.Profile.Info.Settings.Role)) return;

            var world = Singleton<GameWorld>.Instance;
            if (world == null) return;
            var players = world.AllAlivePlayersList;
            if (players == null) return;

            var shotPos = shooter.Position;
            var radiusSqr = CivilianConstants.GunshotHearingRadius * CivilianConstants.GunshotHearingRadius;

            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || !p.HealthController.IsAlive) continue;
                if (!CivilianConstants.IsCivilian(p.Profile.Info.Settings.Role)) continue;

                var bot = p.AIData?.BotOwner;
                if (bot == null) continue;

                if ((p.Position - shotPos).sqrMagnitude > radiusSqr) continue;

                var state = CivilianFleeState.Get(bot);
                state.LastGunshotAt = Time.time;
                state.LastGunshotPos = shotPos;
            }
        }
    }
}
