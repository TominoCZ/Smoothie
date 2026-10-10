using System.Reflection;
using EFT;
using EFT.Animations;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;
using Systems.Effects;
using UnityEngine;

namespace Smoothie;

// Small per-frame waste in vanilla code. Each one keeps the game's behaviour.
internal static class HotPaths
{
    internal static void Enable()
    {
        new FovCoroutinePatch().Enable();
        new DeadAgentsPatch().Enable();
        new MuzzleAttenuationPatch().Enable();
        new CorpseStillPatch().Enable();
        new ObservedCorpseStillPatch().Enable();
    }

    // A corpse's ragdoll simulates until all its parts sleep, or 15 s (30 s for Fika's
    // network corpses). In a big fight that is dozens of ragdolls at once. Call a corpse
    // still after a few seconds instead; it has almost always landed by then.
    private class CorpseStillPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Corpse), nameof(Corpse.CheckCorpseIsStill));

        [PatchPostfix]
        private static void Postfix(float timePass, ref bool __result)
        {
            if (!__result && timePass >= Plugin.CorpseSettleTime.Value) __result = true;
        }
    }

    // Fika's corpses keep their own "still" flag, which its host-sync waits on: once still,
    // the corpse is snapped to the host's final pose when out of view. Set it with the result.
    private class ObservedCorpseStillPatch : ModulePatch
    {
        private static readonly AccessTools.FieldRef<ObservedCorpse, bool> IsStill =
            AccessTools.FieldRefAccess<ObservedCorpse, bool>("_isStill");

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ObservedCorpse), nameof(ObservedCorpse.CheckCorpseIsStill));

        [PatchPostfix]
        private static void Postfix(ObservedCorpse __instance, float timePass, ref bool __result)
        {
            if (__result || timePass < Plugin.CorpseSettleTime.Value) return;
            __result = true;
            IsStill(__instance) = true;
        }
    }

    // Player.LateUpdate starts a coroutine on every alive player every frame. For a
    // third-person player (every bot) it sets RibcageScaleCurrent to 1, waits for the end
    // of the frame, then resets a ribcage scale that is already 1. Doing both now saves an
    // iterator and a Coroutine per bot per frame.
    private class FovCoroutinePatch : ModulePatch
    {
        private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, IProceduralAnimationStrategy> Strategy =
            AccessTools.FieldRefAccess<ProceduralWeaponAnimation, IProceduralAnimationStrategy>("_strategy");

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.StartFovCoroutine));

        [PatchPrefix]
        private static bool Prefix(ProceduralWeaponAnimation __instance, Player player)
        {
            if (!Plugin.HotPathFixes.Value || player.IsYourPlayer || Strategy(__instance) is not ThirdPersonStrategy) return true;

            __instance.ResetFovAdjustments(player);
            player.RibcageScaleCurrent = 1f;
            return false;
        }
    }

    // AICoreController removes the dead bots' brains from its agent list every frame and
    // never clears the to-remove list, so it grows for the whole raid and keeps every dead
    // bot's brain (and through it the BotOwner) alive for the GC to walk.
    private class DeadAgentsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(AICoreController), nameof(AICoreController.Update));

        // A prefix, not a postfix: brains get queued for removal during the agent loop, so
        // clearing afterwards would drop them before they were ever removed.
        [PatchPrefix]
        private static void Prefix(AICoreController __instance)
        {
            if (!Plugin.HotPathFixes.Value || __instance._listToDel.Count == 0) return;

            foreach (var agent in __instance._listToDel) __instance._listAgents.Remove(agent);
            __instance._listToDel.Clear();
        }
    }

    // Every enabled MuzzleManager calls this from its LateUpdate, so it runs once per gun in
    // the world per frame: an environment-tree lookup per muzzle system each time, and the
    // indoor fade advanced by deltaTime once per gun, so it ran N times too fast.
    private class MuzzleAttenuationPatch : ModulePatch
    {
        private static int _frame = -1;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Effects), nameof(Effects.UpdateMBOITIndoorAttenuation), new System.Type[0]);

        [PatchPrefix]
        private static bool Prefix()
        {
            if (!Plugin.HotPathFixes.Value) return true;
            if (_frame == Time.frameCount) return false;
            _frame = Time.frameCount;
            return true;
        }
    }
}
