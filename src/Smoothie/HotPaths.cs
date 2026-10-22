using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
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

    // Screen effects on the main camera that copy the whole screen every frame while showing
    // nothing: switched off while idle, back on the moment they have something to show. Only
    // what this switched off is switched back. Grayscale: the game never sets its amount. Death
    // fade: the game switches it on at the first death and never off again. Blood: 5 s after the
    // last hit. Distortion: no distorting objects (heat haze, explosions). Hue focus: amount 0.
    // A hit shows its blood one frame late at most (checked once a frame, in Update). Streets,
    // 3080 Ti, lightly GPU-bound: GPU wait -0.037 ± 0.016 ms, frame -0.054 ± 0.031 ms.
    private static Camera? _idleCamera;
    private static Behaviour?[] _idleEffects = Array.Empty<Behaviour?>();
    private static readonly HashSet<Behaviour> IdleOff = new();
    private static bool _idleBroken;

    // The game's private fields, apart: if a game update renames one, only this stops.
    private static class IdleFields
    {
        internal static readonly AccessTools.FieldRef<DeathFade, bool> DeathFadeDead = AccessTools.FieldRefAccess<DeathFade, bool>("_isDead");
        internal static readonly AccessTools.FieldRef<BloodOnScreen, float> BloodLastHit = AccessTools.FieldRefAccess<BloodOnScreen, float>("_lastHitTime");
        internal static readonly System.Collections.IList DistortRenderers = (System.Collections.IList)AccessTools.Field(typeof(DistortCameraFX), "DistortNBlurRenderers").GetValue(null);
    }

    internal static void IdleEffects()
    {
        var camera = CameraManager.Instance?.Camera;
        var on = Plugin.On(Plugin.GpuFixes) && camera != null && !_idleBroken;
        if (!on || !ReferenceEquals(camera, _idleCamera))
        {
            RestoreIdleEffects();
            _idleCamera = on ? camera : null;
            if (!on) return;
            _idleEffects = new Behaviour?[]
            {
                camera!.GetComponent<PostprocessGrayscale>(), camera.GetComponent<DeathFade>(), camera.GetComponent<BloodOnScreen>(),
                camera.GetComponent<DistortCameraFX>(), camera.GetComponent<CC_HueFocus>(),
            };
        }

        try
        {
            foreach (var effect in _idleEffects)
            {
                if (effect == null) continue;
                var idle = effect switch
                {
                    PostprocessGrayscale g => Math.Abs(g.EffectAmount) < Mathf.Epsilon,
                    DeathFade d => !IdleFields.DeathFadeDead(d) && Math.Abs(d._fadeValue) < Mathf.Epsilon && Math.Abs(d._closeEyesValue) < Mathf.Epsilon,
                    BloodOnScreen b => Time.time - IdleFields.BloodLastHit(b) > b.MaxBloodTime,
                    DistortCameraFX => IdleFields.DistortRenderers.Count == 0,
                    CC_HueFocus h => h.amount < Mathf.Epsilon,
                    _ => false,
                };
                if (idle && effect.enabled)
                {
                    effect.enabled = false;
                    IdleOff.Add(effect);
                }
                else if (!idle && IdleOff.Remove(effect)) effect.enabled = true;
            }
        }
        catch (Exception ex)
        {
            _idleBroken = true;
            RestoreIdleEffects();
            Plugin.Log.LogWarning($"idle screen effects left on: {ex.Message}");
        }
    }

    private static void RestoreIdleEffects()
    {
        foreach (var effect in IdleOff)
        {
            if (effect != null) effect.enabled = true;
        }

        IdleOff.Clear();
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
        new AirdropLandedPatch().Enable();
        new AirdropInitPatch().Enable();
        new PhysicsRegionsPatch().Enable();
        new PhysicsRatePatch().Enable();
        new AmbientUpdatePatch().Enable();
        new AmbientBufferPatch().Enable();
        new AmbientStencilPatch().Enable();
        // Last: a game update that renames its parameters makes this one throw, not the ones above.
        new DistantShadowFadePatch().Enable();
    }

    // AmbientLight (the indoor ambient lighting) re-records two command buffers for every
    // camera that has ever rendered, every frame in its LateUpdate: about 580 light sources
    // checked against each camera's view. Only the main camera renders every frame; the scope
    // and menu cameras rarely do, and what was recorded for them was thrown away unused (half
    // of the 0.39 ms a frame it cost). Here each camera's recording waits until that camera is
    // about to render (its pre-cull) and is skipped if it doesn't: the same buffers, recorded
    // later in the same frame, from the camera's final position.
    // By reference: looked up for every camera before every render, and Unity's own == on a
    // camera is a call into the engine.
    private static readonly Dictionary<Camera, (AmbientLight Light, AmbientLight.CamSettings Settings)> AmbientDue = new(Diagnostics.ReferenceComparer.Instance);
    private static bool _ambientRecording, _ambientHooked;

    private static void AmbientPreCull(Camera camera)
    {
        if (AmbientDue.Count == 0 || !AmbientDue.TryGetValue(camera, out var due)) return;
        AmbientDue.Remove(camera);
        try
        {
            if (due.Light is null || SceneSkip.NativeOf(due.Light) == IntPtr.Zero) return;
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

    // The airdrop crate registers its rigidbody with EFT's physics controller when it starts to
    // fall (ServerAirDrop.Init) and never unregisters it, not even back in its pool. The
    // controller runs the whole scene's physics step while any registered body is active, so one
    // airdrop kept it running for the rest of the raid (Streets, hosting: Phys 1-3 ms a frame for
    // 30+ min). The cost is the whole step, not the crate: trigger shapes tested against level
    // meshes, continuous-collision sweeps, the query tree rebuilt. Once the crate has landed (and
    // finished tipping over) nothing moves it again, so it's unregistered then. EFT only marks it
    // static after its smoke burns out, minutes later; the smoke is particles, not physics.
    private static readonly HashSet<EFT.Airdrop.ServerAirDrop> LandedAirdrops = new();

    private class AirdropLandedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EFT.Airdrop.ServerAirDrop), nameof(EFT.Airdrop.ServerAirDrop.ManualUpdate));

        [PatchPostfix]
        private static void Postfix(EFT.Airdrop.ServerAirDrop __instance)
        {
            if (__instance._fallingStage != EAirdropFallingStage.Landed) return;
            if (!Plugin.On(Plugin.HotPathFixes) || !LandedAirdrops.Add(__instance)) return;
            var body = __instance.SyncObjectRigidbody;
            if (body != null) PhysicsExtensions.UpdateController.UnsupportRigidbody(body);
        }
    }

    // PhysX finds touching shapes (broadphase) per region of the map: every step, each region with
    // something moving sweeps its moved boxes against all the region's other boxes, so the cost
    // grows with how many objects share a region. EFT sets the regions per map
    // (LevelPhysicsSettings); Unity's own default would be an 8x8 grid. Here the same bounds get
    // Unity's finest grid, 16x16 (PhysX's limit of 256 regions). Which pairs touch doesn't change,
    // only how many boxes each sweep runs through. Objects outside the bounds stay as they were.
    private const int FinestRegions = 16;
    private static Bounds _regionBounds;
    private static int _regionsMap, _regionsNow;
    private static LevelPhysicsSettings? _regionsOwner;

    internal static void SyncPhysicsRegions()
    {
        // Only while the map that set them is loaded. Off (and in the hideout) puts the map's back.
        if (_regionsMap == 0 || _regionsOwner == null) return;
        var want = Plugin.On(Plugin.FinePhysicsRegions) ? Math.Max(_regionsMap, FinestRegions) : _regionsMap;
        if (want == _regionsNow) return;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        Physics.RebuildBroadphaseRegions(_regionBounds, want);
        _regionsNow = want;
        Plugin.Log.LogInfo($"physics regions: {want}x{want} over {_regionBounds.size} m (the map sets {_regionsMap}x{_regionsMap}), " +
                           $"{(System.Diagnostics.Stopwatch.GetTimestamp() - start) * Diagnostics.TickMs:0} ms");
    }

    private class PhysicsRegionsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(LevelPhysicsSettings), nameof(LevelPhysicsSettings.ApplySettings));

        [PatchPostfix]
        private static void Postfix(LevelPhysicsSettings __instance)
        {
            // What EFT just applied (its own choice, or its config override).
            var config = AppEnvironment.Config.Physics;
            _regionsOwner = __instance;
            _regionBounds = config.OverrideMBPSettings ? config.OverrideMBPBounds : __instance.GetGlobalBounds();
            _regionsMap = _regionsNow = Math.Max(1, config.OverrideMBPSettings ? config.OverrideMBPSubdivisions : __instance._mbpSubdivisions);
            Plugin.Log.LogInfo($"physics regions: the map sets {_regionsMap}x{_regionsMap} over {_regionBounds.size} m");
            // Applied from the plugin's update once the raid is on (never in the hideout).
        }
    }

    // In EFT's collision matrix the hit zones of every player and bot (HitCollider layer, ~30 per
    // body, moving every frame) collide with one layer only: Triggers. So while the physics step
    // runs, PhysX tests every hit zone against every trigger sphere and box it's inside (spawn
    // points, quest zones; Streets with ~15 bots: ~5,700 pairs) to send Unity's trigger events.
    // Nothing uses those: every trigger script in the game is driven by EFT's own overlap check
    // per player (TriggerColliderSearcher, every frame, step or not), and the scripts that only
    // listen to Unity's events (the airdrop's landing check, IK and GPU Instancer leftovers) aren't
    // touched by hit zones. Bullets and other queries don't use the matrix. In raids only; off
    // (or the hideout) puts it back. A listener nobody has checked (a mod, a game update) keeps
    // the game's matrix: every loaded assembly is scanned for one, and again whenever one loads.
    private static int _hitLayer = -2, _triggerLayer;
    private static bool _hitTriggersCut;
    private static volatile string? _uncheckedListener;
    private static volatile bool _firstScanDone;
    private static string _loggedListener = "";

    // Listeners in the game's own assemblies, read 2026-10-16: none reacts to hit zones, and in
    // vanilla they only hear from hit zones while the physics step happens to run.
    private static readonly HashSet<string> CheckedListeners = new()
    {
        "EFT.SynchronizableObjects.SyncObjectCollisionChecker", "GPUInstancer.GPUInstancerModificationCollider",
        "PhysicsCustomOverlapBoxSimulationTest+BoxColliderGizmoDrawer", "RainControllerOld",
        "RootMotion.FinalIK.InteractionSystem", "RootMotion.TriggerEventBroadcaster",
        "Cinemachine.CinemachineTriggerAction", "Cinemachine.CinemachineCollisionImpulseSource", "Dissonance.BaseCommsTrigger",
    };

    // Any MonoBehaviour declaring a Unity trigger message, other than EFT's own trigger scripts
    // (driven by its per-player check) and the checked ones above. Null: none.
    internal static string? FindUncheckedListener(IEnumerable<Assembly> assemblies)
    {
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var game = typeof(IPhysicsTrigger).Assembly;
        foreach (var assembly in assemblies)
        {
            if (assembly == typeof(HotPaths).Assembly || assembly.IsDynamic) continue;
            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }

            foreach (var type in types)
            {
                if (type == null || !type.IsSubclassOf(typeof(MonoBehaviour))) continue;
                if (type.GetMethod("OnTriggerEnter", declared) == null && type.GetMethod("OnTriggerStay", declared) == null &&
                    type.GetMethod("OnTriggerExit", declared) == null) continue;
                if (assembly == game && typeof(IPhysicsTrigger).IsAssignableFrom(type)) continue;
                if (CheckedListeners.Contains(type.FullName ?? "")) continue;
                return $"{type.FullName} ({assembly.GetName().Name})";
            }
        }

        return null;
    }

    // The first scan covers everything loaded; the fix waits for it. Later ones (an assembly
    // loaded mid-raid) only matter if they find something.
    private static void ScanListeners(bool first, params Assembly[] assemblies)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _uncheckedListener ??= FindUncheckedListener(assemblies);
            }
            catch (Exception ex)
            {
                _uncheckedListener ??= $"the scan failed: {ex.Message}";
            }
            finally
            {
                if (first) _firstScanDone = true;
            }
        });
    }

    internal static void SyncHitTriggers()
    {
        if (_hitLayer == -2)
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, loaded) => ScanListeners(false, loaded.LoadedAssembly);
            ScanListeners(true, AppDomain.CurrentDomain.GetAssemblies());
            _hitLayer = LayerMask.NameToLayer("HitCollider");
            _triggerLayer = LayerMask.NameToLayer("Triggers");
            // Its masks are built from the matrix once: built from the game's own first.
            RuntimeHelpers.RunClassConstructor(typeof(LayersMaskController).TypeHandle);
            // Already ignored (another mod, a game update): nothing to do, nothing to put back.
            if (_hitLayer < 0 || _triggerLayer < 0 || Physics.GetIgnoreLayerCollision(_hitLayer, _triggerLayer)) _hitLayer = -1;
        }

        var scanned = _firstScanDone;
        var listener = _uncheckedListener;
        if (scanned && _loggedListener != (listener ?? "none"))
        {
            _loggedListener = listener ?? "none";
            Plugin.Log.LogInfo(listener == null
                ? "trigger event listeners: only the game's own, checked ones"
                : $"trigger event listeners: {listener} isn't checked, hit zones keep their trigger checks");
            if (listener != null) Plugin.SetDisabled(Plugin.SkipHitTriggers, $"{listener} listens to trigger events");
        }

        var cut = _hitLayer >= 0 && scanned && listener == null && Plugin.On(Plugin.SkipHitTriggers) && Diagnostics.InRaid;
        if (cut == _hitTriggersCut) return;
        Physics.IgnoreLayerCollision(_hitLayer, _triggerLayer, cut);
        _hitTriggersCut = cut;
        Plugin.Log.LogInfo($"physics: hit zones vs triggers {(cut ? "skipped" : "back as the game has it")}" +
                           (listener != null ? $", {listener} listens to Unity's trigger events" : ""));
    }

    // While anything moves under physics (a grenade, a falling body, dropped loot, a door) EFT
    // steps the whole scene's physics once per rendered frame (Simulation.SmoothSimulate), and a
    // step costs about the same whatever moved: on Streets with bots ~1.2 ms. At 120 fps that's
    // twice the steps a 60 fps player runs, for the same physics. Here the time EFT would have
    // simulated each frame is added up and stepped once 1/60 s of it has built up: physics time
    // runs exactly as in vanilla, in chunks like at 60 fps (each frame's share still capped by
    // EFT's MaxSmoothPhysicsDeltaTime). Below 60 fps every frame steps, as before.
    private const float MinPhysicsStep = 1f / 60f;
    private static float _physicsOwed;

    private class PhysicsRatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(PhysicsExtensions.Simulation), nameof(PhysicsExtensions.Simulation.SmoothSimulate));

        [PatchPrefix]
        private static bool Prefix(float deltaTime)
        {
            if (!Plugin.On(Plugin.CapPhysicsRate))
            {
                _physicsOwed = 0f;
                return true;
            }

            // EFT's own bookkeeping, every frame as before.
            var average = PhysicsExtensions.Simulation._simulationDeltaTimes;
            average.AddValue(deltaTime);
            if (!PhysicsExtensions.Simulation.CanRunSimulate)
            {
                _physicsOwed = 0f;
                return false;
            }

            _physicsOwed += Mathf.Min((float)average.Average, EFTHardSettings.Instance.MaxSmoothPhysicsDeltaTime);
            if (_physicsOwed < MinPhysicsStep) return false;
            var step = _physicsOwed;
            _physicsOwed = 0f;
            Physics.Simulate(step);
            return false;
        }
    }

