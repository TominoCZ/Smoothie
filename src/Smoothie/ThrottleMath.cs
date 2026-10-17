using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security;

namespace Smoothie;

// No Unity types in here, so the self-check can compile this file on its own.
public static class ThrottleMath
{
    // How big something at `fov` degrees looks compared to the player's own FOV setting.
    // Both are vertical FOVs, which is what Unity's Camera.fieldOfView holds.
    public static float Zoom(float referenceFov, float fov)
    {
        if (fov <= 0f || referenceFov <= 0f) return 1f;
        var zoom = Math.Tan(referenceFov * Math.PI / 360.0) / Math.Tan(fov * Math.PI / 360.0);
        return (float)Math.Max(1.0, zoom);
    }

    // Seconds between animator steps. 0 means every frame (left to Unity).
    public static float Interval(
        float distance,
        bool onScreen,
        float zoom,
        float audibleRange,
        float fullRateDistance,
        float minRateDistance,
        float minVisibleHz,
        float offscreenHz,
        float footstepHz)
    {
        float interval;
        if (onScreen)
        {
            // Through a 6x scope a bot at 300 m fills the screen like one at 50 m.
            var apparent = distance / Math.Max(1f, zoom);
            var t = minRateDistance > fullRateDistance
                ? (apparent - fullRateDistance) / (minRateDistance - fullRateDistance)
                : (apparent > fullRateDistance ? 1f : 0f);
            t = Math.Min(1f, Math.Max(0f, t));
            interval = t / Math.Max(1f, minVisibleHz);
        }
        else
        {
            interval = 1f / Math.Max(1f, offscreenHz);
        }

        // Footsteps fire when the animator's FootStep curve changes sign, so the animator
        // has to keep sampling fast enough for every step to land on time.
        if (distance <= audibleRange)
        {
            interval = Math.Min(interval, 1f / Math.Max(1f, footstepHz));
        }

        return interval;
    }
}

// When Occlusion casts its sight lines for one bot, and what it concludes in between.
public sealed class Sight
{
    // Rays at most this often per bot, stretched by the bot's jitter so bots drift apart. A bot
    // stepping out of cover runs at full rate within about this long (was 0.1 s: a visible
    // moment of low-rate animation; a check is ~10 lines for a blocked bot, a few µs each).
    public const float CheckInterval = 1f / 30f;

    // Once a line gets through, the bot counts as seen this long without casting again,
    // unless it leaves the view: back in view, it is checked again right away.
    public const float SeenHold = 0.5f;

    // Blocked this long before a bot counts as hidden. Coming back into view is instant.
    public const float HideDelay = 0.25f;

    private readonly Func<bool> _anyLineClear;
    private readonly float _jitter;
    private float _nextCheck;
    private float _seenUntil;

    // When the bot was first blocked; -1 while it isn't.
    private float _blockedSince = -1f;

    // When the bot left the view; -1 while in view.
    private float _outSince = -1f;

    public Sight(Func<bool> anyLineClear, float jitter)
    {
        _anyLineClear = anyLineClear;
        _jitter = jitter;
    }

    // Whether the bot is hidden at `now`. Lines are only cast for a bot in view, not seen
    // lately, and due for a check.
    public bool Hidden(float now, bool inView)
    {
        bool blocked;
        if (!inView)
        {
            blocked = true;
            _seenUntil = 0f;
            if (_outSince < 0f) _outSince = now;
        }
        else
        {
            // Back in view after a while: checked on that frame, not at the next scheduled check
            // (a bot flickering at the screen's edge with your sway keeps to the schedule).
            if (_outSince >= 0f && now - _outSince >= CheckInterval) _nextCheck = 0f;
            _outSince = -1f;
            if (now < _seenUntil) blocked = false;
            else if (now < _nextCheck) blocked = _blockedSince >= 0f;
            else
            {
                _nextCheck = now + CheckInterval * _jitter;
                blocked = !_anyLineClear();
                if (!blocked) _seenUntil = now + SeenHold;
            }
        }

        if (!blocked) _blockedSince = -1f;
        else if (_blockedSince < 0f) _blockedSince = now;
        return blocked && now - _blockedSince >= HideDelay;
    }
}

