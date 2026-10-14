using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.CameraControl;
using GPUInstancer;
using Koenigz.PerfectCulling;
using Koenigz.PerfectCulling.EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
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
    // Worker: the last BoxWhy needed the scope's depth (such objects are checked every round).
    private static bool _usedScope;
    // Checks this round that used the scope's depth (overlay).
    internal static int ScopeTested;
    private static int _scopeTestedRound;
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
    // finding nothing), and objects actually hidden (a switch per part). Objects asked for
    // the first time aren't budgeted. The next check carries on where this one stopped, so
    // nothing waits behind the same few.
    private static readonly long RetryBudget = (long)(0.1 / Diagnostics.TickMs);
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
        // What the last check made of each cell (debug view): CellOut, CellIdle, CellCovered, CellTested.
        public byte[] CellState = null!;
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
        // Hidden: the unit's renderers, else null.
        public Renderer[]?[] Off = null!;
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
    }

    private static readonly List<Renderer[]> PendingRenderers = new();
    private static readonly List<Bounds> PendingBounds = new();
    private static Set? _set;
    private static readonly List<int> HiddenUnits = new();

    private const int Idle = 0, Reading = 1, Testing = 2, Ready = 3;
    private static int _state;
    private static int _epoch, _roundEpoch, _roundId, _requestFrame;
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
    // renderers, fetched again when the item or hands controller changes and every couple of
    // seconds (attachments changed in a raid), put on the round's screen part by part for the
    // pyramid's gear mask (DepthPyramid.AddGear). A part not listed yet only gets the full margin.
    private static GameObject? _gearRoot;
    private static object? _gearHands;
    private static float _gearAt;
    private static Renderer[] _gearParts = Array.Empty<Renderer>();
    internal static int GearParts;
    private static int _movingRound;
    internal static int MovingKept;

    // Lamps: the level's spot and point lights that don't move. One whose lit area (its range,
    // for a spot only the cone) is covered wherever it's on screen lights nothing you can see: it
    // gets an empty culling mask (no object lit, nothing drawn into its shadow map) until any of
    // that area may show, and the frame a turn brings in a part that wasn't checked. The game never writes a level light's culling mask (only the weapon-modding preview,
    // on its own lights). Boxes and positions are taken each round on the main thread; Streak and
    // Want are the worker's; the rest the main thread's.
    private static Light[] _lamps = Array.Empty<Light>();
    private static Vector3[] _lampMin = Array.Empty<Vector3>(), _lampMax = Array.Empty<Vector3>(), _lampAt = Array.Empty<Vector3>();
    private static bool[] _lampTest = Array.Empty<bool>(), _lampWant = Array.Empty<bool>(), _lampOff = Array.Empty<bool>();
    private static byte[] _lampStreak = Array.Empty<byte>();
    private static int[] _lampMaskWas = Array.Empty<int>();
    // A lamp's lit area on the round's screen (0-1, may reach past it; the worker's), and for a
    // skipped one as its result was applied, with that round's view: a turn may bring in the part
    // past the screen's edge, which wasn't checked (KeepLampsInView).
    private static Vector4[] _lampRect = Array.Empty<Vector4>(), _lampSkipRect = Array.Empty<Vector4>();
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
    private const int PartApply = 0, PartRead = 1, PartRequest = 2;
    private static readonly long[] PartTicks = new long[3];
    private static int _frameHides, _frameShows;
    internal static string PeakNote = "";
    private static string _secondPeakNote = "";

    internal static void Enable()
    {
        Camera.onPreCull += PreCull;
        Camera.onPostRender += DrawCells;
        new PyramidBuiltPatch(nameof(GPUInstancerHiZOcclusionGenerator.OnEndCameraRendering)).Enable();
        new PyramidBuiltPatch(nameof(GPUInstancerHiZOcclusionGenerator.OnEndCameraRenderingBlit)).Enable();
        new BakedSwitchPatch().Enable();
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
    private static HashSet<Renderer> _gameSwitched = new();

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
        var gameSwitched = new HashSet<Renderer>();
        foreach (var switcher in UnityEngine.Object.FindObjectsByType<Koenigz.PerfectCulling.EFT.ScreenDistanceSwitcher>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            gameSwitched.UnionWith(switcher.GetComponentsInChildren<Renderer>(true));
            if (switcher._controlLodGroup != null) gameSwitched.UnionWith(switcher._controlLodGroup.GetComponentsInChildren<Renderer>(true));
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
        _gameSwitched = gameSwitched;
        var moving = new Dictionary<Transform, bool>();
        var groupsSeen = new HashSet<LODGroup>();
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

            // The rain's drop mesh claims endless bounds so it's never culled: nothing to cover.
            if (!(bounds.extents.sqrMagnitude < MaxExtent * MaxExtent)) continue;

            PendingRenderers.Add(unit.ToArray());
            PendingBounds.Add(bounds);
            Tracked++;
        }

        var lamps = new List<Light>();
        foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if ((light.type == LightType.Spot || light.type == LightType.Point)
                && (skipLayers & (1 << light.gameObject.layer)) == 0 && !Moves(light.transform, moving)) lamps.Add(light);
        }

        SetLamps(lamps.ToArray());
    }

    private static void SetLamps(Light[] lamps)
    {
        RestoreLamps();
        _lamps = lamps;
        var n = lamps.Length;
        _lampMin = new Vector3[n];
        _lampMax = new Vector3[n];
        _lampAt = new Vector3[n];
        _lampTest = new bool[n];
        _lampWant = new bool[n];
        _lampOff = new bool[n];
        _lampStreak = new byte[n];
        _lampMaskWas = new int[n];
        _lampRect = new Vector4[n];
        _lampSkipRect = new Vector4[n];
        _lampsPastEdge = 0;
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
    // changed it meanwhile).
    private static void ApplyLamps(bool fits)
    {
        var on = fits && _roundLamps && Plugin.On(Plugin.SkipCoveredLamps);
        int off = 0, pastEdge = 0;
        _lampView = _roundViewProjection;
        for (var l = 0; l < _lamps.Length; l++)
        {
            var light = _lamps[l];
            if (light == null)
            {
                _lampOff[l] = false;
                continue;
            }

            var want = on && _lampTest[l] && _lampWant[l] && (light.transform.position - _lampAt[l]).sqrMagnitude < 1e-4f;
            if (want && !_lampOff[l] && light.cullingMask != 0)
            {
                _lampMaskWas[l] = light.cullingMask;
                light.cullingMask = 0;
                _lampOff[l] = true;
            }
            else if (!want && _lampOff[l])
            {
                if (light.cullingMask == 0) light.cullingMask = _lampMaskWas[l];
                _lampOff[l] = false;
            }

            if (_lampOff[l])
            {
                off++;
                _lampSkipRect[l] = _lampRect[l];
                if (!OnScreen(_lampRect[l])) pastEdge++;
            }
        }

        LampsOff = off;
        _lampsPastEdge = pastEdge;
    }

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
        for (var l = 0; l < _lamps.Length; l++)
        {
            if (!_lampOff[l] || OnScreen(_lampSkipRect[l])) continue;
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
    // interactive objects, players. Remembered per transform, since objects share parents.
    private static bool Moves(Transform transform, Dictionary<Transform, bool> known)
    {
        if (transform == null) return false;
        if (known.TryGetValue(transform, out var moves)) return moves;
        moves = transform.TryGetComponent<Rigidbody>(out _)
            || transform.TryGetComponent<Animator>(out _)
            || transform.TryGetComponent<Animation>(out _)
            || transform.TryGetComponent<EFT.Interactive.InteractableObject>(out _)
            || transform.TryGetComponent<Player>(out _)
            || Moves(transform.parent, known);
        known[transform] = moves;
        return moves;
    }

    private static string NotTracked(Renderer renderer)
    {
        if ((_skipLayers & (1 << renderer.gameObject.layer)) != 0) return $"not tracked: on a left-out layer ({LayerMask.LayerToName(renderer.gameObject.layer)})";
        if (renderer is not MeshRenderer) return "not tracked: not a mesh renderer";
        if (_gameSwitched.Contains(renderer)) return "not tracked: switched by the game's distance stand-ins";
        if (Moves(renderer.transform, new Dictionary<Transform, bool>())) return "not tracked: moves or can be used";
        return renderer.GetComponentInParent<LODGroup>() != null ? "not tracked: LOD group with a part switched off by the game" : "not tracked: appeared after the scan";
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
            UnitOf = new Dictionary<Renderer, int>(count * 2),
            WantedIn = new int[count],
            Off = new Renderer[count][],
            Was = new ShadowCastingMode[count][],
            Saving = new bool[count],
            RetryAt = new float[count],
            DrawnPart = new byte[count],
        };
        var starts = new List<int>();
        var cellMin = new List<Vector3>();
        var cellMax = new List<Vector3>();
        for (var i = 0; i < count; i++)
        {
            var bounds = PendingBounds[order[i]];
            set.Min[i] = bounds.min;
            set.Max[i] = bounds.max;
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

        starts.Add(count);
        set.CellStart = starts.ToArray();
        set.CellMin = cellMin.ToArray();
        set.CellMax = cellMax.ToArray();
        set.CellState = new byte[set.CellMin.Length];

        PendingRenderers.Clear();
        PendingBounds.Clear();
        _set = set;

        Plugin.Log.LogInfo($"hidden objects: tracking {count} level objects in {set.CellMin.Length} cells, scan took {ScanMs:0} ms");
    }

    private const float MaxExtent = 5000f;

    // The main camera, right before culling: apply the last result if the worker is done, and
    // keep this frame's view for the depth the grass system builds at its end.
    private static void PreCull(Camera camera)
    {
        var set = _set;
        if (set == null || !ReferenceEquals(camera, _camera) || Time.frameCount - _activeFrame > 1) return;
        var start = Stopwatch.GetTimestamp();
        try
        {
            if (Volatile.Read(ref _state) == Ready && _error == null)
            {
                _state = Idle;
                if (_roundEpoch == _epoch)
                {
                    var applyStart = Stopwatch.GetTimestamp();
                    Apply(set, camera.transform.position);
                    ApplyLamps((camera.transform.position - _roundEye).sqrMagnitude <= MaxDrift * MaxDrift);
                    PartTicks[PartApply] += Stopwatch.GetTimestamp() - applyStart;
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
                var staleStart = Stopwatch.GetTimestamp();
                ShowEntering(set, camera);
                PartTicks[PartApply] += Stopwatch.GetTimestamp() - staleStart;
            }

            if (_lampsPastEdge > 0) KeepLampsInView(camera);

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
            _frameTicks += Stopwatch.GetTimestamp() - start;
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
        if (!ReferenceEquals(root, _gearRoot) || !ReferenceEquals(hands, _gearHands) || Time.unscaledTime - _gearAt > 2f)
        {
            _gearRoot = root;
            _gearHands = hands;
            _gearAt = Time.unscaledTime;
            var parts = new List<Renderer>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is MeshRenderer or SkinnedMeshRenderer) parts.Add(renderer);
            }

            _gearParts = parts.ToArray();
        }

        foreach (var part in _gearParts)
        {
            if (part == null || !part.enabled || !part.gameObject.activeInHierarchy) continue;
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
            _secondPeakNote = $"applying {PartTicks[PartApply] * Diagnostics.TickMs:0.00} ({_frameHides} hidden, {_frameShows} shown), " +
                              $"reading {PartTicks[PartRead] * Diagnostics.TickMs:0.00}, asking {PartTicks[PartRequest] * Diagnostics.TickMs:0.00}";
        }

        Array.Clear(PartTicks, 0, PartTicks.Length);
        _frameHides = _frameShows = 0;
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
        var settings = Traverse.Create(generator);
        var switched = settings.Field("occlusionCullingType").GetValue<GPUIOcclusionCullingType>() == GPUIOcclusionCullingType.Default
                       && settings.Field("_depthBlitMaterial").GetValue() == null;
        if (switched)
        {
            generator.enabled = false;
            settings.Field("occlusionCullingType").SetValue(GPUIOcclusionCullingType.CopyTexture);
            generator.enabled = true;
        }

        generator.Initialize(camera);
        how = switched ? "blit shader not loaded, using compute shaders" : settings.Field("occlusionCullingType").GetValue().ToString();
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
            Traverse.Create(_scopeAdded).Field("_ssaa").SetValue(null);
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
        AsyncGPUReadback.Request(texture, mip, TextureFormat.RFloat, request =>
        {
            _scopeReading = false;
            if (request.hasError) return;
            var data = request.GetData<float>();
            if (data.Length != width * height) return;
            if (_scopeNext.Length != data.Length) _scopeNext = new float[data.Length];
            data.CopyTo(_scopeNext);
            _scopeNextWidth = width;
            _scopeNextHeight = height;
            _scopeNextViewProjection = viewProjection;
            _scopeNextFrame = Time.frameCount;
        });
    }

    // Right after the grass system built this frame's depth pyramid.
    private static void Request(GPUInstancerHiZOcclusionGenerator generator, Camera camera)
    {
        if (_set == null || _state != Idle || !ReferenceEquals(camera, _camera) || _viewFrame != Time.frameCount
            || Time.frameCount - _activeFrame > 1) return;
        var texture = generator.hiZDepthTexture;
        if (texture == null) return;
        var start = Stopwatch.GetTimestamp();
        _generator = generator;
        var mip = 0;
        while (mip < texture.mipmapCount - 1 && (texture.width >> mip) > MaxWidth) mip++;
        _width = Mathf.Max(1, texture.width >> mip);
        _height = Mathf.Max(1, texture.height >> mip);
        _roundViewProjection = _viewProjection;
        _roundEye = _viewEye;
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
        for (var l = 0; l < _lamps.Length; l++)
        {
            var light = _lamps[l];
            var test = _roundLamps && light != null && light.isActiveAndEnabled;
            _lampTest[l] = test;
            if (!test) continue;
            LampBox(light!, out _lampMin[l], out _lampMax[l]);
            _lampAt[l] = light!.transform.position;
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
        AsyncGPUReadback.Request(texture, mip, TextureFormat.RFloat, request => Read(request, id));
        _frameTicks += Stopwatch.GetTimestamp() - start;
        PartTicks[PartRequest] += Stopwatch.GetTimestamp() - start;
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

        var start = Stopwatch.GetTimestamp();
        _arriveFrames = _arriveFrames * 0.9f + (Time.frameCount - _requestFrame) * 0.1f;
        _arriveMs = _arriveMs * 0.9f + (Time.realtimeSinceStartup - _roundTime) * 1000f * 0.1f;
        var data = request.GetData<float>();
        if (data.Length != _width * _height)
        {
            Status = $"readback size {data.Length}, expected {_width}x{_height}";
            _state = Idle;
            return;
        }

        if (_depth.Length != data.Length) _depth = new float[data.Length];
        data.CopyTo(_depth);
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
            _recheckAll = false;
        }

        if (_resetStreaks)
        {
            Array.Clear(set.Streak, 0, set.Streak.Length);
            Array.Clear(set.NextCheck, 0, set.NextCheck.Length);
            Array.Clear(_lampStreak, 0, _lampStreak.Length);
            _resetStreaks = false;
        }

        _state = Testing;
        Task.Run(() => Test(set));
        _frameTicks += Stopwatch.GetTimestamp() - start;
        PartTicks[PartRead] += Stopwatch.GetTimestamp() - start;
    }

    // On the worker: which units are covered, and which have been for HideAfter rounds.
    private static void Test(Set set)
    {
        try
        {
            var start = Stopwatch.GetTimestamp();
            var m = _roundViewProjection;
            var eye = _roundEye;
            Pyramid.Build(_depth, _width, _height);
            if (_roundScopeDepth) ScopePyramid.Build(_scopeDepth, _scopeWidth, _scopeHeight);
            _scopeTestedRound = 0;
            _movingRound = 0;
            _movingGrow = 0f;
            _movingCover = float.MaxValue;
            SetPlanes(Planes, ref m);
            set.Want.Clear();
            set.Try.Clear();
            set.Stale.Clear();
            var now = _roundTime;
            int tested = 0, outCells = 0, coveredCells = 0;
            var round = _roundId;
            var cells = set.CellStart.Length - 1;
            for (var c = 0; c < cells; c++)
            {
                int from = set.CellStart[c], to = set.CellStart[c + 1];
                var cellMin = set.CellMin[c];
                var cellMax = set.CellMax[c];
                set.CellState[c] = CellOut;
                if (OutOfView(cellMin, cellMax))
                {
                    // Unity doesn't draw any of it.
                    outCells++;
                    for (var i = from; i < to; i++)
                    {
                        if (set.Streak[i] != 0 || set.Off[i] != null) KeepOutOfView(set, i, eye, round);
                    }

                    continue;
                }

                // Nothing in it due (all showed a moment ago, or covered with the eye still): skip.
                DueNow.Clear();
                for (var i = from; i < to; i++)
                {
                    if (Due(set, i, eye, now)) DueNow.Add(i);
                    else if (set.Streak[i] >= HideAfter)
                    {
                        Wanted(set, i, round);
                        // A cell is often only partly in view: only what's in view itself is worth a
                        // try, and only one Apply will try (not hidden yet, not waiting to retry).
                        if (set.Off[i] == null && set.RetryAt[i] <= _roundGameTime && !OutOfView(set.Min[i], set.Max[i])) set.Try.Add(i);
                    }
                }

                set.CellState[c] = CellIdle;
                if (DueNow.Count == 0) continue;
                // Most cells are far: then none of their objects is near either.
                var cellNear = Near(cellMin, cellMax, eye);

                // The whole cell's box covered: so is every box inside it.
                if (DueNow.Count >= CellTestMin && BoxWhy(cellMin, cellMax, ref m) == WhyCovered)
                {
                    coveredCells++;
                    set.CellState[c] = CellCovered;
                    var slack = _usedScope ? 0f : Slack(Pyramid.NearestCover);
                    foreach (var i in DueNow)
                    {
                        if (cellNear && Near(set.Min[i], set.Max[i], eye))
                        {
                            set.Why[i] = WhyNear;
                            set.Streak[i] = 0;
                            continue;
                        }

                        // In view only matters for one Apply will try (hidden ones stay hidden).
                        MarkCovered(set, i, eye, now, slack, round, set.Off[i] == null && !OutOfView(set.Min[i], set.Max[i]));
                    }

                    continue;
                }

                set.CellState[c] = CellTested;
                foreach (var i in DueNow)
                {
                    tested++;
                    var why = cellNear && Near(set.Min[i], set.Max[i], eye) ? WhyNear : BoxWhy(set.Min[i], set.Max[i], ref m);
                    // While the scope's depth is late (a round without it every so often) the last
                    // verdict stands: only what was last seen showing is kept drawn. Next to the
                    // scope's picture it's always drawn: the last verdict there may have come from
                    // the main depth seeing the scope itself.
                    if (why == WhyScopeNoDepth && set.Streak[i] != 0) why = WhyCovered;
                    if (why == WhyCovered)
                    {
                        MarkCovered(set, i, eye, now, _usedScope ? 0f : Slack(Pyramid.NearestCover), round, true);
                        continue;
                    }

                    // Hidden and now out of view, in a cell partly in view: kept like the ones in
                    // cells out of view.
                    if (why == WhyOutOfView && set.Off[i] != null)
                    {
                        KeepOutOfView(set, i, eye, round);
                        if (set.Streak[i] != 0) set.NextCheck[i] = now + VisibleRecheck * (0.75f + (i & 15) / 30f);
                        continue;
                    }

                    if (why == WhyMoving) _movingRound++;
                    set.Why[i] = why;
                    set.Streak[i] = 0;
                    set.NextCheck[i] = now + VisibleRecheck * (0.75f + (i & 15) / 30f);
                    // Covered again later, it's asked at once (Apply), not as a retry.
                    set.RetryAt[i] = 0f;
                }
            }

            // Lamps: covered HideAfter rounds in a row (as objects), shown on the first that isn't.
            // Only the part on screen is checked; where the box lands is kept for the turn check.
            for (var l = 0; l < _lamps.Length; l++)
            {
                var covered = _lampTest[l] && BoxWhy(_lampMin[l], _lampMax[l], ref m) == WhyCovered;
                if (covered && Project(_lampMin[l], _lampMax[l], ref m, out var lx0, out var ly0, out var lx1, out var ly1, out _, out _))
                    _lampRect[l] = new Vector4(lx0, ly0, lx1, ly1);
                _lampStreak[l] = covered ? (byte)Math.Min(_lampStreak[l] + 1, HideAfter) : (byte)0;
                _lampWant[l] = _lampStreak[l] >= HideAfter;
            }

            CellsOut = outCells;
            CellsCovered = coveredCells;
            Cells = cells;
            Tested = tested;
            CoveredCount = set.Want.Count;

            WorkerMs = (Stopwatch.GetTimestamp() - start) * Diagnostics.TickMs;
            Rounds++;
            ScopeTested = _scopeTestedRound;
            MovingKept = _movingRound;
            Status = $"{_width}x{_height} depth (arrives after {_arriveFrames:0.0} frames, {_arriveMs:0.0} ms; results applied {_appliedAge:0.0} frames old, one every {_resultEvery:0.0} frames), "
                     + $"{Tested} tested; cells: {CellsOut} of {Cells} out of view, {CellsCovered} covered whole"
                     + (_roundReach.sqrMagnitude > 0.0001f ? $"; moving {_roundReach.x:0.00} m sideways, {_roundReach.y:0.00} up, {_roundReach.z:0.00} forward before the next result, {MovingKept} kept drawn for it, {GearParts} parts of what you hold not counted as sliding cover" + (MovingKept > 0 ? $" (grown up to {_movingGrow:0.0} texels, cover as near as {_movingCover:0.0} m)" : "") : "")
                     + (_roundLamps ? $"; lamps: {LampsOff} of {_lamps.Length} skipped (their lit area covered)" : "")
                     + (_roundScoped ? _roundScopeDepth ? $"; scope: {_scopeWidth}x{_scopeHeight} depth, {ScopeTested} checked against it, view {ScopeFov:0.0}°, lead {ScopeLeadDegrees:0.0}°" : "; scope: no depth of its own, its view stays drawn" : "");
        }
        catch (Exception ex)
        {
            _error = ex;
        }

        Volatile.Write(ref _state, Ready);
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

    // Showed: checked again after a while. Covered: turning changes nothing, so only once the
    // eye moved enough to shift its nearest cover by a texel (or after a while: doors, bots).
    // Covered but not drawn by the game when Apply last asked (most covered objects): nothing
    // to hide or show, so not checked until it's asked again; then it's checked in that round,
    // so the answer it's hidden on is fresh.
    private static bool Due(Set set, int i, Vector3 eye, float now) =>
        (set.Off[i] != null || set.RetryAt[i] <= _roundGameTime)
        && (now >= set.NextCheck[i] || set.Streak[i] != 0 && (eye - set.CheckedFrom[i]).sqrMagnitude >= set.Slack[i]);

    // The current cell's due objects (worker only).
    private static readonly List<int> DueNow = new();

    private static void Wanted(Set set, int i, int round)
    {
        set.Want.Add(i);
        set.WantedIn[i] = round;
    }

    // Out of view, so it can't be checked: a covered one stays wanted hidden until the eye
    // has moved enough that its cover could have shifted, then it's checked again once back in
    // view. One still hidden then stays hidden too (showing it would cost a switch per part
    // for something Unity isn't drawing, and hiding it again two more checks) and is listed
    // stale: the main thread shows it the frame it comes back into view (ShowEntering).
    private static void KeepOutOfView(Set set, int i, Vector3 eye, int round)
    {
        if (set.Streak[i] != 0 && (eye - set.CheckedFrom[i]).sqrMagnitude >= set.Slack[i])
        {
            set.Streak[i] = 0;
            set.NextCheck[i] = 0f;
        }

        if (set.Streak[i] >= HideAfter) Wanted(set, i, round);
        else if (set.Off[i] != null)
        {
            Wanted(set, i, round);
            set.Stale.Add(i);
        }
    }

    private static void MarkCovered(Set set, int i, Vector3 eye, float now, float slack, int round, bool inView)
    {
        set.Why[i] = WhyCovered;
        // Not confirmed yet: checked again next round, not after the usual wait.
        set.NextCheck[i] = set.Streak[i] + 1 < HideAfter ? now : now + VisibleRecheck * (0.75f + (i & 15) / 30f);
        set.CheckedFrom[i] = eye;
        set.Slack[i] = slack * slack;
        if (set.Streak[i] < HideAfter) set.Streak[i]++;
        if (set.Streak[i] < HideAfter) return;
        Wanted(set, i, round);
        // Due means not waiting to retry (or hidden already).
        if (inView && set.Off[i] == null) set.Try.Add(i);
    }

    // How far the eye may move before the nearest cover (its depth, reversed: d = p23 / v - p22)
    // shifts by a texel.
    private static float Slack(float coverDepth)
    {
        var cover = coverDepth + _p22;
        return cover > 0f ? _p23 / cover * _texelAngle : 0f;
    }

    private static bool Near(Vector3 min, Vector3 max, Vector3 eye)
    {
        var dx = Math.Max(Math.Max(min.x - eye.x, eye.x - max.x), 0f);
        var dy = Math.Max(Math.Max(min.y - eye.y, eye.y - max.y), 0f);
        var dz = Math.Max(Math.Max(min.z - eye.z, eye.z - max.z), 0f);
        return dx * dx + dy * dy + dz * dz < _minDistance * _minDistance;
    }

    // The left, right, bottom and top planes of the round's view, worked out once per round:
    // the box test runs for tens of thousands of objects. The worker's; the main thread has
    // its own (ViewPlanes).
    private static readonly Vector4[] Planes = new Vector4[4];

    private static void SetPlanes(Vector4[] planes, ref Matrix4x4 m)
    {
        planes[0] = new Vector4(m.m30 + m.m00, m.m31 + m.m01, m.m32 + m.m02, m.m33 + m.m03);
        planes[1] = new Vector4(m.m30 - m.m00, m.m31 - m.m01, m.m32 - m.m02, m.m33 - m.m03);
        planes[2] = new Vector4(m.m30 + m.m10, m.m31 + m.m11, m.m32 + m.m12, m.m33 + m.m13);
        planes[3] = new Vector4(m.m30 - m.m10, m.m31 - m.m11, m.m32 - m.m12, m.m33 - m.m13);
    }

    private static bool OutOfView(Vector3 min, Vector3 max) => OutOfView(Planes, min, max);

    // The box against the left, right, bottom and top planes of a view (SetPlanes). Together
    // they also leave out what's wholly behind the eye.
    private static bool OutOfView(Vector4[] planes, Vector3 min, Vector3 max)
    {
        float cx = (min.x + max.x) * 0.5f, cy = (min.y + max.y) * 0.5f, cz = (min.z + max.z) * 0.5f;
        float ex = (max.x - min.x) * 0.5f, ey = (max.y - min.y) * 0.5f, ez = (max.z - min.z) * 0.5f;
        for (var p = 0; p < 4; p++)
        {
            var q = planes[p];
            if (q.x * cx + q.y * cy + q.z * cz + q.w + Math.Abs(q.x) * ex + Math.Abs(q.y) * ey + Math.Abs(q.z) * ez < 0f) return true;
        }

        return false;
    }

    // A box against the depth: out of view, partly behind the camera, covered, or why not.
    // Covered on the main screen but in the scope's area: also covered in the scope's own
    // depth, or it stays drawn.
    private static byte BoxWhy(Vector3 min, Vector3 max, ref Matrix4x4 m)
    {
        _usedScope = false;
        if (OutOfView(min, max)) return WhyOutOfView;
        if (!Project(min, max, ref m, out var xMin, out var yMin, out var xMax, out var yMax, out var nearest, out var farthest)) return WhyBehindCamera;
        float growX = BoxGrowTexels / _width, growY = BoxGrowTexels / _height;
        var result = Pyramid.Test(xMin - growX, yMin - growY, xMax + growX, yMax + growY, nearest);
        if (result == DepthCover.OffScreen) return WhyOutOfView;
        if (result != DepthCover.Covered) return WhyShowsBehind;
        if (_roundScoped && xMax >= _scopeXMin && xMin <= _scopeXMax && yMax >= _scopeYMin && yMin <= _scopeYMax)
        {
            // The scope's area has its own lead (ScopeCovers); the main depth only sees the scope there.
            _usedScope = true;
            return ScopeCovers(min, max);
        }

        // Moving: grown by how far the nearest cover under it can slide across it before the next
        // result, only on the side that can open up. Moving right, near cover slides left past
        // what's behind it, so a gap on the box's right comes towards it (left: the other way).
        // Forward, near cover spreads out from the middle faster, so a gap on the box's side
        // towards the middle comes towards it (back: the outer side). Up and down: both sides
        // (which way is up on the depth image isn't worth getting wrong).
        if (_roundReach == Vector3.zero) return WhyCovered;
        // World cover only: the player's gun moves with the eye, so its edge doesn't slide; behind it
        // things only shift by their own parallax (OwnShiftTexels).
        var cover = Pyramid.NearestCover;
        var gear = Pyramid.GearCover;
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
        if (Pyramid.Test(xMin - growLeft, yMin - growY, xMax + growRight, yMax + growY, nearest) == DepthCover.Covered) return WhyCovered;
        // For the status line (built on this worker).
        _movingGrow = Math.Max(_movingGrow, Math.Max(Math.Max(slideRight, slideLeft), slideY));
        if (cover > 0f && cover + _p22 > 0f) _movingCover = Math.Min(_movingCover, _p23 / (cover + _p22));
        return WhyMoving;
    }

    // In the scope's own depth: wholly inside its picture (part outside may swing in) and
    // covered there.
    private static byte ScopeCovers(Vector3 min, Vector3 max)
    {
        // Wholly outside what the scope sees now or soon: behind the lens on the main screen it's
        // only the scope itself, so the main depth's answer stands. The lens covers far more
        // than a zoomed scope's view, and all of that used to stay drawn.
        var view = _roundScopeView;
        if (Project(min, max, ref view, out var vxMin, out var vyMin, out var vxMax, out var vyMax, out _, out _)
            && (vxMax < 0.5f - _scopeReachX + Math.Min(0f, _scopeShiftX) || vxMin > 0.5f + _scopeReachX + Math.Max(0f, _scopeShiftX)
                || vyMax < 0.5f - _scopeReachY + Math.Min(0f, _scopeShiftY) || vyMin > 0.5f + _scopeReachY + Math.Max(0f, _scopeShiftY))) return WhyCovered;
        if (!_roundScopeDepth) return WhyScopeNoDepth;
        _scopeTestedRound++;
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
        return ScopePyramid.Test(xMin, yMin, xMax, yMax, nearest) switch
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

        // Then hiding. An object asked for the first time since it last showed (or whose baked
        // group the game just switched back on) is asked at once: most of those are being
        // drawn. Re-asking ones the game wasn't drawing gets RetryBudget. New hides are capped
        // so turning towards thousands of covered objects doesn't stall a frame; the rest are
        // still covered next check.
        var want = set.Try;
        int hides = HidesPerCheck, waiting = 0;
        var now = Time.time;
        var retryUntil = Stopwatch.GetTimestamp() + RetryBudget;
        var retriesOut = false;
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

            if (hides <= 0 || retryAt > 0f && (retriesOut || (retriesOut = Stopwatch.GetTimestamp() >= retryUntil)))
            {
                // Out of budget: the next check starts here.
                if (waiting++ == 0) _applyFrom = from + n;
                continue;
            }

            // Hiding what the game isn't drawing saves nothing and costs a switch per renderer;
            // those are asked again a little later, spread so they don't all come due at once.
            if (Hide(set, i)) hides--;
            else set.RetryAt[i] = now + NotDrawnRetry * (0.75f + (i & 15) / 30f);
        }

        Waiting = waiting;
    }

    // A part Hide left alone (destroyed, shadow-only, or switched off by someone else).
    private const ShadowCastingMode NotSwitched = (ShadowCastingMode)(-1);

    private static bool Drawn(Renderer renderer) => renderer != null && renderer.isVisible;

    private static bool Hide(Set set, int i)
    {
        // Drawn at all? One call per part, stopping at the first yes, starting with the part
        // that was drawn last time.
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
        for (var n = 0; n < renderers.Length; n++)
        {
            was[n] = NotSwitched;
            var renderer = renderers[n];
            if (renderer == null) continue;
            var mode = renderer.shadowCastingMode;
            if (HidesShadows)
            {
                if (renderer.forceRenderingOff) continue;
                renderer.forceRenderingOff = true;
                was[n] = ShadowCastingMode.ShadowsOnly;
            }
            else if (mode == ShadowCastingMode.ShadowsOnly) continue;
            else if (mode != ShadowCastingMode.Off)
            {
                renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                was[n] = mode;
            }
            else if (!renderer.forceRenderingOff)
            {
                renderer.forceRenderingOff = true;
                was[n] = ShadowCastingMode.ShadowsOnly;
            }
            else continue;

            any = true;
        }

        if (!any) return false;
        set.Off[i] = renderers;
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
            if (was[n] == ShadowCastingMode.ShadowsOnly) renderer.forceRenderingOff = false;
            else if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) renderer.shadowCastingMode = was[n];
        }

        set.Off[i] = null;
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

    private static void ShowEntering(Set set, Camera camera)
    {
        var m = camera.cullingMatrix;
        SetPlanes(ViewPlanes, ref m);
        var kept = 0;
        for (var n = 0; n < StaleUnits.Count; n++)
        {
            var i = StaleUnits[n];
            if (set.Off[i] == null) continue;
            if (OutOfView(ViewPlanes, set.Min[i], set.Max[i])) StaleUnits[kept++] = i;
            else Show(set, i);
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
        private static void Prefix(PerfectCullingCrossSceneGroup __instance, FastArray<ushort> ____queueActivate)
        {
            var set = _set;
            if (set == null || Time.frameCount - _activeFrame > 1 || ____queueActivate == null) return;
            try
            {
                var groups = __instance.bakeGroups;
                var count = ____queueActivate.Count;
                var buffer = ____queueActivate.Buffer;
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
        Tracked = 0;
        Hidden = 0;
        _gameSwitched = new HashSet<Renderer>();
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
        _scopeNextFrame = -1000;
        ScopeTested = 0;
    }
}