#if DEBUG
    // Debug, Insert: keeps the physics step running (a bodiless kinematic rigidbody registered
    // with EFT's controller, far below the map), so a paired A/B has a steady physics load.
    private static GameObject? _physicsKeeper;

    internal static void TogglePhysicsKeeper()
    {
        if (_physicsKeeper != null)
        {
            UnityEngine.Object.Destroy(_physicsKeeper);
            _physicsKeeper = null;
            Plugin.Log.LogInfo("physics keeper off");
            return;
        }

        _physicsKeeper = new GameObject("Smoothie physics keeper") { transform = { position = new Vector3(0f, -5000f, 0f) } };
        var body = _physicsKeeper.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        PhysicsExtensions.UpdateController.SupportRigidbody(body);
        Plugin.Log.LogInfo("physics keeper on: the physics step runs until Insert again");
    }
#endif

    // A crate taken from the pool again registers itself anew: watch for its landing again.
    private class AirdropInitPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EFT.Airdrop.ServerAirDrop), nameof(EFT.Airdrop.ServerAirDrop.Init));

        [PatchPostfix]
        private static void Postfix(EFT.Airdrop.ServerAirDrop __instance) => LandedAirdrops.Remove(__instance);
    }

    // The distant shadows' mask (a full-screen pass) reads the old and the new distant shadow
    // map for every pixel: every shadow quality preset turns the fade between them on, though
    // the old one only counts during the fade after each swap (at most 0.5 s), and a new map
    // takes seconds to finish (Low: ~37 s, 10 objects a frame). Once the fade is done
    // (GlobalShadowProj.z, time since the swap over the fade time, reaches 1) the mask is drawn
    // with the fade keyword off and it's switched back on after: the same picture, one map read.
    // Streets, Low, 2560x1408, GPU-bound: frame -0.467 ± 0.057 ms, GPU wait -0.426 ± 0.048 ms.
    private class DistantShadowFadePatch : ModulePatch
    {
        private const string FadeKeyword = "DISTANT_SHADOWS_BLEND";

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(DistantShadow), nameof(DistantShadow.SetupComputeShadowMaskCommandBuffer));

        [PatchPrefix]
        private static void Prefix(DistantShadow __instance, CommandBuffer cmdBuf, Vector4 GlobalShadowProj, out bool __state)
        {
            __state = __instance.EnableShadowsBlend && GlobalShadowProj.z >= 1f && Plugin.On(Plugin.GpuFixes);
            if (__state) cmdBuf.DisableShaderKeyword(FadeKeyword);
        }

        [PatchPostfix]
        private static void Postfix(CommandBuffer cmdBuf, bool __state)
        {
            if (__state) cmdBuf.EnableShaderKeyword(FadeKeyword);
        }
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
