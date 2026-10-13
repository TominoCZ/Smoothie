using System;
using System.Diagnostics;
using System.Linq;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Smoothie;

// "Show performance overlay": what each part of the frame costs right now, averaged over the
// last second, with a 30 s graph per row, a yellow mark wherever a Smoothie setting was
// changed and a red one where a memory cleanup finished. Switch a feature off and on and its
// row shows the difference.
internal static class Overlay
{
    private const int Samples = 120;
    private const float SamplePeriod = 0.25f;
    private const int GraphHeight = 20;
    private static readonly double TickMs = Diagnostics.TickMs;

    private sealed class Row
    {
        public readonly string Name;
        public readonly Func<float> Value;
        public readonly Func<string> Extra;
        public readonly ConfigEntry<bool>[] Features;
        public readonly bool Count;

        // Worst frame in each sample instead of the mean.
        public bool Max;

        // Total over the sample instead of the mean (events, e.g. collections).
        public bool Total;
        // Rows with a colour are parts of the frame bar, in row order; Short is their bar label.
        public Color? Color;
        public string Short = "";

        // This frame's value, for Other (the rest of the frame) to subtract.
        public float Last;
        public readonly float[] History = new float[Samples];
        public readonly Texture2D Graph = new(Samples, GraphHeight, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        public double Sum;
        public string Shown = "", ExtraShown = "", Share = "", Tag = "";

        public Row(string name, Func<float> value, Func<string> extra, bool count, params ConfigEntry<bool>[] features)
        {
            Name = name;
            Value = value;
            Extra = extra;
            Count = count;
            Features = features;
        }
    }

