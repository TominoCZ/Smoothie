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
using UnityEngine.Scripting;
using Object = UnityEngine.Object;

namespace Smoothie;

// A small in-game profiler for finding CPU hot paths without a development build.
//
// While "Profile scripts" is on, every Update / LateUpdate / FixedUpdate of the scripts
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
    private static readonly string[] Messages = { "Update", "LateUpdate", "FixedUpdate" };

    private static Harmony? _harmony;
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

    internal static bool Profiling => _harmony != null;

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

    private static void StopEngineTiming()
    {
        if (_originalLoop is { } loop) PlayerLoop.SetPlayerLoop(loop);
        _originalLoop = null;
        _currentStage = -1;
        _topStages.Clear();
    }

    internal static void Start()
    {
        _mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 15);
        _renderThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Render Thread", 15);
        _gcAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 15);
        _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 15);
        _shadowCasters = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Shadow Casters Count", 15);
        _gcCountAtStart = GC.CollectionCount(0);
        GarbageCollector.GCModeChanged += OnGcModeChanged;
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
    private static long _lastTickStamp;
    private static float _lastDeathSnapshot = -100f;

    internal static bool InRaid =>
        Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null;

    internal static void OnRaidStart()
    {
        _fullScanDue = _harmony != null;
        _lastAutoSnapshot = Time.unscaledTime;
        _raidStarted = true;
        _reenabled = false;
        var heap = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
        _heapCeiling = heap + HeapHeadroom;
        Plugin.Log.LogInfo($"raid started: GC mode {GarbageCollector.GCMode} (incremental {GarbageCollector.isIncremental}), mono heap {heap >> 20} MB, " +
                           $"RAM {SystemInfo.systemMemorySize / 1024} GB (game disables GC in raid from {InGameMemoryManagement.Settings.GigabytesRequiredToDisableGCDuringRaid} GB)");
    }

    // The game switches the GC off for a raid (BaseLocalGame.PrepareSession) so it can't
    // freeze the game mid-fight, and back on in the menu (MenuScreen). SPT switches it on
    // and starts an incremental collection on every inventory open, its way of freeing
    // the raid's garbage. That collection is let finish (spread over frames), then the GC
    // goes off again; switching it off right away left the heap growing for the whole raid
    // (2 to 6 GB in five minutes while hosting). The game's setter keeps its own minimum
    // RAM rule and logs the switch.
    private static float _lastGcAttempt = -100f;
    private static float _lastModeLog = -100f;

    // Set once the game has started the raid (and so has had its own chance to switch the
    // GC off). Before that, loading is the game's business.
    private static bool _raidStarted;
    private static bool _reenabled;
    private static int _gcCountAtEnable;

    // Garbage allowed to pile up before a collection is started here too, for raids where
    // the inventory never opens.
    private const long HeapHeadroom = 4L << 30;
    private static long _heapCeiling = long.MaxValue;
    private static float _lastHeapCheck;

    private static void KeepGcOff()
    {
        if (!_raidStarted || !Plugin.On(Plugin.KeepGcOff)) return;

        if (GarbageCollector.GCMode != GarbageCollector.Mode.Enabled)
        {
            // Reading the heap size isn't free; once a second is plenty for a 4 GB margin.
            if (Time.unscaledTime - _lastHeapCheck < 1f) return;
            _lastHeapCheck = Time.unscaledTime;
            if (UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() < _heapCeiling) return;

            // The same cleanup SPT runs on an inventory open; OnGcModeChanged arms the wait.
            _heapCeiling = long.MaxValue;
            _reenabled = true;
            _gcCountAtEnable = GC.CollectionCount(0);
            GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
            GarbageCollector.CollectIncremental(25_000_000);
            Plugin.Log.LogInfo("heap grew 4 GB since the last collection; collecting before switching the GC off again");
            return;
        }

        // Wait for the collection that was started to finish. If it never does, the GC
        // stays on, which is SPT's own behaviour.
        if (_reenabled)
        {
            if (GC.CollectionCount(0) == _gcCountAtEnable) return;
        }
        // Otherwise every 30 s at most, in case the game's RAM rule refuses (its setter
        // logs a warning each time).
        else if (Time.unscaledTime - _lastGcAttempt < 30f)
        {
            return;
        }

        _reenabled = false;
        _lastGcAttempt = Time.unscaledTime;

        InGameMemoryManagement.GCEnabled = false;
        var heap = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
        _heapCeiling = heap + HeapHeadroom;
        Plugin.Log.LogInfo($"GC switched back off for the raid (now {GarbageCollector.GCMode}, mono heap {heap >> 20} MB)");
    }

    // Who turns the GC back on mid-raid. Switched back off once a collection has finished
    // (not inside the caller, which may be the game's own Collect turning it on briefly).
    private static void OnGcModeChanged(GarbageCollector.Mode mode)
    {
        if (!_raidStarted || mode != GarbageCollector.Mode.Enabled || !InRaid) return;
        _reenabled = true;
        _gcCountAtEnable = GC.CollectionCount(0);

        if (!Plugin.On(Plugin.LogDrops) || Time.unscaledTime - _lastModeLog < 5f) return;
        _lastModeLog = Time.unscaledTime;
        var callers = (new StackTrace(1, false).GetFrames() ?? Array.Empty<StackFrame>())
            .Take(8).Select(f => f.GetMethod()).Where(m => m != null)
            .Select(m => $"{m!.DeclaringType?.FullName}.{m.Name}");
        Plugin.Log.LogWarning($"GC switched on mid-raid by: {string.Join(" <- ", callers)}");
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
        if (_harmony != null) return;
        _harmony = new Harmony(HarmonyId);
        _windowFrames = 0;
        _windowStart = Time.unscaledTime;
        _fullScanDue = true;

        try
        {
            StartEngineTiming();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"profiler: engine stage timing unavailable: {ex.Message}");
        }

        PatchRunningScripts();
        PatchSpawnSteps();
    }

    // Each step of building and waking a bot, logged when one takes over 5 ms, so a spawn
    // freeze can be pinned to a step. Async methods are timed up to their first real wait.
    private static readonly (string Type, string Method)[] SpawnSteps =
    {
        ("EFT.Player", "Init"),
        ("EFT.PlayerBody", "Init"),
        ("EFT.GameWorld", "RegisterPlayer"),
        ("EFT.BotOwner", "Create"),
        ("EFT.BotOwner", "PreActivate"),
        ("EFT.BotOwner", "method_10"),
        ("EFT.BotSpawner", "ActivateBotCallback"),
        ("BotCreatorClient", "StoreBotRenderers"),
        // Fika's host steps; skipped when Fika isn't installed.
        ("Fika.Core.Main.GameMode.HostGameController", "CreateBot"),
        ("Fika.Core.Networking.FikaSerializationExtensions", "PutProfile"),
    };

    private static void PatchSpawnSteps()
    {
        // First in and last out, so other mods' prefixes and postfixes (SAIN's) count too.
        var prefix = new HarmonyMethod(typeof(Diagnostics), nameof(SpawnPrefix)) { priority = Priority.First };
        var postfix = new HarmonyMethod(typeof(Diagnostics), nameof(SpawnPostfix)) { priority = Priority.Last };
        foreach (var (typeName, name) in SpawnSteps)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null) continue;
            foreach (var method in AccessTools.GetDeclaredMethods(type).Where(m => m.Name == name && !m.IsAbstract))
            {
                try
                {
                    _harmony!.Patch(method, prefix, postfix);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"profiler: can't time {typeName}.{name}: {ex.Message}");
                }
            }
        }

        _harmony!.Patch(AccessTools.Method(typeof(GameWorld), nameof(GameWorld.RegisterPlayer)),
            postfix: new HarmonyMethod(typeof(Diagnostics), nameof(OnPlayerRegistered)));
    }

    private static void SpawnPrefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void SpawnPostfix(MethodBase __originalMethod, long __state)
    {
        var ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        if (ms > 5.0 && InRaid)
        {
            Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} spawn step {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} {ms:0.0} ms");
        }
    }


    // Times the scripts running right now that aren't timed yet. Rerun every few seconds,
    // because bots and most raid scripts appear after the raid starts.
    private static void PatchRunningScripts()
    {
        if (_harmony == null) return;
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
                _harmony.Patch(method, prefix, postfix);
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
        if (_harmony == null) return;
        StopEngineTiming();
        _harmony.UnpatchSelf();
        _harmony = null;
        SpawnedToScan.Clear();
        Buckets.Clear();
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
        if (iPlayer is Component c) SpawnedToScan.Add(c.gameObject);
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

    private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void Postfix(MethodBase __originalMethod, long __state)
    {
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
        if (!inRaid) _raidStarted = false;
        LogHitch(ms, inRaid);
        if (inRaid) KeepGcOff();

        // A snapshot every 2 minutes in raid, so no hotkey is needed.
        if (inRaid && (Plugin.On(Plugin.LogDrops) || _harmony != null) && Time.unscaledTime - _lastAutoSnapshot > 120f)
        {
            if (_lastAutoSnapshot > 0f) Plugin.Log.LogInfo("snapshot (every 2 min in raid):\n" + Snapshot(25));
            _lastAutoSnapshot = Time.unscaledTime;
        }

        if (_harmony != null)
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

    // A frame over 50 ms in raid gets one line saying what else happened around it.
    private static void LogHitch(float ms, bool inRaid)
    {
        var tickMs = 1000.0 / Stopwatch.Frequency;
        var gcCount = GC.CollectionCount(0);
        var players = inRaid ? Singleton<GameWorld>.Instance.AllAlivePlayersList.Count : 0;

        if (inRaid && Plugin.On(Plugin.LogDrops) && ms > 50f && Time.unscaledTime - _lastHitchLog > 0.25f)
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
            var outside = worstStage >= 0 ? ms - stagesTotal * tickMs : 0;

            var scripts = _harmony != null
                ? $", scripts {_frameScriptTicks * tickMs:0.0} ms (worst {_frameWorst} {_frameWorstTicks * tickMs:0.0} ms)" +
                  (worstStage >= 0 ? $", slowest engine stage {_stageNames[worstStage]} {_stageFrameTicks[worstStage] * tickMs:0.0} ms, outside Unity's frame {outside:0} ms" : "")
                : "";
            Plugin.Log.LogWarning($"{DateTime.Now:HH:mm:ss.fff} hitch {ms:0} ms{scripts}" + (causes.Count > 0 ? " | " + string.Join(", ", causes) : " | no known cause (engine, loading, or a script not profiled)"));
        }

        _lastGcCount = gcCount;
        // Someone just died: the last closed window (up to 2 s) is what the frame cost while
        // they were alive, e.g. a bot fighting you. Compared with the next 2-minute
        // snapshot, it shows what one live bot cost.
        if (inRaid && _harmony != null && players < _lastPlayerCount && Time.unscaledTime - _lastDeathSnapshot > 20f)
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
        var tickMs = 1000.0 / Stopwatch.Frequency;
        var seconds = Math.Max(0.001, Time.unscaledTime - _windowStart);
        const double mb = 1024 * 1024;
        _top = Buckets.Values
            .Where(b => b.Calls > 0)
            .Select(b => (b.Name, Ms: b.Ticks * tickMs / frames, Calls: (double)b.Calls / frames))
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
            if (_stageTicks[i] > 0) stages.Add((_stageNames[i], _stageTicks[i] * tickMs / frames));
            _stageTicks[i] = 0;
        }

        _topStages = stages.OrderByDescending(s => s.Ms).ToList();

        // Heap growth over the window; meaningless if a collection ran in between.
        var heapNow = UsedHeap();
        var gcNow = GC.CollectionCount(0);
        _heapGrowthMBps = gcNow == _windowGcCount && _windowHeap > 0 ? (heapNow - _windowHeap) / mb / seconds : -1;
        _windowHeap = heapNow;
        _windowGcCount = gcNow;

        // Something else set the player loop (seen mid-raid), dropping the markers: put
        // them back into whatever loop is current now.
        if (_topStages.Count == 0 && _originalLoop != null) StartEngineTiming();

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

        if (_harmony == null)
        {
            sb.Append("  (turn on \"Profile scripts\" for a per-script breakdown)");
            return sb.ToString();
        }

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
