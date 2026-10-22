using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.CameraControl;
using GPUInstancer;
using Koenigz.PerfectCulling;
using Koenigz.PerfectCulling.EFT;
using HarmonyLib;
using SPTushonka.Reflection.Patching;
using UnityEngine;
using UnityEngine.Rendering;

namespace Smoothie;

// Static level objects (walls, buildings, cars, furniture, clutter) completely covered by
// nearer ones stop drawing. Most maps have no occlusion culling and the game's baked visibility
// is coarse: on Customs thousands of draw calls are objects behind nearer buildings. The test
// uses the depth the player just saw: the grass system (GPU Instancer) reduces it into a mip
// chain at the end of every frame, and right after that (a postfix on the build, so the copy
// queues on the GPU directly behind it) a ~180-wide level is copied back. A worker thread
// compares every object's screen box with it, and the result is applied in the main camera's
// pre-cull, the last moment before Unity picks what to draw. Hidden after two checks in a row
// find it covered, shown on the first one that doesn't: something coming into view appears a
// couple of frames late, and only when moving (turning doesn't change what covers what).
//
// A covered object is switched off with forceRenderingOff, its shadow with it, and so are the
// game's shadow-only meshes when covered (Plugin.HideShadows). With that off, shadows never
// change: a covered object that casts shadows becomes shadows-only (still in the shadow map,
// not on screen). Only renderers this switched itself are put back, and only if nobody changed
// them since. The game writes shadowCastingMode only on loot, player gear and at map load, none
// of which are tracked. Lamps whose whole lit area is covered get an empty culling mask the same
// way (Plugin.SkipCoveredLamps).
// Everything on screen counts as cover: the player's gun and hands, a bush in their face (the
// gun moving off a box shows it within a round). While aiming through a scope, what's in the
// optic camera's view (the main camera only sees the lens there) must also be covered in the
// optic camera's own depth, or it stays drawn.
internal static class DepthCulling
{
    // Nearer than this (m, the setting, taken each round) an object is always drawn: a late reveal
    // there would be large on screen. Was a fixed 10 m before covered boxes were grown by the eye's
    // movement (which is what made a near reveal late).
    private static float _minDistance = 3f;
    // Each box on screen grown by this many texels of the depth image on every side before the
    // main view's test (~0.5°): only what's covered with that much to spare hides, so moving
    // past cover shows what's behind its edge a little before it peeks out, not a round after.
    private const float BoxGrowTexels = 1f;
    // While moving, a covered box is grown further by how far its nearest cover's edge can slide
    // across it before the next result replaces this one (the eye's speed times how long results
    // take to land and be replaced, both measured): what's about to peek out past cover is drawn
    // before it does, not a few frames after. Never more than this angle (~20°): sprinting past a
    // corner a metre away needs about that for what's a few metres behind it.
    // ponytail: the gun and hands count as near cover here although they move with the eye, so
    // while moving the band along their outline hits this cap; masking their screen area would
    // keep that hidden.
    private const float MaxMoveGrowAngle = 0.35f;
    // Eye speeds above this (m/s) are a teleport or a vehicle, not a step.
    private const float MaxEyeSpeed = 15f;
    private static bool _scoped, _recheckAll;
    // The scope's area on the main screen this round (0-1), if aiming through one.
    private static bool _roundScoped;
    private static float _scopeXMin, _scopeYMin, _scopeXMax, _scopeYMax;
    // The scope's own depth: the optic camera's pyramid (the game's window rendering gives its
    // cameras one; added the same way if missing, only to a camera that renders depth), read
    // back whenever one isn't in flight. The latest is copied in for the next check.
    private static readonly DepthPyramid ScopePyramid = new();
    private static float[] _scopeDepth = Array.Empty<float>(), _scopeNext = Array.Empty<float>();
    private static int _scopeWidth, _scopeHeight, _scopeNextWidth, _scopeNextHeight, _scopeNextFrame = -1000, _scopeRequestFrame;
    private static Matrix4x4 _scopeViewProjection, _scopeNextViewProjection;
    private static bool _scopeReading, _roundScopeDepth;
    private static Camera? _scopeCamera;
    // Checks this round that used the scope's depth (overlay).
    internal static int ScopeTested;
    // Objects that showed last check are checked again only after about this long (spread a
    // little so they don't all come due at once); covered and hidden ones every round, so
    // showing is never slower, only hiding starts a little later.
    private const float VisibleRecheck = 0.25f;
    private const int HideAfter = 2;
    private const float CellSize = 32f;
    // A cell with at least this many units gets a test of its own box first: covered, all of
    // them are, without testing each.
    private const int CellTestMin = 6;
    private const byte CellOut = 0, CellIdle = 1, CellCovered = 2, CellTested = 3;
    // Per check: time spent re-asking objects the game wasn't drawing whether it draws them
    // now (one call per part; a crowd of them all coming due used to spend a millisecond
    // finding nothing), all asking (after a turn thousands are asked for the first time and
    // ~98% of them turn out not drawn: 7k asks were a 3 ms frame), and objects actually hidden
    // (a switch per part). Only hiding waits on these, never showing. The next check carries on
    // where this one stopped, so nothing waits behind the same few.
    private static readonly long RetryBudget = (long)(0.1 / Diagnostics.TickMs);
    private static readonly long AskBudget = (long)(0.3 / Diagnostics.TickMs);
    // The clock is read every this many asks, not per ask (under Wine a read is a system call).
    private const int AsksPerClock = 16;
    private const int HidesPerCheck = 600;
    private static int _applyFrom;
    // Covered objects in view that the last check had no budget left to look at (overlay).
    internal static int Waiting;
    // Covered and in view but not drawn by the game (its own culling): asked again after about
    // this long, or at once when the game's baked visibility switches its group back on
    // (BakedSwitchPatch), the usual way such an object starts being drawn.
    private const float NotDrawnRetry = 0.5f;
    private const float BakedOffRetry = 5f;
    private const int MaxWidth = 192;
    // The eye moved this far between the depth's frame and applying its result: show everything
    // (a fall, a vehicle, a teleport), the result no longer fits the view.
    private const float MaxDrift = 1f;

    // Everything the worker reads, fixed once the scan is done; Streak and Want are the worker's.
    private sealed class Set
    {
        public Renderer[][] Renderers = null!;
        public Vector3[] Min = null!, Max = null!;
        // Units sorted by 32 m cell; cell c holds units CellStart[c] to CellStart[c + 1] - 1,
        // and its box holds all of theirs.
        public int[] CellStart = null!;
        public Vector3[] CellMin = null!, CellMax = null!;
        public int[] CellOf = null!;
        // What the last check made of each cell (debug view): CellOut, CellIdle, CellCovered, CellTested.
        public byte[] CellState = null!;
        // Out of view at its last check with nothing in it covered or hidden: stays so until it's
        // checked in view again (only that marks anything covered, and only that gets hidden), so
        // it isn't walked meanwhile. The worker's.
        public bool[] CellQuiet = null!;
        public byte[] Streak = null!;
        // Why each unit is or isn't hidden after the last round (the census reads it).
        public byte[] Why = null!;
        // When a unit is due for its next check, and for a covered one the eye it was checked
        // from and how far that eye may move before the nearest cover could shift by a texel
        // (squared). The worker's.
        public float[] NextCheck = null!;
        public Vector3[] CheckedFrom = null!;
        public float[] Slack = null!;
        public Dictionary<Renderer, int> UnitOf = null!;
        public readonly List<int> Want = new();
        // Of those, the ones Apply may try to hide: in view, not hidden yet, not waiting to
        // retry. Built by the worker, so Apply doesn't walk every covered object.
        public readonly List<int> Try = new();
        // Of the wanted ones, hidden ones out of view whose last check no longer holds (the eye
        // moved): kept hidden until they come back into view (StaleUnits). Built by the worker.
        public readonly List<int> Stale = new();
        // The round each unit was last wanted hidden (written with Want), and what Hide
        // switched off (main thread).
        public int[] WantedIn = null!;
        // Hidden: the unit's renderers, else null. IsHidden: the same as 1 or 0, for the walk (CellWalk).
        public Renderer[]?[] Off = null!;
        public byte[] IsHidden = null!;
        // Per renderer: the shadow mode it had (made shadows-only), ShadowsOnly when it was
        // switched off with forceRenderingOff instead, or NotSwitched. Made on a unit's first
        // hide and reused.
        public ShadowCastingMode[]?[] Was = null!;
        public bool[] Saving = null!;
        // Covered but not drawn by the game anyway (its baked visibility): left alone until
        // then. 0 when it hasn't been asked since it last showed.
        public float[] RetryAt = null!;
        // The part found drawn last time (a LOD group draws one level at a time): asked first.
        public byte[] DrawnPart = null!;
        // The game's baked visibility group holding all of a unit's parts, if one does: while
        // it's switched off the game draws none of them, which Apply can tell without asking.
        public PerfectCullingBakeGroup?[] BakeGroup = null!;
        // What tells, without asking, that the game draws none of a unit now (GameSkips): the
        // layer all its parts are on (-1 mixed), EFT's own distance culler switching all of
        // them, and for a LOD group its reference point, world size (0 none) and the screen
        // height below which its last level is culled.
        public sbyte[] Layer = null!;
        public CullingObject?[] Culler = null!;
        public Vector3[] LodRef = null!;
        public float[] LodSize = null!, LodLast = null!;
    }

    private static readonly List<Renderer[]> PendingRenderers = new();
    private static readonly List<Bounds> PendingBounds = new();
    private static readonly List<sbyte> PendingLayer = new();
    private static readonly List<(Vector3 Ref, float Size, float Last)> PendingLod = new();
    private static Set? _set;
    private static readonly List<int> HiddenUnits = new();

    private const int Idle = 0, Reading = 1, Testing = 2, Ready = 3;
    private static int _state;
    private static int _epoch, _roundEpoch, _roundId, _requestFrame;
    // The readback in flight. Unity asks the render thread about it once a frame, at the start,
    // and reads the answer only at the next start: a copy the GPU finished early in a frame landed
    // two frames later. Asking again in Update and the main camera's pre-cull (Poll) gets the
    // answer within the frame. IL2CPP: Unity's callback takes an Il2CppSystem.Action of a struct
    // Il2CppInterop can't make, so Poll checks the request and calls the handler itself.
    private static AsyncGPUReadbackRequest _pending, _scopePending;
    private static Action<AsyncGPUReadbackRequest>? _pendingDone, _scopeDone;
    // A new depth is asked for at most this often (the slowest round rate before results landed
    // sooner), so landing sooner never means more rounds.
    private const int MinRoundFrames = 4;
    // How the depth copy travels (averages, overlay): frames and ms from asking for it to
    // having it, how old a result is in frames when it's applied, frames between results.
    private static float _arriveFrames, _arriveMs, _appliedAge, _resultEvery;
    private static int _lastApplyFrame;
    // The same two in seconds, and the eye's speed (m/s) along the camera's right, up and forward
    // axes, the positive and negative directions apart (each up at once, down slowly): how far the
    // eye travels before the next result lands is what a round grows covered boxes by.
    private static float _appliedSeconds, _resultSeconds, _lastApplyTime;
    private static double _speedTime = -1.0;
    private static int _speedFrame;
    private static Vector3 _speedEye, _eyeSpeedPlus, _eyeSpeedMinus;
    private static bool _resetStreaks;
    private static Exception? _error;

    // The main camera's view as it was drawn last frame (captured in its onPreCull).
    private static Camera? _camera;
    private static GPUInstancerHiZOcclusionGenerator? _generator;
    // The one added on a map without grass: switched off with the setting (so it costs
    // nothing then), back on with it. The game's own is never touched.
    private static GPUInstancerHiZOcclusionGenerator? _added;
    private static Matrix4x4 _viewProjection, _projection;
    private static Vector3 _viewEye;
    private static int _viewFrame = -1;
    // The last frame the setting was on in a raid (Tick ran): the camera hooks do nothing otherwise.
    private static int _activeFrame = -10;

    // The round on the worker: the view and depth it tests against.
    private static float[] _depth = Array.Empty<float>();
    private static int _width, _height;
    private static Matrix4x4 _roundViewProjection;
    private static Vector3 _roundEye;
    private static float _roundTime, _roundGameTime, _p22, _p23, _texelAngle;
    // How far (m) the eye may travel right, up and forward before this round's result is replaced,
    // the view's half-width and half-height as tangents, one texel across as an angle, and how
    // many covered boxes the reach kept drawn (overlay).
    private static Vector3 _roundReach, _roundReachPlus, _roundReachMinus;
    private static float _tanX, _tanY, _texelAngleX;
    // Of the boxes kept drawn for movement this round: the widest growth (texels) and the nearest
    // cover it was sized from (m). Overlay, to tell a fast eye from near cover.
    private static float _movingGrow, _movingCover;
    // Whatever the player holds (gun, knife, grenade, meds; nothing with empty hands): its parts'
    // renderers, fetched again when the item, the hands controller or the item's hierarchy changes
    // (attachments changed in a raid), put on the round's screen part by part for the
    // pyramid's gear mask (DepthPyramid.AddGear). A part not listed yet only gets the full margin.
    private static GameObject? _gearRoot;
    private static object? _gearHands;
    private static int _gearHierarchy;
    private static Renderer[] _gearParts = Array.Empty<Renderer>();
    internal static int GearParts;
    internal static int MovingKept;

    // Lamps: the level's spot and point lights that don't move. One whose lit area (its range,
    // for a spot only the cone) is covered wherever it's on screen lights nothing you can see: it
    // gets an empty culling mask (no object lit, nothing drawn into its shadow map) until any of
    // that area may show, and the frame a turn brings in a part that wasn't checked. The game
    // never writes a level light's culling mask (only the weapon-modding preview, on its own
    // lights). Boxes and positions are taken at the scan and again one lamp per round (main
    // thread); Streak and Want are the worker's; the rest the main thread's.
    private static Light[] _lamps = Array.Empty<Light>();
    private static Vector3[] _lampMin = Array.Empty<Vector3>(), _lampMax = Array.Empty<Vector3>(), _lampAt = Array.Empty<Vector3>();
    private static bool[] _lampWant = Array.Empty<bool>(), _lampOff = Array.Empty<bool>();
    private static byte[] _lampStreak = Array.Empty<byte>();
    // As objects' NextCheck, CheckedFrom and Slack (TestLamps). The worker's.
    private static float[] _lampNextCheck = Array.Empty<float>(), _lampSlack = Array.Empty<float>();
    private static Vector3[] _lampCheckedFrom = Array.Empty<Vector3>();
    // Its whole lit area was on screen when last found covered (TestLamps). The worker's.
    private static bool[] _lampWhole = Array.Empty<bool>();
    private static int[] _lampMaskWas = Array.Empty<int>();
    // Whether each lamp is on (active and enabled), asked LampsPerRound at a time: Streets has
    // ~3,800 lamps, and asking all of them every round cost the main thread ~0.4 ms. A lamp the
    // game just switched on is only skipped once it's been asked again, about a second later.
    private static bool[] _lampActive = Array.Empty<bool>();
    private static int _lampCursor;
    private const int LampsPerRound = 256;
    // Lamps in groups of LampGroup neighbours (sorted by LampCell at the scan), each with a box
    // holding all of theirs: a group wholly out of view is judged at once. Most of Streets' lamps
    // are out of view, and asking each cost a round about as much as all its objects' tests.
    private const int LampGroup = 32;
    private static Vector3[] _lampGroupMin = Array.Empty<Vector3>(), _lampGroupMax = Array.Empty<Vector3>();

    private static long LampCell(Light light)
    {
        var p = light.transform.position;
        return ((long)Mathf.FloorToInt(p.x / CellSize) << 32) + Mathf.FloorToInt(p.z / CellSize);
    }

    private static void TakeLampGroup(int g)
    {
        var from = g * LampGroup;
        var to = Math.Min(_lamps.Length, from + LampGroup);
        Vector3 min = _lampMin[from], max = _lampMax[from];
        for (var l = from + 1; l < to; l++)
        {
            min = Vector3.Min(min, _lampMin[l]);
            max = Vector3.Max(max, _lampMax[l]);
        }

        _lampGroupMin[g] = min;
        _lampGroupMax[g] = max;
    }
    // A lamp's lit area on the round's screen (0-1, may reach past it; the worker's), and for a
    // skipped one as its result was applied, with that round's view: a turn may bring in the part
    // past the screen's edge, which wasn't checked (KeepLampsInView).
    private static Vector4[] _lampRect = Array.Empty<Vector4>(), _lampSkipRect = Array.Empty<Vector4>();
    // Per lamp its transform (static lamps: asked once), and the lamps switched off at the last
    // result, so the per-frame view check walks those (~4.6k on Streets) instead of all (~11k).
    private static Transform?[] _lampTransform = Array.Empty<Transform?>();
    private static int[] _offList = Array.Empty<int>();
    private static int _offCount;
    private static Matrix4x4 _lampView;
    private static int _lampsPastEdge;
    private static bool _roundLamps;
    internal static int LampsOff;
    private static readonly DepthPyramid Pyramid = new();
    internal static int Tested, Cells, CellsOut, CellsCovered;
    // Found covered in the last check (hidden or not: the game may not be drawing them anyway).
    internal static int CoveredCount;

