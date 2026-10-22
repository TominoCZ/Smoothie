using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;
using UnityEngine.Video;
using Object = UnityEngine.Object;

namespace Smoothie;

// A small in-game profiler for finding CPU hot paths without a development build.
//
// While "Log time per script (slow)" is on, every Update / LateUpdate / FixedUpdate (and camera callback) of the scripts
// running in the scene (the game's and every mod's) is timed and grouped by type. Unity's
// own main/render thread timings come from ProfilerRecorder, which works in release
// builds. When the frame rate drops and stays down, a snapshot goes to the BepInEx log.
internal static class Diagnostics
{
    private sealed class Bucket
    {
        public string Name = "";
        public long Ticks;
        public int Calls;

        // Calls on objects that draw something but weren't drawn that frame (candidates for
        // skipping while unseen), and calls on objects with nothing to draw (logic).
        public long HiddenTicks;
        public int HiddenCalls;
        public int NoRendererCalls;
    }

    // What the object running a timed script draws, worked out once per object, and whether
    // it was drawn, once per frame. Only while the per-script profiler runs.
    private struct Looks
    {
        public Renderer[]? Renderers;
        public int Frame;
        public byte State;
    }

    private const byte NothingToDraw = 0, Drawn = 1, NotDrawn = 2, PlayerPart = 3;
    private static readonly Dictionary<int, Looks> LooksById = new();

    private const string HarmonyId = "com.tominocz.smoothie.profiler";
    internal static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
    // The per-frame messages, plus the camera ones (scripts on cameras, image effects): those
    // run inside the render stage, so they show what of rendering is scripts.
    private static readonly (string Name, Type[] Args)[] Messages =
    {
        ("Update", Type.EmptyTypes), ("LateUpdate", Type.EmptyTypes), ("FixedUpdate", Type.EmptyTypes),
        ("OnPreCull", Type.EmptyTypes), ("OnPreRender", Type.EmptyTypes), ("OnPostRender", Type.EmptyTypes),
        ("OnRenderImage", new[] { typeof(RenderTexture), typeof(RenderTexture) }),
    };

    private static Harmony? _harmony;
    private static bool _profiling;
    private static readonly Dictionary<MethodBase, Bucket> Buckets = new();

    // Last window's results, ms per frame, worst first.
    private static List<(string Name, double Ms, double Calls, double HiddenMs, double LogicShare)> _top = new();
    private static double _heapGrowthMBps = -1;
    private static long _windowHeap;
    private static int _windowGcCount;
    private static int _windowFrames;

    // Frame pacing over the last window: smoothness is consistency, not the average.
    private static readonly List<float> FrameTimes = new(512);
    private static string _pacing = "";
    private static float _pacingStart;
    private static float _windowStart;

    private static ProfilerRecorder _mainThread;
    private static ProfilerRecorder _renderThread;
    private static ProfilerRecorder _gcAlloc;
    private static ProfilerRecorder _drawCalls;
    private static ProfilerRecorder _shadowCasters;
    private static ProfilerRecorder _setPassCalls;
    private static ProfilerRecorder _batches;
    private static ProfilerRecorder _skinnedMeshes;

    // Frame time, smoothed over about a second and about 20 seconds.
    private static float _fastMs = 7f;
    private static float _slowMs = 7f;
    private static float _droppedSince = -1f;
    private static float _lastSnapshot = -100f;
    private static int _gcCountAtStart;

    internal static bool Profiling => _profiling;

    // Performance lines go to the log only with a diagnostics setting on that asks for them.
    internal static bool Logging => Profiling || Plugin.On(Plugin.LogDrops);

    // Something shows or logs Smoothie's own timings (overlay, stats panel, logging): set once a
    // frame by Plugin.Update. Off, those timings aren't taken: under Proton every clock read is a
    // Wine system call.
    internal static bool Measuring;

    // Engine stages: a timestamp before every step of Unity's player loop (animation,
    // physics, skinned meshes, UI, rendering, ...), so the frame time outside scripts can
    // be split up too. Works in release builds.
    private static PlayerLoopSystem? _originalLoop;
    private static string[] _stageNames = Array.Empty<string>();
    private static long[] _stageTicks = Array.Empty<long>();
    private static long[] _stageFrameTicks = Array.Empty<long>();
    private static int _currentStage = -1;
    private static long _stageStart;
    private static List<(string Name, double Ms)> _topStages = new();

    private static void StartEngineTiming()
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop();
        _originalLoop = loop;
        var names = new List<string>();

        var phases = loop.subSystemList ?? Array.Empty<PlayerLoopSystem>();
        var timedPhases = new PlayerLoopSystem[phases.Length];
        for (var p = 0; p < phases.Length; p++)
        {
            var phase = phases[p];
            var steps = phase.subSystemList ?? Array.Empty<PlayerLoopSystem>();
            var timed = new List<PlayerLoopSystem>(steps.Length * 2 + 1);
            foreach (var step in steps)
            {
                // Ours from an earlier start: wrapped again, every marker would double up.
                if (step.type == MarkerType) continue;
                timed.Add(Marker(names.Count));
                names.Add($"{phase.type?.Name}.{step.type?.Name}");
                timed.Add(step);
            }

            // End of the phase: stop the clock so the gap to the next phase isn't billed.
            timed.Add(Marker(-1));
            phase.subSystemList = timed.ToArray();
            timedPhases[p] = phase;
        }

        loop.subSystemList = timedPhases;
        _stageNames = names.ToArray();
        _stageTicks = new long[_stageNames.Length];
        _stageFrameTicks = new long[_stageNames.Length];
        _currentStage = -1;
        PlayerLoop.SetPlayerLoop(loop);
    }

    // IL2CPP: the loop takes an IL2CPP type; Smoothie's own registered component marks its steps.
    private static Il2CppSystem.Type? _markerType;
    private static Il2CppSystem.Type MarkerType => _markerType ??= Il2CppInterop.Runtime.Il2CppType.Of<Plugin.Runner>();

    private static PlayerLoopSystem Marker(int stage) => new()
    {
        type = MarkerType,
        updateDelegate = (Action)(() =>
        {
            var now = Stopwatch.GetTimestamp();
            if (_currentStage >= 0)
            {
                _stageTicks[_currentStage] += now - _stageStart;
                _stageFrameTicks[_currentStage] += now - _stageStart;
            }

            _currentStage = stage;
            _stageStart = now;
        }),
    };

    // Stage timing runs while the profiler or the cost overlay uses it.
    internal static void UpdateEngineTiming()
    {
        var want = Profiling || Plugin.On(Plugin.ShowCosts);
        if (want == EngineTiming) return;
        if (!want)
        {
            StopEngineTiming();
            return;
        }

        TryStartEngineTiming();
    }

    private static void TryStartEngineTiming()
    {
        try
        {
            StartEngineTiming();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"engine stage timing unavailable: {ex.Message}");
        }
    }

    internal static bool EngineTiming => _originalLoop != null;

    // The game or a mod setting its own player loop (at startup, at raid load, mid-raid)
    // drops the markers: no stage has any time. Put them back into the loop current now, at
    // most every 5 s in case something keeps replacing it.
    private static int _silentFrames;
    private static float _lastMarkerRestore = -100f;

    private static void RestoreDroppedMarkers()
    {
        if (!EngineTiming) return;
        var any = false;
        foreach (var ticks in _stageFrameTicks)
        {
            if (ticks == 0) continue;
            any = true;
            break;
        }

        _silentFrames = any ? 0 : _silentFrames + 1;
        if (_silentFrames < 30 || Time.unscaledTime - _lastMarkerRestore < 5f) return;
        _silentFrames = 0;
        _lastMarkerRestore = Time.unscaledTime;
        if (Logging) Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} engine stage timing lost its markers (the player loop was replaced), putting them back");
        TryStartEngineTiming();
    }

    // This frame's time per stage so far, for the overlay. Cleared at the end of Tick.
    internal static string[] StageNames => _stageNames;
    internal static long[] StageFrameTicks => _stageFrameTicks;

    internal static long DrawCalls => _drawCalls.Valid ? _drawCalls.LastValue : -1;
    internal static long ShadowCasters => _shadowCasters.Valid ? _shadowCasters.LastValue : -1;
    internal static long Batches => _batches.Valid ? _batches.LastValue : -1;
    internal static long SetPassCalls => _setPassCalls.Valid ? _setPassCalls.LastValue : -1;

    private static void StopEngineTiming()
    {
        if (_originalLoop is { } loop) PlayerLoop.SetPlayerLoop(loop);
        _originalLoop = null;
        _currentStage = -1;
        _topStages.Clear();
    }

    internal static void Start()
    {
        _gcCountAtStart = Il2CppSystem.GC.CollectionCount(0);
        ApplyRecorders();
    }

    // Unity's frame counters only while something shows or logs them: with all diagnostics
    // off, nothing of this runs.
    // Time per camera: from the start of its culling to the end of its render, the main
    // thread's share of drawing that camera (culling, shadow maps, submitting draws). Shows
    // how many cameras render and which cost what; summed over the snapshot window. Culling is
    // up to onPreRender; after it come shadow maps and the draws.
    private static readonly Dictionary<Camera, (long Ticks, long CullTicks, int Frames)> CameraTimes = new(Diagnostics.ReferenceComparer.Instance);
    private static readonly Dictionary<Camera, long> CameraCulled = new(Diagnostics.ReferenceComparer.Instance);
    private static readonly Dictionary<Camera, long> CameraStart = new(Diagnostics.ReferenceComparer.Instance);
    private static bool _cameraHooks;
    internal static int CamerasThisFrame;
    private static int _camerasCounting, _cameraFrame = -1;

    private static void CameraPreCull(Camera camera)
    {
        CameraStart[camera] = Stopwatch.GetTimestamp();
        if (_cameraFrame != Time.frameCount)
        {
            _cameraFrame = Time.frameCount;
            CamerasThisFrame = _camerasCounting;
            _camerasCounting = 0;
        }

        _camerasCounting++;
    }

    private static void CameraPreRender(Camera camera) => CameraCulled[camera] = Stopwatch.GetTimestamp();

    private static void CameraPostRender(Camera camera)
    {
        if (!CameraStart.TryGetValue(camera, out var start)) return;
        var culled = CameraCulled.TryGetValue(camera, out var at) && at > start ? at : start;
        CameraTimes.TryGetValue(camera, out var time);
        CameraTimes[camera] = (time.Ticks + Stopwatch.GetTimestamp() - start, time.CullTicks + culled - start, time.Frames + 1);
    }

    // IL2CPP delegates made once: removing one needs the very instance that was added.
    private static readonly Camera.CameraCallback PreCullHook = (System.Action<Camera>)CameraPreCull;
    private static readonly Camera.CameraCallback PreRenderHook = (System.Action<Camera>)CameraPreRender;
    private static readonly Camera.CameraCallback PostRenderHook = (System.Action<Camera>)CameraPostRender;

    private static void HookCameras(bool on)
    {
        if (on == _cameraHooks) return;
        _cameraHooks = on;
        if (on)
        {
            Camera.onPreCull += PreCullHook;
            Camera.onPreRender += PreRenderHook;
            Camera.onPostRender += PostRenderHook;
            return;
        }

        Camera.onPreCull -= PreCullHook;
        Camera.onPreRender -= PreRenderHook;
        Camera.onPostRender -= PostRenderHook;
        CameraTimes.Clear();
        CameraStart.Clear();
        CameraCulled.Clear();
    }

    // IL2CPP: Il2CppInterop's rebuilt ProfilerRecorder.StartNew(category, string) hands Unity a pointer
    // 20 bytes into the name (it adds IL2CPP's string data offset to a .NET string's characters), so
    // no counter was ever found (draw calls and the rest read -1). The char* constructor it calls,
    // with the right pointer and StartNew's own default options (markers such as "Main Thread" need
    // SumAllSamplesInFrame).
    private static unsafe ProfilerRecorder Recorder(ProfilerCategory category, string name)
    {
        fixed (char* chars = name)
            return new ProfilerRecorder(category, chars, name.Length, 15, ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately);
    }

    internal static void ApplyRecorders()
    {
        var want = Logging || Plugin.On(Plugin.ShowCosts) || Plugin.On(Plugin.ShowStats);
        if (want == _drawCalls.Valid) return;
        if (want)
        {
            _mainThread = Recorder(ProfilerCategory.Internal, "Main Thread");
            _renderThread = Recorder(ProfilerCategory.Internal, "Render Thread");
            _gcAlloc = Recorder(ProfilerCategory.Memory, "GC Allocated In Frame");
            _drawCalls = Recorder(ProfilerCategory.Render, "Draw Calls Count");
            _shadowCasters = Recorder(ProfilerCategory.Render, "Shadow Casters Count");
            _setPassCalls = Recorder(ProfilerCategory.Render, "SetPass Calls Count");
            _batches = Recorder(ProfilerCategory.Render, "Batches Count");
            _skinnedMeshes = Recorder(ProfilerCategory.Render, "Visible Skinned Meshes Count");
            HookCameras(true);
            return;
        }

        _mainThread.Dispose();
        _renderThread.Dispose();
        _gcAlloc.Dispose();
        _drawCalls.Dispose();
        _shadowCasters.Dispose();
        _setPassCalls.Dispose();
        _batches.Dispose();
        _skinnedMeshes.Dispose();
        HookCameras(false);
    }

    // For the hitch log: what happened since the last frame.
    private static long _frameScriptTicks;
    private static long _frameWorstTicks;
    private static string _frameWorst = "";
    private static float _lastAutoSnapshot;
    private static bool _scannedThisFrame;
    private static int _lastGcCount;
    private static int _lastPlayerCount;
    private static float _lastHitchLog = -100f;
    private static int _skippedHitches;
    private static long _lastTickStamp;
    private static float _lastDeathSnapshot = -100f;

    // The hideout is a GameWorld with a main player too, but not a raid.
    internal static bool InRaid =>
        Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance is not HideoutGameWorld && Singleton<GameWorld>.Instance.MainPlayer != null;

    internal static bool InHideout => Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance is HideoutGameWorld;

    // After a raid, once a memory cleanup has run: how many of its players are still in memory,
    // and is any of them reachable from Smoothie's own fields? Players are tracked weakly, so
    // the tracking keeps nothing alive. Only while logging.
    private static readonly List<WeakReference> SeenPlayers = new();
    private static readonly HashSet<int> SeenPlayerIds = new();
    private static float _lastPlayerTrack, _raidEndAt = -1f;
    private static int _gcAtRaidEnd;

    internal static void OnRaidEnd()
    {
        _raidEndAt = SeenPlayers.Count > 0 ? Time.unscaledTime : -1f;
        _physicsKeepers = "";
        // The raid's objects are gone; their instance ids could be reused.
        LooksById.Clear();
    }

    private static void CheckRaidLeftovers(bool inRaid)
    {
        if (_raidEndAt >= 0f)
        {
            // A collection that starts 10 s or more after the raid, once the game has let go of it.
            if (Time.unscaledTime - _raidEndAt < 10f) _gcAtRaidEnd = Il2CppSystem.GC.CollectionCount(0);
            else if (Il2CppSystem.GC.CollectionCount(0) != _gcAtRaidEnd) ReportLeftovers();
            return;
        }

        if (!inRaid || !Logging || Time.unscaledTime - _lastPlayerTrack < 1f) return;
        _lastPlayerTrack = Time.unscaledTime;
        foreach (var player in Singleton<GameWorld>.Instance.AllAlivePlayersList)
        {
            if (player != null && SeenPlayerIds.Add(player.GetInstanceID())) SeenPlayers.Add(new WeakReference(player));
        }
    }

    private static void ReportLeftovers()
    {
        _raidEndAt = -1f;
        var alive = SeenPlayers.Select(w => w.Target).OfType<object>().ToList();
        var total = SeenPlayers.Count;
        SeenPlayers.Clear();
        SeenPlayerIds.Clear();
        try
        {
            var held = HeldBySmoothie(alive);
            Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} after the raid, with a memory cleanup run since: {alive.Count} of its {total} players still in memory " +
                               $"(game heap {UsedHeap() >> 20} MB); reachable from Smoothie's fields: {(held.Count == 0 ? "none" : string.Join(", ", held))}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"leftover check failed: {ex.Message}");
        }
    }

    // Walks every static field of Smoothie's types through its own objects, collections, boxed
    // structs and closures (not into game objects) and names the fields that lead to a target.
    // ConditionalWeakTables are skipped: Unity's Boehm GC treats their entries as ephemerons,
    // so they don't keep a key alive.
    private static List<string> HeldBySmoothie(List<object> targets)
    {
        var found = new List<string>();
        if (targets.Count == 0) return found;
        var wanted = new HashSet<object>(targets, ReferenceComparer.Instance);
        var seen = new HashSet<object>(ReferenceComparer.Instance);
        var own = typeof(Diagnostics).Assembly;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var stack = new Stack<object>();
        foreach (var type in own.GetTypes())
        {
            if (type.ContainsGenericParameters) continue;
            foreach (var field in type.GetFields(all | BindingFlags.Static))
            {
                if (field.IsLiteral || field.GetValue(null) is not { } root) continue;
                var hits = 0;
                stack.Push(root);
                while (stack.Count > 0)
                {
                    var o = stack.Pop();
                    if (!seen.Add(o)) continue;
                    if (wanted.Contains(o))
                    {
                        hits++;
                        continue;
                    }

                    var t = o.GetType();
                    if (o is Delegate d)
                    {
                        if (d.Target != null) stack.Push(d.Target);
                    }
                    else if (t.IsPrimitive || o is string || o is MemberInfo || t.Name.StartsWith("ConditionalWeakTable"))
                    {
                    }
                    else if (o is System.Collections.IEnumerable items && (t.IsArray || t.Namespace?.StartsWith("System.Collections") == true))
                    {
                        foreach (var item in items)
                        {
                            if (item != null) stack.Push(item);
                        }
                    }
                    else if (t.Assembly == own || t.IsValueType)
                    {
                        for (var walk = t; walk != null && walk != typeof(object); walk = walk.BaseType)
                        {
                            foreach (var f in walk.GetFields(all | BindingFlags.Instance))
                            {
                                if (f.GetValue(o) is { } value) stack.Push(value);
                            }
                        }
                    }
                }

                if (hits > 0) found.Add($"{type.Name}.{field.Name} ({hits})");
            }
        }

        return found;
    }

    internal sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new();
        bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }

    // 20 s into a raid, once: what is still switched on in each loaded scene. The menu's video
    // background was found playing through raids; this shows anything else left running by a
    // menu, the hideout or a mod. Walks every object once (a one-off hitch), only while logging.
    private static float _censusDueAt = -1f;
    private static float _rendererCensusAt = -1f;
    private static float _raidStartTime = -100f;

    private static void LogSceneCensus()
    {
        var sb = new StringBuilder($"{DateTime.Now:HH:mm:ss.fff} still switched on 20 s into the raid, per scene (cameras and playing videos named):\n");
        var scenes = new List<Scene>();
        for (var i = 0; i < SceneManager.sceneCount; i++) scenes.Add(SceneManager.GetSceneAt(i));
        // DontDestroyOnLoad (where the menu UI and the players live) isn't in the scene list:
        // a throwaway object moved into it gives its handle.
        var probe = new GameObject("Smoothie scene probe");
        Object.DontDestroyOnLoad(probe);
        scenes.Add(probe.scene);
        Object.DestroyImmediate(probe);

        var roots = new List<GameObject>();
        foreach (var scene in scenes)
        {
            if (!scene.isLoaded) continue;
            roots.Clear();
            roots.AddRange(scene.GetRootGameObjects());
            int cameras = 0, videos = 0, sounds = 0, animators = 0, canvases = 0, particles = 0, lights = 0;
            var named = new List<string>();
            foreach (var root in roots)
            {
                if (!root.activeInHierarchy) continue;
                foreach (var c in root.GetComponentsInChildren<Camera>())
                {
                    if (!c.enabled) continue;
                    cameras++;
                    named.Add($"camera {PathOf(c.transform)}{(c.targetTexture != null ? " (into a texture)" : "")}");
                }

                foreach (var v in root.GetComponentsInChildren<VideoPlayer>())
                {
                    if (!v.isPlaying) continue;
                    videos++;
                    named.Add($"video {PathOf(v.transform)} {v.width}x{v.height}");
                }

                foreach (var a in root.GetComponentsInChildren<AudioSource>()) if (a.isPlaying) sounds++;
                foreach (var a in root.GetComponentsInChildren<Animator>()) if (a.isActiveAndEnabled && a.runtimeAnimatorController != null) animators++;
                foreach (var c in root.GetComponentsInChildren<Canvas>()) if (c.isActiveAndEnabled && c.isRootCanvas) canvases++;
                foreach (var p in root.GetComponentsInChildren<ParticleSystem>()) if (p.isPlaying) particles++;
                foreach (var l in root.GetComponentsInChildren<Light>()) if (l.isActiveAndEnabled && l.shadows != LightShadows.None) lights++;
            }

            sb.AppendLine($"  {scene.name}: cameras {cameras}, videos {videos}, sounds playing {sounds}, animators {animators}, " +
                          $"canvases {canvases}, particles playing {particles}, shadowed lights {lights}");
            foreach (var line in named.Take(12)) sb.AppendLine($"      {line}");
        }
#if DEBUG
        GpuCensus(sb);
#endif

        Plugin.Log.LogInfo(sb.ToString().TrimEnd());
    }