// "Prevent memory cleanup stutters in raids". The game pauses the GC for a raid; SPT switches it
// back on with a collection on every inventory open and leaves it on, so the rest of the raid
// gets a collection (a 70+ ms hitch on a ~5 GB heap) every minute or two. Let that collection
// finish, then pause again; if garbage piles up anyway, run one the same way.
public sealed class CleanupPause
{
    public enum Action { None, Pause, Collect }

    // Something switching it on every frame isn't fought every frame.
    public const float MinGap = 5f;

    // Garbage allowed to pile up before a collection is started here, for raids where the
    // inventory never opens.
    public const long Headroom = 4L << 30;

    // Switched back on within this many frames of a pause, this many times in a row: something
    // else switches it on every frame (another mod). It's left on for the rest of the raid
    // instead of fought all raid. Frames, not seconds: SPT switches it on with every inventory
    // open, and nobody reopens the inventory a frame or two after its cleanup finished.
    public const int FightFrames = 3;
    public const int FightsToGiveUp = 3;

    public bool GaveUp { get; private set; }
    private int _fights;
    private bool _waiting;
    private int _collectionsAtOn;
    private float _lastPause = -100f;
    private int _lastPauseFrame = -100;
    private float _lastHeapCheck = -100f;
    private long _ceiling = long.MaxValue;

    public void Start(long heap)
    {
        _waiting = false;
        _ceiling = heap + Headroom;
        _lastPause = -100f;
        _lastPauseFrame = -100;
        _fights = 0;
        GaveUp = false;
    }

    public Action Tick(bool gcOn, int collections, Func<long> heap, float now, int frame)
    {
        if (GaveUp) return Action.None;
        if (!gcOn)
        {
            _waiting = false;
            // Reading the heap size isn't free; once a second is plenty for a 4 GB margin.
            if (now - _lastHeapCheck < 1f) return Action.None;
            _lastHeapCheck = now;
            if (heap() < _ceiling) return Action.None;
            _ceiling = long.MaxValue;
            // Switched on here, not by someone else: not a fight.
            _lastPauseFrame = -100;
            return Action.Collect;
        }

        // Switched on: wait for a collection to finish. If none ever does, it stays on,
        // which is SPT's own behaviour.
        if (!_waiting)
        {
            _fights = frame - _lastPauseFrame <= FightFrames ? _fights + 1 : 0;
            if (_fights >= FightsToGiveUp)
            {
                GaveUp = true;
                return Action.None;
            }

            _waiting = true;
            _collectionsAtOn = collections;
            return Action.None;
        }

        if (collections == _collectionsAtOn || now - _lastPause < MinGap) return Action.None;
        _waiting = false;
        _lastPause = now;
        _lastPauseFrame = frame;
        _ceiling = heap() + Headroom;
        return Action.Pause;
    }
}

// The depth test for covered objects, kept free of Unity types so the self-check can run it.
// The depth is a small mip of the game's depth pyramid (each texel the farthest depth of its
// block): stored reversed (larger is nearer, 0 is the sky) and upside down (row 0 is the
// bottom of the screen). A box is covered when every texel under it holds something nearer
// than the box's nearest point.
public static class DepthCover
{
    public const int Covered = 0, ShowsBehind = 1, OffScreen = 2;

    // Every texel the box touches (and no more: the texel holding its far edge, not the next).
    public static int Test(float[] depth, int width, int height, float xMin, float yMin, float xMax, float yMax, float boxNearest)
    {
        if (xMax <= 0f || yMax <= 0f || xMin >= 1f || yMin >= 1f) return OffScreen;
        var x0 = Math.Max(0, (int)Math.Floor(xMin * width));
        var x1 = Math.Min(width - 1, Math.Max(x0, (int)Math.Ceiling(xMax * width) - 1));
        var y0 = Math.Max(0, (int)Math.Floor(yMin * height));
        var y1 = Math.Min(height - 1, Math.Max(y0, (int)Math.Ceiling(yMax * height) - 1));
        for (var y = y0; y <= y1; y++)
        {
            var row = (height - 1 - y) * width;
            for (var x = x0; x <= x1; x++)
            {
                if (depth[row + x] <= boxNearest) return ShowsBehind;
            }
        }

        return Covered;
    }
}