    private static readonly Row[] Rows =
    {
        new("Frame", () => _frameMs, () => $"{1000f / Math.Max(0.1f, Average(0)):0} fps", false),
        new("Worst frame", () => _frameMs, () => "spikes here are stutters", false) { Max = true },
        new("Bot animation", () => Stage(_animBegin) + Stage(_animEnd) + (Diagnostics.InRaid ? (float)AnimatorThrottle.StepMs : 0f),
            () => $"{AnimatorThrottle.Throttled} reduced; full: {AnimatorThrottle.FullInView} seen, {AnimatorThrottle.FullFighting} fight, {AnimatorThrottle.FullOther} near", false, Plugin.ThrottleAnimators)
            { Color = new Color(0.25f, 0.5f, 0.95f), Short = "Anim" },
        new("Bot IK", () => (float)(_ikFrameTicks * TickMs),
            () => $"{Occlusion.HiddenCount} hidden bots skip it", false)
            { Color = new Color(0.95f, 0.55f, 0.15f), Short = "IK" },
        new("Bot AI", () => (float)(_aiFrameTicks * TickMs),
            () => "bots this PC runs, SAIN included", false)
            { Color = new Color(0.15f, 0.7f, 0.75f), Short = "AI" },
        new("Profiles", () => (float)(_profileFrameTicks * TickMs),
            () => $"{ProfileBudget.Waiting} new bots waiting", false, Plugin.SpreadProfiles)
            { Color = new Color(0.9f, 0.45f, 0.65f), Short = "Profiles" },
        // Every script's Update/LateUpdate/FixedUpdate and coroutines, the game's and every
        // mod's (SAIN's own components too), minus the parts timed above that run inside them.
        new("Scripts", ScriptsMs, Diagnostics.ScriptsNote, false)
            { Color = new Color(0.85f, 0.75f, 0.2f), Short = "Scripts" },
        new("Rendering (CPU)", () => Stage(_render),
            () => $"{Diagnostics.CamerasThisFrame} cameras, {Diagnostics.ShadowCasters} shadow casters", false, Plugin.SimplifyDistant, Plugin.HideCulledWeapons, Plugin.NoMuzzleShadows)
            { Color = new Color(0.6f, 0.35f, 0.85f), Short = "Render" },
        // The game steps physics itself (PhysicsExtensions), so Unity's physics stage is empty.
        // It only simulates while something moves (ragdolls, grenades, dropped loot, doors).
        new("Physics", () => (float)(_physicsFrameTicks * TickMs), PhysicsNote, false)
            { Color = new Color(0.2f, 0.7f, 0.35f), Short = "Phys" },
        new("GPU wait", () => Stage(_gpuWait), () => "high = graphics card is the limit", false)
            { Color = new Color(0.75f, 0.2f, 0.25f), Short = "GPU wait" },
        // The frame minus everything above: engine work nothing above times, mostly loading
        // (textures, models) and time outside the game's frame (driver, shader compiling, OS).
        new("Other", OtherMs, () => $"loading {_loadAverageMs:0.00}, outside frame {_outsideAverageMs:0.00} ms", false)
            { Color = new Color(0.4f, 0.4f, 0.4f), Short = "Other" },
        // Collections that finished, per second; the note has the heap and how fast it grows.
        new("Memory cleanups", () => _gcThisFrame, () => _gcNote ?? "", true, Plugin.KeepCleanupPaused) { Total = true },
        // Videos playing anywhere (counted once a second); the note has the hidden menu
        // background's videos Smoothie paused, which played through raids before.
        new("Videos playing", () => _playingVideos, () => $"{HotPaths.PausedMenuVideoCount} menu videos paused", true, Plugin.HotPathFixes),
        new("Draw calls", () => Diagnostics.DrawCalls, () => $"{Diagnostics.SetPassCalls} SetPass; {DistantDetail.SimplifiedBots} far bots simplified", true, Plugin.SimplifyDistant, Plugin.HideCulledWeapons),
        // How many are hidden right now; "were drawn": Unity was drawing them when they were
        // hidden, the real saving. The work behind it is in "Smoothie itself".
        // Only the ones that were being drawn: most far objects the game's baked visibility
        // already switches off, and hiding those again saves nothing.
        new("Hidden objects", () => Plugin.On(Plugin.HideCovered) ? DepthCulling.Saving : 0,
            () => $"check {DepthCulling.WorkerMs:0.0} ms; {DepthCulling.Hidden} hidden of {DepthCulling.CoveredCount} covered (the rest the game isn't drawing){(DepthCulling.Waiting > 0 ? $", {DepthCulling.Waiting} waiting for a try" : "")}; {DepthCulling.Status}", true, Plugin.HideCovered),
        new("Smoothie itself", () => (float)(_ownFrameTicks * TickMs) + (Diagnostics.InRaid ? (float)(AnimatorThrottle.StepMs + Occlusion.Ms) : 0f),
            () => $"incl. this overlay {_overlayAverageMs:0.00}, hiding {DepthCulling.Ms:0.00} (worst {DepthCulling.PeakMs:0.00}: {DepthCulling.PeakNote}) ms; {Occlusion.Rays} bot rays", false),
    };

    // Which samples a setting changed in; the newest sample is at _head - 1.
    private static readonly bool[] Changed = new bool[Samples];

    // Samples in which a garbage collection finished: a red mark in every graph.
    private static readonly bool[] Collected = new bool[Samples];
    private static int _lastGcCount = -1;
    private static int _gcThisFrame;
    private static bool _collectedNow;
    private static string _gcNote = "";
    private static int _playingVideos;
    private static long _lastHeap;
    private static float _lastHeapAt;
    private static bool _changedNow;
    private static int _head;
    private static int _frames;
    private static float _sampleStart;
    private static float _frameMs;
    private static readonly Color32[] Pixels = new Color32[Samples * GraphHeight];

    // The frame as one fixed-width bar, |--Anim--|----IK----|--Render--|...|--Other--|: each
    // coloured row's share of the last second's average frame time, Other being the rest.
    private static readonly int[] SegmentRows = Enumerable.Range(0, Rows.Length).Where(i => Rows[i].Color != null).ToArray();
    private static readonly float[] SegmentMs = new float[SegmentRows.Length];
    private static float _barTotalMs = 1f;
    private static string _frameLabel = "", _worstLabel = "";
    private static int _preloading = -1;
    private static double _loadSum, _outsideSum, _overlaySum;
    private static float _loadAverageMs, _outsideAverageMs, _overlayAverageMs;