    internal static int Tracked;
    internal static int Hidden;
    internal static int Saving;
    // Main-thread cost: average per frame over the last second, and the worst frame in it.
    internal static double Ms, PeakMs;
    private static double _secondTicks, _secondPeak;
    private static int _secondFrames;
    private static float _secondStart;
    internal static double WorkerMs;
    // Checks done this raid, and what the next one is waiting for (overlay and snapshot).
    internal static int Rounds;
    internal static string Status = "waiting for the scan";
    private static long _frameTicks;
    // This frame's main-thread time by part (applying results, reading the depth copy,
    // asking for it) and what applying did; the worst frame of each second is kept for the
    // overlay, so a spike says what it was.
    private const int PartApply = 0, PartRead = 1, PartRequest = 2, PartLamps = 3;
    private static readonly long[] PartTicks = new long[4];
    private const float SpikeMs = 2f;
    private static int _frameHides, _frameShows, _frameAsks, _frameEntering, _frameLampOns, _frameLampOffs;
    private static float _lastSpikeLog;
    internal static string PeakNote = "";
    private static string _secondPeakNote = "";

    internal static void Enable()
    {
        Camera.onPreCull += (System.Action<Camera>)PreCull;
        Camera.onPreRender += (System.Action<Camera>)StartAfterCull;
        Camera.onPostRender += (System.Action<Camera>)DrawCells;
        new PyramidBuiltPatch(nameof(GPUInstancerHiZOcclusionGenerator.OnEndCameraRendering)).Enable();
        new PyramidBuiltPatch(nameof(GPUInstancerHiZOcclusionGenerator.OnEndCameraRenderingBlit)).Enable();
        new BakedSwitchPatch().Enable();
        LoadNative();
    }

