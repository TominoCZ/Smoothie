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
// While "Log time per script (slow)" is on, every Update / LateUpdate / FixedUpdate of the scripts
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
    }

    private const string HarmonyId = "com.tominocz.smoothie.profiler";
    internal static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
    private static readonly string[] Messages = { "Update", "LateUpdate", "FixedUpdate" };

    private static Harmony? _harmony;
    private static bool _profiling;
    private static readonly Dictionary<MethodBase, Bucket> Buckets = new();

    // Last window's results, ms per frame, worst first.
    private static List<(string Name, double Ms, double Calls)> _top = new();
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

    // Frame time, smoothed over about a second and about 20 seconds.
    private static float _fastMs = 7f;
    private static float _slowMs = 7f;
    private static float _droppedSince = -1f;
    private static float _lastSnapshot = -100f;
    private static int _gcCountAtStart;

    internal static bool Profiling => _profiling;

    // Performance lines go to the log only with a diagnostics setting on that asks for them.
    internal static bool Logging => Profiling || Plugin.On(Plugin.LogDrops);

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
                if (step.type == typeof(Diagnostics)) continue;
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

    private static PlayerLoopSystem Marker(int stage) => new()
    {
        type = typeof(Diagnostics),
        updateDelegate = () =>
        {
            var now = Stopwatch.GetTimestamp();
            if (_currentStage >= 0)
            {
                _stageTicks[_currentStage] += now - _stageStart;
                _stageFrameTicks[_currentStage] += now - _stageStart;
            }

            _currentStage = stage;
            _stageStart = now;
        },
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

    private static void StopEngineTiming()
    {
        if (_originalLoop is { } loop) PlayerLoop.SetPlayerLoop(loop);
        _originalLoop = null;
        _currentStage = -1;
        _topStages.Clear();
    }

    internal static void Start()
    {
        _gcCountAtStart = GC.CollectionCount(0);
        ApplyRecorders();
    }

    // Unity's frame counters only while something shows or logs them: with all diagnostics
    // off, nothing of this runs.
    internal static void ApplyRecorders()
    {
        var want = Logging || Plugin.On(Plugin.ShowCosts) || Plugin.On(Plugin.ShowStats);
        if (want == _drawCalls.Valid) return;
        if (want)
        {
            _mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 15);
            _renderThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Render Thread", 15);
            _gcAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 15);
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 15);
            _shadowCasters = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Shadow Casters Count", 15);
            return;
        }

        _mainThread.Dispose();
        _renderThread.Dispose();
        _gcAlloc.Dispose();
        _drawCalls.Dispose();
        _shadowCasters.Dispose();
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

    internal static bool InRaid =>
        Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null;

    // After a raid, once a memory cleanup has run: how many of its players are still in memory,
    // and is any of them reachable from Smoothie's own fields? Players are tracked weakly, so
    // the tracking keeps nothing alive. Only while logging.
    private static readonly List<WeakReference> SeenPlayers = new();
    private static readonly HashSet<int> SeenPlayerIds = new();
    private static float _lastPlayerTrack, _raidEndAt = -1f;
    private static int _gcAtRaidEnd;

    internal static void OnRaidEnd() => _raidEndAt = SeenPlayers.Count > 0 ? Time.unscaledTime : -1f;

    private static void CheckRaidLeftovers(bool inRaid)
    {
        if (_raidEndAt >= 0f)
        {
            // A collection that starts 10 s or more after the raid, once the game has let go of it.
            if (Time.unscaledTime - _raidEndAt < 10f) _gcAtRaidEnd = GC.CollectionCount(0);
            else if (GC.CollectionCount(0) != _gcAtRaidEnd) ReportLeftovers();
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
        var alive = SeenPlayers.Select(w => w.Target).Where(o => o != null).ToList();
        var total = SeenPlayers.Count;
        SeenPlayers.Clear();
        SeenPlayerIds.Clear();
        try
        {
            var held = HeldBySmoothie(alive);
            Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} after the raid, with a memory cleanup run since: {alive.Count} of its {total} players still in memory " +
                               $"(mono heap {UsedHeap() >> 20} MB); reachable from Smoothie's fields: {(held.Count == 0 ? "none" : string.Join(", ", held))}");
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

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new();
        bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }

    // 20 s into a raid, once: what is still switched on in each loaded scene. The menu's video
    // background was found playing through raids; this shows anything else left running by a
    // menu, the hideout or a mod. Walks every object once (a one-off hitch), only while logging.
    private static float _censusDueAt = -1f;

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
            scene.GetRootGameObjects(roots);
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

        Plugin.Log.LogInfo(sb.ToString().TrimEnd());
    }

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
        var heap = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
        _raidStarted = true;
        Cleanup.Start(heap);
        if (!Logging) return;
        Plugin.Log.LogInfo($"raid started: GC mode {GarbageCollector.GCMode} (incremental {GarbageCollector.isIncremental}), mono heap {heap >> 20} MB, " +
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
        switch (Cleanup.Tick(GarbageCollector.GCMode != GarbageCollector.Mode.Disabled, GC.CollectionCount(0),
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
        foreach (var method in typeof(GC).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == nameof(GC.Collect)))
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

    private static void BotRequestPrefix(List<CountTypeBotWave> conditions)
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
        _windowFrames = 0;
        _windowStart = Time.unscaledTime;
        _fullScanDue = true;
        UpdateEngineTiming();
        PatchRunningScripts();
        PatchSpawnSteps();
        PatchPlayerSteps();
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
        ("EFT.BotOwner", "method_10"),
        ("EFT.BotSpawner", "ActivateBotCallback"),
        ("Fika.Core.Main.GameMode.HostGameController", "CreateBot"),
    };

    private static readonly Dictionary<MethodBase, Bucket> Steps = new();

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
    }

    // First in and last out, so other mods' prefixes and postfixes count too.
    private static Bucket? TimeStep(Dictionary<MethodBase, Bucket> into, MethodBase method, string name)
    {
        if (into.TryGetValue(method, out var known)) return known;
        var step = new Bucket { Name = name };
        try
        {
            into[method] = step;
            _harmony!.Patch(method, new HarmonyMethod(typeof(Diagnostics), nameof(Prefix)) { priority = Priority.First },
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
            Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} spawn steps: {string.Join(", ", parts)}");
        }

        foreach (var step in Steps.Values)
        {
            step.Ticks = 0;
            step.Calls = 0;
        }
    }

    // Times the scripts running right now that aren't timed yet. Rerun every few seconds,
    // because bots and most raid scripts appear after the raid starts.
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
                _harmony!.Patch(method, prefix, postfix);
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
        }

        _playerStepsLine = "";
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
                foreach (var name in Messages)
                {
                    MethodInfo? method;
                    try
                    {
                        method = t.GetMethod(name, flags, null, Type.EmptyTypes, null);
                        if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
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
    private static bool IsGameOrSpt(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        return name == "Assembly-CSharp" || name.StartsWith("UnityEngine") || name.StartsWith("Unity.") || name.StartsWith("spt-") || name == "Fika.Core";
    }

    // The timestamp prefix every method timed here shares.
    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(MethodBase __originalMethod, long __state)
    {
        if (!_profiling) return;
        if (!Buckets.TryGetValue(__originalMethod, out var bucket)) return;
        var ticks = Stopwatch.GetTimestamp() - __state;
        bucket.Ticks += ticks;
        bucket.Calls++;
        _frameScriptTicks += ticks;
        if (ticks > _frameWorstTicks)
        {
            _frameWorstTicks = ticks;
            _frameWorst = bucket.Name;
        }
    }

    // Once per frame, from the plugin's Update.
    internal static void Tick()
    {
        // Measured between our own calls, the same span the script timings cover (Unity's
        // deltaTime is the previous frame's, one frame off from them).
        var stamp = Stopwatch.GetTimestamp();
        var ms = _lastTickStamp == 0 ? Time.unscaledDeltaTime * 1000f : (float)((stamp - _lastTickStamp) * 1000.0 / Stopwatch.Frequency);
        _lastTickStamp = stamp;
        if (FrameTimes.Count < 4096) FrameTimes.Add(ms);
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

        // A snapshot every 2 minutes in raid, so no hotkey is needed.
        if (inRaid && Logging && Time.unscaledTime - _lastAutoSnapshot > 120f)
        {
            if (_lastAutoSnapshot > 0f) Plugin.Log.LogInfo("snapshot (every 2 min in raid):\n" + Snapshot(25));
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
        var gcCount = GC.CollectionCount(0);
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
            if (_scannedThisFrame) causes.Add("profiler rescan");
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

    private static void CloseWindow()
    {
        var frames = Math.Max(1, _windowFrames);
        var seconds = Math.Max(0.001, Time.unscaledTime - _windowStart);
        const double mb = 1024 * 1024;
        _top = Buckets.Values
            .Where(b => b.Calls > 0)
            .Select(b => (b.Name, Ms: b.Ticks * TickMs / frames, Calls: (double)b.Calls / frames))
            .OrderByDescending(r => r.Ms)
            .ToList();

        foreach (var bucket in Buckets.Values)
        {
            bucket.Ticks = 0;
            bucket.Calls = 0;
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
        var gcNow = GC.CollectionCount(0);
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
        sb.AppendLine($"  GC alloc/frame {Kb(_gcAlloc)} | GC runs since start {GC.CollectionCount(0) - _gcCountAtStart}" +
                      $" | mono heap {UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024 * 1024)} MB" +
                      $" | draw calls {Count(_drawCalls)} | shadow casters {Count(_shadowCasters)}");
        if (_pacing.Length > 0) sb.AppendLine("  " + _pacing);
        sb.AppendLine($"  targetFrameRate {Application.targetFrameRate} | vSyncCount {QualitySettings.vSyncCount}" +
                      $" | timeScale {Time.timeScale} | fixedDeltaTime {Time.fixedDeltaTime * 1000f:0.0} ms" +
                      $" | throttled bots {AnimatorThrottle.Throttled} | hidden close bots {Occlusion.HiddenCount}" +
                      $" | simplified bots {DistantDetail.SimplifiedBots} ({DistantDetail.HiddenRenderers} pieces hidden)");
        if (InRaid) sb.AppendLine($"  players alive {Singleton<GameWorld>.Instance.AllAlivePlayersList.Count}, memory cleanup {(GarbageCollector.GCMode == GarbageCollector.Mode.Disabled ? "off" : "on")}");
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
        sb.AppendLine($"  scripts total {scripts:0.00} ms/frame, top {Math.Min(rows, _top.Count)}:");
        foreach (var row in _top.Take(rows))
        {
            sb.AppendLine($"    {row.Ms,7:0.000} ms  {row.Calls,6:0.#} calls  {row.Name}");
        }

        sb.AppendLine(_heapGrowthMBps >= 0 ? $"  mono heap growing {_heapGrowthMBps:0.0} MB/s" : "  mono heap growth: a collection ran, no total this time");

        return sb.ToString().TrimEnd();
    }

    private static string Ms(ProfilerRecorder r) => r.Valid ? $"{r.LastValue / 1e6:0.0} ms" : "n/a";
    private static string Kb(ProfilerRecorder r) => r.Valid ? $"{r.LastValue / 1024} KB" : "n/a";
    private static string Count(ProfilerRecorder r) => r.Valid ? r.LastValue.ToString() : "n/a";
}
