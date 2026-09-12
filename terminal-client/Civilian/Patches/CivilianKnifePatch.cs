using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.Terminal.Civilian.Patches
{
    // 1.0's CivilianBotWeaponSelector redirects EVERY weapon-change request to
    // the Scabbard — the pistol satisfies the activation pipeline (melee-only
    // bots never leave PreActive) but never reaches their hands. 4.0 ships the
    // same trick as the INFECTED selector (InfectedBotWeaponSelector); civilians get the plain
    // base selector, so a role-guarded prefix on the base virtual reproduces it.
    // safe against the virtual-base trap: only infected roles ride the override.
    internal sealed class CivilianKnifePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotWeaponSelector), nameof(BotWeaponSelector.TryChangeToSlot));

        [PatchPrefix]
        private static void PatchPrefix(BotWeaponSelector __instance, ref EquipmentSlot slot, ref bool changeToMain)
        {
            try
            {
                var owner = __instance._owner;
                var role = owner?.Profile?.Info?.Settings?.Role;
                if (role == null || !CivilianConstants.IsCivilian(role.Value)) return;
                if (slot == EquipmentSlot.Scabbard) return;
                slot = EquipmentSlot.Scabbard;
                changeToMain = false;
            }
            catch { }
        }
    }
}
