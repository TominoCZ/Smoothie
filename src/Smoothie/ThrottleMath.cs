using System;
using System.Collections.Generic;

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
    // Rays at most this often per bot, stretched by the bot's jitter so bots drift apart.
    public const float CheckInterval = 0.1f;

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
        }
        else if (now < _seenUntil) blocked = false;
        else if (now < _nextCheck) blocked = _blockedSince >= 0f;
        else
        {
            _nextCheck = now + CheckInterval * _jitter;
            blocked = !_anyLineClear();
            if (!blocked) _seenUntil = now + SeenHold;
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
public sealed unsafe class DepthPyramid
{
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

        if (_far.Length < total)
        {
            _far = new float[total];
            _nearWorld = new float[total];
            _gearAny = new bool[total];
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

    // The current test (one at a time): the box's fine texel range and nearest depth, and the
    // pinned arrays.
    private int _x0, _x1, _r0, _r1;
    private float _boxNearest;
    private float* _pDepth, _pFar, _pNearWorld;
    private bool* _pGearMask, _pGearAny;
    private int* _pOffset, _pWid;

    // After a Covered result: the depth of the nearest cover under the box (larger is nearer),
    // and whether some of the cover was gear.
    private float _nearestCover;
    private bool _gearCover;
    public float NearestCover => _nearestCover;
    public bool GearCover => _gearCover;

    public int Test(float xMin, float yMin, float xMax, float yMax, float boxNearest)
    {
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
        _x0 = x0;
        _x1 = x1;
        _r0 = height - 1 - y1;
        _r1 = height - 1 - y0;
        _boxNearest = boxNearest;
        _nearestCover = 0f;
        _gearCover = false;

        // Start where the box spans at most 4x4 texels, and look closer only inside texels that
        // can't decide: the work follows the edges of the cover, not the area of the box.
        var level = 0;
        while (level < _levels - 1 && ((_x1 >> level) - (_x0 >> level) > 3 || (_r1 >> level) - (_r0 >> level) > 3)) level++;
        fixed (float* depth = _depth, far = _far, nearWorld = _nearWorld)
        fixed (bool* gearMask = _gearMask, gearAny = _gearAny)
        fixed (int* offset = _offset, wid = _wid)
        {
            _pDepth = depth;
            _pFar = far;
            _pNearWorld = nearWorld;
            _pGearMask = gearMask;
            _pGearAny = gearAny;
            _pOffset = offset;
            _pWid = wid;
            int rEnd = _r1 >> level, xEnd = _x1 >> level;
            for (var r = _r0 >> level; r <= rEnd; r++)
            for (var x = _x0 >> level; x <= xEnd; x++)
            {
                if (ShowsBehind(level, x, r)) return DepthCover.ShowsBehind;
            }
        }

        return DepthCover.Covered;
    }

    // True when something at or behind the box shows in this texel's part of the box.
    private bool ShowsBehind(int level, int x, int r)
    {
        if (level == 0)
        {
            var i = r * _width + x;
            var d = _pDepth[i];
            if (d <= _boxNearest) return true;
            if (_pGearMask[i]) _gearCover = true;
            else if (d > _nearestCover) _nearestCover = d;
            return false;
        }

        var t = _pOffset[level] + r * _pWid[level] + x;
        // Everything below is nearer than the box: covered.
        if (_pFar[t] > _boxNearest)
        {
            var near = _pNearWorld[t];
            if (near > _nearestCover) _nearestCover = near;
            if (_pGearAny[t]) _gearCover = true;
            return false;
        }

        // Lying wholly inside the box with something behind it somewhere below: it shows.
        int bx0 = x << level, by0 = r << level;
        int bx1 = ((x + 1) << level) - 1, by1 = ((r + 1) << level) - 1;
        if (bx1 > _width - 1) bx1 = _width - 1;
        if (by1 > _height - 1) by1 = _height - 1;
        if (bx0 >= _x0 && bx1 <= _x1 && by0 >= _r0 && by1 <= _r1) return true;

        var shift = level - 1;
        int rFrom = r * 2, rTo = r * 2 + 1, xFrom = x * 2, xTo = x * 2 + 1;
        if (_r0 >> shift > rFrom) rFrom = _r0 >> shift;
        if (_r1 >> shift < rTo) rTo = _r1 >> shift;
        if (_x0 >> shift > xFrom) xFrom = _x0 >> shift;
        if (_x1 >> shift < xTo) xTo = _x1 >> shift;
        for (var cr = rFrom; cr <= rTo; cr++)
        for (var cx = xFrom; cx <= xTo; cx++)
        {
            if (ShowsBehind(shift, cx, cr)) return true;
        }

        return false;
    }
}
