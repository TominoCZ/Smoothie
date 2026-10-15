using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.CameraControl;
using EFT.Interactive;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using Systems.Effects;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Video;

namespace Smoothie;

// Small per-frame waste in vanilla code. Each one keeps the game's behaviour.
internal static class HotPaths
{
    // The main camera draws opaque objects grouped by material instead of front to back:
    // fewer shader switches (SetPass) for the CPU and render thread, some more overdraw for
    // the GPU (near objects no longer hide what's behind them before it's shaded). Customs,
    // 2026-10-13: ~80 SetPass and ~0.05 ms saved; Streets 2026-10-14: ~270 SetPass and
    // ~0.09 ms. On by default: Tarkov is CPU-bound. The camera's own mode goes back when off.
    private static Camera? _sortCamera;
    private static OpaqueSortMode _sortWas;

    internal static void SortByMaterial()
    {
        var camera = CameraManager.Instance.Camera;
        var on = Plugin.On(Plugin.SortByMaterial) && camera != null;
        if (_sortCamera != null && (!on || _sortCamera != camera))
        {
            if (_sortCamera.opaqueSortMode == OpaqueSortMode.NoDistanceSort) _sortCamera.opaqueSortMode = _sortWas;
            _sortCamera = null;
        }

        if (!on || _sortCamera != null) return;
        _sortWas = camera!.opaqueSortMode;
        camera.opaqueSortMode = OpaqueSortMode.NoDistanceSort;
        _sortCamera = camera;
    }

    internal static void Enable()
    {
        new FovCoroutinePatch().Enable();
        new DeadAgentsPatch().Enable();
        new MuzzleAttenuationPatch().Enable();
        new CorpseStillPatch().Enable();
        new ObservedCorpseStillPatch().Enable();
        new MuzzleShadowsPatch().Enable();
        new BotCreationDataLeakPatch().Enable();
        new SpawnDelaysLeakPatch().Enable();
        new DispatcherStopLeakPatch().Enable();
        new MenuVideoPatch().Enable();
        new MenuVideoInitPatch().Enable();
        new AmbientUpdatePatch().Enable();
        new AmbientBufferPatch().Enable();
        new AmbientStencilPatch().Enable();
    }

    // AmbientLight (the indoor ambient lighting) re-records two command buffers for every
    // camera that has ever rendered, every frame in its LateUpdate: about 580 light sources
    // checked against each camera's view. Only the main camera renders every frame; the scope
    // and menu cameras rarely do, and what was recorded for them was thrown away unused (half
    // of the 0.39 ms a frame it cost). Here each camera's recording waits until that camera is
    // about to render (its pre-cull) and is skipped if it doesn't: the same buffers, recorded
    // later in the same frame, from the camera's final position.
    private static readonly Dictionary<Camera, (AmbientLight Light, AmbientLight.CamSettings Settings)> AmbientDue = new();
    private static bool _ambientRecording, _ambientHooked;

    private static void AmbientPreCull(Camera camera)
    {
        if (AmbientDue.Count == 0 || !AmbientDue.TryGetValue(camera, out var due)) return;
        AmbientDue.Remove(camera);
        try
        {
            if (due.Light == null) return;
            due.Light.InitAmbientBuffer(due.Settings, camera);
            due.Light.DrawStencil(due.Settings, camera);
        }
        catch (Exception ex)
        {
            Plugin.Fail(Plugin.HotPathFixes, ex);
        }
    }