    private static float OtherMs()
    {
        // Other is the bar's last part; everything before it is subtracted.
        var ms = _frameMs;
        for (var i = 0; i < SegmentRows.Length - 1; i++)
        {
            var last = Rows[SegmentRows[i]].Last;
            if (!float.IsNaN(last)) ms -= last;
        }

        return Math.Max(0f, ms);
    }

    private static string[]? _stageNames;
    private static int _animBegin = -1, _animEnd = -1, _render = -1, _gpuWait = -1;
    private static int[] _scriptStages = Array.Empty<int>();

    // Bots' VisualPass (IK, procedural weapon, head aim), Physics.Simulate and bots' AI
    // (BotsController.UpdateByUnity), timed only while the overlay is on.
    private static Harmony? _harmony;
    private static bool _on;
    private static long _ikTicks;
    private static long _ikFrameTicks;
    private static long _physicsTicks;
    private static long _ownFrameTicks;
    private static long _aiTicks;
    private static long _aiFrameTicks;
    private static long _physicsFrameTicks;
    private static long _syncTicks, _syncFrameTicks;
    private static long _profileTicks, _profileFrameTicks, _profileStart;
    private static int _profileDepth;
    private static System.Threading.Thread? _mainThread;
    private static double _simSum, _syncSum;
    private static float _simAverageMs, _syncAverageMs;