#if DEBUG
    // Debug: GPU work in the raid that nothing may need (the menu videos were such a find): the
    // cameras' image effects and command buffers, shadowed lights, realtime reflection probes,
    // live render textures, and what's drawn that the covered-object hiding can't touch.
    private static void GpuCensus(StringBuilder sb)
    {
        sb.AppendLine("  GPU census:");
        var manager = EFT.CameraControl.CameraManager.Instance;
        foreach (var camera in new[] { manager?.Camera, manager?.OpticCameraManager?.Camera })
        {
            if (camera == null) continue;
            sb.AppendLine($"    camera {camera.name} ({(camera.isActiveAndEnabled ? "on" : "off")}): {camera.actualRenderingPath}, {camera.pixelWidth}x{camera.pixelHeight}, " +
                          $"HDR {camera.allowHDR}, MSAA {camera.allowMSAA}, depth texture {camera.depthTextureMode}, far {camera.farClipPlane:0} m");
            // Scripts that draw on the camera: image effects (OnRenderImage, in order) and pre/post render hooks.
            var hooks = camera.GetComponents<MonoBehaviour>().Where(b => b != null)
                .Select(b => (b, Hooks: string.Join("+", new[] { "OnRenderImage", "OnPreRender", "OnPostRender" }.Where(m => Declares(b.GetType(), m)))))
                .Where(x => x.Hooks.Length > 0)
                .Select(x => $"{x.b.GetType().Name} [{x.Hooks}]{(x.b.enabled ? "" : " (off)")}");
            sb.AppendLine("      scripts drawing: " + string.Join(", ", hooks));
            foreach (UnityEngine.Rendering.CameraEvent e in Enum.GetValues(typeof(UnityEngine.Rendering.CameraEvent)))
            {
                var buffers = camera.GetCommandBuffers(e);
                if (buffers.Length > 0) sb.AppendLine($"      {e}: " + string.Join(", ", buffers.Select(b => $"{b.name} ({b.sizeInBytes} B)")));
            }
        }

        var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Where(l => l.isActiveAndEnabled).ToArray();
        sb.AppendLine($"    lights on: {lights.Length}; casting shadows: " + string.Join(", ", lights.Where(l => l.shadows != LightShadows.None)
            .GroupBy(l => $"{l.type} {(l.shadowCustomResolution > 0 ? l.shadowCustomResolution.ToString() : l.shadowResolution.ToString())}")
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")));
        foreach (var light in lights.Where(l => l.commandBufferCount > 0).Take(12))
        {
            var names = Enum.GetValues(typeof(UnityEngine.Rendering.LightEvent)).Cast<UnityEngine.Rendering.LightEvent>()
                .SelectMany(e => light.GetCommandBuffers(e).Select(b => $"{e} {b.name}"));
            sb.AppendLine($"      light {PathOf(light.transform)} ({light.type}, {(light.cullingMask == 0 ? "skipped" : "lighting")}): {string.Join(", ", names)}");
        }

        var probes = Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None).Where(p => p.isActiveAndEnabled).ToArray();
        sb.AppendLine($"    reflection probes on: {probes.Length} (" + string.Join(", ", probes.GroupBy(p => p.mode).Select(g => $"{g.Count()} {g.Key}")) + ")");
        foreach (var probe in probes.Where(p => p.mode == UnityEngine.Rendering.ReflectionProbeMode.Realtime).Take(12))
            sb.AppendLine($"      realtime {PathOf(probe.transform)}: {probe.refreshMode}, {probe.timeSlicingMode}, {probe.resolution}px");

        var textures = Resources.FindObjectsOfTypeAll<RenderTexture>().Where(t => t.IsCreated()).ToArray();
        sb.AppendLine($"    render textures created: {textures.Length}, {textures.Sum(t => UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t)) >> 20} MB; largest: " +
                      string.Join(", ", textures.OrderByDescending(t => (long)t.width * t.height).Take(12).Select(t => $"{t.name} {t.width}x{t.height} {t.format}")));

        // Drawn this frame (any camera, shadows included) and not a plain mesh: what the hiding
        // leaves alone by kind. Players and their gear apart.
        var drawn = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).Where(r => r.isVisible && r is not MeshRenderer)
            .GroupBy(r => r.GetType().Name + (r.GetComponentInParent<Player>() != null ? " (players)" : ""));
        foreach (var kind in drawn.OrderByDescending(g => g.Count()))
            sb.AppendLine($"    drawn {kind.Key}: {kind.Count()}; " + string.Join(", ", kind.GroupBy(r => r.name).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Count()} {g.Key}")));

        // Draw calls GPU instancing or batching could merge: drawn level meshes (any camera,
        // shadows included) by mesh + materials. Groups of 2+ are what instancing could draw in one
        // call each, if their materials allow it and their shaders support it.
        var meshes = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None).Where(r => r.isVisible && r.GetComponentInParent<Player>() == null).ToArray();
        var groups = meshes.GroupBy(r => (Mesh: r.GetComponent<MeshFilter>()?.sharedMesh, Materials: string.Join("|", r.sharedMaterials.Select(m => m == null ? 0 : m.GetInstanceID())))).ToArray();
        var shared = groups.Where(g => g.Count() > 1).ToArray();
        var materials = meshes.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToArray();
        sb.AppendLine($"    drawn level meshes: {meshes.Length}, {meshes.Count(r => r.isPartOfStaticBatch)} static-batched, {meshes.Count(r => r.sharedMaterials.Length > 1)} with several materials; " +
                      $"{groups.Length} mesh+material groups, {shared.Sum(g => g.Count())} meshes in {shared.Length} groups of 2+ (instancing could save up to {shared.Sum(g => g.Count() - 1)} draws); " +
                      $"materials {materials.Length}, {materials.Count(m => m.enableInstancing)} with instancing on");
        sb.AppendLine("      largest groups: " + string.Join(", ", shared.OrderByDescending(g => g.Count()).Take(10).Select(g => $"{g.Count()} {g.First().name} ({g.First().sharedMaterial?.shader?.name})")));
        sb.AppendLine("      shaders of grouped meshes: " + string.Join(", ", shared.SelectMany(g => g).GroupBy(r => r.sharedMaterial?.shader?.name ?? "none").OrderByDescending(g => g.Count()).Take(10).Select(g => $"{g.Count()} {g.Key}")));
    }

    // Debug, F10: the small level meshes drawn right now (by any camera, shadows included): what
    // they are, how far and how big on screen, whether the game's baked visibility owns them, why
    // the covered-object hiding left them drawn, and whether they could be merged (readable
    // meshes, shared materials). For views where the draw count jumps (Customs: 3,900 draws,
    // mostly one-quad wall decals).
    internal static void LogSmallCensus()
    {
        var camera = EFT.CameraControl.CameraManager.Instance?.Camera;
        if (camera == null) return;
        var eye = camera.transform.position;
        // Pixels per metre at 1 m distance at the camera's render resolution.
        var pixelsPerUnit = camera.pixelHeight / (2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
        var baked = new HashSet<Renderer>(ReferenceComparer.Instance);
        foreach (var crossScene in Object.FindObjectsByType<Koenigz.PerfectCulling.EFT.PerfectCullingCrossSceneGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (crossScene.bakeGroups == null) continue;
            foreach (var group in crossScene.bakeGroups)
            {
                if (group?.renderers == null) continue;
                foreach (var renderer in group.renderers)
                {
                    if (renderer != null) baked.Add(renderer);
                }
            }
        }

        var drawn = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None).Where(r => r.isVisible && r.GetComponentInParent<Player>() == null).ToArray();
        var small = new List<(MeshRenderer Renderer, Mesh Mesh, float Distance, float Pixels, bool InView)>();
        var planes = GeometryUtility.CalculateFrustumPlanes(camera);
        foreach (var r in drawn)
        {
            var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) continue;
            long indices = 0;
            for (var s = 0; s < mesh.subMeshCount; s++) indices += mesh.GetIndexCount(s);
            if (indices > 108) continue;
            var bounds = r.bounds;
            var distance = Mathf.Max(0.01f, Vector3.Distance(eye, bounds.ClosestPoint(eye)));
            var seen = GeometryUtility.TestPlanesAABB(planes, bounds);
            small.Add((r, mesh, distance, bounds.size.magnitude * pixelsPerUnit / distance, seen));
        }

        string Buckets(IEnumerable<float> values, float[] edges, string unit) =>
            string.Join(", ", edges.Select((e, k) => (e, k)).Select(x =>
                $"{(x.k == 0 ? "<" : "")}{(x.k == 0 ? x.e : edges[x.k - 1]).ToString("0.#")}{(x.k == 0 ? "" : "-" + x.e.ToString("0.#"))}{unit}: {values.Count(v => (x.k == 0 || v >= edges[x.k - 1]) && v < x.e)}"))
            + $", {edges[edges.Length - 1]:0.#}{unit}+: {values.Count(v => v >= edges[edges.Length - 1])}";

        var inView = small.Where(s => s.InView).ToArray();
        var sb = new StringBuilder($"{DateTime.Now:HH:mm:ss.fff} small level meshes (36 triangles or fewer) drawn now: {small.Count} of {drawn.Length} drawn level meshes, {inView.Length} in the main view (the rest only in shadows or other cameras); render size {camera.pixelWidth}x{camera.pixelHeight}\n");
        sb.AppendLine("  in view, by distance: " + Buckets(inView.Select(s => s.Distance), new[] { 5f, 15f, 30f, 60f, 120f }, " m"));
        sb.AppendLine("  in view, by size on screen: " + Buckets(inView.Select(s => s.Pixels), new[] { 1f, 2f, 4f, 8f, 16f, 32f }, " px"));
        sb.AppendLine($"  in view: {inView.Count(s => baked.Contains(s.Renderer))} in the game's baked visibility, {inView.Count(s => s.Renderer.GetComponentInParent<LODGroup>() != null)} in an LOD group, " +
                      $"{inView.Count(s => s.Mesh.isReadable)} with readable meshes, {inView.Count(s => s.Renderer.isPartOfStaticBatch)} static-batched");
        sb.AppendLine("  why the hiding left them drawn: " + string.Join(", ", inView.GroupBy(s => DepthCulling.Explain(s.Renderer)).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")));
        sb.AppendLine("  materials: " + string.Join(", ", inView.GroupBy(s => s.Renderer.sharedMaterial?.name ?? "none").OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Count()} {g.Key}")) +
                      $" ({inView.Select(s => s.Renderer.sharedMaterial).Distinct().Count()} distinct)");
        sb.AppendLine("  layers: " + string.Join(", ", inView.GroupBy(s => LayerMask.LayerToName(s.Renderer.gameObject.layer)).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")));
        sb.AppendLine("  names: " + string.Join(", ", inView.GroupBy(s => s.Renderer.name).OrderByDescending(g => g.Count()).Take(10).Select(g => $"{g.Count()} {g.Key}")));
        sb.AppendLine("  parents: " + string.Join(", ", inView.GroupBy(s => s.Renderer.transform.parent != null ? s.Renderer.transform.parent.name : "-").OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Count()} {g.Key}")));

        // Why the size test can't take them: the hiding works per unit (a whole LOD group), and a
        // unit's box on screen in render pixels (width x height); and how far the game's own LOD
        // keeps them (lodBias multiplies every group's size on screen).
        var unitSizes = new List<(int Renderers, float W, float H)>();
        foreach (var s in inView)
        {
            if (!DepthCulling.UnitOf(s.Renderer, out var count, out var min, out var max)) continue;
            float x0 = 1f, y0 = 1f, x1 = 0f, y1 = 0f;
            for (var c = 0; c < 8; c++)
            {
                var v = camera.WorldToViewportPoint(new Vector3((c & 1) == 0 ? min.x : max.x, (c & 2) == 0 ? min.y : max.y, (c & 4) == 0 ? min.z : max.z));
                x0 = Mathf.Min(x0, v.x); y0 = Mathf.Min(y0, v.y); x1 = Mathf.Max(x1, v.x); y1 = Mathf.Max(y1, v.y);
            }

            unitSizes.Add((count, (x1 - x0) * camera.pixelWidth, (y1 - y0) * camera.pixelHeight));
        }

        sb.AppendLine($"  hiding units of these: {unitSizes.Count} tracked; renderers per unit: " + Buckets(unitSizes.Select(u => (float)u.Renderers), new[] { 2f, 5f, 20f, 100f }, "") +
                      "; unit box, larger side: " + Buckets(unitSizes.Select(u => Mathf.Max(u.W, u.H)), new[] { 1.5f, 4f, 16f, 64f }, " px") +
                      "; smaller side: " + Buckets(unitSizes.Select(u => Mathf.Min(u.W, u.H)), new[] { 1.5f, 4f, 16f }, " px"));
        var groups = inView.Select(s => s.Renderer.GetComponentInParent<LODGroup>()).Where(g => g != null).Distinct().ToArray();
        sb.AppendLine($"  LOD: lodBias {QualitySettings.lodBias}, maximumLODLevel {QualitySettings.maximumLODLevel}; {groups.Length} LOD groups hold them, renderers per group: " +
                      Buckets(groups.Select(g => (float)g!.GetLODs().Sum(l => l.renderers.Length)), new[] { 2f, 5f, 20f, 100f }, "") +
                      "; last LOD's cull height: " + Buckets(groups.Select(g => { var l = g!.GetLODs(); return l.Length == 0 ? 0f : l[l.Length - 1].screenRelativeTransitionHeight * 100f; }), new[] { 0.5f, 1f, 2f, 5f }, " %"));

        // Everything drawn in the main view, not just the small meshes, by why it's drawn: players,
        // non-mesh renderers by type, and level meshes by what the hiding makes of them (kept as
        // visible, or never collected and why). Draws estimated as one per material.
        var everything = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).Where(r => r.isVisible && GeometryUtility.TestPlanesAABB(planes, r.bounds)).ToArray();
        string KindOf(Renderer r) =>
            r.GetComponentInParent<Player>() != null ? "players (bots and you)"
            : r is not MeshRenderer ? r.GetType().Name
            : DepthCulling.Explain(r);
        sb.AppendLine($"  everything drawn in the main view: {everything.Length} renderers, ~{everything.Sum(r => r.sharedMaterials.Length)} draws, by kind:");
        foreach (var kind in everything.GroupBy(KindOf).OrderByDescending(g => g.Sum(r => r.sharedMaterials.Length)))
            sb.AppendLine($"    ~{kind.Sum(r => r.sharedMaterials.Length),5} draws, {kind.Count(),5} renderers: {kind.Key}; e.g. " +
                          string.Join(", ", kind.GroupBy(r => r.name).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Count()} {g.Key}")));

        // Flat ones (one local axis under 1% of the largest): a one-sided flat mesh seen from
        // behind draws nothing but still costs its draw call. Which side is the front needs the
        // mesh's normals, so only readable meshes can tell; the eye must be 5 cm behind the plane.
        int flat = 0, readableFlat = 0, facingAway = 0, facingAwayShadows = 0;
        foreach (var s in inView)
        {
            var size = s.Mesh.bounds.size;
            var largest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            if (largest <= 0f || Mathf.Min(size.x, Mathf.Min(size.y, size.z)) > largest * 0.01f) continue;
            flat++;
            if (!s.Mesh.isReadable) continue;
            readableFlat++;
            var normals = s.Mesh.normals;
            if (normals.Length == 0) continue;
            var t = s.Renderer.transform;
            var normal = t.TransformDirection(normals[0]).normalized;
            var point = t.TransformPoint(s.Mesh.bounds.center);
            if (Vector3.Dot(normal, eye - point) >= -0.05f) continue;
            facingAway++;
            if (s.Renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) facingAwayShadows++;
        }

        sb.Append($"  flat: {flat} of {inView.Length} in view, {readableFlat} of them readable; facing away from the camera: {facingAway} ({facingAwayShadows} of those cast shadows); " +
                  $"flat ones casting shadows: {inView.Count(s => s.Renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)} of all small in view");
        Plugin.Log.LogInfo(sb.ToString());
    }

    private static bool Declares(Type type, string method)
    {
        for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
        {
            if (t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Any(m => m.Name == method)) return true;
        }

        return false;
    }
#endif

    private static string PathOf(Transform transform)
    {
        var path = transform.name;
        for (var parent = transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
        return path;
    }

    internal static void OnRaidStart()
    {
        // No collection between the last raid and this one: report what's left anyway.
        if (_raidEndAt >= 0f) ReportLeftovers();
        _fullScanDue = _profiling;
        _lastAutoSnapshot = Time.unscaledTime;
        _censusDueAt = Time.unscaledTime + 20f;
        _raidStartTime = Time.unscaledTime;
        // With the per-script profiler already on, the renderer census waits for the raid to
        // settle too, instead of running while the level is still coming in.
        if (_profiling) _rendererCensusAt = Time.unscaledTime + 30f;
        var heap = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
        _raidStarted = true;
        Cleanup.Start(heap);
        if (!Logging) return;
        Plugin.Log.LogInfo($"raid started: GC mode {GarbageCollector.GCMode} (incremental {GarbageCollector.isIncremental}), game heap {heap >> 20} MB, " +
                           $"RAM {SystemInfo.systemMemorySize / 1024} GB (game disables GC in raid from {InGameMemoryManagement.Settings.GigabytesRequiredToDisableGCDuringRaid} GB)");
    }

    // "Prevent memory cleanup stutters in raids" (CleanupPause has the why). Unity's switch is used
    // directly, with the game's own minimum-RAM rule: the game's setter logs every switch.
    private static readonly CleanupPause Cleanup = new();
    private static bool _raidStarted;

    // Called by the plugin every frame.
    internal static void KeepCleanupPaused()
    {
        if (!InRaid) _raidStarted = false;
        if (!_raidStarted || !Plugin.On(Plugin.KeepCleanupPaused)) return;
        if (InGameMemoryManagement.CurrentMemorySize < InGameMemoryManagement.Settings.GigabytesRequiredToDisableGCDuringRaid) return;
        var gaveUp = Cleanup.GaveUp;
        switch (Cleanup.Tick(GarbageCollector.GCMode != GarbageCollector.Mode.Disabled, Il2CppSystem.GC.CollectionCount(0),
                    UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong, Time.unscaledTime, Time.frameCount))
        {
            case CleanupPause.Action.Pause:
                GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                break;
            case CleanupPause.Action.Collect:
                // The same cleanup SPT runs on an inventory open.
                GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
                GarbageCollector.CollectIncremental(25_000_000);
                break;
        }

        if (!gaveUp && Cleanup.GaveUp && Logging)
        {
            Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} memory cleanup was switched back on right after each of the last {CleanupPause.FightsToGiveUp} pauses: " +
                               "something else wants it on, so it stays on for the rest of this raid");
        }
    }

    // In raid, log who forces a collection: GC.Collect and Resources.UnloadUnusedAssets
    // still collect with the GC switched off.
    internal static void WatchForcedCollections(Harmony harmony)
    {
        var prefix = new HarmonyMethod(typeof(Diagnostics), nameof(ForcedCollectionPrefix));
        foreach (var method in typeof(Il2CppSystem.GC).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == nameof(Il2CppSystem.GC.Collect)))
        {
            harmony.Patch(method, prefix);
        }

        harmony.Patch(AccessTools.Method(typeof(Resources), nameof(Resources.UnloadUnusedAssets)), prefix);
    }

    // Every bot-profile request to the server: what was asked for and which code asked.
    // Raids were seen sending hundreds of these for a dozen bots that actually spawned.
    internal static void WatchBotRequests(Harmony harmony) =>
        harmony.Patch(AccessTools.Method(typeof(ClientBackendSession), nameof(ClientBackendSession.LoadBots)),
            new HarmonyMethod(typeof(Diagnostics), nameof(BotRequestPrefix)) { priority = Priority.First });

    // Summed and written as one line a second at most: a flood is hundreds of requests.
    private static int _requests, _requestedProfiles, _receivedProfiles;
    private static readonly Dictionary<string, int> RequestKinds = new();
    private static float _lastRequestLog;

    // In raid, which code builds profiles (each one a full profile object, bot or not): bots
    // have a known path; anything else, in a raid without bots too, is worth knowing. Summed
    // and logged once every 10 s at most.
    private static readonly Dictionary<string, int> ProfileCallers = new();
    private static float _lastProfileLog;

    internal static void NoteProfileBuilt()
    {
        if (!Logging || !InRaid) return;
        var caller = (new StackTrace(2, false).GetFrames() ?? Array.Empty<StackFrame>())
            .Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType != null && !m.DeclaringType.FullName!.StartsWith("System.") && m.DeclaringType != typeof(Profile) && !m.DeclaringType.Assembly.Equals(typeof(Diagnostics).Assembly))
            .Select(m => $"{m!.DeclaringType!.Name}.{m.Name}")
            .FirstOrDefault() ?? "?";
        ProfileCallers[caller] = ProfileCallers.TryGetValue(caller, out var n) ? n + 1 : 1;
        if (Time.unscaledTime - _lastProfileLog < 10f) return;
        _lastProfileLog = Time.unscaledTime;
        Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} profiles built in raid: {string.Join(", ", ProfileCallers.OrderByDescending(c => c.Value).Select(c => $"{c.Value}x {c.Key}"))}");
        ProfileCallers.Clear();
    }

    private static void BotRequestPrefix(Il2CppSystem.Collections.Generic.List<CountTypeBotWave> conditions)
    {
        if (!Logging || !InRaid || conditions == null) return;
        var asked = string.Join(", ", conditions.Select(c => $"{c.Limit}x {c.Role} {c.Difficulty}"));

        // The first caller outside the async plumbing: the game code that asked.
        var caller = (new StackTrace(2, false).GetFrames() ?? Array.Empty<StackFrame>())
            .Select(f => f.GetMethod()?.DeclaringType?.FullName)
            .FirstOrDefault(n => n != null && !n.StartsWith("System.")) ?? "?";
        var kind = $"{asked} from {caller}";
        RequestKinds[kind] = RequestKinds.TryGetValue(kind, out var n) ? n + 1 : 1;
        _requests++;
        foreach (var c in conditions) _requestedProfiles += c.Limit;
    }

    internal static void NoteProfilesReceived(int count)
    {
        if (Logging && InRaid) _receivedProfiles += count;
    }

    private static void LogBotRequests()
    {
        if (_requests + _receivedProfiles == 0 || Time.unscaledTime - _lastRequestLog < 1f) return;
        _lastRequestLog = Time.unscaledTime;
        var top = RequestKinds.OrderByDescending(k => k.Value).Take(3).Select(k => $"{k.Value}x [{k.Key}]");
        Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} bot profiles: {_requests} requests for {_requestedProfiles}, {_receivedProfiles} received, " +
                           $"{ProfileBudget.Waiting} waiting to be built" + (_requests > 0 ? $" | {string.Join(", ", top)}" : ""));
        _requests = _requestedProfiles = _receivedProfiles = 0;
        RequestKinds.Clear();
    }

    private static float _lastForcedLog = -100f;

    private static void ForcedCollectionPrefix(MethodBase __originalMethod)
    {
        if (!Plugin.On(Plugin.LogDrops) || !InRaid || Time.unscaledTime - _lastForcedLog < 1f) return;
        _lastForcedLog = Time.unscaledTime;

        var frames = new StackTrace(2, false).GetFrames() ?? Array.Empty<StackFrame>();
        var callers = frames.Take(6).Select(f => f.GetMethod()).Where(m => m != null)
            .Select(m => $"{m!.DeclaringType?.FullName}.{m.Name}");
        Plugin.Log.LogWarning($"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} called in raid (GC mode {GarbageCollector.GCMode}) from: {string.Join(" <- ", callers)}");
    }

    internal static void StartProfiling()
    {
        if (_profiling) return;
        // Patched once per session and never unpatched (see StopProfiling): only methods not
        // timed yet get patched here.
        var first = _harmony == null;
        _harmony ??= new Harmony(HarmonyId);
        _profiling = true;
        _rendererCensusAt = Time.unscaledTime + 3f;
        _windowFrames = 0;
        _windowStart = Time.unscaledTime;
        _fullScanDue = true;
        UpdateEngineTiming();
        PatchRunningScripts();
        PatchSpawnSteps();
        PatchPlayerSteps();
        if (first) PatchUi();
        if (first)
        {
            _harmony.Patch(AccessTools.Method(typeof(GameWorld), nameof(GameWorld.RegisterPlayer)),
                postfix: new HarmonyMethod(typeof(Diagnostics), nameof(OnPlayerRegistered)));
        }
    }

    // The steps of a bot spawn, summed per second and logged as one line when any adds up to
    // over 5 ms. The game's spawn check (NonWavesSpawnScenario.Update) was seen taking 120 ms
    // every 15 s in Horde raids; this splits it up. Async methods count up to their first wait.
    private static readonly (string Type, string Method)[] SpawnSteps =
    {
        ("EFT.NonWavesSpawnScenario", "Update"),
        ("EFT.NonWaveGroupScenario", "TrySpawn"),
        ("EFT.BotsController", "ActivateBotsWithoutWave"),
        ("EFT.BotSpawner", "ActivateBotsWithoutWave"),
        ("EFT.BotSpawner", "TrySpawnFreeAndDelay"),
        ("BotCreationData", "Create"),
        ("BotProfileClient", "FillCreationDataWithProfiles"),
        ("BotProfileClient", "GetNewProfile"),
        ("BotProfileClient", "CreateProfile"),
        ("EFT.Player", "Init"),
        ("EFT.GameWorld", "RegisterPlayer"),
        ("EFT.BotOwner", "Create"),
        ("EFT.BotOwner", "PreActivate"),
        ("EFT.BotOwner", "Activate"),
        ("EFT.BotSpawner", "ActivateBotCallback"),
        ("Fika.Core.Main.GameMode.HostGameController", "CreateBot"),
    };

    private static readonly Dictionary<MethodBase, Bucket> Steps = new();

    // Opened up one level: every call these make directly is timed too, as are the handlers
    // of the death events. A bot's activation took 20-40 ms and a death up to 180 ms in one
    // frame; this says which part. Inner calls only count while inside one of these, so the
    // same method called from elsewhere doesn't.
    private static readonly (Type Type, string Method)[] Opened =
    {
        (typeof(BotOwner), nameof(BotOwner.PreActivate)),
        (typeof(BotOwner), nameof(BotOwner.Activate)),
#if DEBUG
        // Debug builds only: the hooks stay until the game closes, and a death sequence rebuilt
        // by Harmony (OnDead, everything it calls, every mod's death handler) is too much to
        // risk for players who switch the profiler on once.
        (typeof(Player), nameof(Player.OnDead)),
#endif
    };