    private class AmbientUpdatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(AmbientLight), nameof(AmbientLight.method_1));

        [PatchPrefix]
        private static void Prefix()
        {
            if (!Plugin.On(Plugin.HotPathFixes))
            {
                // Off: the game records as before; nothing left waiting for a camera.
                AmbientDue.Clear();
                return;
            }

            if (!_ambientHooked)
            {
                Camera.onPreCull += AmbientPreCull;
                _ambientHooked = true;
            }

            // Cameras that were due but never rendered (a scope put away, a destroyed camera).
            if (AmbientDue.Count > 16) AmbientDue.Clear();
            _ambientRecording = true;
        }

        [PatchFinalizer]
        private static void Finalizer() => _ambientRecording = false;
    }

    private class AmbientBufferPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(AmbientLight), nameof(AmbientLight.InitAmbientBuffer));

        // Only the per-frame recording in LateUpdate waits; set-up and resizes run as they are.
        [PatchPrefix]
        private static bool Prefix(AmbientLight __instance, AmbientLight.CamSettings camSettings, Camera currentCamera)
        {
            if (!_ambientRecording) return true;
            AmbientDue[currentCamera] = (__instance, camSettings);
            return false;
        }
    }

    private class AmbientStencilPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(AmbientLight), nameof(AmbientLight.DrawStencil));

        [PatchPrefix]
        private static bool Prefix() => !_ambientRecording;
    }

    // One of the main menu's backgrounds (VideoEnvironmentUIRoot) is four 2048x1024 60 fps
    // video loops. Its Init moves the video players out of the background, and the menu hides
    // the background for a raid by switching off only the background itself, so the videos
    // played on for the whole raid. Under Proton that's FFmpeg decoding them in software on
    // 2-3 cores, the main thread's cores: 80 fps instead of 180 whenever the menu had picked
    // that background. Paused while the background is hidden, played again when it's shown.
    private static readonly AccessTools.FieldRef<EnvironmentUI, EnvironmentUIRoot> CurrentEnvironment =
        AccessTools.FieldRefAccess<EnvironmentUI, EnvironmentUIRoot>("_currentEnvironment");

    private static readonly AccessTools.FieldRef<VideoEnvironmentUIRoot, VideoPlayer[]> EnvironmentVideos =
        AccessTools.FieldRefAccess<VideoEnvironmentUIRoot, VideoPlayer[]>("_videoPlayers");

    // For the overlay. Only the menu background's few players, emptied whenever it's shown.
    private static readonly List<VideoPlayer> PausedMenuVideos = new();

    internal static int PausedMenuVideoCount => PausedMenuVideos.Count(v => v != null && v.isPaused);

    private static readonly AccessTools.FieldRef<EnvironmentUI, bool> EnvironmentShown =
        AccessTools.FieldRefAccess<EnvironmentUI, bool>("_lastVisibleStateEnvironment");

    // Paused while hidden and this fix is on; everything paused here plays again otherwise.
    private static void SyncMenuVideos(EnvironmentUIRoot? environment, bool shown)
    {
        try
        {
            // The A/B switch doesn't bring the videos back (see Plugin.OnEvenWhenPaused).
            var pause = !shown && Plugin.OnEvenWhenPaused(Plugin.HotPathFixes);
            if (!pause)
            {
                foreach (var player in PausedMenuVideos)
                {
                    if (player != null && player.isPaused) player.Play();
                }

                PausedMenuVideos.Clear();
                return;
            }

            if (environment is not VideoEnvironmentUIRoot root || EnvironmentVideos(root) is not { } players) return;
            foreach (var player in players)
            {
                if (player == null || !Playing(player)) continue;
                player.Pause();
                if (!PausedMenuVideos.Contains(player)) PausedMenuVideos.Add(player);
            }
        }
        catch (Exception ex)
        {
            Plugin.Fail(Plugin.HotPathFixes, ex);
        }
    }

    // Playing, or about to start by itself (still loading with play-on-awake). A stopped
    // video is left alone, so resuming never starts one that wasn't playing.
    private static bool Playing(VideoPlayer player) =>
        player.isPlaying || player.playOnAwake && player.isActiveAndEnabled && !player.isPaused && !player.isPrepared;

    // After a settings change (the fix or the A/B switch flipped mid-raid), and every few
    // seconds in raid for a video that started late: the background isn't hidden or shown
    // again until the raid ends, so apply its current state now.
    internal static void ResyncMenuVideos()
    {
        if (!MonoBehaviourSingleton<EnvironmentUI>.Instantiated) return;
        var ui = MonoBehaviourSingleton<EnvironmentUI>.Instance;
        SyncMenuVideos(CurrentEnvironment(ui), EnvironmentShown(ui));
    }

    // Raid HUD and hideout hide the background, the menu shows it again.
    private class MenuVideoPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EnvironmentUI), nameof(EnvironmentUI.ShowEnvironment));

        [PatchPostfix]
        private static void Postfix(EnvironmentUI __instance, bool value) => SyncMenuVideos(CurrentEnvironment(__instance), value);
    }

    // A background is loaded asynchronously and shown or hidden before its Init collects the
    // video players, so one that finishes loading while hidden (a raid starting) would play.
    private class MenuVideoInitPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(VideoEnvironmentUIRoot), nameof(VideoEnvironmentUIRoot.Init));

        [PatchPostfix]
        private static void Postfix(VideoEnvironmentUIRoot __instance)
        {
            if (!__instance.gameObject.activeSelf) SyncMenuVideos(__instance, false);
        }
    }

    // Every BotCreationData (one per spawn attempt, with its bots' whole profiles) and every
    // raid's SpawnDelaysService (its queue of waiting bots, and through their callbacks the
    // raid's BotSpawner) subscribes to GlobalEventDispatcher.OnStopBotSpawn. The dispatcher
    // lives as long as the game, nothing ever raises that event (no caller in the game or any
    // installed mod), and the dispatcher's Stop() at raid end clears every event but this one:
    // all of it stayed in memory until the game was closed, gigabytes after a Horde raid.
    // Unsubscribed as soon as it's made (the event never fires, so nothing else changes), and
    // the event cleared at raid end for anything that subscribed while this was off.
    private static readonly AccessTools.FieldRef<GlobalEventDispatcher, Action> StopBotSpawnEvent =
        AccessTools.FieldRefAccess<GlobalEventDispatcher, Action>("_onStopBotSpawn");

    private class BotCreationDataLeakPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Constructor(typeof(BotCreationData), new[] { typeof(IGetProfileData) });

        [PatchPostfix]
        private static void Postfix(BotCreationData __instance)
        {
            try
            {
                if (Plugin.On(Plugin.HotPathFixes)) Singleton<GlobalEventDispatcher>.Instance.OnStopBotSpawn -= __instance.StopSpawn;
            }
            catch (Exception ex)
            {
                Plugin.Fail(Plugin.HotPathFixes, ex);
            }
        }
    }

    private class SpawnDelaysLeakPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Constructor(typeof(SpawnDelaysService));

        [PatchPostfix]
        private static void Postfix(SpawnDelaysService __instance)
        {
            try
            {
                if (Plugin.On(Plugin.HotPathFixes)) Singleton<GlobalEventDispatcher>.Instance.OnStopBotSpawn -= __instance.ClearDelays;
            }
            catch (Exception ex)
            {
                Plugin.Fail(Plugin.HotPathFixes, ex);
            }
        }
    }

    private class DispatcherStopLeakPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GlobalEventDispatcher), nameof(GlobalEventDispatcher.Stop));

        [PatchPostfix]
        private static void Postfix(GlobalEventDispatcher __instance)
        {
            if (Plugin.On(Plugin.HotPathFixes)) StopBotSpawnEvent(__instance) = null!;
        }
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
            if (Plugin.Fixing && !__result && timePass >= Plugin.CorpseSettleTime.Value && !RuptureInstalled) __result = true;
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
            if (!Plugin.Fixing || __result || timePass < Plugin.CorpseSettleTime.Value || RuptureInstalled) return;
            __result = true;
            IsStill(__instance) = true;
        }
    }

    // Rupture answers "not still" for good on purpose: it moves corpses in its own physics
    // scene and keeps the game's ragdoll as a shell bullets can still hit, wake and cut. "Still"
    // makes the game take that shell apart, so with Rupture installed corpses are left alone.
    // Looked up on first use, once every plugin has loaded.
    private static bool? _ruptureInstalled;

    internal static bool RuptureInstalled
    {
        get
        {
            if (_ruptureInstalled is { } installed) return installed;
            _ruptureInstalled = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.rootdarkarchon.rupture");
            return _ruptureInstalled.Value;
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
            if (!Plugin.On(Plugin.HotPathFixes) || player.IsYourPlayer || Strategy(__instance) is not ThirdPersonStrategy) return true;

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
            if (!Plugin.On(Plugin.HotPathFixes) || __instance._listToDel.Count == 0) return;

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
            if (!Plugin.On(Plugin.HotPathFixes)) return true;
            if (_frame == Time.frameCount) return false;
            _frame = Time.frameCount;
            return true;
        }
    }

    // A muzzle flash is a light. If its weapon's prefab gives it shadows, every shot renders
    // the scene around it into a shadow map, 6 times for a point light, for the few frames
    // it's lit: shadow draws more than doubled (2.3k to 5.4k) in a fight. Other players'
    // flashes still light things up, just without shadows; your own gun's are left alone.
    private class MuzzleShadowsPatch : ModulePatch
    {
        // As the prefab had them, to put back when the setting is switched off.
        private static readonly ConditionalWeakTable<MuzzleLight, LightShadows[]> Original = new();
        private static bool _touched;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MuzzleLight), nameof(MuzzleLight.OnShot));

        [PatchPostfix]
        private static void Postfix(MuzzleLight __instance)
        {
            try
            {
                var lights = __instance.Lights;
                var on = Plugin.On(Plugin.NoMuzzleShadows);
                // Off and never switched on this session: nothing to put back.
                if (lights == null || !on && !_touched) return;
                _touched = true;
                var original = Original.GetValue(__instance, m => Array.ConvertAll(m.Lights, l => l ? l.shadows : LightShadows.None));

                // Checked every shot, not cached: weapons are pooled and change hands.
                var me = on ? Singleton<GameWorld>.Instance?.MainPlayer : null;
                var off = on && !(me != null && __instance.transform.IsChildOf(me.Transform.Original));
                for (var i = 0; i < lights.Length && i < original.Length; i++)
                {
                    var wanted = off ? LightShadows.None : original[i];
                    if (lights[i] && lights[i].shadows != wanted) lights[i].shadows = wanted;
                }
            }
            catch (Exception ex)
            {
                Plugin.Fail(Plugin.NoMuzzleShadows, ex);
            }
        }
    }
}