    internal static void Apply()
    {
        Diagnostics.UpdateEngineTiming();
        // Patched once and never unpatched: every unpatch and patch again leaves another copy
        // of each patched method behind. While the overlay is off the hooks return at once.
        _on = Plugin.On(Plugin.ShowCosts);
        if (!_on || _harmony != null) return;

        _harmony = new Harmony("com.tominocz.smoothie.overlay");
        var prefix = new HarmonyMethod(typeof(Overlay), nameof(Begin)) { priority = Priority.First };
        var postfix = new HarmonyMethod(typeof(Overlay), nameof(IkEnd)) { priority = Priority.Last };
        _harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.VisualPass)), prefix, postfix);
        if (Occlusion.ObservedVisualPass() is { } observed) _harmony.Patch(observed, prefix, postfix);
        _harmony.Patch(AccessTools.Method(typeof(Physics), nameof(Physics.Simulate), new[] { typeof(float) }),
            prefix, new HarmonyMethod(typeof(Overlay), nameof(PhysicsEnd)));
        // Collider positions brought up to date for raycasts, once a frame for your own player
        // (Unity's automatic sync is off in EFT). Physics.SyncTransforms itself is native.
        _harmony.Patch(AccessTools.Method(typeof(PhysicsExtensions.Simulation), nameof(PhysicsExtensions.Simulation.ForceSyncTransforms)),
            prefix, new HarmonyMethod(typeof(Overlay), nameof(SyncEnd)));
        // The whole AI tick: brains (AICoreController, where BigBrain/SAIN layers run), AI
        // tasks (vision), smoke vision and every bot's BotOwner.UpdateManual.
        _harmony.Patch(AccessTools.Method(typeof(BotsController), nameof(BotsController.UpdateByUnity)),
            prefix, new HarmonyMethod(typeof(Overlay), nameof(AiEnd)) { priority = Priority.Last });
        // Building bot profiles, by whoever builds them: the game per spawn attempt
        // (BotProfileClient.CreateProfile, 197 at once in a vanilla Horde spawn check), the
        // game's LoadBots and Smoothie's budget (both through this Profile constructor).
        _mainThread = System.Threading.Thread.CurrentThread;
        var profileBegin = new HarmonyMethod(typeof(Overlay), nameof(ProfileBegin)) { priority = Priority.First };
        var profileEnd = new HarmonyMethod(typeof(Overlay), nameof(ProfileEnd));
        _harmony.Patch(AccessTools.Constructor(typeof(Profile), new[] { typeof(ProfileDescriptor) }), profileBegin, finalizer: profileEnd);
        if (Plugin.FindType("BotProfileClient") is { } client)
        {
            foreach (var method in AccessTools.GetDeclaredMethods(client).Where(m => m.Name == "CreateProfile" && !m.IsAbstract))
            {
                _harmony.Patch(method, profileBegin, finalizer: profileEnd);
            }
        }
    }

    // Outermost call only, so a profile built inside CreateProfile isn't counted twice; a
    // finalizer, so a throw can't leave the depth stuck. Main thread only: the experimental
    // worker builds profiles off it, and they don't cost the frame.
    private static void ProfileBegin()
    {
        if (System.Threading.Thread.CurrentThread != _mainThread || _profileDepth++ != 0) return;
        _profileStart = Stopwatch.GetTimestamp();
        Diagnostics.NoteProfileBuilt();
    }

    private static void ProfileEnd()
    {
        if (System.Threading.Thread.CurrentThread == _mainThread && --_profileDepth == 0 && _on) _profileTicks += Stopwatch.GetTimestamp() - _profileStart;
    }

    // Harmony hands __state from prefix to postfix only within one class, so the overlay
    // keeps its own timestamp prefix instead of sharing Diagnostics'.
    private static void Begin(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void PhysicsEnd(long __state)
    {
        if (_on) _physicsTicks += Stopwatch.GetTimestamp() - __state;
    }

    private static void SyncEnd(long __state)
    {
        if (!_on) return;
        var took = Stopwatch.GetTimestamp() - __state;
        _physicsTicks += took;
        _syncTicks += took;
    }

    // What keeps the game's physics running: it only steps while one of the bodies it was
    // told about (UpdateController) is active and, if it has a visibility check, seen.
    private static string PhysicsNote()
    {
        int corpses = 0, loot = 0, other = 0;
        foreach (var body in PhysicsExtensions.UpdateController._rigidbodies)
        {
            if (body.rigidbody == null) continue;
            if (body.rigidbody.GetComponentInParent<EFT.Interactive.Corpse>() != null) corpses++;
            else if (body.rigidbody.GetComponentInParent<EFT.Interactive.LootItem>() != null) loot++;
            else other++;
        }

        return $"step {_simAverageMs:0.00} + sync {_syncAverageMs:0.00} ms; moving {corpses + loot + other}";
    }

    private static void AiEnd(long __state)
    {
        if (_on) _aiTicks += Stopwatch.GetTimestamp() - __state;
    }

    private static void IkEnd(Player __instance, long __state)
    {
        if (_on && !__instance.IsYourPlayer) _ikTicks += Stopwatch.GetTimestamp() - __state;
    }

    internal static void MarkChange() => _changedNow = true;

    private static float ScriptsMs()
    {
        if (!Diagnostics.EngineTiming || _scriptStages.Length == 0) return float.NaN;
        var ms = 0f;
        foreach (var index in _scriptStages) ms += Stage(index);
        ms -= (float)((_ikFrameTicks + _aiFrameTicks + _physicsFrameTicks + _profileFrameTicks) * TickMs);
        if (Diagnostics.InRaid) ms -= (float)AnimatorThrottle.StepMs;
        return Math.Max(0f, ms);
    }

    private static float Stage(int index)
    {
        var ticks = Diagnostics.StageFrameTicks;
        return Diagnostics.EngineTiming && index >= 0 && index < ticks.Length ? (float)(ticks[index] * TickMs) : float.NaN;
    }

    // Once per frame from Diagnostics.Tick, before it clears this frame's stage times.
    internal static void Sample(float frameMs)
    {
        if (!Plugin.On(Plugin.ShowCosts)) return;
        try
        {
            if (!ReferenceEquals(_stageNames, Diagnostics.StageNames))
            {
                _stageNames = Diagnostics.StageNames;
                _animBegin = Array.IndexOf(_stageNames, "PreLateUpdate.DirectorUpdateAnimationBegin");
                _animEnd = Array.IndexOf(_stageNames, "PreLateUpdate.DirectorUpdateAnimationEnd");
                _render = Array.IndexOf(_stageNames, "PostLateUpdate.FinishFrameRendering");
                _gpuWait = Array.FindIndex(_stageNames, n => n.EndsWith(".WaitForLastPresentationAndUpdateTime"));
                _preloading = Array.IndexOf(_stageNames, "EarlyUpdate.UpdatePreloading");
                var scripts = new System.Collections.Generic.List<int>();
                for (var i = 0; i < _stageNames.Length; i++)
                {
                    if (_stageNames[i].Contains(".ScriptRun")) scripts.Add(i);
                }

                _scriptStages = scripts.ToArray();
            }

            _frameMs = frameMs;
            var gcCount = GC.CollectionCount(0);
            _gcThisFrame = _lastGcCount < 0 ? 0 : gcCount - _lastGcCount;
            _lastGcCount = gcCount;
            if (_gcThisFrame > 0) _collectedNow = true;
            _ikFrameTicks = _ikTicks;
            _ikTicks = 0;
            _physicsFrameTicks = _physicsTicks;
            _physicsTicks = 0;
            _syncFrameTicks = _syncTicks;
            _syncTicks = 0;
            _syncSum += _syncFrameTicks * TickMs;
            var load = Stage(_preloading);
            _loadSum += float.IsNaN(load) ? 0f : load;
            long stages = 0;
            foreach (var ticks in Diagnostics.StageFrameTicks) stages += ticks;
            _outsideSum += Diagnostics.EngineTiming ? Math.Max(0.0, frameMs - stages * TickMs) : 0.0;
            _simSum += (_physicsFrameTicks - _syncFrameTicks) * TickMs;
            _aiFrameTicks = _aiTicks;
            _aiTicks = 0;
            _profileFrameTicks = _profileTicks;
            _profileTicks = 0;
            _ownFrameTicks = Plugin.OwnTicks;
            Plugin.OwnTicks = 0;
            _overlaySum += Plugin.OverlayTicks * TickMs;
            Plugin.OverlayTicks = 0;
            foreach (var row in Rows)
            {
                var v = row.Value();
                row.Last = v;
                row.Sum = row.Max ? Math.Max(row.Sum, v) : row.Sum + v;
            }
            _frames++;

            if (Time.unscaledTime - _sampleStart < SamplePeriod) return;
            _sampleStart = Time.unscaledTime;
            Push();
        }
        catch (Exception ex)
        {
            Plugin.Fail(Plugin.ShowCosts, ex);
        }
    }

    private static void Push()
    {
        Changed[_head] = _changedNow;
        _changedNow = false;
        Collected[_head] = _collectedNow;
        _collectedNow = false;
        foreach (var row in Rows)
        {
            row.History[_head] = row.Max || row.Total ? (float)row.Sum : (float)(row.Sum / Math.Max(1, _frames));
            row.Sum = 0;
        }

        _simAverageMs = (float)(_simSum / Math.Max(1, _frames));
        _syncAverageMs = (float)(_syncSum / Math.Max(1, _frames));
        _loadAverageMs = (float)(_loadSum / Math.Max(1, _frames));
        _outsideAverageMs = (float)(_outsideSum / Math.Max(1, _frames));
        _overlayAverageMs = (float)(_overlaySum / Math.Max(1, _frames));
        _simSum = _syncSum = _loadSum = _outsideSum = _overlaySum = 0;
        _frames = 0;
        _head = (_head + 1) % Samples;

        // Heap size once a second; reading it isn't free.
        if (Time.unscaledTime - _lastHeapAt >= 1f)
        {
            var heap = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            var growth = _lastHeapAt > 0f ? (heap - _lastHeap) / (1024.0 * 1024.0) / (Time.unscaledTime - _lastHeapAt) : 0.0;
            _gcNote = $"{heap >> 20} MB ({growth:+0;-0} MB/s), auto {(UnityEngine.Scripting.GarbageCollector.GCMode == UnityEngine.Scripting.GarbageCollector.Mode.Disabled ? "off" : "on")}";
            _lastHeap = heap;
            _lastHeapAt = Time.unscaledTime;
            _playingVideos = UnityEngine.Object.FindObjectsOfType<UnityEngine.Video.VideoPlayer>().Count(v => v.isPlaying);
        }

        var frame = Average(0);
        for (var index = 0; index < Rows.Length; index++)
        {
            var row = Rows[index];
            var avg = row.Max ? Peak(index) : row.Total ? Average(index) / SamplePeriod : Average(index);
            row.Shown = float.IsNaN(avg) ? "n/a" : row.Count ? $"{avg:0}" : $"{avg:0.00} ms";
            row.Share = row.Color != null && !float.IsNaN(avg) ? $"{avg / Math.Max(0.001f, frame):0%}" : "";
            // Whether the Smoothie settings that cut this cost are on, so a toggle can be matched to the row.
            var on = 0;
            foreach (var feature in row.Features)
            {
                if (Plugin.On(feature)) on++;
            }

            row.Tag = row.Features.Length == 0 ? "" : on == row.Features.Length ? "<color=#7c7>fix on</color>" : on == 0 ? "<color=#e77>fix off</color>" : $"<color=#ec7>fix {on}/{row.Features.Length}</color>";
            row.ExtraShown = $"<color=#bbb>{row.Extra()}</color>";
            Redraw(row);
        }

        for (var i = 0; i < SegmentRows.Length; i++)
        {
            var ms = Average(SegmentRows[i]);
            SegmentMs[i] = float.IsNaN(ms) ? 0f : ms;
        }

        // The segments, not the measured frame, are the whole: they can overlap it by a hair.
        var total = 0f;
        foreach (var ms in SegmentMs) total += ms;
        _barTotalMs = Math.Max(0.001f, total);
        _frameLabel = $"{frame:0.0} ms   {1000f / Math.Max(0.1f, frame):0} fps";
        _worstLabel = $"<color=#bbb>worst frame {Peak(1):0.0} ms</color>";
    }

    // For the snapshot in the log: the frame's parts as the overlay shows them, so snapshots
    // minutes apart show which part grew. Empty while the overlay is off (nothing sampled).
    internal static string PartsSummary()
    {
        if (!Plugin.On(Plugin.ShowCosts)) return "";
        var parts = "";
        foreach (var i in SegmentRows) parts += (parts.Length > 0 ? ", " : "") + $"{Rows[i].Short} {Rows[i].Shown}";
        return $"  overlay, last second: frame {Rows[0].Shown} = {parts} (Other: loading {_loadAverageMs:0.00}, outside the frame {_outsideAverageMs:0.00}) | Smoothie itself {Rows[Rows.Length - 1].Shown} (this overlay {_overlayAverageMs:0.00} ms)";
    }

    // Highest of the last second of samples.
    private static float Peak(int rowIndex)
    {
        var history = Rows[rowIndex].History;
        var peak = 0f;
        for (var i = 1; i <= (int)(1f / SamplePeriod); i++) peak = Math.Max(peak, history[(_head - i + Samples) % Samples]);
        return peak;
    }

    // Mean of the last second of samples.
    private static float Average(int rowIndex)
    {
        var history = Rows[rowIndex].History;
        const int n = (int)(1f / SamplePeriod);
        var sum = 0f;
        for (var i = 1; i <= n; i++) sum += history[(_head - i + Samples) % Samples];
        return sum / n;
    }

    private static void Redraw(Row row)
    {
        // Scaled to the row's own peak, so a change shows whatever the magnitude.
        var max = row.Count ? 10f : 0.5f;
        foreach (var v in row.History)
        {
            if (!float.IsNaN(v) && v > max) max = v;
        }

        var background = new Color32(0, 0, 0, 110);
        var bar = new Color32(220, 220, 220, 255);
        var mark = new Color32(255, 200, 0, 255);
        var gcMark = new Color32(230, 40, 40, 255);
        for (var x = 0; x < Samples; x++)
        {
            var sample = (_head + x) % Samples;
            var v = row.History[sample];
            var height = float.IsNaN(v) ? 0 : Mathf.CeilToInt(v / max * GraphHeight);
            for (var y = 0; y < GraphHeight; y++)
            {
                Pixels[y * Samples + x] = Changed[sample] ? mark : Collected[sample] ? gcMark : y < height ? bar : background;
            }
        }

        row.Graph.SetPixels32(Pixels);
        row.Graph.Apply(false);
    }

    private static GUIStyle? _text, _right, _big;

    // Draws the panel at (x, y) and returns its bottom edge.
    internal static float Draw(float x, float y)
    {
        const float rowHeight = 22f;
        const float width = 1000f;
        const float barWidth = width - 20f;
        const float name = 24f, value = 154f, share = 226f, graph = 282f, tag = graph + Samples * 2 + 10f, note = tag + 62f;
        if (_text == null)
        {
            _text = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, wordWrap = false, clipping = TextClipping.Clip };
            _right = new GUIStyle(_text) { alignment = TextAnchor.UpperRight };
            _big = new GUIStyle(_text) { fontSize = 20, fontStyle = FontStyle.Bold };
        }

        // Two gaps: between the frame rows, the bar's parts and the rest.
        var top = y + 104f;
        var height = top - y + Rows.Length * rowHeight + 2 * 8f + 6f;
        // Nothing here takes input, so the layout and input passes of OnGUI can skip it.
        if (Event.current.type != EventType.Repaint) return y + height;
        var saved = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.8f);
        GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
        GUI.color = saved;

        GUI.Label(new Rect(x + 10, y + 4, 400, 28), _frameLabel, _big);
        GUI.Label(new Rect(x + 10, y + 8, barWidth, 22), _worstLabel, _right);
        GUI.Label(new Rect(x + 10, y + 32, barWidth, 20), (Plugin.Fixing ? "<color=#999>Where each frame's time goes, average of the last second</color>" : (Plugin.SpawnFixesPaused ? "<color=#e77>ALL FIXES OFF (A/B test): the game as without Smoothie</color>" : "<color=#e77>ALL FIXES OFF (A/B test), except bot spawning fixes (switched off mid-raid)</color>")), _text);

        var left = x + 10;
        for (var i = 0; i < SegmentMs.Length; i++)
        {
            var w = SegmentMs[i] / _barTotalMs * barWidth;
            if (w < 1f) continue;
            var row = Rows[SegmentRows[i]];
            GUI.color = row.Color!.Value;
            GUI.DrawTexture(new Rect(left, y + 52, w - 1f, 26), Texture2D.whiteTexture);
            GUI.color = saved;
            // Too narrow for a name: its row below has the numbers.
            if (w > 44f) GUI.Label(new Rect(left + 4, y + 56, w - 4, 22), row.Short, _text);
            left += w;
        }

        GUI.Label(new Rect(x + value, y + 82, 70, 20), "<color=#999>per frame</color>", _right);
        GUI.Label(new Rect(x + share, y + 82, 50, 20), "<color=#999>share</color>", _right);
        GUI.Label(new Rect(x + graph, y + 82, 600, 20), "<color=#999>last 30 s   <color=#fc0>|</color> setting changed   <color=#e33>|</color> memory cleanup</color>", _text);

        var last = SegmentRows[SegmentRows.Length - 1];
        var line = 0;
        for (var i = 0; i < Rows.Length; i++)
        {
            if (i == SegmentRows[0] || i == last + 1) top += 8f;
            var row = Rows[i];
            Line(row.Color, row.Name, row.Shown, row.Share);
            GUI.DrawTexture(new Rect(x + graph, top + 1, Samples * 2, GraphHeight), row.Graph);
            GUI.Label(new Rect(x + tag, top + 2, 60, 20), row.Tag, _text);
            GUI.Label(new Rect(x + note, top + 2, width - note - 8, 20), row.ExtraShown, _text);
            top += rowHeight;
        }

        void Line(Color? color, string label, string shown, string part)
        {
            if (line++ % 2 == 1)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.05f);
                GUI.DrawTexture(new Rect(x + 4, top, width - 8, rowHeight), Texture2D.whiteTexture);
                GUI.color = saved;
            }

            if (color != null)
            {
                GUI.color = color.Value;
                GUI.DrawTexture(new Rect(x + 9, top + 6, 11, 11), Texture2D.whiteTexture);
                GUI.color = saved;
            }

            GUI.Label(new Rect(x + name, top + 2, 130, 20), label, _text);
            GUI.Label(new Rect(x + value, top + 2, 70, 20), shown, _right);
            GUI.Label(new Rect(x + share, top + 2, 50, 20), part, _right);
        }

        return y + height;
    }
}