    // SmoothieNative.dll (the depth pyramid with SSE2, next to this plugin) loaded by its full
    // path, as the plugin's folder isn't where Windows looks; the pyramid's DllImport by name then
    // finds it loaded. Missing or not answering: the pyramid's own C# code is used.
    internal static string NativeNote = "not loaded";

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    private static void LoadNative()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(DepthCulling).Assembly.Location)!, "SmoothieNative.dll");
        if (!System.IO.File.Exists(path)) NativeNote = "SmoothieNative.dll missing, C# fallback";
        else if (LoadLibraryW(path) == IntPtr.Zero) NativeNote = $"SmoothieNative.dll didn't load (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}), C# fallback";
        else NativeNote = DepthPyramid.TryNative() ? "native (SSE2)" : "SmoothieNative.dll didn't answer, C# fallback";
        Plugin.Log.LogInfo($"hidden objects: depth checks {NativeNote}");
    }

    // The grass system just built this frame's depth pyramid (one of the two, depending on
    // the GPU path): ask for the copy now, so it runs on the GPU right behind the build.
    private class PyramidBuiltPatch : ModulePatch
    {
        private readonly string _method;
        public PyramidBuiltPatch(string method) => _method = method;
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(GPUInstancerHiZOcclusionGenerator), _method);

        [PatchPostfix]
        private static void Postfix(GPUInstancerHiZOcclusionGenerator __instance, Camera camera)
        {
            try
            {
                if (_scopeCamera != null && ReferenceEquals(camera, _scopeCamera)) RequestScope(__instance, camera);
                else Request(__instance, camera);
            }
            catch (Exception ex)
            {
                _error ??= ex;
            }
        }
    }

    // The scan: once per raid, 10 s in, a slice per frame (its total time is logged).
    private static IEnumerator<bool>? _scan;
    private static float _scanAt = -1f;
    private static bool _scanned;
    private static long _scanTicks;
    private static readonly long ScanSlice = (long)(2.0 / Diagnostics.TickMs);
    internal static double ScanMs;
    private static int _skipLayers;
    private static HashSet<Renderer> _gameSwitched = new(Diagnostics.ReferenceComparer.Instance);

    internal static void Scan()
    {
        if (_scanned) return;
        var now = Time.time;
        if (_scanAt < 0f) _scanAt = now + 10f;
        if (now < _scanAt) return;
        _scan ??= ScanLevel();
        var started = Stopwatch.GetTimestamp();
        var more = _scan.MoveNext();
        _scanTicks += Stopwatch.GetTimestamp() - started;
        if (more) return;
        _scan = null;
        _scanned = true;
        ScanMs = _scanTicks * Diagnostics.TickMs;
        Seal();
    }

    // Every static mesh of the level, shadow-only ones included: not on a player, loot, sky,
    // border-wall or trigger layer, not switched by the game's distance stand-ins (or anyone) with forceRenderingOff, and nothing
    // that moves or can be used. A LOD group is one object (all its levels hide together).
    private static IEnumerator<bool> ScanLevel()
    {
        var sliceStart = Stopwatch.GetTimestamp();
        var gameSwitched = new HashSet<Renderer>(Diagnostics.ReferenceComparer.Instance);
        var standIns = new HashSet<Renderer>(Diagnostics.ReferenceComparer.Instance);
        foreach (var switcher in UnityEngine.Object.FindObjectsByType<Koenigz.PerfectCulling.EFT.ScreenDistanceSwitcher>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            // Its own children: forceRenderingOff when the culling sampler isn't running (Show).
            gameSwitched.UnionWith(switcher.GetComponentsInChildren<Renderer>(true));
            // Its far stand-in (the baked LOD group) only ever gets renderer.enabled (Show) and
            // its object SetActive: hiding it with forceRenderingOff is add-only like any object,
            // so it's tracked, unless it sits under the switcher (then the line above has it).
            if (switcher._controlLodGroup != null && !switcher._controlLodGroup.transform.IsChildOf(switcher.transform))
                standIns.UnionWith(switcher._controlLodGroup.GetComponentsInChildren<Renderer>(true));
            foreach (var content in switcher.GetComponents<Koenigz.PerfectCulling.EFT.PerfectCullingCrossSceneContent>())
            {
                if (content.BakeGroups == null) continue;
                foreach (var group in content.BakeGroups)
                {
                    if (group?.renderers == null) continue;
                    foreach (var renderer in group.renderers)
                    {
                        if (renderer != null) gameSwitched.Add(renderer);
                    }
                }
            }
        }

        var skipLayers = 0;
        foreach (var layer in new[] { "Loot", "Interactive", "PlayerRenderers", "Player", "Deadbody", "Sky", "LevelBorder", "Triggers" })
        {
            var index = LayerMask.NameToLayer(layer);
            if (index >= 0) skipLayers |= 1 << index;
        }

        _skipLayers = skipLayers;
        standIns.ExceptWith(gameSwitched);
        _gameSwitched = gameSwitched;
        var moving = new Dictionary<Transform, bool>(Diagnostics.ReferenceComparer.Instance);
        var groupsSeen = new HashSet<LODGroup>(Diagnostics.ReferenceComparer.Instance);
        var unit = new List<Renderer>();
        var all = 0;
        foreach (var renderer in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if ((++all & 63) == 0 && Stopwatch.GetTimestamp() - sliceStart > ScanSlice)
            {
                yield return true;
                sliceStart = Stopwatch.GetTimestamp();
            }

            if (renderer == null || !Candidate(renderer) || Moves(renderer.transform, moving)) continue;
            var bounds = renderer.bounds;
            unit.Clear();
            var lod = renderer.GetComponentInParent<LODGroup>();
            if (lod == null) unit.Add(renderer);
            else
            {
                if (!groupsSeen.Add(lod)) continue;
                var ok = true;
                foreach (var level in lod.GetLODs())
                {
                    foreach (var member in level.renderers)
                    {
                        if (member == null || unit.Contains(member)) continue;
                        // A group the game switches is not ours; billboards are left alone.
                        if (gameSwitched.Contains(member) || member.forceRenderingOff)
                        {
                            ok = false;
                            break;
                        }

                        if (member is not MeshRenderer || !Candidate(member)) continue;
                        bounds.Encapsulate(member.bounds);
                        unit.Add(member);
                    }

                    if (!ok) break;
                }

                if (!ok || unit.Count == 0) continue;
            }

            // No part switched on (helper cubes, blockers): never drawn, so asking about it every
            // half second only ever found that out. One the game switches on later is just never hidden.
            var anyOn = standIns.Contains(unit[0]);
            foreach (var part in unit)
            {
                if (!part.enabled) continue;
                anyOn = true;
                break;
            }

            if (!anyOn) continue;

            // The rain's drop mesh claims endless bounds so it's never culled: nothing to cover.
            if (!(bounds.extents.sqrMagnitude < MaxExtent * MaxExtent)) continue;

            PendingRenderers.Add(unit.ToArray());
            PendingBounds.Add(bounds);
            var layer = unit[0].gameObject.layer;
            foreach (var part in unit)
            {
                if (part.gameObject.layer != layer) layer = -1;
            }

            PendingLayer.Add((sbyte)layer);
            var lods = lod != null ? lod.GetLODs() : null;
            // A disabled LOD group (the game's stand-ins) does no LOD culling of its own.
            if (lods is { Length: > 0 } && lod!.enabled && lods[lods.Length - 1].screenRelativeTransitionHeight > 0f)
            {
                var t = lod!.transform;
                var scale = t.lossyScale;
                PendingLod.Add((t.TransformPoint(lod.localReferencePoint), lod.size * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)),
                    lods[lods.Length - 1].screenRelativeTransitionHeight));
            }
            else PendingLod.Add((Vector3.zero, 0f, 0f));
            Tracked++;
        }

        var lamps = new List<Light>();
        foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if ((light.type == LightType.Spot || light.type == LightType.Point) && OfLevel(light)
                && (skipLayers & (1 << light.gameObject.layer)) == 0 && !Moves(light.transform, moving)) lamps.Add(light);
        }

        // Neighbours next to each other, so a group of them can be judged out of view at once (TestLamps).
        var lampArray = lamps.ToArray();
        var cells = new long[lampArray.Length];
        for (var l = 0; l < cells.Length; l++) cells[l] = LampCell(lampArray[l]);
        Array.Sort(cells, lampArray);
        SetLamps(lampArray);
    }

    // Part of the map, not of a camera rig the game keeps across scenes: the item icon camera
    // (IconCreatorBase: DontDestroyOnLoad at (0, -10, -1), lit by lights on it and its parents)
    // and preview rigs light only their own camera's view, which the depth here knows nothing
    // about. Skipped as "covered", their icons rendered black (and stayed cached).
    private static bool OfLevel(Component light) =>
        light.gameObject.scene.name != "DontDestroyOnLoad" && light.GetComponentInChildren<Camera>(true) == null;

    // A round on the worker reads the lamp arrays: they're only swapped between rounds.
    // (A report from Interchange: arrays replaced while a round still ran, an index past the new
    // ones.) Its results were for the old arrays, so they're dropped.
    private static void WaitForRound()
    {
        if (Volatile.Read(ref _state) != Testing) return;
        SpinWait.SpinUntil(() => Volatile.Read(ref _state) != Testing, 250);
        _epoch++;
    }

    private static void SetLamps(Light[] lamps)
    {
        WaitForRound();
        RestoreLamps();
        _lamps = lamps;
        var n = lamps.Length;
        _lampMin = new Vector3[n];
        _lampMax = new Vector3[n];
        _lampAt = new Vector3[n];
        _lampWant = new bool[n];
        _lampOff = new bool[n];
        _lampStreak = new byte[n];
        _lampNextCheck = new float[n];
        _lampSlack = new float[n];
        _lampCheckedFrom = new Vector3[n];
        _lampWhole = new bool[n];
        _lampMaskWas = new int[n];
        _lampActive = new bool[n];
        _lampCursor = 0;
        _lampRect = new Vector4[n];
        _lampSkipRect = new Vector4[n];
        _lampTransform = new Transform?[n];
        _lampGroupMin = new Vector3[(n + LampGroup - 1) / LampGroup];
        _lampGroupMax = new Vector3[_lampGroupMin.Length];
        _offList = new int[n];
        _offCount = 0;
        _lampsPastEdge = 0;
        for (var l = 0; l < n; l++)
        {
            _lampTransform[l] = lamps[l] != null ? lamps[l].transform : null;
            TakeLampBox(l);
            _lampActive[l] = lamps[l] != null && lamps[l].isActiveAndEnabled;
        }

        for (var g = 0; g < _lampGroupMin.Length; g++) TakeLampGroup(g);
    }

    // A lamp's box and where it was taken: at the scan, then one lamp per round (a lamp that moved
    // anyway is never skipped, ApplyLamps compares with _lampAt).
    private static void TakeLampBox(int l)
    {
        var light = _lamps[l];
        if (light == null) return;
        LampBox(light, out _lampMin[l], out _lampMax[l]);
        _lampAt[l] = light.transform.position;
    }

    // The space a lamp can light: its range around it; for a spot only the cone out to its range
    // (a box around the apex and the far end's disk), unless it's so wide the sphere is smaller.
    private static void LampBox(Light light, out Vector3 min, out Vector3 max)
    {
        var t = light.transform;
        var p = t.position;
        var r = light.range;
        min = p - Vector3.one * r;
        max = p + Vector3.one * r;
        if (light.type != LightType.Spot || light.spotAngle >= 120f) return;
        var end = p + t.forward * r;
        var disk = Vector3.one * (r * Mathf.Tan(light.spotAngle * 0.5f * Mathf.Deg2Rad));
        min = Vector3.Max(min, Vector3.Min(p, end - disk));
        max = Vector3.Min(max, Vector3.Max(p, end + disk));
    }

    // Main thread, after a result is applied. A lamp is switched off only while its last round
    // wants it and it hasn't moved since; everything else gets its mask back (unless someone
    // changed it meanwhile). Whether a lamp already off moved is asked every LampMoveTurns-th
    // result only: transform.position is two calls into the engine, and Streets keeps 3-4k
    // lamps off, which cost ~0.5 ms a result. ponytail: a lamp that moves while off stays off
    // up to LampMoveTurns results (~30 frames); per-lamp move tracking if that ever shows.
    private const int LampMoveTurns = 8;
    private static int _lampMoveTurn;
    // Switching lamps off is capped per result (a turn wanted ~3.5k at once; a switch-off costs
    // ~6 µs, 300 were 2 ms), nearest first; the rest wait for the next result (they light as
    // before meanwhile). Switching them back on is never capped.
    private const int LampOffsPerResult = 100;
    private static float[] _offDistance = Array.Empty<float>();
    private static int[] _offLamp = Array.Empty<int>();

    private static void ApplyLamps(bool fits)
    {
        var on = fits && _roundLamps && Plugin.On(Plugin.SkipCoveredLamps);
        int off = 0, candidates = 0;
        _offCount = 0;
        var turn = _lampMoveTurn = (_lampMoveTurn + 1) % LampMoveTurns;
        _lampView = _roundViewProjection;
        if (_offLamp.Length < _lamps.Length)
        {
            _offDistance = new float[_lamps.Length];
            _offLamp = new int[_lamps.Length];
        }

        for (var l = 0; l < _lamps.Length; l++)
        {
            // Most lamps: not skipped and not wanted, nothing to do (no call into the engine).
            // Wanted implies in view this round (TestLamps resets the streak of one out of view).
            var wanted = on && _lampWant[l];
            if (!wanted && !_lampOff[l]) continue;
            if (wanted && !_lampOff[l])
            {
                // To switch off: after this loop, nearest first.
                _offDistance[candidates] = (_lampAt[l] - _roundEye).sqrMagnitude;
                _offLamp[candidates++] = l;
                continue;
            }

            // Already off and still wanted, not its turn to be asked whether it moved: stays off,
            // untouched (~4.6k lamps a result on Streets). Destroyed meanwhile: found on its turn.
            if (wanted && l % LampMoveTurns != turn)
            {
                off++;
                KeepRect(l);
                continue;
            }

            var light = _lamps[l];
            if (Gone(light))
            {
                _lampOff[l] = false;
                continue;
            }

            // Already off: still wanted, unless it moved (asked every LampMoveTurns-th result).
            if (!wanted || (_lampTransform[l]!.position - _lampAt[l]).sqrMagnitude >= 1e-4f)
            {
                if (light.cullingMask == 0) light.cullingMask = _lampMaskWas[l];
                _lampOff[l] = false;
                _frameLampOns++;
                continue;
            }

            off++;
            KeepRect(l);
        }

        if (candidates > LampOffsPerResult) Array.Sort(_offDistance, _offLamp, 0, candidates);
        for (var c = 0; c < candidates && c < LampOffsPerResult; c++)
        {
            var l = _offLamp[c];
            var light = _lamps[l];
            if (Gone(light) || (_lampTransform[l]!.position - _lampAt[l]).sqrMagnitude >= 1e-4f || light.cullingMask == 0) continue;
            _lampMaskWas[l] = light.cullingMask;
            light.cullingMask = 0;
            _lampOff[l] = true;
            _frameLampOffs++;
            off++;
            KeepRect(l);
        }

        LampsOff = off;
        _lampsPastEdge = _offCount;
    }

    // Destroyed: what Unity's == ends up checking for a Light, without the managed comparison
    // around it (~4.6k lamps a result).
    private static bool Gone(Light light) => light is null || SceneSkip.NativeOf(light) == IntPtr.Zero;

    // A skipped lamp's checked area, for KeepLampsInView: only those reaching past the screen's
    // edge are listed (_offList), as only they can come into view by turning.
    private static void KeepRect(int l)
    {
        _lampSkipRect[l] = _lampRect[l];
        if (!OnScreen(_lampRect[l])) _offList[_offCount++] = l;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool OnScreen(Vector4 rect) => rect.x >= 0f && rect.y >= 0f && rect.z <= 1f && rect.w <= 1f;

    // Every frame before culling, while a skipped lamp's lit area reached past the screen's edge
    // when it was checked: where the view is now, on that check's screen (the current view's
    // corners 50 m out, put through that check's view; a turn moves them, a step barely). If the
    // lamp's area there reaches past what was checked, its light is put back before this frame is
    // drawn, so a turn never shows an unlit area. Turned around more than that: all of them back.
    private static void KeepLampsInView(Camera camera)
    {
        float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
        var everywhere = false;
        var m = _lampView;
        for (var c = 0; c < 4; c++)
        {
            var p = camera.ViewportToWorldPoint(new Vector3(c & 1, c >> 1, 50f));
            var w = m.m30 * p.x + m.m31 * p.y + m.m32 * p.z + m.m33;
            if (w <= 0.01f)
            {
                everywhere = true;
                break;
            }

            var sx = (m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03) / w * 0.5f + 0.5f;
            var sy = (m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13) / w * 0.5f + 0.5f;
            xMin = Math.Min(xMin, sx);
            xMax = Math.Max(xMax, sx);
            yMin = Math.Min(yMin, sy);
            yMax = Math.Max(yMax, sy);
        }

        // Up to a texel of the depth image past the checked edge is let through, as every box is
        // grown by a texel anyway: without it rounding, weapon sway and breathing put such lamps
        // back every frame.
        float slackX = BoxGrowTexels / _width, slackY = BoxGrowTexels / _height;
        var pastEdge = 0;
        for (var k = 0; k < _offCount; k++)
        {
            var l = _offList[k];
            if (!_lampOff[l]) continue;
            var r = _lampSkipRect[l];
            float x0 = Math.Max(r.x, xMin), y0 = Math.Max(r.y, yMin), x1 = Math.Min(r.z, xMax), y1 = Math.Min(r.w, yMax);
            if (everywhere || x0 < x1 && y0 < y1 && (x0 < -slackX || y0 < -slackY || x1 > 1f + slackX || y1 > 1f + slackY))
            {
                ReleaseLamp(l);
                LampsOff--;
            }
            else pastEdge++;
        }

        _lampsPastEdge = pastEdge;
    }

    // Its mask back, unless someone changed it since.
    private static void ReleaseLamp(int l)
    {
        if (!_lampOff[l]) return;
        _lampOff[l] = false;
        var light = _lamps[l];
        if (light != null && light.cullingMask == 0) light.cullingMask = _lampMaskWas[l];
        _frameLampOns++;
    }

    private static void RestoreLamps()
    {
        for (var l = 0; l < _lamps.Length; l++)
        {
            ReleaseLamp(l);
        }

        Array.Clear(_lampStreak, 0, _lampStreak.Length);
        LampsOff = _lampsPastEdge = 0;
    }

    // Not renderer.enabled: the game's room zones and baked visibility switch level objects
    // with it, so both apply and neither undoes the other.
    private static bool Candidate(Renderer renderer) =>
        (_skipLayers & (1 << renderer.gameObject.layer)) == 0
        && !renderer.forceRenderingOff
        && (_gameSwitched.Count == 0 || !_gameSwitched.Contains(renderer));

    // Plugin.HideShadows: a covered object is switched off whole, its shadow with it, and the
    // game's shadow-only meshes (tracked like any other object) too. Off, a covered object only
    // stops drawing on screen and shadow-only meshes are left alone.
    private static bool HidesShadows => Plugin.On(Plugin.HideShadows);

    // Anything that can move or be used: physics bodies, animated parts, doors and other
    // interactive objects, players, trains (Reserve, Lighthouse: static props moved by script).
    // Remembered per transform, since objects share parents.
    internal static bool Moves(Transform transform, Dictionary<Transform, bool> known)
    {
        if (transform == null) return false;
        if (known.TryGetValue(transform, out var moves)) return moves;
        moves = transform.TryGetComponent<Rigidbody>(out _)
            || transform.TryGetComponent<Animator>(out _)
            || transform.TryGetComponent<Animation>(out _)
            || transform.TryGetComponent<EFT.Interactive.InteractableObject>(out _)
            || transform.TryGetComponent<Player>(out _)
            || transform.TryGetComponent<EFT.MovingPlatforms.MovingPlatform>(out _)
            || Moves(transform.parent, known);
        known[transform] = moves;
        return moves;
    }

    private static string NotTracked(Renderer renderer)
    {
        if ((_skipLayers & (1 << renderer.gameObject.layer)) != 0) return $"not tracked: on a left-out layer ({LayerMask.LayerToName(renderer.gameObject.layer)})";
        if (renderer is not MeshRenderer) return "not tracked: not a mesh renderer";
        if (_gameSwitched.Contains(renderer)) return "not tracked: switched by the game's distance stand-ins";
        if (Moves(renderer.transform, new Dictionary<Transform, bool>(Diagnostics.ReferenceComparer.Instance))) return "not tracked: moves or can be used";
        var lod = renderer.GetComponentInParent<LODGroup>();
        if (lod == null) return renderer.enabled ? "not tracked: appeared after the scan" : "not tracked: switched off at the scan";
        foreach (var member in lod.GetComponentsInChildren<Renderer>(true))
        {
            if (_gameSwitched.Contains(member)) return "not tracked: LOD group with a part switched by the game's distance stand-ins";
            if (member.forceRenderingOff) return "not tracked: LOD group with a part switched off by someone else";
        }

        return "not tracked: LOD group switched off at the scan, or appeared after it";
    }

    // The scan is done: freeze what it found.
    internal static void Seal()
    {
        var count = PendingRenderers.Count;

        // Sorted by cell, so each cell's units sit together and a cell can be skipped whole.
        var keys = new long[count];
        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            var c = PendingBounds[i].center;
            var size = PendingBounds[i].size;
            // Bigger than a cell: a cell of its own (keys of real cells are never negative), so
            // it doesn't stretch a cell of small objects that would otherwise skip together.
            keys[i] = size.x > CellSize || size.y > CellSize || size.z > CellSize
                ? -1L - i
                : ((long)Mathf.FloorToInt(c.x / CellSize) & 0x1FFFFF) << 42 | ((long)Mathf.FloorToInt(c.y / CellSize) & 0x1FFFFF) << 21 | ((long)Mathf.FloorToInt(c.z / CellSize) & 0x1FFFFF);
            order[i] = i;
        }

        Array.Sort(keys, order);
        var renderers = new Renderer[count][];
        for (var i = 0; i < count; i++) renderers[i] = PendingRenderers[order[i]];
        var set = new Set
        {
            Renderers = renderers,
            Min = new Vector3[count],
            Max = new Vector3[count],
            Streak = new byte[count],
            Why = new byte[count],
            NextCheck = new float[count],
            CheckedFrom = new Vector3[count],
            Slack = new float[count],
            UnitOf = new Dictionary<Renderer, int>(count * 2, Diagnostics.ReferenceComparer.Instance),
            WantedIn = new int[count],
            Off = new Renderer[count][],
            IsHidden = new byte[count],
            Was = new ShadowCastingMode[count][],
            Saving = new bool[count],
            RetryAt = new float[count],
            DrawnPart = new byte[count],
            Layer = new sbyte[count],
            Culler = new CullingObject?[count],
            LodRef = new Vector3[count],
            LodSize = new float[count],
            LodLast = new float[count],
        };
        var starts = new List<int>();
        var cellMin = new List<Vector3>();
        var cellMax = new List<Vector3>();
        for (var i = 0; i < count; i++)
        {
            var bounds = PendingBounds[order[i]];
            set.Min[i] = bounds.min;
            set.Max[i] = bounds.max;
            set.Layer[i] = PendingLayer[order[i]];
            (set.LodRef[i], set.LodSize[i], set.LodLast[i]) = PendingLod[order[i]];
            if (i == 0 || keys[i] != keys[i - 1])
            {
                starts.Add(i);
                cellMin.Add(bounds.min);
                cellMax.Add(bounds.max);
            }
            else
            {
                var last = cellMin.Count - 1;
                cellMin[last] = Vector3.Min(cellMin[last], bounds.min);
                cellMax[last] = Vector3.Max(cellMax[last], bounds.max);
            }

            foreach (var renderer in set.Renderers[i])
            {
                // Destroyed during the scan (it runs over several seconds): Hide skips it.
                if (renderer != null) set.UnitOf[renderer] = i;
            }
        }

        // The baked groups live on each scene's PerfectCullingCrossSceneGroup (see the census).
        set.BakeGroup = new PerfectCullingBakeGroup?[count];
        var inGroup = new int[count];
        foreach (var crossScene in UnityEngine.Object.FindObjectsByType<PerfectCullingCrossSceneGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (crossScene.bakeGroups == null) continue;
            foreach (var group in crossScene.bakeGroups)
            {
                if (group?.renderers == null) continue;
                foreach (var renderer in group.renderers)
                {
                    if (renderer == null || !set.UnitOf.TryGetValue(renderer, out var i)) continue;
                    // Parts in different groups: -1, never counted as wholly in one.
                    if (set.BakeGroup[i] == null && inGroup[i] == 0) set.BakeGroup[i] = group;
                    else if (!ReferenceEquals(set.BakeGroup[i], group)) inGroup[i] = -1;
                    if (inGroup[i] >= 0) inGroup[i]++;
                }
            }
        }

        for (var i = 0; i < count; i++)
        {
            var parts = 0;
            foreach (var renderer in set.Renderers[i])
            {
                if (renderer != null) parts++;
            }

            if (inGroup[i] != parts) set.BakeGroup[i] = null;
        }

        // EFT's own distance culling: the renderers each culler switches (listed, or under a
        // listed object). A unit counts only when one culler switches all of its parts.
        var culled = new int[count];
        foreach (var culler in UnityEngine.Object.FindObjectsByType<CullingObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (culler._componentsToTurnOff != null)
            {
                foreach (var component in culler._componentsToTurnOff)
                {
                    if (component is Renderer renderer) CountCuller(set, culled, culler, renderer);
                }
            }

            if (culler._gameObjectsToTurnOff == null) continue;
            foreach (var go in culler._gameObjectsToTurnOff)
            {
                if (go == null) continue;
                foreach (var renderer in go.GetComponentsInChildren<Renderer>(true)) CountCuller(set, culled, culler, renderer);
            }
        }

        for (var i = 0; i < count; i++)
        {
            if (set.Culler[i] is not null && culled[i] != set.Renderers[i].Length) set.Culler[i] = null;
        }

        starts.Add(count);
        set.CellStart = starts.ToArray();
        set.CellMin = cellMin.ToArray();
        set.CellMax = cellMax.ToArray();
        set.CellState = new byte[set.CellMin.Length];
        set.CellQuiet = new bool[set.CellMin.Length];
        set.CellOf = new int[count];
        for (var c = 0; c < set.CellMin.Length; c++)
        for (var i = set.CellStart[c]; i < set.CellStart[c + 1]; i++) set.CellOf[i] = c;

        PendingRenderers.Clear();
        PendingBounds.Clear();
        PendingLayer.Clear();
        PendingLod.Clear();
        _set = set;
        _native = SceneSkip.Check();

        int withCuller = 0, withLod = 0, layerCulled = 0;
        var cull = Camera.main != null ? System.Linq.Enumerable.ToArray(Camera.main.layerCullDistances) : new float[32];
        for (var i = 0; i < count; i++)
        {
            if (set.Culler[i] is not null) withCuller++;
            if (set.LodSize[i] > 0f) withLod++;
            if (set.Layer[i] >= 0 && set.Layer[i] < cull.Length && cull[set.Layer[i]] > 0f) layerCulled++;
        }

        Plugin.Log.LogInfo($"hidden objects: tracking {count} level objects in {set.CellMin.Length} cells, scan took {ScanMs:0} ms; " +
                           $"{withCuller} switched by EFT's distance culling, {withLod} LOD groups with a culled last level, {layerCulled} on a layer with a cull distance");
    }

    private const float MaxExtent = 5000f;

    private static void CountCuller(Set set, int[] culled, CullingObject culler, Renderer renderer)
    {
        if (renderer == null || !set.UnitOf.TryGetValue(renderer, out var i) || culled[i] < 0) return;
        if (set.Culler[i] is null) set.Culler[i] = culler;
        // Parts under different cullers: never counted as one's.
        if (ReferenceEquals(set.Culler[i], culler)) culled[i]++;
        else culled[i] = -1;
    }

    // The main camera, right before culling: apply the last result if the worker is done, and
    // keep this frame's view for the depth the grass system builds at its end.
    private static void PreCull(Camera camera)
    {
        var set = _set;
        if (set == null || !ReferenceEquals(camera, _camera) || Time.frameCount - _activeFrame > 1) return;
        _inPreCull = true;
        Poll(); // Read times itself
        _inPreCull = false;
        // Timed only while something shows or logs it (Diagnostics.Measuring).
        var timing = Diagnostics.Measuring;
        var start = timing ? Stopwatch.GetTimestamp() : 0;
        try
        {
            if (Volatile.Read(ref _state) == Ready && _error == null)
            {
                _state = Idle;
                if (_roundEpoch == _epoch)
                {
                    var applyStart = timing ? Stopwatch.GetTimestamp() : 0;
                    Apply(set, camera.transform.position);
                    var lampStart = timing ? Stopwatch.GetTimestamp() : 0;
                    PartTicks[PartApply] += lampStart - applyStart;
                    var fits = (camera.transform.position - _roundEye).sqrMagnitude <= MaxDrift * MaxDrift;
                    ApplyLamps(fits);
                    if (timing) PartTicks[PartLamps] += Stopwatch.GetTimestamp() - lampStart;
                    _appliedAge = _appliedAge * 0.9f + (Time.frameCount - _requestFrame) * 0.1f;
                    if (_lastApplyFrame > 0) _resultEvery = _resultEvery * 0.9f + (Time.frameCount - _lastApplyFrame) * 0.1f;
                    _lastApplyFrame = Time.frameCount;
                    var applied = Time.realtimeSinceStartup;
                    // Up at once, down slowly: the margin is for the slow rounds, not the average one.
                    _appliedSeconds = Rise(_appliedSeconds, applied - _roundTime);
                    if (_lastApplyTime > 0f) _resultSeconds = Rise(_resultSeconds, applied - _lastApplyTime);
                    _lastApplyTime = applied;
                }
            }

            if (StaleUnits.Count > 0)
            {
                var staleStart = timing ? Stopwatch.GetTimestamp() : 0;
                ShowEntering(set, camera);
                if (timing) PartTicks[PartApply] += Stopwatch.GetTimestamp() - staleStart;
            }

            if (_lampsPastEdge > 0)
            {
                var keepStart = timing ? Stopwatch.GetTimestamp() : 0;
                KeepLampsInView(camera);
                if (timing) PartTicks[PartLamps] += Stopwatch.GetTimestamp() - keepStart;
            }

            _projection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            _viewProjection = _projection * camera.worldToCameraMatrix;
            _viewEye = camera.transform.position;
            _viewFrame = Time.frameCount;
            // Once a frame (a second render of the camera in the same frame would read as a jump).
            if (_speedFrame != Time.frameCount)
            {
                _speedFrame = Time.frameCount;
                var now = Time.realtimeSinceStartupAsDouble;
                if (_speedTime > 0.0 && now > _speedTime)
                {
                    var v = camera.transform.InverseTransformDirection(_viewEye - _speedEye) / (float)(now - _speedTime);
                    _eyeSpeedPlus = new Vector3(TrackSpeed(_eyeSpeedPlus.x, v.x), TrackSpeed(_eyeSpeedPlus.y, v.y), TrackSpeed(_eyeSpeedPlus.z, v.z));
                    _eyeSpeedMinus = new Vector3(TrackSpeed(_eyeSpeedMinus.x, -v.x), TrackSpeed(_eyeSpeedMinus.y, -v.y), TrackSpeed(_eyeSpeedMinus.z, -v.z));
                }

                _speedEye = _viewEye;
                _speedTime = now;
            }
        }
        catch (Exception ex)
        {
            _error ??= ex;
        }
        finally
        {
            if (timing) _frameTicks += Stopwatch.GetTimestamp() - start;
        }
    }

    // Main thread, while no check runs (the pyramid is the worker's then).
    private static void TakeGear(ref Matrix4x4 m)
    {
        Pyramid.ClearGear();
        GearParts = 0;
        var hands = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer?.HandsController;
        var root = hands != null ? hands.ControllerGameObject : null;
        if (root == null) return;
        var hierarchy = root.transform.hierarchyCount;
        if (!ReferenceEquals(root, _gearRoot) || !ReferenceEquals(hands, _gearHands) || hierarchy != _gearHierarchy)
        {
            _gearRoot = root;
            _gearHands = hands;
            _gearHierarchy = hierarchy;
            var parts = new List<Renderer>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is MeshRenderer or SkinnedMeshRenderer) parts.Add(renderer);
            }

            _gearParts = parts.ToArray();
        }

        // Only parts Unity drew last frame: only those are in the depth image (one that just came on
        // counts as world cover for a round, which only keeps more drawn).
        foreach (var part in _gearParts)
        {
            if (!Drawn(part)) continue;
            var bounds = part.bounds;
            if (!Project(bounds.min, bounds.max, ref m, out var xMin, out var yMin, out var xMax, out var yMax, out var near, out var far)) continue;
            Pyramid.AddGear(xMin, yMin, xMax, yMax, near, far);
            GearParts++;
        }
    }

    // Speed in one direction: up at once (a sprint starts), down slowly (a pause in a stride isn't a stop).
    private static float TrackSpeed(float was, float velocity) => Rise(was, Math.Min(Math.Max(velocity, 0f), MaxEyeSpeed));

    private static float Rise(float was, float value) => value > was ? value : was * 0.9f + value * 0.1f;

    // Every frame in raid while the setting is on: keeps track of the main camera and hands
    // errors from the camera callbacks and the worker to the caller (which switches it off).
    internal static void Tick()
    {
        var frameMs = _frameTicks * Diagnostics.TickMs;
        _frameTicks = 0;
        _secondTicks += frameMs;
        if (frameMs > _secondPeak)
        {
            _secondPeak = frameMs;
            _secondPeakNote = $"applying {PartTicks[PartApply] * Diagnostics.TickMs:0.00} ({_frameHides} hidden, {_frameShows} shown of which {_frameEntering} entering the view, {_frameAsks} asked), " +
                              $"lamps {PartTicks[PartLamps] * Diagnostics.TickMs:0.00} ({_frameLampOffs} off, {_frameLampOns} back on), " +
                              $"reading {PartTicks[PartRead] * Diagnostics.TickMs:0.00}, asking {PartTicks[PartRequest] * Diagnostics.TickMs:0.00}";
        }

        Array.Clear(PartTicks, 0, PartTicks.Length);
        // A spike on its own line (the snapshot keeps only the last second's worst).
        if (frameMs >= SpikeMs && Diagnostics.Logging && Time.unscaledTime - _lastSpikeLog > 1f)
        {
            _lastSpikeLog = Time.unscaledTime;
            Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} hiding took {frameMs:0.00} ms on the main thread: {_secondPeakNote}");
        }

        _frameHides = _frameShows = _frameAsks = _frameEntering = _frameLampOns = _frameLampOffs = 0;
        _secondFrames++;
        if (Time.unscaledTime - _secondStart >= 1f)
        {
            Ms = _secondTicks / _secondFrames;
            PeakMs = _secondPeak;
            PeakNote = _secondPeakNote;
            _secondTicks = _secondPeak = 0;
            _secondFrames = 0;
            _secondStart = Time.unscaledTime;
        }
        if (_set == null) return;
        // The scope's view switching on: what the gun covered may now be seen through it. The
        // round in flight (judged without the scope) is dropped and everything is checked again
        // in the next one; what the scope's depth then shows is shown, while the game is still
        // fading the lens in. Not shown all at once: that flashed everything back on every aim.
        var scoped = Scoped(out _, out var opticCamera);
        _scopeCamera = scoped ? ScopeCamera(opticCamera) : null;
        if (scoped != _scoped)
        {
            _scoped = scoped;
            if (scoped)
            {
                _epoch++;
                _recheckAll = true;
            }
        }

        _activeFrame = Time.frameCount;
        if (_added != null && !_added.enabled) _added.enabled = true;
        var camera = CameraManager.Instance.Camera;
        if (!ReferenceEquals(camera, _camera))
        {
            _camera = camera;
            _viewFrame = -1;
            _generator = camera != null ? Generator(camera) : null;
        }

        if (_error != null)
        {
            var error = _error;
            _error = null;
            _state = Idle;
            throw new InvalidOperationException("hiding covered objects failed", error);
        }

        if (camera == null) Status = "no camera";
        else if (_state == Reading && Time.frameCount - _requestFrame > 60)
        {
            // A readback that never came back: start over (its late answer is ignored).
            _state = Idle;
            Status = "a readback never came back";
        }
        else if (_state == Idle && Time.frameCount - _requestFrame > 60)
        {
            Status = _generator == null ? "no depth pyramid on this map" : _viewFrame < 0 ? "the camera's view was never captured" : "the depth pyramid isn't built";
        }
        else
        {
            StartAfterCull(camera);
            Poll();
        }