#if DEBUG
    private static readonly string[] DeathEvents = { "OnPlayerDead", "OnPlayerDeadStatic", "OnPlayerDeadOrUnspawn", "OnIPlayerDeadOrUnspawn" };
#endif
    private static readonly HashSet<MethodBase> Inner = new();
    // How deep inside an opened-up method (spawn and death steps) and a drilled one we are;
    // kept apart so one doesn't count the other's parts.
    private static int _inside, _insideDrilled;

    private static void PatchSpawnSteps()
    {
        foreach (var (typeName, name) in SpawnSteps)
        {
            var type = Plugin.FindType(typeName);
            if (type == null) continue;
            foreach (var method in AccessTools.GetDeclaredMethods(type).Where(m => m.Name == name && !m.IsAbstract))
            {
                TimeStep(Steps, method, $"{type.Name}.{name}");
            }
        }

        // IL2CPP: the methods as declared; no overrides from other mods' types (Fika isn't on SPT 5.0)
        // and no scan of every loaded type for them (it took the game down here).
        var opened = Opened.SelectMany(o => AccessTools.GetDeclaredMethods(o.Type).Where(m => m.Name == o.Method && !m.IsAbstract));
        foreach (var parent in opened.ToList()) OpenUp(parent);
#if DEBUG
        TimeHandlers(null);
#endif
    }

    private static void OpenUp(MethodInfo parent)
    {
        if (TimeStep(Steps, parent, $"{parent.DeclaringType?.Name}.{parent.Name}") == null) return;
        try
        {
            Hook(parent, new HarmonyMethod(typeof(Diagnostics), nameof(Enter)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(Diagnostics), nameof(Leave)));
            foreach (var callee in Callees(parent)) TimeInner(callee, $"{parent.Name} > {callee.DeclaringType!.Name}.{callee.Name}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"profiler: can't open up {parent.Name}: {ex.Message}");
        }
    }

    // Drill-down: every 2 s while profiling, the heaviest timed method not opened yet (a
    // script, or a part already found inside one) is opened one level, so each snapshot shows
    // what the top scripts are made of, a level deeper each time. Parts only count while
    // inside an opened method, so the same method called from elsewhere doesn't.
    private static readonly Dictionary<MethodBase, Bucket> Parts = new();
    private static readonly HashSet<MethodBase> Drilled = new();
    private static List<(string Name, double Ms, double Calls)> _topParts = new();
    private const int MaxDrilled = 40;

    private static void DrillDown(int frames)
    {
        if (Drilled.Count >= MaxDrilled) return;
        MethodBase? best = null;
        long bestTicks = 0;
        foreach (var pair in Buckets) Consider(pair.Key, pair.Value);
        foreach (var pair in Parts) Consider(pair.Key, pair.Value);
        // Under 0.03 ms a frame there's nothing worth finding inside.
        if (best == null || bestTicks * TickMs / frames < 0.03) return;
        Drilled.Add(best);
        _scannedThisFrame = true;
        var parent = $"{best.DeclaringType?.Name}.{best.Name}";
        try
        {
            Hook(best, new HarmonyMethod(typeof(Diagnostics), nameof(EnterDrilled)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(Diagnostics), nameof(LeaveDrilled)));
            foreach (var callee in Callees(best))
            {
                if (Parts.ContainsKey(callee)) continue;
                var part = new Bucket { Name = $"{parent} > {callee.DeclaringType!.Name}.{callee.Name}" };
                Parts[callee] = part;
                try
                {
                    Hook(callee, new HarmonyMethod(typeof(Diagnostics), nameof(Prefix)) { priority = Priority.First },
                        new HarmonyMethod(typeof(Diagnostics), nameof(PartPostfix)) { priority = Priority.Last });
                }
                catch (Exception)
                {
                    Parts.Remove(callee);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"profiler: can't open up {parent}: {ex.Message}");
        }

        void Consider(MethodBase method, Bucket bucket)
        {
            if (bucket.Ticks > bestTicks && !Drilled.Contains(method) && method.DeclaringType != null && IsGameCode(method.DeclaringType.Assembly))
            {
                best = method;
                bestTicks = bucket.Ticks;
            }
        }
    }

    // The game's own methods a method calls directly (not property accessors). Never Unity's:
    // its tiny helpers (Object != null) run hundreds of thousands of times a second, so timing
    // them costs whole frames, and patching its rendering internals broke the picture. Never
    // generic ones either: Mono shares their code between instantiations, and patching
    // MyExtensions.RandomElement<ISpawnPoint> crashed the game natively when a bot started a patrol.
    // IL2CPP: no IL to read; the native code's direct calls, as Il2CppInterop's xref scan finds them.
    // What a method's machine code calls, read once and kept. IL2CPP: not XrefScanner.XrefScan, which
    // reads each global the method loads as an object; in a method that hasn't run yet those are
    // Unity 2022's unresolved metadata tokens, and reading one crashed the game outright. These are
    // the same decoded call targets, globals left alone. A method someone already patched starts
    // with a jump into Harmony's code instead, so
    // Hook reads a method before hooking it, and one patched by someone else counts as calling nothing.
    private static readonly Dictionary<MethodBase, List<MethodBase>> CallsCache = new();

    internal static List<MethodBase> CallsOf(MethodBase method)
    {
        if (CallsCache.TryGetValue(method, out var known)) return known;
        var calls = new List<MethodBase>();
        var info = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method)?.GetValue(null);
        if (info is IntPtr pointer && pointer != IntPtr.Zero && Harmony.GetPatchInfo(method) is not { Owners.Count: > 0 })
        {
            calls = Il2CppInterop.Common.XrefScans.XrefScannerLowLevel.JumpTargets(BodyOf(pointer))
                .Where(target => target != IntPtr.Zero)
                .Select(Il2CppInterop.Common.XrefScans.XrefScanMethodDb.TryResolvePointer).OfType<MethodBase>().Distinct().ToList();
        }

        CallsCache[method] = calls;
        return calls;
    }

    private static IEnumerable<MethodInfo> Callees(MethodBase method) =>
        CallsOf(method).OfType<MethodInfo>()
            .Where(m => !m.IsAbstract && !m.IsGenericMethod && m.DeclaringType != null && !m.DeclaringType.IsGenericType && IsGameCode(m.DeclaringType.Assembly)
                        && !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_") && !m.Name.StartsWith("add_") && !m.Name.StartsWith("remove_")
                        && m.GetMethodBody() != null)
            .ToList();

    private static void PartPostfix(MethodBase __originalMethod, long __state)
    {
        if (!_profiling || _insideDrilled == 0 || !Parts.TryGetValue(__originalMethod, out var part)) return;
        part.Ticks += Stopwatch.GetTimestamp() - __state;
        part.Calls++;
    }

    // The in-raid HUD only (no inventory or loot screen open, those are meant to be busy):
    // which UI elements ask Unity to rebuild them, per second, and how long Unity's UI update
    // takes. An element rebuilt every frame though nothing about it changed is the classic
    // Unity UI waste. Counted per element, named only when the snapshot is written.
    private static readonly Dictionary<int, (Component Element, int Layouts, int Graphics)> UiDirty = new();
    private static long _uiTicks;
    private static int _uiUpdates;
    private static string _uiLine = "";

    private static void PatchUi()
    {
        var registry = AccessTools.TypeByName("UnityEngine.UI.CanvasUpdateRegistry");
        if (registry == null) return;
        try
        {
            _harmony!.Patch(AccessTools.Method(registry, "InternalRegisterCanvasElementForLayoutRebuild"),
                postfix: new HarmonyMethod(typeof(Diagnostics), nameof(UiLayoutPostfix)));
            _harmony.Patch(AccessTools.Method(registry, "InternalRegisterCanvasElementForGraphicRebuild"),
                postfix: new HarmonyMethod(typeof(Diagnostics), nameof(UiGraphicPostfix)));
            _harmony.Patch(AccessTools.Method(registry, "PerformUpdate"),
                new HarmonyMethod(typeof(Diagnostics), nameof(Prefix)), new HarmonyMethod(typeof(Diagnostics), nameof(UiUpdatePostfix)));
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"profiler: can't watch UI rebuilds: {ex.Message}");
        }
    }

    private static bool HudOnly() =>
        _profiling && InRaid && Singleton<GameWorld>.Instance.MainPlayer is { } me && !me.IsInventoryOpened;

    private static void UiLayoutPostfix(object element, bool __result)
    {
        if (__result && HudOnly()) CountUi(element, true);
    }

    private static void UiGraphicPostfix(object element, bool __result)
    {
        if (__result && HudOnly()) CountUi(element, false);
    }

    private static void CountUi(object element, bool layout)
    {
        if (element is not Component component || component == null) return;
        var id = component.GetInstanceID();
        UiDirty.TryGetValue(id, out var entry);
        UiDirty[id] = (component, entry.Layouts + (layout ? 1 : 0), entry.Graphics + (layout ? 0 : 1));
    }

    private static void UiUpdatePostfix(long __state)
    {
        if (!HudOnly()) return;
        _uiTicks += Stopwatch.GetTimestamp() - __state;
        _uiUpdates++;
    }

    // Once per 2 s window: per second, with the busiest elements named.
    private static void CloseUiWindow(double seconds)
    {
        if (_uiUpdates == 0)
        {
            UiDirty.Clear();
            return;
        }

        var layouts = UiDirty.Values.Sum(e => e.Layouts);
        var graphics = UiDirty.Values.Sum(e => e.Graphics);
        var top = UiDirty.Values.Where(e => e.Element != null).OrderByDescending(e => e.Layouts + e.Graphics).Take(12)
            .Select(e =>
            {
                var canvas = e.Element.GetComponentInParent<Canvas>();
                var root = canvas != null ? canvas.rootCanvas.name : "?";
                return $"{(e.Layouts + e.Graphics) / seconds:0}/s {root}/{e.Element.gameObject.name} ({e.Element.GetType().Name}{(e.Layouts > 0 ? ", layout" : "")})";
            });
        _uiLine = $"  in-raid UI (no inventory open), per second: {layouts / seconds:0} layout and {graphics / seconds:0} graphic rebuilds of {UiDirty.Count} elements, " +
                  $"Unity's UI update {_uiTicks * TickMs / _uiUpdates:0.000} ms/frame; busiest: {string.Join(", ", top)}";
        UiDirty.Clear();
        _uiTicks = 0;
        _uiUpdates = 0;
    }

    private static void TimeInner(MethodBase method, string name)
    {
        if (Steps.ContainsKey(method)) return;
        if (TimeStep(Steps, method, name) != null) Inner.Add(method);
    }