// The depth image reduced into coarser and coarser levels, each texel the farthest and the
// nearest of the four below it. A box is first looked at on the level where it spans at most
// 4x4 texels: a texel whose farthest depth is nearer than the box decides its whole block as
// cover, one lying wholly inside the box with anything farther than the box shows it; only
// texels that can't decide are looked at one level finer. The answer is always
// DepthCover.Test's on the fine image.
//
// Run on the worker for every box of every check, so written for the game's Mono JIT, which
// keeps array bounds checks and rarely inlines: the coarse levels sit in one array per kind,
// pinned once per test and walked with pointers, and min/max are plain comparisons.
//
// With UseNative (SmoothieNative, src/SmoothieNative/pyramid.c: the same build and test compiled
// with SSE2) Build and Test go there instead, over the same arrays, pinned for good; the code
// below stays as the fallback and as the reference the self-check compares it against.
public sealed unsafe class DepthPyramid
{
    private const string Native = "SmoothieNative";

    [DllImport(Native, CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
    private static extern int sm_version();

    [DllImport(Native, CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
    private static extern void sm_build(float* fine, float* fineWorld, bool* fineGear, int levels, int* offset, int* wid, int* hei,
        float* far, float* nearWorld, bool* gearAny);

    [DllImport(Native, CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
    private static extern int sm_test(NativeLevels* pyramid, float xMin, float yMin, float xMax, float yMax, float boxNearest,
        out float nearestCover, out int gearCover);

    // pyramid.c's Pyramid: pointers into this pyramid's pinned arrays, written after each Build.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLevels
    {
        public float* Depth, Far, NearWorld;
        public bool* GearMask, GearAny;
        public int* Offset, Wid;
        public int Levels, Width, Height;
    }

    // Set once the library answered (TryNative); the same for every pyramid.
    public static bool UseNative { get; set; }

    // The library must already be loadable by its name (in the game: loaded from the plugin's
    // folder first, see DepthCulling). False, and the C# code stays in use, if it isn't.
    public static bool TryNative()
    {
        try
        {
            UseNative = sm_version() == 2;
        }
        catch (Exception)
        {
            UseNative = false;
        }

        return UseNative;
    }

    // Arrays the native side reads, pinned while they're in use; the levels' struct.
    private readonly Dictionary<Array, GCHandle> _pinned = new();
    private NativeLevels* _native;

    private T* Pin<T>(T[] array) where T : unmanaged
    {
        if (!_pinned.TryGetValue(array, out var handle)) _pinned[array] = handle = GCHandle.Alloc(array, GCHandleType.Pinned);
        return (T*)handle.AddrOfPinnedObject();
    }

    // A replaced array: let it go.
    private void Unpin(Array array)
    {
        if (!_pinned.TryGetValue(array, out var handle)) return;
        handle.Free();
        _pinned.Remove(array);
    }

    // Texels a covered box must grow by so its cover's edge can't slide off it while the eye
    // moves `reach` metres: parallax r * (1/cover - 1/far), with 1/distance of a depth value d
    // being (d + p22) / p23 (larger d is nearer). Taken at the box's farthest point, it also
    // holds for every box inside a bigger one (their cover is no nearer, their far point no farther).
    public static float MoveGrowTexels(float reach, float coverDepth, float farDepth, float p23, float texelAngle, float cap) =>
        Math.Min(Math.Max(0f, reach * (coverDepth - farDepth) / p23 / texelAngle), cap);

    // Behind gear that moves with the eye (the player's gun): the gear's edge stays put on screen,
    // what's behind it shifts by its own parallax only, r / distance, largest at its nearest point.
    public static float OwnShiftTexels(float reach, float nearestDepth, float p22, float p23, float texelAngle, float cap) =>
        Math.Min(Math.Max(0f, reach * (nearestDepth + p22) / p23 / texelAngle), cap);

    // Parts of the player's own gear on screen: a texel inside a part's box whose depth lies within
    // that part's depth range is the part, not the world. Such cover isn't taken as NearestCover,
    // only noted in GearCover. Worked out once per Build, into every level (the world's nearest
    // depth in each block, and whether gear is in it), so a test costs the same with gear as without.
    private readonly List<(float XMin, float YMin, float XMax, float YMax, float Near, float Far)> _gearOnScreen = new();
    private bool[] _gearMask = Array.Empty<bool>();
    private float[] _nearWorld0 = Array.Empty<float>();

    public void ClearGear() => _gearOnScreen.Clear();

    // Screen box 0-1 and the depth of its nearest and farthest point (larger is nearer), placed at
    // the next Build, with 10% to spare on the depth (the parts' boxes are loose already).
    public void AddGear(float xMin, float yMin, float xMax, float yMax, float near, float far) =>
        _gearOnScreen.Add((xMin, yMin, xMax, yMax, near * 1.1f, far * 0.9f));

    // The fine level: which texels are gear, and the depth with gear left out (none: MinValue).
    private void PlaceGear(float[] depth, int width, int height)
    {
        var n = width * height;
        if (_gearMask.Length != n)
        {
            Unpin(_gearMask);
            Unpin(_nearWorld0);
            _gearMask = new bool[n];
            _nearWorld0 = new float[n];
        }

        Array.Clear(_gearMask, 0, n);
        foreach (var g in _gearOnScreen)
        {
            if (g.XMax <= 0f || g.YMax <= 0f || g.XMin >= 1f || g.YMin >= 1f) continue;
            int x0 = Math.Max(0, (int)Math.Floor(g.XMin * width)), x1 = Math.Min(width - 1, (int)Math.Ceiling(g.XMax * width) - 1);
            int y0 = Math.Max(0, (int)Math.Floor(g.YMin * height)), y1 = Math.Min(height - 1, (int)Math.Ceiling(g.YMax * height) - 1);
            // Rows as stored: upside down.
            for (var r = height - 1 - y1; r <= height - 1 - y0; r++)
            for (var x = x0; x <= x1; x++)
            {
                var d = depth[r * width + x];
                if (d <= g.Near && d >= g.Far) _gearMask[r * width + x] = true;
            }
        }

        for (var i = 0; i < n; i++) _nearWorld0[i] = _gearMask[i] ? float.MinValue : depth[i];
    }

    // Levels 1 and up, each in one array: level L's texel (x, r) is at Offset[L] + r * Wid[L] + x.
    // Far: the farthest depth below it; NearWorld: the nearest one that isn't gear; GearAny: gear below.
    private float[] _far = Array.Empty<float>(), _nearWorld = Array.Empty<float>();
    private bool[] _gearAny = Array.Empty<bool>();
    private readonly int[] _offset = new int[32], _wid = new int[32], _hei = new int[32];
    private float[] _depth = Array.Empty<float>();
    private float[]? _lastDepth;
    private int _levels, _width, _height;

    public void Build(float[] depth, int width, int height)
    {
        _depth = depth;
        PlaceGear(depth, width, height);
        int levels = 1, w = width, h = height, total = 0;
        _wid[0] = width;
        _hei[0] = height;
        while (w > 1 || h > 1)
        {
            w = (w + 1) / 2;
            h = (h + 1) / 2;
            _offset[levels] = total;
            _wid[levels] = w;
            _hei[levels] = h;
            total += w * h;
            levels++;
        }

        // 4 to spare: the native test reads a row 4 texels at a time.
        if (_far.Length < total + 4)
        {
            Unpin(_far);
            Unpin(_nearWorld);
            Unpin(_gearAny);
            _far = new float[total + 4];
            _nearWorld = new float[total + 4];
            _gearAny = new bool[total + 4];
        }

        if (UseNative)
        {
            if (!ReferenceEquals(_lastDepth, depth))
            {
                if (_lastDepth != null) Unpin(_lastDepth);
                _lastDepth = depth;
            }

            var offset = Pin(_offset);
            var wid = Pin(_wid);
            sm_build(Pin(depth), Pin(_nearWorld0), Pin(_gearMask), levels, offset, wid, Pin(_hei), Pin(_far), Pin(_nearWorld), Pin(_gearAny));
            if (_native == null) _native = (NativeLevels*)Marshal.AllocHGlobal(sizeof(NativeLevels));
            *_native = new NativeLevels
            {
                Depth = Pin(depth), Far = Pin(_far), NearWorld = Pin(_nearWorld), GearMask = Pin(_gearMask), GearAny = Pin(_gearAny),
                Offset = offset, Wid = wid, Levels = levels, Width = width, Height = height,
            };
            _levels = levels;
            _width = width;
            _height = height;
            return;
        }

        fixed (float* fine = depth, fineWorld = _nearWorld0, far = _far, nearWorld = _nearWorld)
        fixed (bool* fineGear = _gearMask, gearAny = _gearAny)
        {
            for (var level = 1; level < levels; level++)
            {
                int cw = _wid[level - 1], ch = _hei[level - 1], pw = _wid[level], ph = _hei[level];
                float* srcFar = level == 1 ? fine : far + _offset[level - 1];
                float* srcWorld = level == 1 ? fineWorld : nearWorld + _offset[level - 1];
                bool* srcGear = level == 1 ? fineGear : gearAny + _offset[level - 1];
                float* dstFar = far + _offset[level];
                float* dstWorld = nearWorld + _offset[level];
                bool* dstGear = gearAny + _offset[level];
                for (var r = 0; r < ph; r++)
                {
                    // Odd sizes: the last row or column has no partner; it pairs with itself.
                    var down = r * 2 + 1 < ch ? cw : 0;
                    for (var x = 0; x < pw; x++)
                    {
                        var i = r * 2 * cw + x * 2;
                        var right = x * 2 + 1 < cw ? 1 : 0;
                        int i1 = i + right, j = i + down, j1 = j + right;
                        var lo = Math.Min(Math.Min(srcFar[i], srcFar[i1]), Math.Min(srcFar[j], srcFar[j1]));
                        var hi = Math.Max(Math.Max(srcWorld[i], srcWorld[i1]), Math.Max(srcWorld[j], srcWorld[j1]));
                        var gear = srcGear[i] | srcGear[i1] | srcGear[j] | srcGear[j1];
                        var t = r * pw + x;
                        dstFar[t] = lo;
                        dstWorld[t] = hi;
                        dstGear[t] = gear;
                    }
                }
            }
        }

        _levels = levels;
        _width = width;
        _height = height;
    }

    public int Width => _width;
    public int Height => _height;

    // One test's state, on the caller's stack so several threads can test at once: the box's
    // fine texel range and nearest depth, the pinned arrays, and what covers it.
    private struct Probe
    {
        public int X0, X1, R0, R1;
        public float BoxNearest, NearestCover;
        public bool GearCover;
        public float* Depth, Far, NearWorld;
        public bool* GearMask, GearAny;
        public int* Offset, Wid;
    }

    // After a Covered result of the single-threaded Test: the depth of the nearest cover under
    // the box (larger is nearer), and whether some of the cover was gear.
    private float _nearestCover;
    private bool _gearCover;
    public float NearestCover => _nearestCover;
    public bool GearCover => _gearCover;

    public int Test(float xMin, float yMin, float xMax, float yMax, float boxNearest) =>
        Test(xMin, yMin, xMax, yMax, boxNearest, out _nearestCover, out _gearCover);

    // Thread-safe once built: the cover comes back through the out parameters.
    public int Test(float xMin, float yMin, float xMax, float yMax, float boxNearest, out float nearestCover, out bool gearCover)
    {
        if (_native != null && UseNative)
        {
            var result = sm_test(_native, xMin, yMin, xMax, yMax, boxNearest, out nearestCover, out var gear);
            gearCover = gear != 0;
            return result;
        }

        nearestCover = 0f;
        gearCover = false;
        int width = _width, height = _height;
        if (xMax <= 0f || yMax <= 0f || xMin >= 1f || yMin >= 1f) return DepthCover.OffScreen;
        var x0 = (int)Math.Floor(xMin * width);
        if (x0 < 0) x0 = 0;
        var x1 = (int)Math.Ceiling(xMax * width) - 1;
        if (x1 < x0) x1 = x0;
        if (x1 > width - 1) x1 = width - 1;
        var y0 = (int)Math.Floor(yMin * height);
        if (y0 < 0) y0 = 0;
        var y1 = (int)Math.Ceiling(yMax * height) - 1;
        if (y1 < y0) y1 = y0;
        if (y1 > height - 1) y1 = height - 1;
        // Rows as stored: upside down.
        var p = new Probe { X0 = x0, X1 = x1, R0 = height - 1 - y1, R1 = height - 1 - y0, BoxNearest = boxNearest };

        // Start where the box spans at most 4x4 texels, and look closer only inside texels that
        // can't decide: the work follows the edges of the cover, not the area of the box.
        var level = 0;
        while (level < _levels - 1 && ((p.X1 >> level) - (p.X0 >> level) > 3 || (p.R1 >> level) - (p.R0 >> level) > 3)) level++;
        fixed (float* depth = _depth, far = _far, nearWorld = _nearWorld)
        fixed (bool* gearMask = _gearMask, gearAny = _gearAny)
        fixed (int* offset = _offset, wid = _wid)
        {
            p.Depth = depth;
            p.Far = far;
            p.NearWorld = nearWorld;
            p.GearMask = gearMask;
            p.GearAny = gearAny;
            p.Offset = offset;
            p.Wid = wid;
            int rEnd = p.R1 >> level, xEnd = p.X1 >> level;
            for (var r = p.R0 >> level; r <= rEnd; r++)
            for (var x = p.X0 >> level; x <= xEnd; x++)
            {
                if (ShowsBehind(ref p, level, x, r)) return DepthCover.ShowsBehind;
            }
        }

        nearestCover = p.NearestCover;
        gearCover = p.GearCover;
        return DepthCover.Covered;
    }

    // True when something at or behind the box shows in this texel's part of the box.
    private bool ShowsBehind(ref Probe p, int level, int x, int r)
    {
        if (level == 0)
        {
            var i = r * _width + x;
            var d = p.Depth[i];
            if (d <= p.BoxNearest) return true;
            if (p.GearMask[i]) p.GearCover = true;
            else if (d > p.NearestCover) p.NearestCover = d;
            return false;
        }

        var t = p.Offset[level] + r * p.Wid[level] + x;
        // Everything below is nearer than the box: covered.
        if (p.Far[t] > p.BoxNearest)
        {
            var near = p.NearWorld[t];
            if (near > p.NearestCover) p.NearestCover = near;
            if (p.GearAny[t]) p.GearCover = true;
            return false;
        }

        // Lying wholly inside the box with something behind it somewhere below: it shows.
        int bx0 = x << level, by0 = r << level;
        int bx1 = ((x + 1) << level) - 1, by1 = ((r + 1) << level) - 1;
        if (bx1 > _width - 1) bx1 = _width - 1;
        if (by1 > _height - 1) by1 = _height - 1;
        if (bx0 >= p.X0 && bx1 <= p.X1 && by0 >= p.R0 && by1 <= p.R1) return true;

        var shift = level - 1;
        int rFrom = r * 2, rTo = r * 2 + 1, xFrom = x * 2, xTo = x * 2 + 1;
        if (p.R0 >> shift > rFrom) rFrom = p.R0 >> shift;
        if (p.R1 >> shift < rTo) rTo = p.R1 >> shift;
        if (p.X0 >> shift > xFrom) xFrom = p.X0 >> shift;
        if (p.X1 >> shift < xTo) xTo = p.X1 >> shift;
        for (var cr = rFrom; cr <= rTo; cr++)
        for (var cx = xFrom; cx <= xTo; cx++)
        {
            if (ShowsBehind(ref p, shift, cx, cr)) return true;
        }

        return false;
    }
}

// The worker's walk over one cell of covered-object checking (DepthCulling.TestCell): which
// objects are due for a depth test, and which stay wanted hidden without one. Every object of
// every cell, every round (tens of thousands on Streets), so with DepthPyramid.UseNative it runs
// in SmoothieNative (src/SmoothieNative/walk.c, SSE2); the C# below is the fallback and the
// reference the self-check compares it against. Both do the same, in the same order.
public static unsafe class CellWalk
{
    private const string Native = "SmoothieNative";

    // Per object, indexed alike (DepthCulling's Set, pinned by the caller). CheckedFrom, Min and
    // Max hold 3 floats each. Hidden: 1 while hidden (the main thread's, read as it is).
    [StructLayout(LayoutKind.Sequential)]
    public struct Units
    {
        public float* RetryAt, NextCheck, Slack, CheckedFrom, Min, Max;
        public byte* Streak, Hidden;
        public int* WantedIn;
    }

    [DllImport(Native, CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
    private static extern void sm_walk_in(Units* u, int from, int to, float* eye, float now, float gameTime, int round,
        float* planes, int hideAfter, int* due, int* want, int* maybeTry, int* counts);

    [DllImport(Native, CallingConvention = CallingConvention.Cdecl), SuppressUnmanagedCodeSecurity]
    private static extern int sm_walk_out(Units* u, int from, int to, float* eye, int round, int hideAfter,
        int* want, int* stale, int* counts);

    // A cell in view. Due: not hidden and waiting to retry, or past its next check, or covered and
    // the eye moved past its slack. Wanted (not due, covered hideAfter rounds): WantedIn set to
    // round, listed, and listed again in maybeTry when not hidden, not waiting to retry and in view
    // (planes: the view's left, right, bottom and top, 4 floats each). Counts out: due, want, maybeTry.
    public static void InView(Units* u, int from, int to, float* eye, float now, float gameTime, int round, float* planes,
        int hideAfter, int* due, int* want, int* maybeTry, int* counts)
    {
        if (DepthPyramid.UseNative)
        {
            sm_walk_in(u, from, to, eye, now, gameTime, round, planes, hideAfter, due, want, maybeTry, counts);
            return;
        }

        int nd = 0, nw = 0, nt = 0;
        for (var i = from; i < to; i++)
        {
            bool isDue;
            if (u->Hidden[i] == 0 && u->RetryAt[i] > gameTime) isDue = false;
            else if (now >= u->NextCheck[i]) isDue = true;
            else if (u->Streak[i] == 0) isDue = false;
            else isDue = Moved2(eye, u->CheckedFrom + i * 3) >= u->Slack[i];
            if (isDue) due[nd++] = i;
            else if (u->Streak[i] >= hideAfter)
            {
                u->WantedIn[i] = round;
                want[nw++] = i;
                if (u->Hidden[i] == 0 && u->RetryAt[i] <= gameTime && !OutOfView(planes, u->Min + i * 3, u->Max + i * 3)) maybeTry[nt++] = i;
            }
        }

        counts[0] = nd;
        counts[1] = nw;
        counts[2] = nt;
    }

    // A cell out of view: a covered object whose cover may have shifted (the eye moved past its
    // slack) is forgotten and checked again once back in view; covered ones stay wanted, hidden
    // ones too and are listed stale. True when nothing in it is covered or hidden. Counts out:
    // want, stale.
    public static bool OutOfViewCell(Units* u, int from, int to, float* eye, int round, int hideAfter, int* want, int* stale, int* counts)
    {
        if (DepthPyramid.UseNative) return sm_walk_out(u, from, to, eye, round, hideAfter, want, stale, counts) != 0;
        int nw = 0, ns = 0;
        var quiet = true;
        for (var i = from; i < to; i++)
        {
            if (u->Streak[i] == 0 && u->Hidden[i] == 0) continue;
            if (u->Streak[i] != 0 && Moved2(eye, u->CheckedFrom + i * 3) >= u->Slack[i])
            {
                u->Streak[i] = 0;
                u->NextCheck[i] = 0f;
            }

            if (u->Streak[i] >= hideAfter)
            {
                u->WantedIn[i] = round;
                want[nw++] = i;
            }
            else if (u->Hidden[i] != 0)
            {
                u->WantedIn[i] = round;
                want[nw++] = i;
                stale[ns++] = i;
            }

            quiet &= u->Streak[i] == 0 && u->Hidden[i] == 0;
        }

        counts[0] = nw;
        counts[1] = ns;
        return quiet;
    }

    private static float Moved2(float* eye, float* at)
    {
        float dx = eye[0] - at[0], dy = eye[1] - at[1], dz = eye[2] - at[2];
        return dx * dx + dy * dy + dz * dz;
    }

    // As DepthCulling's Sides.Out, same order of operations.
    public static bool OutOfView(float* planes, float* min, float* max)
    {
        float cx = (min[0] + max[0]) * 0.5f, cy = (min[1] + max[1]) * 0.5f, cz = (min[2] + max[2]) * 0.5f;
        float ex = (max[0] - min[0]) * 0.5f, ey = (max[1] - min[1]) * 0.5f, ez = (max[2] - min[2]) * 0.5f;
        for (var k = 0; k < 4; k++)
        {
            var p = planes + k * 4;
            if (p[0] * cx + p[1] * cy + p[2] * cz + p[3] + Math.Abs(p[0]) * ex + Math.Abs(p[1]) * ey + Math.Abs(p[2]) * ez < 0f) return true;
        }

        return false;
    }
}