#if DEBUG
        if (_testNote is { } note)
        {
            _testNote = null;
            Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} {note}");
        }
#endif
    }

    // The camera's depth pyramid generator: the grass system's, or on maps without grass
    // (Factory, Labs) the same one added here and left on the camera. Picked up only after the
    // scan, 10 s into the raid, when the grass system has long added its own.
    private static GPUInstancerHiZOcclusionGenerator Generator(Camera camera)
    {
        var generator = camera.GetComponent<GPUInstancerHiZOcclusionGenerator>();
        if (generator != null)
        {
            if (ReferenceEquals(generator, _added)) generator.enabled = true;
            return generator;
        }

        generator = AddGenerator(camera, out var how);
        _added = generator;
        Plugin.Log.LogInfo($"hidden objects: no grass on this map, so its depth pyramid generator was added to the camera ({how})");
        return generator;
    }

    // A depth pyramid generator on a camera that has none (the main camera on a map without
    // grass, a scope camera).
    private static GPUInstancerHiZOcclusionGenerator AddGenerator(Camera camera, out string how)
    {
        // The grass system's settings, which its managers load when they start: on a map
        // without grass nothing has, and the generator can't set itself up without them.
        if (GPUInstancerConstants.gpuiSettings == null) GPUInstancerConstants.gpuiSettings = GPUInstancerSettings.GetDefaultGPUInstancerSettings();
        var generator = camera.gameObject.AddComponent<GPUInstancerHiZOcclusionGenerator>();
        // It builds the pyramid with a blit shader or with compute shaders, per the game's
        // settings. The blit shader may not be loaded on a map without grass; the compute
        // shaders always are (built-in resources), so it's switched over then. Both feed the
        // grass system's own culling, so the pyramid reads the same either way.
        var switched = generator.occlusionCullingType == GPUIOcclusionCullingType.Default && generator._depthBlitMaterial == null;
        if (switched)
        {
            generator.enabled = false;
            generator.occlusionCullingType = GPUIOcclusionCullingType.CopyTexture;
            generator.enabled = true;
        }

        generator.Initialize(camera);
        how = switched ? "blit shader not loaded, using compute shaders" : generator.occlusionCullingType.ToString();
        return generator;
    }

    // The optic camera, once it has a depth pyramid of its own; null (its view stays drawn)
    // if it renders no depth texture: its pyramid would be built from another camera's.
    private static Camera? ScopeCamera(Camera camera)
    {
        if (camera.actualRenderingPath != RenderingPath.DeferredShading && (camera.depthTextureMode & DepthTextureMode.Depth) == 0) return null;
        if (camera.GetComponent<GPUInstancerHiZOcclusionGenerator>() == null)
        {
            _scopeAdded = AddGenerator(camera, out _);
            // Sized to this camera's own picture: its anti-aliasing component reports the main
            // screen's size, which isn't what this camera renders (its depth is its pixel size).
            _scopeAdded._ssaa = null;
        }

        return camera;
    }

    // The scope camera's generator if we added it (only ours is made to look again).
    private static GPUInstancerHiZOcclusionGenerator? _scopeAdded;

    // Right after the scope's depth pyramid is built: read back if none is in flight.
    private static void RequestScope(GPUInstancerHiZOcclusionGenerator generator, Camera camera)
    {
        // As it was just drawn (ScopeArea reads it too: at other times the camera may be elsewhere).
        _scopeRenderView = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix;
        _scopeCamToWorld = camera.cameraToWorldMatrix;
        _scopeProjection = camera.projectionMatrix;
        _scopeRenderFrame = Time.frameCount;
        // One in flight at a time; one that never came back is given up on after 30 frames.
        if (_set == null || _scopeReading && Time.frameCount - _scopeRequestFrame < 30) return;
        var texture = generator.hiZDepthTexture;
        if (texture == null) return;
        // Built from this camera's own depth? The generator keeps the first depth texture it
        // finds, and on the scope camera that has been the main camera's: ours is made to look
        // again every frame, and the pyramid is only used when it isn't the main camera's.
        var source = generator.unityDepthTexture;
        var mainSource = _generator != null ? _generator.unityDepthTexture : null;
        if (ReferenceEquals(generator, _scopeAdded)) generator.unityDepthTexture = null;
        // The source alternates between the scope's own depth and the main camera's from one
        // frame to the next: only the scope's own frames are used.
        if (source == null || ReferenceEquals(source, mainSource)
            || source.width != (int)generator.hiZTextureSize.x || source.height != (int)generator.hiZTextureSize.y) return;
        var mip = 0;
        while (mip < texture.mipmapCount - 1 && (texture.width >> mip) > MaxWidth) mip++;
        var width = Mathf.Max(1, texture.width >> mip);
        var height = Mathf.Max(1, texture.height >> mip);
        var viewProjection = _scopeRenderView;
        _scopeReading = true;
        _scopeRequestFrame = Time.frameCount;
        _scopePending = Readback(texture, mip);
        _scopeDone = request =>
        {
            _scopeReading = false;
            if (request.hasError || !CopyReadback(request, width * height, ref _scopeNext)) return;
            _scopeNextWidth = width;
            _scopeNextHeight = height;
            _scopeNextViewProjection = viewProjection;
            _scopeNextFrame = Time.frameCount;
        };
    }

    // IL2CPP: GetData<T> wraps Unity's NativeArray in an interop object; the raw pointer copies
    // straight into the array. False if the readback isn't the expected size.
    private static unsafe bool CopyReadback(AsyncGPUReadbackRequest request, int count, ref float[] into)
    {
        if (request.layerDataSize != count * sizeof(float)) return false;
        if (into.Length != count) into = new float[count];
        System.Runtime.InteropServices.Marshal.Copy(request.GetDataRaw(0), into, 0, count);
        return true;
    }

    // IL2CPP: the game's build stripped AsyncGPUReadback's C# side (no game script uses it, and every
    // Request overload is a stub that throws); the engine's internal call underneath is still there,
    // and called directly here. Its C# wrapper only added a format check and the callback, which the
    // polling here doesn't need. RFloat is R32_SFloat.
    private unsafe delegate void RequestTextureCall(IntPtr src, int mipIndex, UnityEngine.Experimental.Rendering.GraphicsFormat dstFormat,
        IntPtr nativeArrayData, AsyncGPUReadbackRequest* request);

    private static RequestTextureCall? _requestTexture;

    private static unsafe AsyncGPUReadbackRequest Readback(Texture texture, int mip)
    {
        _requestTexture ??= Il2CppInterop.Runtime.IL2CPP.ResolveICall<RequestTextureCall>(
            "UnityEngine.Rendering.AsyncGPUReadback::Request_Internal_Texture_2_Injected");
        AsyncGPUReadbackRequest request = default;
        _requestTexture(Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtrNotNull(texture), mip,
            UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat, IntPtr.Zero, &request);
        return request;
    }

    private static void Poll()
    {
        Poll(ref _pending, ref _pendingDone);
        Poll(ref _scopePending, ref _scopeDone);
    }

    private static void Poll(ref AsyncGPUReadbackRequest request, ref Action<AsyncGPUReadbackRequest>? done)
    {
        if (done == null) return;
        request.Update();
        if (!request.done) return;
        var handler = done;
        done = null;
        handler(request);
    }

    // A depth read at the main camera's pre-cull starts its check once the cull is over (its
    // onPreRender; Tick if the camera never got there): started at once, the check's lanes would
    // compete with Unity's culling jobs, which the main thread waits for.
    private static bool _inPreCull;
    private static Set? _afterCull;

    private static void StartAfterCull(Camera camera)
    {
        if (_afterCull is not { } set || !ReferenceEquals(camera, _camera)) return;
        _afterCull = null;
        if (_state == Testing) Task.Run(() => Test(set));
    }

    // Right after the grass system built this frame's depth pyramid.
    private static void Request(GPUInstancerHiZOcclusionGenerator generator, Camera camera)
    {
        if (_set == null || _state != Idle || !ReferenceEquals(camera, _camera) || _viewFrame != Time.frameCount
            || Time.frameCount - _activeFrame > 1 || Time.frameCount - _requestFrame < MinRoundFrames) return;
        var texture = generator.hiZDepthTexture;
        if (texture == null) return;
        var start = Diagnostics.Measuring ? Stopwatch.GetTimestamp() : 0;
        _generator = generator;
        var mip = 0;
        while (mip < texture.mipmapCount - 1 && (texture.width >> mip) > MaxWidth) mip++;
        _width = Mathf.Max(1, texture.width >> mip);
        _height = Mathf.Max(1, texture.height >> mip);
        _roundViewProjection = _viewProjection;
        _roundEye = _viewEye;
        TakeGameCulling(camera);
        TakeGear(ref _roundViewProjection);
        _minDistance = Plugin.HideBeyond.Value;
        _roundScoped = ScopeArea();
        _p22 = _projection.m22;
        _p23 = _projection.m23;
        // One texel of the depth image, as an angle: how far the nearest cover may shift.
        _texelAngle = 2f / (Mathf.Abs(_projection.m11) * _height);
        var latency = _appliedSeconds + _resultSeconds;
        _roundReachPlus = Vector3.Min(_eyeSpeedPlus * latency, Vector3.one * MaxDrift);
        _roundReachMinus = Vector3.Min(_eyeSpeedMinus * latency, Vector3.one * MaxDrift);
        _roundReach = Vector3.Max(_roundReachPlus, _roundReachMinus);
        _roundLamps = Plugin.On(Plugin.SkipCoveredLamps);
        var lampCount = _lamps.Length;
        if (lampCount > 0)
        {
            var retaken = _roundId % lampCount;
            TakeLampBox(retaken);
            TakeLampGroup(retaken / LampGroup);
            for (var k = Math.Min(LampsPerRound, lampCount); k > 0; k--)
            {
                var light = _lamps[_lampCursor];
                _lampActive[_lampCursor] = light != null && light.isActiveAndEnabled;
                if (++_lampCursor >= lampCount) _lampCursor = 0;
            }
        }

        _tanX = 1f / Mathf.Abs(_projection.m00);
        _tanY = 1f / Mathf.Abs(_projection.m11);
        _texelAngleX = 2f * _tanX / _width;
        _roundEpoch = _epoch;
        _roundTime = Time.realtimeSinceStartup;
        _roundGameTime = Time.time;
        _requestFrame = Time.frameCount;
        _state = Reading;
        var id = ++_roundId;
        _pending = Readback(texture, mip);
        _pendingDone = request => Read(request, id);
        if (start == 0) return;
        var took = Stopwatch.GetTimestamp() - start;
        _frameTicks += took;
        PartTicks[PartRequest] += took;
    }

    // Debug view (Plugin.ShowCells, in Release too: static level objects only), through walls, within DebugRange, in view: every object by
    // its last result (red hidden, yellow something behind it shows, green kept drawn while moving). Covered objects the game isn't
    // drawing anyway aren't shown.
    private const float DebugRange = 150f;
    private static Material? _lineMaterial;

    private static void DrawCells(Camera camera)
    {
        var set = _set;
        if (set == null || !ReferenceEquals(camera, _camera) || !Plugin.On(Plugin.ShowCells)) return;
        if (_lineMaterial == null)
        {
            _lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            _lineMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _lineMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _lineMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
            _lineMaterial.SetInt("_ZWrite", 0);
            _lineMaterial.SetInt("_Cull", (int)CullMode.Off);
        }

        var eye = camera.transform.position;
        _lineMaterial.SetPass(0);
        GL.PushMatrix();
        GL.Begin(GL.LINES);
        for (var c = 0; c < set.CellState.Length; c++)
        {
            if (set.CellState[c] == CellOut || !InRange(set.CellMin[c], set.CellMax[c], eye)) continue;
            for (var i = set.CellStart[c]; i < set.CellStart[c + 1]; i++)
            {
                if (!InRange(set.Min[i], set.Max[i], eye)) continue;
                var why = set.Why[i];
                if (set.Off[i] != null) GL.Color(new Color(1f, 0.1f, 0.1f, 0.55f));
                else if (why == WhyShowsBehind) GL.Color(new Color(1f, 0.9f, 0f, 0.18f));
                else if (why == WhyScopeShows) GL.Color(new Color(1f, 0.2f, 1f, 0.35f));
                else if (why == WhyScopeAhead) GL.Color(new Color(0.2f, 0.9f, 1f, 0.3f));
                else if (why == WhyScopeNoDepth) GL.Color(new Color(1f, 0.5f, 0f, 0.35f));
                else if (why == WhyMoving) GL.Color(new Color(0.2f, 1f, 0.3f, 0.35f));
                else continue;
                Box(set.Min[i], set.Max[i]);
            }
        }

        // Lamps switched off for lighting only covered space: their lit area in blue.
        GL.Color(new Color(0.3f, 0.5f, 1f, 0.5f));
        for (var l = 0; l < _lamps.Length; l++)
        {
            if (_lampOff[l] && InRange(_lampMin[l], _lampMax[l], eye)) Box(_lampMin[l], _lampMax[l]);
        }

        GL.End();
        GL.PopMatrix();
    }

    private static bool InRange(Vector3 min, Vector3 max, Vector3 eye) =>
        (Vector3.Max(min, Vector3.Min(eye, max)) - eye).sqrMagnitude < DebugRange * DebugRange;

    private static void Box(Vector3 a, Vector3 b)
    {
        for (var e = 0; e < 4; e++)
        {
            // The 4 edges along each axis.
            GL.Vertex3(a.x, (e & 1) == 0 ? a.y : b.y, (e & 2) == 0 ? a.z : b.z);
            GL.Vertex3(b.x, (e & 1) == 0 ? a.y : b.y, (e & 2) == 0 ? a.z : b.z);
            GL.Vertex3((e & 1) == 0 ? a.x : b.x, a.y, (e & 2) == 0 ? a.z : b.z);
            GL.Vertex3((e & 1) == 0 ? a.x : b.x, b.y, (e & 2) == 0 ? a.z : b.z);
            GL.Vertex3((e & 1) == 0 ? a.x : b.x, (e & 2) == 0 ? a.y : b.y, a.z);
            GL.Vertex3((e & 1) == 0 ? a.x : b.x, (e & 2) == 0 ? a.y : b.y, b.z);
        }
    }

    // The depth image as the overlay shows it (Plugin.ShowDepth): grey by distance, purple the
    // scope's area while aiming through one (checked against the scope's own depth too). Only
    // what's on screen anyway.
    internal static Texture2D? Preview;
    // The scope's own depth as the last check used it (shown next to Preview).
    internal static Texture2D? ScopePreview;
    private static Color32[] _scopePreviewPixels = Array.Empty<Color32>();
    private static Color32[] _previewPixels = Array.Empty<Color32>();

    private static void DrawPreview()
    {
        if (Preview == null || Preview.width != _width || Preview.height != _height)
        {
            if (Preview != null) UnityEngine.Object.Destroy(Preview);
            Preview = new Texture2D(_width, _height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            _previewPixels = new Color32[_width * _height];
        }

        var logFar = Mathf.Log(1000f);
        for (var r = 0; r < _height; r++)
        for (var x = 0; x < _width; x++)
        {
            var i = r * _width + x;
            var d = _depth[i];
            Color32 c;
            // Stored rows run the other way from the screen coordinates the area is in.
            var sx = (x + 0.5f) / _width;
            var sy = 1f - (r + 0.5f) / _height;
            if (_roundScoped && sx >= _scopeXMin && sx <= _scopeXMax && sy >= _scopeYMin && sy <= _scopeYMax) c = new Color32(140, 0, 140, 255);
            else
            {
                var distance = d + _p22 > 0f ? _p23 / (d + _p22) : 1000f;
                var b = (byte)(255f * Mathf.Clamp01(1f - Mathf.Log(Mathf.Max(distance, 1f)) / logFar));
                c = new Color32(b, b, b, 255);
            }

            // The stored first row is the bottom of the screen, as is the texture's.
            _previewPixels[i] = c;
        }

        Preview.SetPixels32(_previewPixels);
        Preview.Apply(false);
        if (!_roundScopeDepth)
        {
            // Not scoped (or no depth of its own): no stale picture next to the main one.
            if (ScopePreview != null) UnityEngine.Object.Destroy(ScopePreview);
            ScopePreview = null;
            return;
        }
        if (ScopePreview == null || ScopePreview.width != _scopeWidth || ScopePreview.height != _scopeHeight)
        {
            if (ScopePreview != null) UnityEngine.Object.Destroy(ScopePreview);
            ScopePreview = new Texture2D(_scopeWidth, _scopeHeight, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            _scopePreviewPixels = new Color32[_scopeWidth * _scopeHeight];
        }

        // Brighter is nearer (straight from the depth: enough to see the picture is the right
        // way up and lines up with the scope).
        for (var i = 0; i < _scopeDepth.Length; i++)
        {
            var b = (byte)(255f * Mathf.Clamp01(Mathf.Pow(Mathf.Max(_scopeDepth[i], 0f), 0.25f)));
            _scopePreviewPixels[i] = new Color32(b, b, b, 255);
        }

        ScopePreview.SetPixels32(_scopePreviewPixels);
        ScopePreview.Apply(false);
    }

    // Aiming through a scope: its sight and the optic camera drawing what it shows.
    private static bool Scoped(out OpticSight sight, out Camera opticCamera)
    {
        var optics = CameraManager.Instance.OpticCameraManager;
        sight = (optics != null && optics.IsAnyOpticCameraRendering ? optics.CurrentOpticSight : null)!;
        opticCamera = (optics != null ? optics.Camera : null)!;
        return sight != null && opticCamera != null && opticCamera.isActiveAndEnabled;
    }

    // While aiming through a scope: where on the main screen the world may show through it. The
    // optic camera doesn't see the gun, the hands or the scope, so what they cover on screen
    // may be in its view: its lens, and everything inside the optic camera's view (at _minDistance
    // and 1 km, for the parallax between it and the eye), grown by a texel. False without a scope.
    // Zoomed in, a small flick sweeps the scope across a lot, and what comes into it must
    // already be drawn rather than appear a few frames late. Around the scope's picture the
    // main view sees the scope itself (its tube, the gun), not the world, so its depth says
    // nothing there: ScopeCovers keeps drawn what's within ScopeMargin degrees of the scope's
    // view (the first frames of a flick, before its rate is known, a change of direction;
    // degrees, so the zoomed-in view, only a few degrees wide, gets a ring many times its own
    // size), and what it will reach ScopeLead seconds ahead at the rate it's turning (a result
    // is in use until about then), at most ScopeMaxLead degrees. A bounded cone around where
    // the scope points; outside it the main depth decides as everywhere else.
    private const float ScopeMargin = 5f;
    private const float ScopeLead = 0.1f;
    private const float ScopeMaxLead = 30f;
    private const float TurningShare = 0.1f;
    private static Quaternion _scopeRotation;
    private static float _scopeAt = -1f;
    // The optic camera's view this round and how far from its centre it may see soon, in its
    // viewport units (0.5 is its edge): the margin and the turn lead on every side.
    private static Matrix4x4 _roundScopeView, _scopeRenderView, _scopeCamToWorld, _scopeProjection;
    private static int _scopeRenderFrame = -1000;
    // For the status line: the optic camera's vertical view and this round's turn lead.
    internal static float ScopeFov, ScopeLeadDegrees;
    private static float _scopeReachX, _scopeReachY, _scopeShiftX, _scopeShiftY;
    // A turn of 90 degrees and more: everything may come into view.
    private const float MaxShift = 1000f;
    private static bool ScopeArea()
    {
        if (!Scoped(out var sight, out var opticCamera))
        {
            _scopeAt = -1f;
            return false;
        }

        // The turn since the last round, carried ScopeLead ahead: the eye's (the scope turns
        // with it), since the optic camera's own transform isn't steady from round to round.
        var rotation = _camera != null ? _camera.transform.rotation : opticCamera.transform.rotation;
        var now = Time.unscaledTime;
        var ahead = Quaternion.identity;
        var lead = 0f;
        if (_scopeAt >= 0f && now > _scopeAt)
        {
            (rotation * Quaternion.Inverse(_scopeRotation)).ToAngleAxis(out var angle, out var axis);
            if (angle > 180f) angle -= 360f;
            lead = Mathf.Clamp(angle / (now - _scopeAt) * ScopeLead, -ScopeMaxLead, ScopeMaxLead);
            if (Mathf.Abs(angle) > 0.01f) ahead = Quaternion.AngleAxis(lead, axis);
        }

        ScopeLeadDegrees = Mathf.Abs(lead);
        _scopeRotation = rotation;
        _scopeAt = now;
        // The optic camera as it last rendered: the game moves it into place for its own
        // render only, so read at any other time it can be somewhere else entirely.
        var fresh = Time.frameCount - _scopeRenderFrame <= 30;
        var camToWorld = fresh ? _scopeCamToWorld : opticCamera.cameraToWorldMatrix;
        var projection = fresh ? _scopeProjection : opticCamera.projectionMatrix;
        Vector3 origin = camToWorld.GetColumn(3);

        var m = _roundViewProjection;
        float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
        var lens = sight.LensRenderer;
        if (lens != null && lens.enabled && lens.isVisible && !lens.bounds.Contains(_roundEye))
        {
            // The lens's own box turned with it (the world-aligned one, this close to the eye,
            // covers far more of the screen than the lens).
            var min = lens.localBounds.min;
            var max = lens.localBounds.max;
            var toWorld = lens.localToWorldMatrix;
            for (var c = 0; c < 8; c++)
            {
                var p = toWorld.MultiplyPoint3x4(new Vector3((c & 1) == 0 ? min.x : max.x, (c & 2) == 0 ? min.y : max.y, (c & 4) == 0 ? min.z : max.z));
                Clip[c] = new Vector3(m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03, m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13, m.m30 * p.x + m.m31 * p.y + m.m32 * p.z + m.m33);
            }

            // Corners in front of the camera, and where the box's edges cross just in front of
            // it: a lens right at the eye gets its true area.
            for (var c = 0; c < 8; c++)
            {
                if (Clip[c].z > Front) Extend(Clip[c], ref xMin, ref yMin, ref xMax, ref yMax);
                for (var bit = 1; bit < 8; bit <<= 1)
                {
                    var d = c | bit;
                    if (d == c || (Clip[c].z > Front) == (Clip[d].z > Front)) continue;
                    var t = (Front - Clip[c].z) / (Clip[d].z - Clip[c].z);
                    Extend(Clip[c] + (Clip[d] - Clip[c]) * t, ref xMin, ref yMin, ref xMax, ref yMax);
                }
            }
        }

        // The view's edges ScopeMargin degrees further out, in viewport units (0-1 is the view).
        var halfV = Mathf.Atan(1f / projection.m11);
        var halfH = Mathf.Atan(1f / projection.m00);
        ScopeFov = 2f * halfV * Mathf.Rad2Deg;
        var margin = ScopeMargin * Mathf.Deg2Rad;
        var reachX = 0.5f * Mathf.Tan(Mathf.Min(halfH + margin, 1.4f)) / Mathf.Tan(halfH);
        var reachY = 0.5f * Mathf.Tan(Mathf.Min(halfV + margin, 1.4f)) / Mathf.Tan(halfV);
        _scopeReachX = reachX;
        _scopeReachY = reachY;
        _roundScopeView = fresh ? _scopeRenderView : GL.GetGPUProjectionMatrix(projection, true) * opticCamera.worldToCameraMatrix;
        // Where the scope's centre will be after the lead, in its current view: the reach only
        // grows that way (what's on the trailing side is leaving the scope).
        _scopeShiftX = _scopeShiftY = 0f;
        if (ahead != Quaternion.identity)
        {
            var v = _roundScopeView;
            var p = origin + ahead * (camToWorld.MultiplyVector(Vector3.back) * 100f);
            var w = v.m30 * p.x + v.m31 * p.y + v.m32 * p.z + v.m33;
            if (w > 0.01f)
            {
                _scopeShiftX = Mathf.Clamp((v.m00 * p.x + v.m01 * p.y + v.m02 * p.z + v.m03) / w * 0.5f, -MaxShift, MaxShift);
                _scopeShiftY = Mathf.Clamp((v.m10 * p.x + v.m11 * p.y + v.m12 * p.z + v.m13) / w * 0.5f, -MaxShift, MaxShift);
            }
            else _scopeShiftX = _scopeShiftY = MaxShift;
        }
        // Plus everything the scope sees now or may soon: its view grown by the margin and the
        // turn lead (a low zoom sees past the lens's edge; around the lens the main view sees
        // the scope's tube and the gun, so what's there is ScopeCovers' to judge).
        float nx0 = -2f * reachX + 2f * Math.Min(0f, _scopeShiftX), nx1 = 2f * reachX + 2f * Math.Max(0f, _scopeShiftX);
        float ny0 = -2f * reachY + 2f * Math.Min(0f, _scopeShiftY), ny1 = 2f * reachY + 2f * Math.Max(0f, _scopeShiftY);
        for (var c = 0; c < 8; c++)
        {
            var d = (c & 4) == 0 ? _minDistance : 1000f;
            // Camera space looks down -z.
            var p = camToWorld.MultiplyPoint3x4(new Vector3(((c & 1) == 0 ? nx0 : nx1) * d / projection.m00, ((c & 2) == 0 ? ny0 : ny1) * d / projection.m11, -d));
            var clip = new Vector3(m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03, m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13, m.m30 * p.x + m.m31 * p.y + m.m32 * p.z + m.m33);
            if (clip.z > Front) Extend(clip, ref xMin, ref yMin, ref xMax, ref yMax);
        }

        if (xMin > xMax) return false;
        // Grown by a texel.
        _scopeXMin = xMin - 1f / _width;
        _scopeXMax = xMax + 1f / _width;
        _scopeYMin = yMin - 1f / _height;
        _scopeYMax = yMax + 1f / _height;
        return true;
    }

    private const float Front = 0.01f;
    private static readonly Vector3[] Clip = new Vector3[8];

    private static void Extend(Vector3 clip, ref float xMin, ref float yMin, ref float xMax, ref float yMax)
    {
        var sx = clip.x / clip.z * 0.5f + 0.5f;
        var sy = clip.y / clip.z * 0.5f + 0.5f;
        xMin = Math.Min(xMin, sx);
        xMax = Math.Max(xMax, sx);
        yMin = Math.Min(yMin, sy);
        yMax = Math.Max(yMax, sy);
    }

    private static void Read(AsyncGPUReadbackRequest request, int id)
    {
        if (id != _roundId || _state != Reading) return;
        var set = _set;
        if (request.hasError || set == null || _roundEpoch != _epoch)
        {
            if (request.hasError) Status = "readback failed";
            _state = Idle;
            return;
        }

        var start = Diagnostics.Measuring ? Stopwatch.GetTimestamp() : 0;
        _arriveFrames = _arriveFrames * 0.9f + (Time.frameCount - _requestFrame) * 0.1f;
        _arriveMs = _arriveMs * 0.9f + (Time.realtimeSinceStartup - _roundTime) * 1000f * 0.1f;
        if (!CopyReadback(request, _width * _height, ref _depth))
        {
            Status = $"readback size {request.layerDataSize / sizeof(float)}, expected {_width}x{_height}";
            _state = Idle;
            return;
        }

        // The scope's latest depth, if it's from the last few frames.
        _roundScopeDepth = _roundScoped && Time.frameCount - _scopeNextFrame <= 4 && _scopeNext.Length == _scopeNextWidth * _scopeNextHeight;
        if (_roundScopeDepth)
        {
            if (_scopeDepth.Length != _scopeNext.Length) _scopeDepth = new float[_scopeNext.Length];
            Array.Copy(_scopeNext, _scopeDepth, _scopeNext.Length);
            _scopeWidth = _scopeNextWidth;
            _scopeHeight = _scopeNextHeight;
            _scopeViewProjection = _scopeNextViewProjection;
        }

        if (Plugin.On(Plugin.ShowDepth)) DrawPreview();
        if (_recheckAll)
        {
            Array.Clear(set.NextCheck, 0, set.NextCheck.Length);
            Array.Clear(_lampNextCheck, 0, _lampNextCheck.Length);
            _recheckAll = false;
        }

        if (_resetStreaks)
        {
            Array.Clear(set.Streak, 0, set.Streak.Length);
            Array.Clear(set.NextCheck, 0, set.NextCheck.Length);
            Array.Clear(_lampStreak, 0, _lampStreak.Length);
            Array.Clear(_lampNextCheck, 0, _lampNextCheck.Length);
            _resetStreaks = false;
        }

        _state = Testing;
        if (_inPreCull)
        {
            _afterCull = set;
#if DEBUG
            _testAfterCull++;
#endif
        }
        else Task.Run(() => Test(set));
        if (start == 0) return;
        var took = Stopwatch.GetTimestamp() - start;
        _frameTicks += took;
        PartTicks[PartRead] += took;
    }

    // On the worker: which units are covered, and which have been for HideAfter rounds.
    private static void Test(Set set)
    {
        try
        {
            var start = Stopwatch.GetTimestamp();
            var m = _roundViewProjection;
            Pyramid.Build(_depth, _width, _height);
            if (_roundScopeDepth) ScopePyramid.Build(_scopeDepth, _scopeWidth, _scopeHeight);
            long phase = start, pyramidTicks = 0, lanesTicks = 0;
            Stamp(ref pyramidTicks, ref phase);
            SetPlanes(Planes, ref m);
            TakePlanes();
            _testMatrix = m;
            foreach (var lane in Lanes) lane.Clear();
            _nextClaim = 0;
            Parallel.For(0, Lanes.Length, k => RunLane(set, Lanes[k]));
            Stamp(ref lanesTicks, ref phase);

            // Merged in lane order: each lane's lists hold its own units, in the order it found them.
            set.Want.Clear();
            set.Try.Clear();
            set.Stale.Clear();
            int tested = 0, outCells = 0, coveredCells = 0, scopeTested = 0, moving = 0, deep = 0, skipped = 0, lampsOn = 0, lampsInView = 0;
            float movingGrow = 0f, movingCover = float.MaxValue;
            foreach (var lane in Lanes)
            {
                set.Want.AddRange(lane.Want);
                set.Try.AddRange(lane.Try);
                set.Stale.AddRange(lane.Stale);
                tested += lane.Tested;
                outCells += lane.OutCells;
                coveredCells += lane.CoveredCells;
                scopeTested += lane.ScopeTested;
                moving += lane.Moving;
                deep += lane.Deep;
                skipped += lane.GameSkipped;
                lampsOn += lane.LampsOn;
                lampsInView += lane.LampsInView;
                movingGrow = Math.Max(movingGrow, lane.MovingGrow);
                movingCover = Math.Min(movingCover, lane.MovingCover);
            }

            _movingGrow = movingGrow;
            _movingCover = movingCover;
            var cells = set.CellStart.Length - 1;
            LampsOn = lampsOn;
            LampsInView = lampsInView;
            CellsOut = outCells;
            CellsCovered = coveredCells;
            Cells = cells;
            Tested = tested;
            CoveredCount = set.Want.Count;

            Rounds++;
            ScopeTested = scopeTested;
            MovingKept = moving;
            CoveredDeep = deep;
            GameSkipped = skipped;
            // The round's time and the status line are only shown or logged (Diagnostics.Measuring).
            if (Diagnostics.Measuring) WorkerMs = (Stopwatch.GetTimestamp() - start) * Diagnostics.TickMs;
            if (Diagnostics.Measuring) Status = $"{_width}x{_height} depth (arrives after {_arriveFrames:0.0} frames, {_arriveMs:0.0} ms; results applied {_appliedAge:0.0} frames old, one every {_resultEvery:0.0} frames), "
                     + $"{Tested} tested, depth checks {(DepthPyramid.UseNative ? "native" : "C#")} ({CoveredDeep} covered deep, checked again later; {GameSkipped} covered not asked about, the game draws none of them now); cells: {CellsOut} of {Cells} out of view, {CellsCovered} covered whole"
                     + (SqrDistance(_roundReach, default) > 0.0001f ? $"; moving {_roundReach.x:0.00} m sideways, {_roundReach.y:0.00} up, {_roundReach.z:0.00} forward before the next result, {MovingKept} kept drawn for it, {GearParts} parts of what you hold not counted as sliding cover" + (MovingKept > 0 ? $" (grown up to {_movingGrow:0.0} texels, cover as near as {_movingCover:0.0} m)" : "") : "")
                     + (_roundLamps ? $"; lamps: {LampsOff} of {_lamps.Length} skipped (their lit area covered)" : "")
                     + (_roundScoped ? _roundScopeDepth ? $"; scope: {_scopeWidth}x{_scopeHeight} depth, {ScopeTested} checked against it, view {ScopeFov:0.0}°, lead {ScopeLeadDegrees:0.0}°" : "; scope: no depth of its own, its view stays drawn" : "");
            TimeParts(pyramidTicks, lanesTicks, Stopwatch.GetTimestamp() - phase);
        }
        catch (Exception ex)
        {
            _error = ex;
        }

        Volatile.Write(ref _state, Ready);
    }

    // One lane: claims chunks of cells until none are left, then chunks of lamps.
    private static void RunLane(Set set, Lane lane)
    {
        var m = _testMatrix;
        var eye = _roundEye;
        var now = _roundTime;
        var round = _roundId;
        var cells = set.CellStart.Length - 1;
        var cellClaims = (cells + CellsPerClaim - 1) / CellsPerClaim;
        var lampClaims = (_lamps.Length + LampsPerClaim - 1) / LampsPerClaim;
        var claims = cellClaims + lampClaims;
        int claim;
        long since = 0;
        Mark(ref since);
        while ((claim = Interlocked.Increment(ref _nextClaim) - 1) < claims)
        {
            if (claim < cellClaims)
            {
                var end = Math.Min(cells, (claim + 1) * CellsPerClaim);
                for (var c = claim * CellsPerClaim; c < end; c++) TestCell(set, lane, c, eye, now, round, ref m);
                Stamp(ref lane.CellTicks, ref since);
                continue;
            }
            else
            {
                var from = (claim - cellClaims) * LampsPerClaim;
                TestLamps(lane, from, Math.Min(_lamps.Length, from + LampsPerClaim), ref m);
            }

            Stamp(ref lane.LampTicks, ref since);
        }
    }

    private static unsafe void TestCell(Set set, Lane lane, int c, Vector3 eye, float now, int round, ref Matrix4x4 m)
    {
        int from = set.CellStart[c], to = set.CellStart[c + 1], n = to - from;
        long since = 0;
        Mark(ref since);
        var cellMin = set.CellMin[c];
        var cellMax = set.CellMax[c];
        set.CellState[c] = CellOut;
        // Unity doesn't draw any of it.
        var outOfView = OutOfView(cellMin, cellMax);
        if (outOfView)
        {
            lane.OutCells++;
            if (set.CellQuiet[c]) return;
        }
        else set.CellQuiet[c] = false;

        var due = lane.Due;
        due.Clear();
        if (lane.Walk.Length < n * 3) lane.Walk = new int[n * 3];
        // Every object of every cell, every round: CellWalk (native when it can be), over the
        // arrays pinned for the call.
        fixed (float* retryAt = set.RetryAt, nextCheck = set.NextCheck, slack = set.Slack)
        fixed (Vector3* checkedFrom = set.CheckedFrom, min = set.Min, max = set.Max)
        fixed (byte* streak = set.Streak, hidden = set.IsHidden)
        fixed (int* wantedIn = set.WantedIn, walk = lane.Walk, counts = lane.Counts)
        fixed (Vector4* planes = Planes)
        {
            var units = new CellWalk.Units
            {
                RetryAt = retryAt, NextCheck = nextCheck, Slack = slack, CheckedFrom = (float*)checkedFrom, Min = (float*)min, Max = (float*)max,
                Streak = streak, Hidden = hidden, WantedIn = wantedIn,
            };
            if (outOfView)
            {
                // As KeepOutOfView for each object that's covered or hidden.
                set.CellQuiet[c] = CellWalk.OutOfViewCell(&units, from, to, (float*)&eye, round, HideAfter, walk, walk + n, counts);
                for (var k = 0; k < counts[0]; k++) lane.Want.Add(walk[k]);
                for (var k = 0; k < counts[1]; k++) lane.Stale.Add(walk[n + k]);
                Count(ref lane.WalkedOut, n);
                Count(ref lane.CellsWalked, 1);
                Stamp(ref lane.WalkTicks, ref since);
                return;
            }

            // Due? Showed: checked again after a while. Covered: turning changes nothing, so only
            // once the eye moved enough to shift its nearest cover by a texel (or after a while:
            // doors, bots). Covered but not drawn by the game when Apply last asked (most covered
            // objects): nothing to hide or show, so not checked until it's asked again; then it's
            // checked in that round, so the answer it's hidden on is fresh.
            CellWalk.InView(&units, from, to, (float*)&eye, now, _roundGameTime, round, (float*)planes, HideAfter, walk, walk + n, walk + n * 2, counts);
            for (var k = 0; k < counts[0]; k++) due.Add(walk[k]);
            // Wanted, not due (WantedIn already set). A cell is often only partly in view: only
            // what's in view itself is worth a try, and only one Apply will try (not hidden yet,
            // not waiting to retry); the walk lists those, GameSkips needs the managed objects.
            for (var k = 0; k < counts[1]; k++) lane.Want.Add(walk[n + k]);
            for (var k = 0; k < counts[2]; k++)
            {
                var i = walk[n * 2 + k];
                if (!GameSkips(set, lane, i)) lane.Try.Add(i);
            }
        }

        Count(ref lane.Walked, to - from);
        Count(ref lane.CellsWalked, 1);
        Count(ref lane.DueCount, due.Count);
        Stamp(ref lane.WalkTicks, ref since);
        set.CellState[c] = CellIdle;
        // Nothing in it due (all showed a moment ago, or covered with the eye still): skip.
        if (due.Count == 0) return;
        // Most cells are far: then none of their objects is near either.
        var cellNear = Near(cellMin, cellMax, eye);

        // The whole cell's box covered: so is every box inside it.
        if (due.Count >= CellTestMin && BoxWhy(lane, cellMin, cellMax, ref m) == WhyCovered)
        {
            lane.CoveredCells++;
            set.CellState[c] = CellCovered;
            var slack = lane.UsedScope ? 0f : CoveredSlack(lane);
            foreach (var i in due)
            {
                if (cellNear && Near(set.Min[i], set.Max[i], eye))
                {
                    set.Why[i] = WhyNear;
                    set.Streak[i] = 0;
                    continue;
                }

                // In view only matters for one Apply will try (hidden ones stay hidden).
                MarkCovered(set, lane, i, eye, now, slack, round, set.Off[i] == null && !OutOfView(set.Min[i], set.Max[i]));
            }

            return;
        }

        set.CellState[c] = CellTested;
        foreach (var i in due)
        {
            lane.Tested++;
            var why = cellNear && Near(set.Min[i], set.Max[i], eye) ? WhyNear : BoxWhy(lane, set.Min[i], set.Max[i], ref m);
            // While the scope's depth is late (a round without it every so often) the last
            // verdict stands: only what was last seen showing is kept drawn. Next to the
            // scope's picture it's always drawn: the last verdict there may have come from
            // the main depth seeing the scope itself.
            if (why == WhyScopeNoDepth && set.Streak[i] != 0) why = WhyCovered;
            if (why == WhyCovered)
            {
                MarkCovered(set, lane, i, eye, now, lane.UsedScope ? 0f : CoveredSlack(lane), round, true);
                continue;
            }

            // Hidden and now out of view, in a cell partly in view: kept like the ones in
            // cells out of view.
            if (why == WhyOutOfView && set.Off[i] != null)
            {
                KeepOutOfView(set, lane, i, eye, round);
                if (set.Streak[i] != 0) set.NextCheck[i] = now + VisibleRecheck * (0.75f + (i & 15) / 30f);
                continue;
            }

            if (why == WhyMoving) lane.Moving++;
            set.Why[i] = why;
            set.Streak[i] = 0;
            set.NextCheck[i] = now + VisibleRecheck * (0.75f + (i & 15) / 30f);
            // Covered again later, it's asked at once (Apply), not as a retry.
            set.RetryAt[i] = 0f;
        }
    }

    // Lamps: covered HideAfter rounds in a row (as objects), shown on the first that isn't.
    // Only the part on screen is checked; where the box lands is kept for the turn check. Checked
    // again on the objects' terms (Due): one found showing after VisibleRecheck; one found covered
    // after that too, or as soon as the eye moved far enough for its nearest cover to shift by a
    // texel (CoveredSlack, which also grows it while moving). A covered one only keeps its verdict
    // if its whole lit area was on screen when checked: then a turn can't bring in a part that
    // wasn't (as area lights); one reaching past the edge is checked every round, as before. Out
    // of view or switched off: as before, and checked at once when back.
    private static void TestLamps(Lane lane, int from, int to, ref Matrix4x4 m)
    {
        var eye = _roundEye;
        var now = _roundTime;
        // Claims start on a group's first lamp (LampsPerClaim is a multiple of LampGroup).
        for (var g = from; g < to; g += LampGroup)
        for (int l = g, end = Math.Min(to, g + LampGroup), outOfView = -1; l < end; l++)
        {
            // _lampActive is only written between rounds (Request, SetLamps).
            var test = _roundLamps && _lampActive[l];
            // A group wholly out of view: so is each of its lamps (what BoxWhy would find), asked once.
            if (test && outOfView < 0) outOfView = OutOfView(_lampGroupMin[g / LampGroup], _lampGroupMax[g / LampGroup]) ? 1 : 0;
            if (test) lane.LampsOn++;
            var inView = test && outOfView == 0 && !OutOfView(_lampMin[l], _lampMax[l]);
            var streak = _lampStreak[l];
            if (inView && now < _lampNextCheck[l] && (streak == 0 || _lampWhole[l] && SqrDistance(eye, _lampCheckedFrom[l]) < _lampSlack[l]))
            {
                // Not due: its last verdict stands. A covered one keeps the box it was checked with,
                // wholly on screen, so the turn check leaves it alone: all of it was checked.
                lane.LampsInView++;
                continue;
            }

            if (inView) Count(ref lane.LampTests, 1);
            var why = inView ? BoxWhy(lane, _lampMin[l], _lampMax[l], ref m) : WhyOutOfView;
            var covered = why == WhyCovered;
            if (why != WhyOutOfView) lane.LampsInView++;
            _lampWhole[l] = false;
            if (covered && Project(_lampMin[l], _lampMax[l], ref m, out var lx0, out var ly0, out var lx1, out var ly1, out _, out _))
            {
                _lampRect[l] = V4(lx0, ly0, lx1, ly1);
                _lampWhole[l] = OnScreen(_lampRect[l]);
            }

            _lampStreak[l] = covered ? (byte)Math.Min(streak + 1, HideAfter) : (byte)0;
            _lampWant[l] = _lampStreak[l] >= HideAfter;
            if (covered)
            {
                var slack = lane.UsedScope ? 0f : CoveredSlack(lane);
                _lampCheckedFrom[l] = eye;
                _lampSlack[l] = slack * slack;
                // Not confirmed yet: checked again next round.
                _lampNextCheck[l] = _lampStreak[l] < HideAfter ? now : now + VisibleRecheck * (0.75f + (l & 15) / 30f);
            }
            else _lampNextCheck[l] = why == WhyOutOfView ? 0f : now + VisibleRecheck * (0.75f + (l & 15) / 30f);
        }
    }

    private const byte WhyCovered = 1, WhyNear = 2, WhyOutOfView = 3, WhyBehindCamera = 4, WhyShowsBehind = 5,
        WhyScopeShows = 6, WhyScopeAhead = 7, WhyScopeNoDepth = 8, WhyMoving = 9;

    private static readonly string[] WhyText =
    {
        "not checked yet", "covered", "nearer than hiding starts", "out of view",
        "partly behind the camera", "something farther (or sky) shows somewhere in its box",
        "something farther shows in the scope's own depth", "within reach of the scope's view (it may swing in), kept drawn",
        "in the scope's view, which has no depth of its own",
        "covered, but you're moving and its cover's edge could slide off it before the next check, kept drawn",
    };

    // For the renderer census (profiling only): what the hiding makes of a drawn renderer.
    internal static string Explain(Renderer renderer)
    {
        var set = _set;
        if (set == null) return "objects not collected yet";
        if (!set.UnitOf.TryGetValue(renderer, out var i)) return NotTracked(renderer);
        if (set.Off[i] != null) return "hidden, yet drawn (switched back on by someone else)";
        var why = set.Why[i];
        return why == WhyCovered ? "covered, counting down to hide" : why < WhyText.Length ? WhyText[why] : "unknown";
    }

    // One worker thread's share of a round: its scratch, what it found, and its counts, merged
    // into the round's after all lanes finish. Every unit, cell and lamp is handled by exactly
    // one lane, so the per-unit arrays need no locking.
    private sealed class Lane
    {
        // The current cell's due objects.
        public readonly List<int> Due = new();
        // The walk's output lists (CellWalk), three cells' worth of objects.
        public int[] Walk = Array.Empty<int>();
        public readonly int[] Counts = new int[3];
        public readonly List<int> Want = new(), Try = new(), Stale = new();
        public int Tested, OutCells, CoveredCells, ScopeTested, Moving, Deep, GameSkipped, LampsOn, LampsInView;
        // Of the boxes kept drawn for movement: the widest growth (texels) and the nearest cover.
        public float MovingGrow, MovingCover;
        // The last BoxWhy needed the scope's depth (such objects are checked every round).
        public bool UsedScope;
        // The last box BoxWhy put on screen (0-1), its nearest depth, and what covered it, for CoveredSlack.
        public float X0, Y0, X1, Y1, Nearest, NearestCover;
        public bool GearCover;
        // Where the round's time goes (Debug builds only, TimeParts): time on cells (of which
        // walking them for due objects) and on lamps, objects walked in cells in and out of
        // view, and how many were due.
        public long CellTicks, WalkTicks, LampTicks;
        public int Walked, WalkedOut, DueCount, LampTests, CellsWalked;

        public void Clear()
        {
            Want.Clear();
            Try.Clear();
            Stale.Clear();
            Tested = OutCells = CoveredCells = ScopeTested = Moving = Deep = GameSkipped = LampsOn = LampsInView = 0;
            CellTicks = WalkTicks = LampTicks = 0;
            Walked = WalkedOut = DueCount = LampTests = CellsWalked = 0;
            MovingGrow = 0f;
            MovingCover = float.MaxValue;
        }
    }

    // A lane per 4 hardware threads, up to 4 (Unity's own job threads need the rest).
    // ponytail: fixed at start; the round is mostly memory-bound box tests, so more lanes than
    // this mostly wait on each other's cache misses.
    private static readonly Lane[] Lanes = MakeLanes(Math.Max(1, Math.Min(4, Environment.ProcessorCount / 4)));

    private static Lane[] MakeLanes(int count)
    {
        var lanes = new Lane[count];
        for (var k = 0; k < count; k++) lanes[k] = new Lane();
        return lanes;
    }

    // Round timing for Debug builds (calls vanish in Release): Mark starts a span, Stamp adds the
    // time since into a total and starts the next.
    [Conditional("DEBUG")]
    private static void Mark(ref long since) => since = Stopwatch.GetTimestamp();

    [Conditional("DEBUG")]
    private static void Stamp(ref long into, ref long since)
    {
        var t = Stopwatch.GetTimestamp();
        into += t - since;
        since = t;
    }

    [Conditional("DEBUG")]
    private static void Count(ref int into, int n) => into += n;

    // Debug test log, every TestLogSeconds: where the worker's rounds spent their time (average and
    // worst, ms; walking, testing and lamps summed over lanes), how many objects a round walked and
    // tested, and how late results are. Built on the worker, logged by Tick.
#if DEBUG
    private const double TestLogSeconds = 30.0;
    private static volatile string? _testNote;
    private static long _testSince;
    private static int _testRounds, _testAfterCull;
    private static double _sumPyramid, _sumLanes, _sumWalk, _sumTests, _sumLamps, _sumMerge, _worstRound, _worstLanes;
    private static long _sumWalked, _sumWalkedOut, _sumDue, _sumTested, _sumLampsOn, _sumLampsInView, _sumLampTests, _sumCellsWalked;
    // What one clock read costs on this machine (ms; under Proton each is a Wine system call): every
    // walked cell's span holds one, so the walking time is also logged without them.
    private static double _clockReadMs = -1;
#endif

    [Conditional("DEBUG")]
    private static void TimeParts(long pyramid, long lanes, long merge)
    {
#if DEBUG
        long cell = 0, walk = 0, lamps = 0;
        foreach (var lane in Lanes)
        {
            cell += lane.CellTicks;
            walk += lane.WalkTicks;
            lamps += lane.LampTicks;
            _sumWalked += lane.Walked;
            _sumCellsWalked += lane.CellsWalked;
            _sumWalkedOut += lane.WalkedOut;
            _sumDue += lane.DueCount;
            _sumLampTests += lane.LampTests;
        }

        var ms = Diagnostics.TickMs;
        _sumPyramid += pyramid * ms;
        _sumLanes += lanes * ms;
        _sumWalk += walk * ms;
        _sumTests += (cell - walk) * ms;
        _sumLamps += lamps * ms;
        _sumMerge += merge * ms;
        _sumTested += Tested;
        _sumLampsOn += LampsOn;
        _sumLampsInView += LampsInView;
        _worstRound = Math.Max(_worstRound, (pyramid + lanes + merge) * ms);
        _worstLanes = Math.Max(_worstLanes, lanes * ms);
        _testRounds++;
        var now = Stopwatch.GetTimestamp();
        if (_testSince == 0) _testSince = now;
        if (_clockReadMs < 0)
        {
            const int reads = 2000;
            var t0 = Stopwatch.GetTimestamp();
            for (var k = 0; k < reads; k++) Stopwatch.GetTimestamp();
            _clockReadMs = (Stopwatch.GetTimestamp() - t0) * ms / reads;
        }

        if ((now - _testSince) * ms < TestLogSeconds * 1000.0) return;
        double n = _testRounds;
        _testNote = $"depth test, {_testRounds} rounds: round {(_sumPyramid + _sumLanes + _sumMerge) / n:0.00} ms avg, {_worstRound:0.00} worst | pyramid {_sumPyramid / n:0.00}, "
                    + $"{Lanes.Length} threads {_sumLanes / n:0.00} wall ({_worstLanes:0.00} worst; summed over threads: walking cells {_sumWalk / n:0.00} ({(_sumWalk - _sumCellsWalked * _clockReadMs) / n:0.00} without the clock reads: {_sumCellsWalked / n:0} cells at {_clockReadMs * 1e6:0} ns), testing {_sumTests / n:0.00}, lamps {_sumLamps / n:0.00}), merge {_sumMerge / n:0.00} | "
                    + $"per round: walked {_sumWalked / n:0} objects in view + {_sumWalkedOut / n:0} out of view, {_sumDue / n:0} due, {_sumTested / n:0} tested, "
                    + $"lamps {_sumLampsOn / n:0} on of {_lamps.Length} ({_sumLampsInView / n:0} reaching into view, {_sumLampTests / n:0}  tested) | "
                    + $"arrives after {_arriveFrames:0.0} frames, applied {_appliedAge:0.0} old, one every {_resultEvery:0.0}, {_testAfterCull} started after the cull";
        _testSince = now;
        _testRounds = _testAfterCull = 0;
        _sumPyramid = _sumLanes = _sumWalk = _sumTests = _sumLamps = _sumMerge = _worstRound = _worstLanes = 0;
        _sumWalked = _sumWalkedOut = _sumDue = _sumTested = _sumLampsOn = _sumLampsInView = _sumLampTests = _sumCellsWalked = 0;
#endif
    }

    // Work is claimed in chunks: cells first, then lamps.
    private const int CellsPerClaim = 8, LampsPerClaim = 512;
    private static int _nextClaim;
    private static Matrix4x4 _testMatrix;

    // The main camera's own culling for this round, for GameSkips: each layer's cull distance
    // (0 means the far plane; read again every LayerCullEvery rounds, the getter allocates),
    // its forward axis, and what turns a LOD group's size and distance into screen height.
    private static readonly float[] RoundCull = new float[32];
    private static Vector3 _roundForward;
    private static float _roundLodScale;
    private static int _layerCullRounds;
    private const int LayerCullEvery = 60;
    // Below this share of its last level's screen height a LOD group counts as culled: a little
    // under Unity's own, as the camera can differ (scope, a FOV change) between rounds.
    private const float LodMargin = 0.9f;
    internal static int GameSkipped;

    private static void TakeGameCulling(Camera camera)
    {
        if (_layerCullRounds-- <= 0)
        {
            _layerCullRounds = LayerCullEvery;
            var cull = camera.layerCullDistances;
            var far = camera.farClipPlane;
            for (var l = 0; l < 32; l++) RoundCull[l] = l < cull.Length && cull[l] > 0f ? Math.Min(cull[l], far) : far;
        }

        _roundForward = camera.transform.forward;
        _roundLodScale = QualitySettings.lodBias / (2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
    }

    // Worker: the game draws none of this unit now, so Apply asking would only find that out
    // (~98% of first asks after a turn did). A wrong yes only leaves it drawn, never shows
    // anything late. Switched off by EFT's distance culler; past its layer's cull distance
    // even at its nearest point along the view; or a LOD group smaller on screen than its
    // last level.
    private static bool GameSkips(Set set, Lane lane, int i)
    {
        var culler = set.Culler[i];
        if (culler is not null && !(culler.IsVisible && culler.IsAutocullVisible)) return Skipped(lane);
        var layer = set.Layer[i];
        if (layer >= 0)
        {
            Vector3 min = set.Min[i], max = set.Max[i], f = _roundForward;
            var depth = ((min.x + max.x) * 0.5f - _roundEye.x) * f.x + ((min.y + max.y) * 0.5f - _roundEye.y) * f.y + ((min.z + max.z) * 0.5f - _roundEye.z) * f.z
                        - ((max.x - min.x) * Math.Abs(f.x) + (max.y - min.y) * Math.Abs(f.y) + (max.z - min.z) * Math.Abs(f.z)) * 0.5f;
            if (depth > RoundCull[layer]) return Skipped(lane);
        }

        var size = set.LodSize[i];
        return size > 0f && size * _roundLodScale < set.LodLast[i] * LodMargin * MathF.Sqrt(SqrDistance(set.LodRef[i], _roundEye)) && Skipped(lane);
    }

    private static bool Skipped(Lane lane)
    {
        lane.GameSkipped++;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Wanted(Set set, Lane lane, int i, int round)
    {
        lane.Want.Add(i);
        set.WantedIn[i] = round;
    }

    // Out of view, so it can't be checked: a covered one stays wanted hidden until the eye
    // has moved enough that its cover could have shifted, then it's checked again once back in
    // view. One still hidden then stays hidden too (showing it would cost a switch per part
    // for something Unity isn't drawing, and hiding it again two more checks) and is listed
    // stale: the main thread shows it the frame it comes back into view (ShowEntering).
    private static void KeepOutOfView(Set set, Lane lane, int i, Vector3 eye, int round)
    {
        if (set.Streak[i] != 0 && SqrDistance(eye, set.CheckedFrom[i]) >= set.Slack[i])
        {
            set.Streak[i] = 0;
            set.NextCheck[i] = 0f;
        }

        if (set.Streak[i] >= HideAfter) Wanted(set, lane, i, round);
        else if (set.Off[i] != null)
        {
            Wanted(set, lane, i, round);
            lane.Stale.Add(i);
        }
    }

    private static void MarkCovered(Set set, Lane lane, int i, Vector3 eye, float now, float slack, int round, bool inView)
    {
        set.Why[i] = WhyCovered;
        // Not confirmed yet: checked again next round, not after the usual wait.
        set.NextCheck[i] = set.Streak[i] + 1 < HideAfter ? now : now + VisibleRecheck * (0.75f + (i & 15) / 30f);
        set.CheckedFrom[i] = eye;
        set.Slack[i] = slack * slack;
        if (set.Streak[i] < HideAfter) set.Streak[i]++;
        if (set.Streak[i] < HideAfter) return;
        Wanted(set, lane, i, round);
        // Due means not waiting to retry (or hidden already).
        if (inView && set.Off[i] == null && !GameSkips(set, lane, i)) lane.Try.Add(i);
    }

    // Boxes found covered this round with room to spare, so checked again later (overlay).
    internal static int CoveredDeep;
    // For the overlay's short rows (the full picture is Status, in the log).
    internal static float ResultEvery => _resultEvery;
    internal static float ResultAge => _appliedAge;
    internal static int LampCount => _lamps.Length;
    // Lamps switched on, and of those, the ones whose lit area reaches into the view (the only
    // ones that cost anything: Unity already leaves out the rest).
    internal static int LampsOn, LampsInView;
    internal static float MovingGrow => _movingGrow;
    private static readonly float[] SpareTexels = { 6f, 18f };

    // How far the eye may move before a box BoxWhy just found covered needs checking again: as
    // far as its nearest cover can slide a texel, or, if the box grown by a few more texels on
    // every side is still covered, that many texels (with the nearest cover under the grown box).
    // The deeper inside its cover, the longer it holds: most hidden objects sit well inside
    // walls, and moving re-checked all of them every round. A grown box reaching past the
    // screen's edge stops there (what's past it wasn't checked). Cover that includes what the
    // player holds: checked every round, it moves on its own.
    private static float CoveredSlack(Lane lane)
    {
        if (lane.GearCover) return 0f;
        var slack = Slack(lane.NearestCover);
        float x0 = lane.X0, y0 = lane.Y0, x1 = lane.X1, y1 = lane.Y1;
        foreach (var spare in SpareTexels)
        {
            float gx = spare / _width, gy = spare / _height;
            if (x0 - gx < 0f || y0 - gy < 0f || x1 + gx > 1f || y1 + gy > 1f) break;
            if (Pyramid.Test(x0 - gx, y0 - gy, x1 + gx, y1 + gy, lane.Nearest, out var cover, out var gear) != DepthCover.Covered || gear) break;
            slack = Slack(cover) * spare;
            if (spare == SpareTexels[0]) lane.Deep++;
        }

        return slack;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Slack(float coverDepth)
    {
        var cover = coverDepth + _p22;
        return cover > 0f ? _p23 / cover * _texelAngle : 0f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Near(Vector3 min, Vector3 max, Vector3 eye)
    {
        // Distance from the eye to the box: zero along an axis the eye is within.
        float dx = 0f, dy = 0f, dz = 0f;
        if (eye.x < min.x) dx = min.x - eye.x;
        else if (eye.x > max.x) dx = eye.x - max.x;
        if (eye.y < min.y) dy = min.y - eye.y;
        else if (eye.y > max.y) dy = eye.y - max.y;
        if (eye.z < min.z) dz = min.z - eye.z;
        else if (eye.z > max.z) dz = eye.z - max.z;
        return dx * dx + dy * dy + dz * dz < _minDistance * _minDistance;
    }

    // The left, right, bottom and top planes of the round's view, worked out once per round:
    // the box test runs for tens of thousands of objects. The worker's; the main thread has
    // its own (ViewPlanes).
    private static readonly Vector4[] Planes = new Vector4[4];

    private static void SetPlanes(Vector4[] planes, ref Matrix4x4 m)
    {
        planes[0] = V4(m.m30 + m.m00, m.m31 + m.m01, m.m32 + m.m02, m.m33 + m.m03);
        planes[1] = V4(m.m30 - m.m00, m.m31 - m.m01, m.m32 - m.m02, m.m33 - m.m03);
        planes[2] = V4(m.m30 + m.m10, m.m31 + m.m11, m.m32 + m.m12, m.m33 + m.m13);
        planes[3] = V4(m.m30 - m.m10, m.m31 - m.m11, m.m32 - m.m12, m.m33 - m.m13);
    }

    // The worker's threads aren't IL2CPP's, and Unity's struct math isn't plain C# here: every
    // Vector3/Vector4 constructor, operator and property is an il2cpp_runtime_invoke that boxes
    // its result on the game's heap. A collection started by one of those on the worker killed
    // the game ("Fatal error in GC: Collecting from unknown thread"). Worker code uses these and
    // the fields only (tools/il2cpp-stripcheck lists any interop call it reaches).
    private static Vector3 V3(float x, float y, float z) => new() { x = x, y = y, z = z };
    private static Vector4 V4(float x, float y, float z, float w) => new() { x = x, y = y, z = z, w = w };

    private static float SqrDistance(Vector3 a, Vector3 b)
    {
        float x = a.x - b.x, y = a.y - b.y, z = a.z - b.z;
        return x * x + y * y + z * z;
    }

    // A view's left, right, bottom and top planes (SetPlanes) with their normals' absolute
    // values worked out once, for testing many boxes. Together they also leave out what's
    // wholly behind the eye. One for the worker (every box of every round), one for the main
    // thread (ShowEntering): the worker's is rewritten while it runs.
    private struct Sides
    {
        private Vector4 _p0, _p1, _p2, _p3;
        private Vector3 _a0, _a1, _a2, _a3;

        public void Take(Vector4[] planes)
        {
            _p0 = planes[0];
            _p1 = planes[1];
            _p2 = planes[2];
            _p3 = planes[3];
            _a0 = V3(Math.Abs(_p0.x), Math.Abs(_p0.y), Math.Abs(_p0.z));
            _a1 = V3(Math.Abs(_p1.x), Math.Abs(_p1.y), Math.Abs(_p1.z));
            _a2 = V3(Math.Abs(_p2.x), Math.Abs(_p2.y), Math.Abs(_p2.z));
            _a3 = V3(Math.Abs(_p3.x), Math.Abs(_p3.y), Math.Abs(_p3.z));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Out(Vector3 min, Vector3 max)
        {
            float cx = (min.x + max.x) * 0.5f, cy = (min.y + max.y) * 0.5f, cz = (min.z + max.z) * 0.5f;
            float ex = (max.x - min.x) * 0.5f, ey = (max.y - min.y) * 0.5f, ez = (max.z - min.z) * 0.5f;
            return _p0.x * cx + _p0.y * cy + _p0.z * cz + _p0.w + _a0.x * ex + _a0.y * ey + _a0.z * ez < 0f
                || _p1.x * cx + _p1.y * cy + _p1.z * cz + _p1.w + _a1.x * ex + _a1.y * ey + _a1.z * ez < 0f
                || _p2.x * cx + _p2.y * cy + _p2.z * cz + _p2.w + _a2.x * ex + _a2.y * ey + _a2.z * ez < 0f
                || _p3.x * cx + _p3.y * cy + _p3.z * cz + _p3.w + _a3.x * ex + _a3.y * ey + _a3.z * ez < 0f;
        }
    }

    private static Sides _workerSides, _viewSides;

    private static void TakePlanes() => _workerSides.Take(Planes);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool OutOfView(Vector3 min, Vector3 max) => _workerSides.Out(min, max);

    // A box against the depth: out of view, partly behind the camera, covered, or why not.
    // Covered on the main screen but in the scope's area: also covered in the scope's own
    // depth, or it stays drawn.
    private static byte BoxWhy(Lane lane, Vector3 min, Vector3 max, ref Matrix4x4 m)
    {
        lane.UsedScope = false;
        if (OutOfView(min, max)) return WhyOutOfView;
        if (!Project(min, max, ref m, out var xMin, out var yMin, out var xMax, out var yMax, out var nearest, out var farthest)) return WhyBehindCamera;
        lane.X0 = xMin;
        lane.Y0 = yMin;
        lane.X1 = xMax;
        lane.Y1 = yMax;
        lane.Nearest = nearest;
        float growX = BoxGrowTexels / _width, growY = BoxGrowTexels / _height;
        var result = Pyramid.Test(xMin - growX, yMin - growY, xMax + growX, yMax + growY, nearest, out lane.NearestCover, out lane.GearCover);
        if (result == DepthCover.OffScreen) return WhyOutOfView;
        if (result != DepthCover.Covered) return WhyShowsBehind;
        if (_roundScoped && xMax >= _scopeXMin && xMin <= _scopeXMax && yMax >= _scopeYMin && yMin <= _scopeYMax)
        {
            // The scope's area has its own lead (ScopeCovers); the main depth only sees the scope there.
            lane.UsedScope = true;
            return ScopeCovers(lane, min, max);
        }

        // Moving: grown by how far the nearest cover under it can slide across it before the next
        // result, only on the side that can open up. Moving right, near cover slides left past
        // what's behind it, so a gap on the box's right comes towards it (left: the other way).
        // Forward, near cover spreads out from the middle faster, so a gap on the box's side
        // towards the middle comes towards it (back: the outer side). Up and down: both sides
        // (which way is up on the depth image isn't worth getting wrong).
        // Unity's == (within 1e-5).
        if (SqrDistance(_roundReach, default) < 1e-10f) return WhyCovered;
        // World cover only: the player's gun moves with the eye, so its edge doesn't slide; behind it
        // things only shift by their own parallax (OwnShiftTexels).
        var cover = lane.NearestCover;
        var gear = lane.GearCover;
        var outX = Math.Min(1f, Math.Max(Math.Abs(2f * xMin - 1f), Math.Abs(2f * xMax - 1f))) * _tanX;
        var outY = Math.Min(1f, Math.Max(Math.Abs(2f * yMin - 1f), Math.Abs(2f * yMax - 1f))) * _tanY;
        var rightOfMiddle = xMin + xMax > 1f;
        float forward = _roundReachPlus.z * outX, back = _roundReachMinus.z * outX;
        float reachRight = _roundReachPlus.x + (rightOfMiddle ? back : forward), reachLeft = _roundReachMinus.x + (rightOfMiddle ? forward : back);
        var reachY = _roundReach.y + _roundReach.z * outY;
        float capX = MaxMoveGrowAngle / _texelAngleX, capY = MaxMoveGrowAngle / _texelAngle;
        var slideRight = DepthPyramid.MoveGrowTexels(reachRight, cover, farthest, _p23, _texelAngleX, capX);
        var slideLeft = DepthPyramid.MoveGrowTexels(reachLeft, cover, farthest, _p23, _texelAngleX, capX);
        var slideY = DepthPyramid.MoveGrowTexels(reachY, cover, farthest, _p23, _texelAngle, capY);
        if (gear)
        {
            slideRight = Math.Max(slideRight, DepthPyramid.OwnShiftTexels(reachRight, nearest, _p22, _p23, _texelAngleX, capX));
            slideLeft = Math.Max(slideLeft, DepthPyramid.OwnShiftTexels(reachLeft, nearest, _p22, _p23, _texelAngleX, capX));
            slideY = Math.Max(slideY, DepthPyramid.OwnShiftTexels(reachY, nearest, _p22, _p23, _texelAngle, capY));
        }

        if (slideRight <= BoxGrowTexels && slideLeft <= BoxGrowTexels && slideY <= BoxGrowTexels) return WhyCovered;
        float growRight = Math.Max(slideRight, BoxGrowTexels) / _width, growLeft = Math.Max(slideLeft, BoxGrowTexels) / _width;
        growY = Math.Max(slideY, BoxGrowTexels) / _height;
        if (Pyramid.Test(xMin - growLeft, yMin - growY, xMax + growRight, yMax + growY, nearest, out _, out _) == DepthCover.Covered) return WhyCovered;
        // For the status line.
        lane.MovingGrow = Math.Max(lane.MovingGrow, Math.Max(Math.Max(slideRight, slideLeft), slideY));
        if (cover > 0f && cover + _p22 > 0f) lane.MovingCover = Math.Min(lane.MovingCover, _p23 / (cover + _p22));
        return WhyMoving;
    }

    // In the scope's own depth: wholly inside its picture (part outside may swing in) and
    // covered there.
    private static byte ScopeCovers(Lane lane, Vector3 min, Vector3 max)
    {
        // Wholly outside what the scope sees now or soon: behind the lens on the main screen it's
        // only the scope itself, so the main depth's answer stands. The lens covers far more
        // than a zoomed scope's view, and all of that used to stay drawn.
        var view = _roundScopeView;
        if (Project(min, max, ref view, out var vxMin, out var vyMin, out var vxMax, out var vyMax, out _, out _)
            && (vxMax < 0.5f - _scopeReachX + Math.Min(0f, _scopeShiftX) || vxMin > 0.5f + _scopeReachX + Math.Max(0f, _scopeShiftX)
                || vyMax < 0.5f - _scopeReachY + Math.Min(0f, _scopeShiftY) || vyMin > 0.5f + _scopeReachY + Math.Max(0f, _scopeShiftY))) return WhyCovered;
        if (!_roundScopeDepth) return WhyScopeNoDepth;
        lane.ScopeTested++;
        var m = _scopeViewProjection;
        // Wholly outside the scope's picture but near it (the margin, ahead of a turn): kept
        // drawn, it may come into the scope before the next check. Otherwise only the part
        // inside the picture is tested, like the main view does at its edges, except while
        // turning by more than a tenth of the scope's view per lead time: then it must not stick
        // out on the side the scope is turning towards, so what's about to sweep in is already
        // drawn (zoomed in, a smaller turn counts).
        if (!Project(min, max, ref m, out var xMin, out var yMin, out var xMax, out var yMax, out var nearest, out _)) return WhyScopeAhead;
        if (_scopeShiftX > TurningShare && xMax > 1f || _scopeShiftX < -TurningShare && xMin < 0f
            || _scopeShiftY > TurningShare && yMax > 1f || _scopeShiftY < -TurningShare && yMin < 0f) return WhyScopeAhead;
        return ScopePyramid.Test(xMin, yMin, xMax, yMax, nearest, out _, out _) switch
        {
            DepthCover.Covered => WhyCovered,
            DepthCover.OffScreen => WhyScopeAhead,
            _ => WhyScopeShows,
        };
    }

    // The box on screen (0-1) and the depth of its nearest corner (larger is nearer); false
    // when part of it is behind the camera.
    private static bool Project(Vector3 min, Vector3 max, ref Matrix4x4 m, out float xMin, out float yMin, out float xMax, out float yMax, out float nearest, out float farthest)
    {
        xMin = yMin = farthest = float.MaxValue;
        xMax = yMax = nearest = float.MinValue;
        for (var c = 0; c < 8; c++)
        {
            var x = (c & 1) == 0 ? min.x : max.x;
            var y = (c & 2) == 0 ? min.y : max.y;
            var z = (c & 4) == 0 ? min.z : max.z;
            var w = m.m30 * x + m.m31 * y + m.m32 * z + m.m33;
            if (w <= 0.01f) return false;
            var sx = (m.m00 * x + m.m01 * y + m.m02 * z + m.m03) / w * 0.5f + 0.5f;
            var sy = (m.m10 * x + m.m11 * y + m.m12 * z + m.m13) / w * 0.5f + 0.5f;
            var sz = (m.m20 * x + m.m21 * y + m.m22 * z + m.m23) / w;
            xMin = Math.Min(xMin, sx);
            xMax = Math.Max(xMax, sx);
            yMin = Math.Min(yMin, sy);
            yMax = Math.Max(yMax, sy);
            nearest = Math.Max(nearest, sz);
            farthest = Math.Min(farthest, sz);
        }

        return true;
    }

    // Main thread: hide what the round wants hidden, show what it no longer does.
    private static void Apply(Set set, Vector3 eye)
    {
        if ((eye - _roundEye).sqrMagnitude > MaxDrift * MaxDrift)
        {
            ShowHidden(set);
            return;
        }

        // Showing first, never capped: what the round no longer wants. Ones already shown on
        // entering the view (ShowEntering) are dropped from the list.
        var round = _roundId;
        var kept = 0;
        for (var n = 0; n < HiddenUnits.Count; n++)
        {
            var i = HiddenUnits[n];
            if (set.Off[i] == null) continue;
            if (set.WantedIn[i] == round) HiddenUnits[kept++] = i;
            else Show(set, i);
        }

        HiddenUnits.RemoveRange(kept, HiddenUnits.Count - kept);

        StaleUnits.Clear();
        foreach (var i in set.Stale)
        {
            if (set.Off[i] != null) StaleUnits.Add(i);
        }

        StaleCount = StaleUnits.Count;

        // Then hiding. Asking gets AskBudget, re-asking ones the game wasn't drawing only
        // RetryBudget of it, and new hides are capped, so turning towards thousands of covered
        // objects doesn't stall a frame; the rest are still covered next check.
        var want = set.Try;
        int hides = HidesPerCheck, waiting = 0, asks = 0;
        var now = Time.time;
        var askStart = Stopwatch.GetTimestamp();
        bool asksOut = false, retriesOut = false;
        var from = want.Count > 0 ? _applyFrom % want.Count : 0;
        _applyFrom = from;
        for (var n = 0; n < want.Count; n++)
        {
            var i = want[(from + n) % want.Count];
            var retryAt = set.RetryAt[i];
            if (set.Off[i] != null || now < retryAt) continue;
            // Its baked group is switched off: the game draws none of it. Asked again when the
            // group comes back on (BakedSwitchPatch), or after BakedOffRetry in case that's missed.
            var bake = set.BakeGroup[i];
            if (bake != null && !bake.isGroupEnabled)
            {
                set.RetryAt[i] = now + BakedOffRetry;
                continue;
            }

            if (hides <= 0 || asksOut || retryAt > 0f && retriesOut)
            {
                // Out of budget: the next check starts here.
                if (waiting++ == 0) _applyFrom = from + n;
                continue;
            }

            // Hiding what the game isn't drawing saves nothing and costs a switch per renderer;
            // those are asked again a little later, spread so they don't all come due at once.
            if (Hide(set, i)) hides--;
            else set.RetryAt[i] = now + NotDrawnRetry * (0.75f + (i & 15) / 30f);
            if (++asks % AsksPerClock == 0)
            {
                var spent = Stopwatch.GetTimestamp() - askStart;
                asksOut = spent >= AskBudget;
                retriesOut = spent >= RetryBudget;
            }
        }

        Waiting = waiting;
    }

    // A part Hide left alone (destroyed, shadow-only, or switched off by someone else).
    private const ShadowCastingMode NotSwitched = (ShadowCastingMode)(-1);

    // Native reads instead of a call into Unity per renderer when the build matches (SceneSkip):
    // Apply asks thousands of these a check, and those calls were its peaks (1.5-3 ms).
    private static bool _native;

    private static bool Drawn(Renderer renderer) =>
        _native ? renderer is not null && SceneSkip.IsVisible(renderer) : renderer != null && renderer.isVisible;

    private static bool ForceOff(Renderer renderer) => _native ? SceneSkip.ForceRenderingOff(renderer) : renderer.forceRenderingOff;

    private static void SetForceOff(Renderer renderer, bool off)
    {
        if (_native) SceneSkip.SetForceRenderingOff(renderer, off);
        else renderer.forceRenderingOff = off;
    }

    private static bool Hide(Set set, int i)
    {
        // Drawn at all? One call per part, stopping at the first yes, starting with the part
        // that was drawn last time.
        _frameAsks++;
        var renderers = set.Renderers[i];
        int last = set.DrawnPart[i], drawn = -1;
        if (last < renderers.Length && Drawn(renderers[last])) drawn = last;
        else
        {
            for (var n = 0; n < renderers.Length; n++)
            {
                if (n == last || !Drawn(renderers[n])) continue;
                drawn = n;
                break;
            }
        }

        if (drawn < 0) return false;
        if (drawn < 256) set.DrawnPart[i] = (byte)drawn;

        // One pass, into the unit's own record. A part someone else switched off is only made
        // shadow-only (it draws nothing either way) when it casts shadows, so Show never
        // switches back on what it didn't switch off.
        var was = set.Was[i] ??= new ShadowCastingMode[renderers.Length];
        var any = false;
        var hidesShadows = HidesShadows;
        for (var n = 0; n < renderers.Length; n++)
        {
            was[n] = NotSwitched;
            var renderer = renderers[n];
            if (renderer == null) continue;
            if (hidesShadows)
            {
                if (ForceOff(renderer)) continue;
                SetForceOff(renderer, true);
                was[n] = ShadowCastingMode.ShadowsOnly;
                any = true;
                continue;
            }

            var mode = renderer.shadowCastingMode;
            if (mode == ShadowCastingMode.ShadowsOnly) continue;
            else if (mode != ShadowCastingMode.Off)
            {
                renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                was[n] = mode;
            }
            else if (!ForceOff(renderer))
            {
                SetForceOff(renderer, true);
                was[n] = ShadowCastingMode.ShadowsOnly;
            }
            else continue;

            any = true;
        }

        if (!any) return false;
        set.Off[i] = renderers;
        set.IsHidden[i] = 1;
        set.Saving[i] = true;
        HiddenUnits.Add(i);
        Hidden++;
        _frameHides++;
        Saving++;
        return true;
    }

    // Puts back exactly what Hide changed, unless someone changed it since.
    private static void Show(Set set, int i)
    {
        var off = set.Off[i];
        var was = set.Was[i];
        if (off == null || was == null) return;
        for (var n = 0; n < off.Length; n++)
        {
            if (was[n] == NotSwitched) continue;
            var renderer = off[n];
            if (renderer == null) continue;
            if (was[n] == ShadowCastingMode.ShadowsOnly) SetForceOff(renderer, false);
            else if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) renderer.shadowCastingMode = was[n];
        }

        set.Off[i] = null;
        set.IsHidden[i] = 0;
        Hidden--;
        _frameShows++;
        if (set.Saving[i]) Saving--;
    }

    private static void ShowHidden(Set set)
    {
        foreach (var i in HiddenUnits) Show(set, i);
        HiddenUnits.Clear();
        StaleUnits.Clear();
        StaleCount = 0;
    }

    // Hidden objects out of view whose last check no longer holds (Apply copies the worker's
    // list): shown the frame they come back into the camera's view, before Unity culls it,
    // instead of when they're shown or hidden again by a check.
    private static readonly List<int> StaleUnits = new();
    internal static int StaleCount;
    private static readonly Vector4[] ViewPlanes = new Vector4[4];

    // The list comes in runs of one cell (the worker goes cell by cell, in unit order), and a
    // cell's box holds its units' boxes: a cell out of view keeps its whole run without a test
    // per unit.
    private static void ShowEntering(Set set, Camera camera)
    {
        var m = camera.cullingMatrix;
        SetPlanes(ViewPlanes, ref m);
        _viewSides.Take(ViewPlanes);
        int kept = 0, cell = -1;
        var cellOut = false;
        for (var n = 0; n < StaleUnits.Count; n++)
        {
            var i = StaleUnits[n];
            if (set.Off[i] == null) continue;
            var c = set.CellOf[i];
            if (c != cell)
            {
                cell = c;
                cellOut = _viewSides.Out(set.CellMin[c], set.CellMax[c]);
            }

            if (cellOut || _viewSides.Out(set.Min[i], set.Max[i])) StaleUnits[kept++] = i;
            else
            {
                Show(set, i);
                _frameEntering++;
            }
        }

        StaleUnits.RemoveRange(kept, StaleUnits.Count - kept);
        StaleCount = kept;
    }

    // The game's baked visibility (PerfectCulling) switching groups back on, a few per frame
    // as the camera moves between its cells: their objects the last checks found not drawn
    // are asked again in the next one instead of after NotDrawnRetry. UpdateSwitchQueues takes
    // the groups from the end of the queue; the group's IsEnabled setter and Toggle are
    // AggressiveInlining, so they can't be patched.
    private class BakedSwitchPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(PerfectCullingCrossSceneGroup), nameof(PerfectCullingCrossSceneGroup.UpdateSwitchQueues));

        [PatchPrefix]
        private static void Prefix(PerfectCullingCrossSceneGroup __instance)
        {
            var set = _set;
            if (set == null || Time.frameCount - _activeFrame > 1) return;
            var queue = __instance._queueActivate;
            if (queue == null) return;
            try
            {
                var groups = __instance.bakeGroups;
                var count = queue.Count;
                var buffer = queue.Buffer;
                var take = Math.Min(count, PerfectCullingSettings.Instance.numActivationsPerVolume / 2);
                for (var k = 1; k <= take; k++)
                {
                    var g = buffer[count - k];
                    if (g >= groups.Length) continue;
                    var renderers = groups[g]?.renderers;
                    if (renderers == null) continue;
                    foreach (var renderer in renderers)
                    {
                        if (renderer is not null && set.UnitOf.TryGetValue(renderer, out var i) && set.RetryAt[i] > 0f) set.RetryAt[i] = 0f;
                    }
                }
            }
            catch (Exception ex)
            {
                _error ??= ex;
            }
        }
    }

    // Setting off, A/B or an error: show everything, and the next rounds start counting afresh.
    internal static void ShowAll()
    {
        if (_added != null) _added.enabled = false;
        _epoch++;
        _resetStreaks = true;
        if (_set != null) ShowHidden(_set);
        RestoreLamps();
    }

    // Leaving a raid: its objects are gone with the scene.
    internal static void Clear()
    {
        ShowAll();
        SetLamps(Array.Empty<Light>());
        _set = null;
        PendingRenderers.Clear();
        PendingBounds.Clear();
        PendingLayer.Clear();
        PendingLod.Clear();
        Tracked = 0;
        Hidden = 0;
        _gameSwitched = new HashSet<Renderer>(Diagnostics.ReferenceComparer.Instance);
        Rounds = 0;
        _lastApplyFrame = 0;
        _lastApplyTime = 0f;
        _speedTime = -1.0;
        _eyeSpeedPlus = _eyeSpeedMinus = Vector3.zero;
        MovingKept = 0;
        Waiting = 0;
        _applyFrom = 0;
        Status = "waiting for the scan";
        Saving = 0;
        _camera = null;
        _generator = null;
        _scanned = false;
        _scanAt = -1f;
        _scan = null;
        _scanTicks = 0;
        _viewFrame = -1;
        _scoped = false;
        _scopeCamera = null;
        _scopeAdded = null;
        _scopeReading = false;
        _scopeDone = null;
        _scopeNextFrame = -1000;
        ScopeTested = 0;
    }
}