#if DEBUG
    // The death events' handlers: game code, Fika and other mods. Per player, so scanned
    // again as players spawn; null for the static one.
    private static void TimeHandlers(Player? player)
    {
        foreach (var name in DeathEvents)
        {
            var field = AccessTools.Field(typeof(Player), name);
            if (field == null || field.IsStatic != (player == null)) continue;
            if (field.GetValue(player) is not Delegate handlers) continue;
            foreach (var handler in handlers.GetInvocationList())
            {
                var method = handler.Method;
                if (method.GetMethodBody() == null || method.DeclaringType == null) continue;
                TimeInner(method, $"OnDead > {name} > {method.DeclaringType.Name}.{method.Name}");
            }
        }
    }
#endif

    private static void Enter() => _inside++;

    private static void EnterDrilled() => _insideDrilled++;

    private static Exception? LeaveDrilled(Exception? __exception)
    {
        _insideDrilled--;
        return __exception;
    }

    private static Exception? Leave(Exception? __exception)
    {
        _inside--;
        return __exception;
    }

    // First in and last out, so other mods' prefixes and postfixes count too.
    private static Bucket? TimeStep(Dictionary<MethodBase, Bucket> into, MethodBase method, string name)
    {
        if (into.TryGetValue(method, out var known)) return known;
        var step = new Bucket { Name = name };
        try
        {
            into[method] = step;
            Hook(method, new HarmonyMethod(typeof(Diagnostics), nameof(Prefix)) { priority = Priority.First },
                new HarmonyMethod(typeof(Diagnostics), nameof(StepPostfix)) { priority = Priority.Last });
            return step;
        }
        catch (Exception ex)
        {
            into.Remove(method);
            Plugin.Log.LogWarning($"profiler: can't time {name}: {ex.Message}");
            return null;
        }
    }

    private static void StepPostfix(MethodBase __originalMethod, long __state)
    {
        if (!_profiling) return;
        if (!Steps.TryGetValue(__originalMethod, out var step) && !PlayerSteps.TryGetValue(__originalMethod, out step)) return;
        if (_inside == 0 && Inner.Contains(__originalMethod)) return;
        step.Ticks += Stopwatch.GetTimestamp() - __state;
        step.Calls++;
    }

    // What every player's own per-frame update is made of, summed over all players: with
    // Horde's 35 bots it took the frame rate below 100 while all of them were far and calm.
    // Nested: the indented parts run inside the line before them. Shown in the snapshot.
    private static readonly (string Label, Type Type, string Method)[] PlayerStepList =
    {
        ("LateUpdate", typeof(Player), nameof(Player.LateUpdate)),
        ("  animator states", typeof(MovementContext), nameof(MovementContext.AnimatorStatesLateUpdate)),
        ("  stamina", typeof(PhysicalBase), nameof(PhysicalBase.LateUpdate)),
        ("  IK", typeof(Player), nameof(Player.VisualPass)),
        ("  arms", typeof(Player), nameof(Player.ArmsLateUpdate)),
        ("  movement late", typeof(MovementContext), nameof(MovementContext.LateFixedUpdate)),
        ("  AI data", typeof(AIData), nameof(AIData.LateUpdate)),
        ("ManualUpdate", typeof(Player), nameof(Player.ManualUpdate)),
        ("  movement", typeof(Player), nameof(Player.MovementUpdate)),
        ("  health", typeof(Player), nameof(Player.HealthControllerUpdate)),
        ("  body animator", typeof(Player), nameof(Player.BodyUpdate)),
        ("  trigger search", typeof(Player), nameof(Player.UpdateTriggerColliderSearcher)),
        ("bot brains (BotOwner)", typeof(BotOwner), nameof(BotOwner.UpdateManual)),
    };

    private static readonly Dictionary<MethodBase, Bucket> PlayerSteps = new();

    // In PlayerStepList's order, for the report.
    private static readonly Bucket?[] PlayerStepRows = new Bucket?[PlayerStepList.Length];
    private static float _playerStepsStart;
    private static int _playerStepsFrame;
    private static string _playerStepsLine = "";

    private static void PatchPlayerSteps()
    {
        for (var i = 0; i < PlayerStepList.Length; i++)
        {
            var (label, type, name) = PlayerStepList[i];
            var method = AccessTools.DeclaredMethod(type, name);
            PlayerStepRows[i] = method == null ? null : TimeStep(PlayerSteps, method, label);
        }

        _playerStepsStart = Time.unscaledTime;
        _playerStepsFrame = Time.frameCount;
    }

    // Once a second: last second's per-frame average, then start over.
    private static void RollPlayerSteps()
    {
        if (!_profiling || PlayerSteps.Count == 0 || Time.unscaledTime - _playerStepsStart < 1f) return;
        var frames = Math.Max(1, Time.frameCount - _playerStepsFrame);
        var sb = new StringBuilder("  players' own updates, ms/frame over all players (indented = inside the line above):");
        foreach (var step in PlayerStepRows)
        {
            if (step == null) continue;
            sb.Append($"\n    {step.Ticks * TickMs / frames,7:0.000} ms  {step.Calls / (float)frames,5:0.#} calls  {step.Name}");
            step.Ticks = 0;
            step.Calls = 0;
        }

        _playerStepsLine = sb.ToString();
        _playerStepsStart = Time.unscaledTime;
        _playerStepsFrame = Time.frameCount;
    }

    // Summed over a second; one line if they took long in it, then start over.
    private static float _spawnWindowStart;

    private static void LogSpawnSteps()
    {
        if (!_profiling || Steps.Count == 0 || Time.unscaledTime - _spawnWindowStart < 1f) return;
        _spawnWindowStart = Time.unscaledTime;
        long worst = 0;
        foreach (var step in Steps.Values) worst = Math.Max(worst, step.Ticks);
        if (worst * TickMs > 5.0 && InRaid)
        {
            var parts = Steps.Values.Where(s => s.Calls > 0).OrderByDescending(s => s.Ticks)
                .Select(s => $"{s.Name} {s.Ticks * TickMs:0.0} ms ({s.Calls}x)");
            Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} spawn and death steps: {string.Join(", ", parts)}");
        }

        foreach (var step in Steps.Values)
        {
            step.Ticks = 0;
            step.Calls = 0;
        }
    }

    // Times the scripts running right now that aren't timed yet. Rerun every few seconds,
    // because bots and most raid scripts appear after the raid starts.
    // Every profiler hook on game code goes through here. IL2CPP: the compiler gives identical
    // methods one body (an empty Update shares its body with 3000+ methods), and a hook on it runs
    // for all of them, reading their arguments as this method's own: the game crashed outright.
    // Methods without a body of their own are left untimed.
    private static void Hook(MethodBase method, HarmonyMethod? prefix, HarmonyMethod? postfix = null, HarmonyMethod? finalizer = null)
    {
        // A Harmony patch on an IL2CPP constructor runs it on a newly made object, not the game's.
        if (method.IsConstructor) throw new InvalidOperationException("it's a constructor");
        var sharers = BodySharers(method);
        if (sharers > 1) throw new InvalidOperationException($"its machine code is shared with {sharers - 1} other methods");
        CallsOf(method);
        _harmony!.Patch(method, prefix, postfix, finalizer: finalizer);
    }

    // Methods per native body, over every loaded IL2CPP class; counted once (about a second).
    private static Dictionary<IntPtr, int>? _bodies;

    private static unsafe int BodySharers(MethodBase method)
    {
        var info = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method)?.GetValue(null);
        if (info is not IntPtr pointer || pointer == IntPtr.Zero) return 0;
        if (_bodies == null)
        {
            _bodies = new Dictionary<IntPtr, int>();
            uint count = 0;
            var assemblies = (IntPtr*)Il2CppInterop.Runtime.IL2CPP.il2cpp_domain_get_assemblies(Il2CppInterop.Runtime.IL2CPP.il2cpp_domain_get(), ref count);
            for (var n = 0; n < count; n++)
            {
                var image = Il2CppInterop.Runtime.IL2CPP.il2cpp_assembly_get_image(assemblies[n]);
                var classes = Il2CppInterop.Runtime.IL2CPP.il2cpp_image_get_class_count(image);
                for (uint i = 0; i < classes; i++)
                {
                    var klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_image_get_class(image, i);
                    if (klass == IntPtr.Zero) continue;
                    var iterator = IntPtr.Zero;
                    IntPtr each;
                    while ((each = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_methods(klass, ref iterator)) != IntPtr.Zero)
                    {
                        var body = BodyOf(each);
                        if (body != IntPtr.Zero) _bodies[body] = _bodies.GetValueOrDefault(body) + 1;
                    }
                }
            }
        }

        return _bodies.GetValueOrDefault(BodyOf(pointer));
    }

    private static unsafe IntPtr BodyOf(IntPtr methodInfo) =>
        Il2CppInterop.Runtime.Runtime.UnityVersionHandler.Wrap((Il2CppInterop.Runtime.Runtime.Il2CppMethodInfo*)methodInfo).MethodPointer;

    private static void PatchRunningScripts()
    {
        if (!_profiling) return;
        _scannedThisFrame = true;

        var prefix = new HarmonyMethod(typeof(Diagnostics), nameof(Prefix));
        var postfix = new HarmonyMethod(typeof(Diagnostics), nameof(Postfix));
        var watch = Stopwatch.StartNew();
        var patched = 0;
        var failed = 0;

        foreach (var method in ScriptMessages(ScanTypes()))
        {
            if (Buckets.ContainsKey(method)) continue;
            try
            {
                Buckets[method] = new Bucket { Name = $"{method.DeclaringType?.FullName}.{method.Name}" };
                Hook(method, prefix, postfix);
                patched++;
            }
            catch (Exception)
            {
                Buckets.Remove(method);
                failed++;
            }
        }

        if (patched + failed > 0)
        {
            Plugin.Log.LogInfo($"profiler: now timing {Buckets.Count} script methods (+{patched}, {failed} skipped) in {watch.ElapsedMilliseconds} ms");
        }
#if DEBUG
        // Death handlers are added as players spawn (bots' brains, mods watching kills).
        if (InRaid)
        {
            foreach (var player in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (player != null) TimeHandlers(player);
            }
        }
#endif
    }

    internal static void StopProfiling()
    {
        if (!_profiling) return;
        // The hooks stay: every unpatch and patch again leaves another copy of each patched
        // method behind, hundreds per start. Switched off they return at once; the timing
        // slots are kept, emptied, for the next start.
        _profiling = false;
        foreach (var bucket in Buckets.Values.Concat(Steps.Values).Concat(PlayerSteps.Values))
        {
            bucket.Ticks = 0;
            bucket.Calls = 0;
            bucket.HiddenTicks = 0;
            bucket.HiddenCalls = 0;
            bucket.NoRendererCalls = 0;
        }

        _playerStepsLine = "";
        LooksById.Clear();
        UpdateEngineTiming();
        SpawnedToScan.Clear();
        _top.Clear();
    }

    // A full scan (FindObjectsOfType) freezes a frame for ~100 ms, so it runs only when
    // profiling starts and at raid start. After that only spawned players are scanned:
    // they bring almost every new script type (bots' AI and mod components).
    private static bool _fullScanDue;
    private static readonly List<GameObject> SpawnedToScan = new();

    private static IEnumerable<Type> ScanTypes()
    {
        IEnumerable<MonoBehaviour> scripts;
        if (_fullScanDue)
        {
            scripts = Object.FindObjectsOfType<MonoBehaviour>();
        }
        else
        {
            scripts = SpawnedToScan.Where(o => o != null).SelectMany(o => o.GetComponentsInChildren<MonoBehaviour>(true)).ToList();
        }

        _fullScanDue = false;
        SpawnedToScan.Clear();
        return scripts.Where(b => b != null).Select(b => b.GetType()).Distinct().ToList();
    }

    private static void OnPlayerRegistered(IPlayer iPlayer)
    {
        if (_profiling && iPlayer is Component c) SpawnedToScan.Add(c.gameObject);
    }

    private static IEnumerable<MethodBase> ScriptMessages(IEnumerable<Type> types)
    {
        var seen = new HashSet<MethodBase>();
        var ours = typeof(Diagnostics).Assembly;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (var type in types)
        {
            if (type.Assembly == ours || !IsGameOrSpt(type.Assembly)) continue;

            // Unity calls the most derived message method, which may be declared on a base.
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                foreach (var (name, args) in Messages)
                {
                    MethodInfo? method;
                    try
                    {
                        method = t.GetMethod(name, flags, null, args, null);
                        if (method == null || method.IsAbstract || method.IsGenericMethod || method.DeclaringType?.IsGenericType != false || method.GetMethodBody() == null) continue;
                    }
                    catch (Exception)
                    {
                        // A mod type that can't be reflected on: skip it, keep scanning.
                        continue;
                    }

                    if (seen.Add(method)) yield return method;
                }
            }
        }
    }

    // Reading the heap size is slow (it cost ~7 ms a frame when done around every call), so
    // it is only read once per 2 s window, for the total growth.
    private static long UsedHeap() => UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();

    // Only the game's, Unity's, SPT's and Fika's own code is ever patched, never another mod's. Other
    // mods' time still shows in the engine stages (ScriptRunBehaviourUpdate and so on).
    private static bool IsGameCode(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? "";
        return name == "Assembly-CSharp" || name == "Assembly-CSharp-firstpass" || name.StartsWith("SPTushonka.") || name == "Fika.Core";
    }

    private static bool IsGameOrSpt(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? "";
        return name == "Assembly-CSharp" || name == "Assembly-CSharp-firstpass" || name.StartsWith("UnityEngine") || name.StartsWith("Unity.") || name.StartsWith("SPTushonka.") || name == "Fika.Core";
    }

    // The timestamp prefix every method timed here shares.
    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static byte LooksOf(Component component)
    {
        if (component == null) return NothingToDraw;
        var id = component.GetInstanceID();
        if (!LooksById.TryGetValue(id, out var looks))
        {
            // Players are handled by their own systems (animator throttle, culling).
            if (component.GetComponentInParent<Player>() != null) looks.State = PlayerPart;
            else
            {
                // A script on a big root (the game world, a whole building) isn't about any
                // one thing it holds, and checking thousands of renderers per call made the
                // profiler itself the slowest thing in the frame: those count as logic.
                var renderers = component.GetComponentsInChildren<Renderer>(true);
                looks.Renderers = renderers.Length is > 0 and <= 32 ? renderers : null;
            }

            looks.Frame = -1;
            if (LooksById.Count >= 50000) LooksById.Clear();
        }

        if (looks.State != PlayerPart && looks.Frame != Time.frameCount)
        {
            looks.Frame = Time.frameCount;
            looks.State = NothingToDraw;
            if (looks.Renderers != null)
            {
                looks.State = NotDrawn;
                foreach (var renderer in looks.Renderers)
                {
                    if (renderer != null && renderer.isVisible)
                    {
                        looks.State = Drawn;
                        break;
                    }
                }
            }

            LooksById[id] = looks;
        }

        return looks.State;
    }

    private static void Postfix(MethodBase __originalMethod, long __state, object __instance)
    {
        if (!_profiling) return;
        if (!Buckets.TryGetValue(__originalMethod, out var bucket)) return;
        var ticks = Stopwatch.GetTimestamp() - __state;
        bucket.Ticks += ticks;
        bucket.Calls++;
        switch (__instance is Component component ? LooksOf(component) : NothingToDraw)
        {
            case NotDrawn:
                bucket.HiddenTicks += ticks;
                bucket.HiddenCalls++;
                break;
            case NothingToDraw:
                bucket.NoRendererCalls++;
                break;
        }

        _frameScriptTicks += ticks;
        if (ticks > _frameWorstTicks)
        {
            _frameWorstTicks = ticks;
            _frameWorst = bucket.Name;
        }
    }

    // Once per frame, from the plugin's Update.
    // What keeps the game's whole-scene physics step running (it runs while any body it was told
    // about is active and, if it has a visibility check, seen): logged when that set changes, by
    // the bodies' root objects, so a Phys bar that jumps (a corpse, the BTR) names its cause.
    private static float _physicsCheckAt;
    private static string _physicsKeepers = "";

    private static void LogPhysicsKeepers()
    {
        var keepers = PhysicsKeepers;
        if (keepers == _physicsKeepers) return;
        _physicsKeepers = keepers;
        Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} physics step {(keepers.Length > 0 ? $"kept running by: {keepers}" : "idle")}");
    }

    internal static string PhysicsKeepers
    {
        get
        {
            var roots = new SortedDictionary<string, int>();
            foreach (var body in PhysicsExtensions.UpdateController._rigidbodies)
            {
                if (body.rigidbody == null || !body.rigidbody.gameObject.activeInHierarchy) continue;
                if (body.visibilityChecker != null && !body.visibilityChecker.IsVisible) continue;
                var name = $"{body.rigidbody.transform.root.name}/{body.rigidbody.name}{(body.rigidbody.isKinematic ? " (kinematic)" : "")}";
                roots[name] = roots.TryGetValue(name, out var n) ? n + 1 : 1;
            }

            return string.Join(", ", roots.Select(r => r.Value > 1 ? $"{r.Key} x{r.Value}" : r.Key));
        }
    }

#if DEBUG
    // Debug, Home: what PhysX is busy with while the scene step runs. Two snapshots of every
    // enabled collider 1 s apart: colliders moved without a rigidbody (static actors, whose tree
    // PhysX rebuilds), kinematic and dynamic ones moved, trigger shapes on moved things, and the
    // awake bodies. Grouped by layer and object (digits as #), a one-off hitch.
    private static Collider[]? _censusColliders;
    private static Vector3[] _censusAt = Array.Empty<Vector3>();
    private static float _censusDue = -1f;

    internal static void StartPhysicsCensus()
    {
        _censusColliders = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None);
        _censusAt = new Vector3[_censusColliders.Length];
        for (var i = 0; i < _censusColliders.Length; i++) _censusAt[i] = _censusColliders[i].transform.position;
        _censusDue = Time.unscaledTime + 1f;
        Plugin.Log.LogInfo($"physics census: {_censusColliders.Length} colliders, second look in 1 s (physics step {(_physicsKeepers.Length > 0 ? "running" : "idle")})");
    }

    private static string CensusName(Component c) =>
        System.Text.RegularExpressions.Regex.Replace($"{LayerMask.LayerToName(c.gameObject.layer)}: {c.transform.root.name}/{c.name}", "[0-9]+", "#");

    private static void CensusAdd(Dictionary<string, int> into, string key) => into[key] = into.TryGetValue(key, out var n) ? n + 1 : 1;

    private static string CensusTop(Dictionary<string, int> groups) =>
        string.Join("; ", groups.OrderByDescending(g => g.Value).Take(14).Select(g => $"{g.Value}x {g.Key}"));

    private static long Tris(Mesh? mesh)
    {
        long n = 0;
        if (mesh != null) for (var i = 0; i < mesh.subMeshCount; i++) n += mesh.GetIndexCount(i) / 3;
        return n;
    }

    private static void FinishPhysicsCensus()
    {
        var colliders = _censusColliders!;
        _censusColliders = null;
        _censusDue = -1f;
        var movedStatic = new Dictionary<string, int>();
        var movedKinematic = new Dictionary<string, int>();
        var movedDynamic = new Dictionary<string, int>();
        var triggers = new Dictionary<string, int>();
        var touched = new Dictionary<string, int>();
        var near = new Collider[256];
        int statics = 0, kinematics = 0, dynamics = 0, enabled = 0, pairs = 0, triggerPairs = 0;
        for (var i = 0; i < colliders.Length; i++)
        {
            var c = colliders[i];
            if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
            enabled++;
            if ((c.transform.position - _censusAt[i]).sqrMagnitude < 1e-8f) continue;
            var key = CensusName(c);
            var body = c.attachedRigidbody;
            if (body == null)
            {
                statics++;
                CensusAdd(movedStatic, key);
            }
            else if (body.isKinematic)
            {
                kinematics++;
                CensusAdd(movedKinematic, key);
            }
            else
            {
                dynamics++;
                CensusAdd(movedDynamic, key);
            }

            // What PhysX pairs it with: everything its bounds overlap on a layer it collides with
            // (broadphase pairs), and the triggers among them (tested every step it moves).
            var b = c.bounds;
            var hits = Physics.OverlapBoxNonAlloc(b.center, b.extents, near, Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
            for (var h = 0; h < hits; h++)
            {
                var o = near[h];
                if (o == c || o.attachedRigidbody == body || Physics.GetIgnoreLayerCollision(c.gameObject.layer, o.gameObject.layer)) continue;
                pairs++;
                if (!o.isTrigger && !c.isTrigger) continue;
                triggerPairs++;
                CensusAdd(touched, $"{CensusName(o)} ({o.GetType().Name}{(o is MeshCollider m ? m.convex ? " convex" : $" {Tris(m.sharedMesh)} tris" : "")}{(o.attachedRigidbody == null ? "" : ", on a body")})");
            }

            if (!c.isTrigger) continue;
            var scale = c.transform.lossyScale;
            var shape = c is SphereCollider sphere
                ? $"sphere r={sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z))):0.0}"
                : c.GetType().Name;
            CensusAdd(triggers, $"{key} ({shape})");
        }

        var awake = new Dictionary<string, int>();
        foreach (var body in Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
        {
            if (body == null || body.IsSleeping()) continue;
            CensusAdd(awake, CensusName(body) + (body.isKinematic ? " kinematic" : "") +
                             (body.collisionDetectionMode != CollisionDetectionMode.Discrete ? $" {body.collisionDetectionMode}" : ""));
        }

        Plugin.Log.LogInfo($"physics census: {enabled} enabled colliders; moved in 1 s: {statics} without a rigidbody, {kinematics} kinematic, {dynamics} dynamic; physics step {(_physicsKeepers.Length > 0 ? "running: " + _physicsKeepers : "idle")}");
        Plugin.Log.LogInfo($"physics census, moved without a rigidbody: {CensusTop(movedStatic)}");
        Plugin.Log.LogInfo($"physics census, kinematic moved: {CensusTop(movedKinematic)}");
        Plugin.Log.LogInfo($"physics census, dynamic moved: {CensusTop(movedDynamic)}");
        Plugin.Log.LogInfo($"physics census, triggers on moved things: {CensusTop(triggers)}");
        Plugin.Log.LogInfo($"physics census, overlaps of moved things on colliding layers: {pairs}, with a trigger: {triggerPairs}: {CensusTop(touched)}");
        Plugin.Log.LogInfo($"physics census, awake bodies: {CensusTop(awake)}");
    }
#endif


    internal static void Tick()
    {
        // Measured between our own calls, the same span the script timings cover (Unity's
        // deltaTime is the previous frame's, one frame off from them).
#if DEBUG
        if (_censusDue > 0f && Time.unscaledTime >= _censusDue) FinishPhysicsCensus();
#endif
        var stamp = Measuring ? Stopwatch.GetTimestamp() : 0;
        var ms = _lastTickStamp == 0 || stamp == 0 ? Time.unscaledDeltaTime * 1000f : (float)((stamp - _lastTickStamp) * 1000.0 / Stopwatch.Frequency);
        _lastTickStamp = stamp;
        if (Measuring && FrameTimes.Count < 4096) FrameTimes.Add(ms);
        if (Logging && InRaid && Time.unscaledTime - _physicsCheckAt >= 1f)
        {
            _physicsCheckAt = Time.unscaledTime;
            LogPhysicsKeepers();
        }

        if (Time.unscaledTime - _pacingStart >= 5f)
        {
            _pacingStart = Time.unscaledTime;
            ClosePacing();
        }
        _fastMs += (ms - _fastMs) * 0.05f;
        _slowMs += (ms - _slowMs) * 0.003f;

        var inRaid = InRaid;
        RestoreDroppedMarkers();
        LogSpawnSteps();
        RollPlayerSteps();
        LogBotRequests();
        CheckRaidLeftovers(inRaid);
        if (_censusDueAt > 0f && Time.unscaledTime >= _censusDueAt)
        {
            _censusDueAt = -1f;
            if (inRaid && Logging) LogSceneCensus();
        }

        Overlay.Sample(ms);
        LogHitch(ms, inRaid);

        // Once, a few seconds after the per-script profiler is switched on in raid, so the
        // census doesn't need waiting for the 2-minute snapshot.
        if (_profiling && inRaid && _rendererCensusAt > 0f && Time.unscaledTime >= _rendererCensusAt && Time.unscaledTime - _raidStartTime > 25f)
        {
            _rendererCensusAt = -1f;
            LogRendererCensus();
        }

        // A snapshot every 2 minutes in raid, so no hotkey is needed.
        if (inRaid && Logging && Time.unscaledTime - _lastAutoSnapshot > 120f)
        {
            if (_lastAutoSnapshot > 0f)
            {
                Plugin.Log.LogInfo("snapshot (every 2 min in raid):\n" + Snapshot(25));
                if (_profiling) LogRendererCensus();
            }
            _lastAutoSnapshot = Time.unscaledTime;
        }

        if (_profiling)
        {
            _windowFrames++;
            if (Time.unscaledTime - _windowStart >= 2f) CloseWindow();

            if (_fullScanDue || SpawnedToScan.Count > 0) PatchRunningScripts();
        }

        // Loading screens always tank the frame rate; only raids are interesting.
        if (!Plugin.On(Plugin.LogDrops) || !inRaid)
        {
            _droppedSince = -1f;
            return;
        }

        // Frame time 50% up on the long-run average for 3 seconds: the "140 to 70 and
        // stays there" case. One snapshot, then quiet for a minute.
        if (_fastMs > _slowMs * 1.5f)
        {
            if (_droppedSince < 0f) _droppedSince = Time.unscaledTime;
            if (Time.unscaledTime - _droppedSince > 3f && Time.unscaledTime - _lastSnapshot > 60f)
            {
                _lastSnapshot = Time.unscaledTime;
                Plugin.Log.LogWarning("frame rate dropped:\n" + Snapshot(30));

                // Once the drop is the new normal, judge the next one against it.
                _slowMs = _fastMs;
            }
        }
        else
        {
            _droppedSince = -1f;
        }
    }

    // A frame over 50 ms gets one line saying what else happened around it. In the menu too
    // (at most one a second, loading screens are full of them), marked as such.
    private static void LogHitch(float ms, bool inRaid)
    {
        var gcCount = Il2CppSystem.GC.CollectionCount(0);
        var players = inRaid ? Singleton<GameWorld>.Instance.AllAlivePlayersList.Count : 0;

        // One line a second at most, counting the ones in between, so a stretch of bad frames
        // can't flood the log.
        var hitch = Plugin.On(Plugin.LogDrops) && ms > 50f;
        if (hitch && Time.unscaledTime - _lastHitchLog < 1f) _skippedHitches++;
        else if (hitch)
        {
            _lastHitchLog = Time.unscaledTime;
            var causes = new List<string>();
            if (gcCount != _lastGcCount) causes.Add($"GC ran ({gcCount - _lastGcCount}x)");
            if (players > _lastPlayerCount) causes.Add($"{players - _lastPlayerCount} player(s) spawned");
            if (_scannedThisFrame) causes.Add("profiler work (rescan, drill-down or census)");
            if (gcCount != _lastGcCount) causes[0] += $", GC mode {GarbageCollector.GCMode}";
            var worstStage = -1;
            long stagesTotal = 0;
            for (var i = 0; i < _stageFrameTicks.Length; i++)
            {
                stagesTotal += _stageFrameTicks[i];
                if (worstStage < 0 || _stageFrameTicks[i] > _stageFrameTicks[worstStage]) worstStage = i;
            }

            // Frame time no stage of Unity's frame accounts for: spent outside the player
            // loop (the OS or Wine message pump, a stalled present, the process paused).
            var outside = worstStage >= 0 ? ms - stagesTotal * TickMs : 0;

            // Stage times come with the overlay too, not just the deep profiler: cheap, and
            // they say which part of the frame (scripts, animation, rendering) the time went to.
            var scripts = (_profiling ? $", scripts {_frameScriptTicks * TickMs:0.0} ms (worst {_frameWorst} {_frameWorstTicks * TickMs:0.0} ms)" : "") +
                          (EngineTiming && worstStage >= 0 ? $", slowest engine stage {_stageNames[worstStage]} {_stageFrameTicks[worstStage] * TickMs:0.0} ms, outside Unity's frame {outside:0} ms" : "");
            var unknown = _profiling ? " | no known cause" : " | no known cause (turn on \"Log time per script (slow)\" to see which script)";
            Plugin.Log.LogWarning($"{DateTime.Now:HH:mm:ss.fff} {(inRaid ? "" : "menu ")}hitch {ms:0} ms{scripts}" + (causes.Count > 0 ? " | " + string.Join(", ", causes) : unknown)
                                  + (_skippedHitches > 0 ? $" (+{_skippedHitches} more since the last line)" : ""));
            _skippedHitches = 0;
        }

        _lastGcCount = gcCount;
        // Someone just died: the last closed window (up to 2 s) is what the frame cost while
        // they were alive, e.g. a bot fighting you. Compared with the next 2-minute
        // snapshot, it shows what one live bot cost.
        if (inRaid && _profiling && players < _lastPlayerCount && Time.unscaledTime - _lastDeathSnapshot > 20f)
        {
            _lastDeathSnapshot = Time.unscaledTime;
            Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} someone died; the 2 s before:\n" + Snapshot(15));
        }

        _lastPlayerCount = players;
        _frameScriptTicks = 0;
        _frameWorstTicks = 0;
        _frameWorst = "";
        Array.Clear(_stageFrameTicks, 0, _stageFrameTicks.Length);
        _scannedThisFrame = false;
    }

    private static void ClosePacing()
    {
        if (FrameTimes.Count < 10)
        {
            FrameTimes.Clear();
            return;
        }

        var jumps = 0.0;
        for (var i = 1; i < FrameTimes.Count; i++) jumps += Math.Abs(FrameTimes[i] - FrameTimes[i - 1]);
        var avgJump = jumps / (FrameTimes.Count - 1);

        FrameTimes.Sort();
        float At(double q) => FrameTimes[Math.Min(FrameTimes.Count - 1, (int)(q * FrameTimes.Count))];
        _pacing = $"frame times: median {At(0.5):0.0} ms, 95% {At(0.95):0.0} ms, 99% {At(0.99):0.0} ms, worst {FrameTimes[FrameTimes.Count - 1]:0.0} ms, " +
                  $"average frame-to-frame jump {avgJump:0.0} ms";
        FrameTimes.Clear();
    }

    // For the overlay's Scripts row: how many scripts ran and the costliest, last 2 s window.
    internal static string ScriptsNote()
    {
        if (!_profiling || _top.Count == 0) return "game + mod code; per-script log names them";
        var top = _top[0];
        return $"{_top.Count} scripts; top {TypeName(top.Name)} {top.Ms:0.00} ms";
    }

    // "EFT.GameWorldUnityTickListener.Update" -> "GameWorldUnityTickListener".
    private static string TypeName(string name)
    {
        var shortName = Short(name);
        var dot = shortName.LastIndexOf('.');
        return dot > 0 ? shortName.Substring(0, dot) : shortName;
    }

    // "EFT.GameWorldUnityTickListener.Update" -> "GameWorldUnityTickListener.Update".
    private static string Short(string name)
    {
        var method = name.LastIndexOf('.');
        var type = method > 0 ? name.LastIndexOf('.', method - 1) : -1;
        return type >= 0 ? name.Substring(type + 1) : name;
    }

    private static void CloseWindow()
    {
        var frames = Math.Max(1, _windowFrames);
        var seconds = Math.Max(0.001, Time.unscaledTime - _windowStart);
        const double mb = 1024 * 1024;
        _top = Buckets.Values
            .Where(b => b.Calls > 0)
            .Select(b => (b.Name, Ms: b.Ticks * TickMs / frames, Calls: (double)b.Calls / frames,
                HiddenMs: b.HiddenTicks * TickMs / frames, LogicShare: (double)b.NoRendererCalls / b.Calls))
            .OrderByDescending(r => r.Ms)
            .ToList();

        DrillDown(frames);
        CloseUiWindow(seconds);
        _topParts = Parts.Values.Where(p => p.Calls > 0)
            .Select(p => (p.Name, Ms: p.Ticks * TickMs / frames, Calls: (double)p.Calls / frames))
            .OrderByDescending(p => p.Ms).ToList();
        foreach (var part in Parts.Values)
        {
            part.Ticks = 0;
            part.Calls = 0;
        }

        foreach (var bucket in Buckets.Values)
        {
            bucket.Ticks = 0;
            bucket.Calls = 0;
            bucket.HiddenTicks = 0;
            bucket.HiddenCalls = 0;
            bucket.NoRendererCalls = 0;
        }

        var stages = new List<(string Name, double Ms)>(_stageNames.Length);
        for (var i = 0; i < _stageNames.Length; i++)
        {
            if (_stageTicks[i] > 0) stages.Add((_stageNames[i], _stageTicks[i] * TickMs / frames));
            _stageTicks[i] = 0;
        }

        _topStages = stages.OrderByDescending(s => s.Ms).ToList();

        // Heap growth over the window; meaningless if a collection ran in between.
        var heapNow = UsedHeap();
        var gcNow = Il2CppSystem.GC.CollectionCount(0);
        _heapGrowthMBps = gcNow == _windowGcCount && _windowHeap > 0 ? (heapNow - _windowHeap) / mb / seconds : -1;
        _windowHeap = heapNow;
        _windowGcCount = gcNow;

        _windowFrames = 0;
        _windowStart = Time.unscaledTime;
    }

    internal static string Snapshot(int rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  fps {1000f / _fastMs:0} now, {1000f / _slowMs:0} before | frame {_fastMs:0.0} ms" +
                      $" | main thread {Ms(_mainThread)} | render thread {Ms(_renderThread)}");
        sb.AppendLine($"  GC alloc/frame {Kb(_gcAlloc)} | GC runs since start {Il2CppSystem.GC.CollectionCount(0) - _gcCountAtStart}" +
                      $" | game heap {UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024 * 1024)} MB" +
                      $" | draw calls {Count(_drawCalls)} | shadow casters {Count(_shadowCasters)}");
        sb.AppendLine($"  batches {Count(_batches)} | SetPass calls {Count(_setPassCalls)} | visible skinned meshes {Count(_skinnedMeshes)}");
        AppendRenderBreakdown(sb);
        if (_pacing.Length > 0) sb.AppendLine("  " + _pacing);
        sb.AppendLine($"  targetFrameRate {Application.targetFrameRate} | vSyncCount {QualitySettings.vSyncCount}" +
                      $" | timeScale {Time.timeScale} | fixedDeltaTime {Time.fixedDeltaTime * 1000f:0.0} ms" +
                      $" | throttled bots {AnimatorThrottle.Throttled} | hidden close bots {Occlusion.HiddenCount}" +
                      $" | simplified bots {DistantDetail.SimplifiedBots} ({DistantDetail.HiddenRenderers} pieces hidden)");

        if (InRaid && Plugin.On(Plugin.HideCovered))
            sb.AppendLine($"  hidden objects: {DepthCulling.Hidden} hidden ({DepthCulling.Saving} were being drawn) of {DepthCulling.Tracked} (scan {DepthCulling.ScanMs:0} ms) | {DepthCulling.Ms:0.00} ms/frame on the main thread (worst {DepthCulling.PeakMs:0.00}: {DepthCulling.PeakNote}), check {DepthCulling.WorkerMs:0.00} ms on a worker thread, {DepthCulling.Rounds} checks, {DepthCulling.Status}");
        if (InRaid)
        {
            // Against the animation row: it counts the bots whose animation ran last frame.
            int awake = 0, asleep = 0, humans = 0;
            var kinds = new Dictionary<string, int>();
            var missed = new List<string>();
            foreach (var player in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (player == null || player.IsYourPlayer) continue;
                if (!AnimatorThrottle.IsBot(player))
                {
                    humans++;
                    var kind = $"{player.GetType().Name} {player.Profile?.Info?.Settings?.Role}";
                    kinds[kind] = kinds.TryGetValue(kind, out var n) ? n + 1 : 1;
                }
                else if (player.isActiveAndEnabled)
                {
                    awake++;
                    if (!AnimatorThrottle.HandledRecently(player) && missed.Count < 8)
                    {
                        var camera = EFT.CameraControl.CameraManager.Instance?.Camera;
                        var distance = camera != null ? Vector3.Distance(camera.transform.position, player.Position) : -1f;
                        missed.Add($"{player.GetType().Name} {player.Profile?.Info?.Settings?.Role} {distance:0} m{(player.IsVisible ? " seen" : "")}");
                    }
                }
                else asleep++;
            }

            sb.AppendLine($"  players alive {Singleton<GameWorld>.Instance.AllAlivePlayersList.Count}: {awake} bots awake, {asleep} asleep, {humans} other humans" +
                          (kinds.Count > 0 ? $" ({string.Join(", ", kinds.Select(k => $"{k.Value} {k.Key}"))})" : "") +
                          $"; animation row counted {AnimatorThrottle.Throttled + AnimatorThrottle.FullInView + AnimatorThrottle.FullFighting + AnimatorThrottle.FullOther}" +
                          (missed.Count > 0 ? $", awake but not stepped last frame: {string.Join(", ", missed)}" : "") + $" | memory cleanup {(GarbageCollector.GCMode == GarbageCollector.Mode.Disabled ? "off" : "on")}");
        }
        var parts = Overlay.PartsSummary();
        if (parts.Length > 0) sb.AppendLine(parts);

        if (!_profiling)
        {
            sb.Append("  (turn on \"Log time per script (slow)\" for a per-script breakdown)");
            return sb.ToString();
        }

        if (_playerStepsLine.Length > 0) sb.AppendLine(_playerStepsLine);
        sb.AppendLine($"  smoothie's own cost: animator steps {AnimatorThrottle.StepMs:0.00} ms ({AnimatorThrottle.Steps} steps), " +
                      $"hidden-bot checks {Occlusion.Ms:0.00} ms ({Occlusion.Rays} rays) last frame");
        if (_topStages.Count > 0)
        {
            sb.AppendLine($"  engine stages total {_topStages.Sum(s => s.Ms):0.00} ms/frame (scripts run inside the ScriptRunBehaviour* stages), top 15:");
            foreach (var stage in _topStages.Take(15))
            {
                sb.AppendLine($"    {stage.Ms,7:0.000} ms  {stage.Name}");
            }
        }

        var scripts = _top.Sum(r => r.Ms);
        sb.AppendLine($"  scripts total {scripts:0.00} ms/frame (camera callbacks and image effects included; {_top.Sum(r => r.HiddenMs):0.00} ms on objects that draw something but weren't drawn), top {Math.Min(rows, _top.Count)}:");
        foreach (var row in _top.Take(rows))
        {
            var tag = row.LogicShare >= 0.99 ? "draws nothing" : $"{row.HiddenMs:0.000} ms on objects not drawn";
            sb.AppendLine($"    {row.Ms,7:0.000} ms  {row.Calls,6:0.#} calls  {row.Name}  ({tag})");
        }

        if (_uiLine.Length > 0) sb.AppendLine(_uiLine);

        if (_topParts.Count > 0)
        {
            sb.AppendLine($"  inside the heaviest scripts ({Drilled.Count} opened so far, one more every 2 s), top {Math.Min(rows, _topParts.Count)}:");
            foreach (var part in _topParts.Take(rows)) sb.AppendLine($"    {part.Ms,7:0.000} ms  {part.Calls,6:0.#} calls  {part.Name}");
        }

        sb.AppendLine(_heapGrowthMBps >= 0 ? $"  game heap growing {_heapGrowthMBps:0.0} MB/s" : "  game heap growth: a collection ran, no total this time");

        return sb.ToString().TrimEnd();
    }

    // A census that throws (a scene half loaded, a mod's odd renderer) is logged and skipped:
    // it must never take the rest of the diagnostics down with it.
    private static void LogRendererCensus()
    {
        try
        {
            // Over 180,000 renderers and a line each for the candidates: a hitch of its own.
            _scannedThisFrame = true;
            Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} renderer census:\n" + RendererCensus());
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"renderer census failed: {ex}");
        }
    }

    // Who owns what is drawn this frame (by any camera, shadows included): players, loose
    // loot, or the level, split by whether the game's baked visibility (PerfectCulling) already
    // culls it, and by size and distance. Shows where more culling would pay. Walks every
    // renderer, a one-off hitch, so only with the per-script profiler on.
    private static string RendererCensus()
    {
        AnimatorThrottle.NextFrame();
        var camera = AnimatorThrottle.CameraPosition;
        var baked = new HashSet<Renderer>(Diagnostics.ReferenceComparer.Instance);
        // The baked groups live on each scene's PerfectCullingCrossSceneGroup (a plain field);
        // the volumes' bakeGroups property throws at runtime, with no bake descriptor active.
        foreach (var crossScene in Object.FindObjectsByType<Koenigz.PerfectCulling.EFT.PerfectCullingCrossSceneGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var groups = crossScene.bakeGroups;
            if (groups == null) continue;
            foreach (var group in groups)
            {
                if (group?.renderers == null) continue;
                foreach (var renderer in group.renderers)
                {
                    if (renderer != null) baked.Add(renderer);
                }
            }
        }

        var rows = new (string Name, int Drawn, int Small, int Medium, int Far, int Shadows)[4];
        int otherMediumNear = 0, otherMediumInView = 0, otherMediumBlocked = 0;
        int bakedNear = 0, bakedBlocked = 0, shadowOnlyDrawn = 0;
        // Why prop hiding leaves the blocked ones drawn, by reason.
        var explained = new Dictionary<string, int>();
        var explainedOther = new Dictionary<string, int>();
        var explain = Plugin.On(Plugin.HideCovered);
        // What the drawn level renderers are, by layer and by the script on them (decal,
        // tree impostor, plain prop...): what the draw calls are made of.
        var byLayer = new Dictionary<string, int>();
        var byKind = new Dictionary<string, int>();
        // Level renderers beyond 60 m in view (most of the draw calls in practice): how many
        // have the line to their centre blocked, by size. Big ones can still show over a wall.
        var farInView = new int[3];
        var farBlocked = new int[3];
        // Why far-object hiding leaves the blocked ones drawn, by reason.
        var farWhy = new Dictionary<string, int>();
        rows[0].Name = "players";
        rows[1].Name = "loose loot";
        rows[2].Name = "level, baked";
        rows[3].Name = "level, other";
        var all = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        foreach (var renderer in all)
        {
            if (!renderer.isVisible) continue;
            var i = renderer.GetComponentInParent<Player>() != null ? 0
                : renderer.GetComponentInParent<EFT.Interactive.LootItem>() != null ? 1
                : baked.Contains(renderer) ? 2 : 3;
            var bounds = renderer.bounds;
            var size = bounds.size.magnitude;
            rows[i].Drawn++;
            if (i >= 2)
            {
                var layer = LayerMask.LayerToName(renderer.gameObject.layer);
                byLayer[layer] = byLayer.TryGetValue(layer, out var l) ? l + 1 : 1;
                var kind = renderer.GetComponent<MonoBehaviour>() is { } script ? script.GetType().Name : renderer.GetType().Name;
                byKind[kind] = byKind.TryGetValue(kind, out var k) ? k + 1 : 1;
            }
            if (renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
            {
                shadowOnlyDrawn++;
                continue;
            }

            // The game's baked visibility should switch its props off behind walls: how many
            // of its small and medium props in view are drawn with a wall in the way?
            if (i == 2 && size < 2.5f && AnimatorThrottle.HasCamera && AnimatorThrottle.InView(bounds)
                && Vector3.Distance(bounds.center, camera) is > 15f and < 150f)
            {
                bakedNear++;
                var grownBaked = bounds;
                grownBaked.Expand(0.2f);
                if (Physics.Linecast(camera, bounds.center, out var bakedHit, Occlusion.SightMask, QueryTriggerInteraction.Ignore)
                    && !grownBaked.Contains(bakedHit.point))
                {
                    bakedBlocked++;
                    if (explain)
                    {
                        var why = DepthCulling.Explain(renderer);
                        explained[why] = explained.TryGetValue(why, out var n) ? n + 1 : 1;
                    }
                }
            }
            if (size < 0.35f) rows[i].Small++;
            else if (size < 2.5f)
            {
                rows[i].Medium++;

                // Level props nothing culls, within loot-hiding range: is the straight line
                // from the camera to their centre blocked? Roughly how many sight checks
                // could hide.
                var toCenter = Vector3.Distance(bounds.center, camera);
                if (i == 3 && AnimatorThrottle.HasCamera && toCenter is > 15f and < 150f)
                {
                    otherMediumNear++;

                    // Drawn but out of view: only its shadow is drawn. In view: blocked only if
                    // something stops the line before it reaches the prop (a hit on the prop's
                    // own collider, inside its bounds, doesn't count).
                    if (AnimatorThrottle.InView(bounds))
                    {
                        otherMediumInView++;
                        var grown = bounds;
                        grown.Expand(0.2f);
                        if (Physics.Linecast(camera, bounds.center, out var hit, Occlusion.SightMask, QueryTriggerInteraction.Ignore)
                            && !grown.Contains(hit.point))
                        {
                            otherMediumBlocked++;
                            if (explain)
                            {
                                var why = DepthCulling.Explain(renderer);
                                explainedOther[why] = explainedOther.TryGetValue(why, out var n) ? n + 1 : 1;
                            }
                        }
                    }
                }
            }
            if (AnimatorThrottle.HasCamera && Vector3.Distance(bounds.center, camera) > 60f)
            {
                rows[i].Far++;
                if (i >= 2 && AnimatorThrottle.InView(bounds))
                {
                    var bucket = size < 2.5f ? 0 : size < 10f ? 1 : 2;
                    farInView[bucket]++;
                    var grownFar = bounds;
                    grownFar.Expand(0.2f);
                    var farLineBlocked = Physics.Linecast(camera, bounds.center, out var farHit, Occlusion.SightMask, QueryTriggerInteraction.Ignore)
                                         && !grownFar.Contains(farHit.point);
                    if (farLineBlocked) farBlocked[bucket]++;
                    if (farLineBlocked && explain)
                    {
                        var why = DepthCulling.Explain(renderer);
                        farWhy[why] = farWhy.TryGetValue(why, out var n) ? n + 1 : 1;
                    }
                }
            }
            if (renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) rows[i].Shadows++;
        }


        var sb = new StringBuilder($"  renderers drawn this frame (any camera, shadows included), of {all.Length} loaded; {baked.Count} in the game's baked visibility groups:");
        foreach (var row in rows)
        {
            sb.Append($"\n    {row.Name,-13} {row.Drawn,6} drawn: {row.Small,6} small (<35 cm), {row.Medium,6} medium (<2.5 m), {row.Far,6} beyond 60 m, {row.Shadows,6} cast shadows");
        }

        sb.Append($"\n    {shadowOnlyDrawn} of the drawn renderers are shadow-only copies (counted above, not below)");
        sb.Append($"\n    level, baked, small+medium, in view, 15-150 m: {bakedNear}, of which {bakedBlocked} have the line to their centre blocked (drawn although the game's baked visibility should hide them)");
        if (explained.Count > 0)
        {
            sb.Append("\n      hiding on those: " + string.Join(", ", explained.OrderByDescending(e => e.Value).Select(e => $"{e.Value} {e.Key}")));
        }
        if (explainedOther.Count > 0)
        {
            sb.Append("\n      hiding on the blocked level, other, medium ones below: " + string.Join(", ", explainedOther.OrderByDescending(e => e.Value).Select(e => $"{e.Value} {e.Key}")));
        }

        sb.Append($"\n    level beyond 60 m in view, centre line blocked: {farBlocked[0]} of {farInView[0]} under 2.5 m, {farBlocked[1]} of {farInView[1]} 2.5-10 m, {farBlocked[2]} of {farInView[2]} over 10 m");
        if (farWhy.Count > 0)
        {
            sb.Append("\n      hiding on those: " + string.Join(", ", farWhy.OrderByDescending(e => e.Value).Select(e => $"{e.Value} {e.Key}")));
        }
        sb.Append("\n    level renderers drawn, by layer: " + string.Join(", ", byLayer.OrderByDescending(e => e.Value).Take(10).Select(e => $"{e.Value} {e.Key}")));
        sb.Append("\n    level renderers drawn, by what they are: " + string.Join(", ", byKind.OrderByDescending(e => e.Value).Take(12).Select(e => $"{e.Value} {e.Key}")));
        // Terrain and GPU Instancer's trees and grass aren't Renderers, so the census above
        // misses them: what's there and the settings that decide their draw calls.
        var terrains = Terrain.activeTerrains;
        if (terrains.Length > 0)
        {
            sb.Append($"\n    terrains: {terrains.Length}, {terrains.Count(t => t.drawHeightmap)} drawing ground ({terrains.Count(t => t.GetComponent<TerrainLod>() is { TerrainIsVisible: false })} as a mesh instead), "
                      + $"pixel error {terrains.Min(t => t.heightmapPixelError):0.#}-{terrains.Max(t => t.heightmapPixelError):0.#}, max LOD {terrains.Max(t => t.heightmapMaximumLOD)}, "
                      + $"{terrains.Count(t => t.drawInstanced)} instanced, {terrains.Count(t => t.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)} cast shadows, "
                      + $"base map from {terrains.Min(t => t.basemapDistance):0}-{terrains.Max(t => t.basemapDistance):0} m, "
                      + $"Unity trees {terrains.Sum(t => t.terrainData != null ? t.terrainData.treeInstanceCount : 0)} to {terrains.Max(t => t.treeDistance):0} m (billboards from {terrains.Max(t => t.treeBillboardDistance):0} m), "
                      + $"grass to {terrains.Max(t => t.detailObjectDistance):0} m");
        }
        var managers = GPUInstancer.GPUInstancerManager.activeManagerList;
        if (managers != null && managers.Count > 0)
        {
            sb.Append("\n    GPU Instancer: " + string.Join(", ", managers.Where(m => m != null).GroupBy(m => m.GetType().Name).Select(g =>
            {
                var data = g.Where(m => m.runtimeDataList != null).SelectMany(m => m.runtimeDataList).Where(d => d != null && d.instanceCount > 0).ToList();
                return $"{g.Count()} {g.Key} ({data.Count} kinds, {data.Sum(d => d.instanceCount)} instances, {data.Sum(d => d.instanceLODs?.Count ?? 0)} LOD meshes, {data.Count(d => d.hasShadowCasterBuffer)} casting shadows, "
                       + $"to {(data.Count > 0 ? data.Max(d => d.prototype.maxDistance) : 0):0} m, {data.Count(d => d.prototype.isOcclusionCulling)} occlusion culled)";
            })));
        }
        sb.Append($"\n    level, other, medium, 15-150 m: {otherMediumNear} drawn ({otherMediumNear - otherMediumInView} only for their shadow, out of view); of the {otherMediumInView} in view, {otherMediumBlocked} have the line from the camera to their centre blocked by something else (a rough upper bound of what sight checks could hide)");
        return sb.ToString();
    }

    // Cameras by main-thread time since the last snapshot, split into culling and drawing.
    // Resets the camera totals.
    private static void AppendRenderBreakdown(StringBuilder sb)
    {
        var frames = Math.Max(1, CameraTimes.Values.Select(t => t.Frames).DefaultIfEmpty(1).Max());
        var cameras = CameraTimes
            .Where(pair => pair.Key != null)
            .Select(pair => (Name: pair.Key.name, Ms: pair.Value.Ticks * TickMs / frames, CullMs: pair.Value.CullTicks * TickMs / frames, Share: (double)pair.Value.Frames / frames))
            .OrderByDescending(c => c.Ms)
            .ToList();
        if (cameras.Count > 0)
        {
            sb.AppendLine($"  cameras rendering (main-thread ms/frame of which culling, share of frames): " +
                          string.Join(", ", cameras.Take(8).Select(c => $"{c.Name} {c.Ms:0.00} (cull {c.CullMs:0.00}, {c.Share:P0})")));
        }

        CameraTimes.Clear();
    }

    private static string Ms(ProfilerRecorder r) => r.Valid ? $"{r.LastValue / 1e6:0.0} ms" : "n/a";
    private static string Kb(ProfilerRecorder r) => r.Valid ? $"{r.LastValue / 1024} KB" : "n/a";
    private static string Count(ProfilerRecorder r) => r.Valid ? r.LastValue.ToString() : "n/a";
}
