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
public sealed class DepthPyramid
{
    private readonly List<float[]> _far = new();
    private readonly List<float[]> _near = new();
    private readonly List<int> _widths = new();
    private readonly List<int> _heights = new();
    private float[] _depth = Array.Empty<float>();
    private int _levels;

    public void Build(float[] depth, int width, int height)
    {
        _depth = depth;
        _levels = 1;
        int w = width, h = height;
        float[] far = depth, near = depth;
        while (w > 1 || h > 1)
        {
            int pw = (w + 1) / 2, ph = (h + 1) / 2;
            if (_far.Count < _levels)
            {
                _far.Add(Array.Empty<float>());
                _near.Add(Array.Empty<float>());
                _widths.Add(0);
                _heights.Add(0);
            }

            var level = _levels - 1;
            if (_far[level].Length != pw * ph)
            {
                _far[level] = new float[pw * ph];
                _near[level] = new float[pw * ph];
            }

            float[] pf = _far[level], pn = _near[level];
            for (var r = 0; r < ph; r++)
            for (var x = 0; x < pw; x++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (var dr = 0; dr < 2; dr++)
                for (var dx = 0; dx < 2; dx++)
                {
                    int cr = r * 2 + dr, cx = x * 2 + dx;
                    if (cr >= h || cx >= w) continue;
                    var i = cr * w + cx;
                    lo = Math.Min(lo, far[i]);
                    hi = Math.Max(hi, near[i]);
                }

                pf[r * pw + x] = lo;
                pn[r * pw + x] = hi;
            }

            _widths[level] = pw;
            _heights[level] = ph;
            far = pf;
            near = pn;
            w = pw;
            h = ph;
            _levels++;
        }

        Width = width;
        Height = height;
    }

    public int Width { get; private set; }
    public int Height { get; private set; }

    // The box's fine texel range for the current test (one test at a time).
    private int _x0, _x1, _r0, _r1;
    private float _boxNearest;

    // After a Covered result: the depth of the nearest cover under the box (larger is nearer).
    public float NearestCover { get; private set; }

    public int Test(float xMin, float yMin, float xMax, float yMax, float boxNearest)
    {
        int width = Width, height = Height;
        if (xMax <= 0f || yMax <= 0f || xMin >= 1f || yMin >= 1f) return DepthCover.OffScreen;
        _x0 = Math.Max(0, (int)Math.Floor(xMin * width));
        _x1 = Math.Min(width - 1, Math.Max(_x0, (int)Math.Ceiling(xMax * width) - 1));
        var y0 = Math.Max(0, (int)Math.Floor(yMin * height));
        var y1 = Math.Min(height - 1, Math.Max(y0, (int)Math.Ceiling(yMax * height) - 1));
        // Rows as stored: upside down.
        _r0 = height - 1 - y1;
        _r1 = height - 1 - y0;
        _boxNearest = boxNearest;
        NearestCover = 0f;

        // Start where the box spans at most 4x4 texels, and look closer only inside texels that
        // can't decide: the work follows the edges of the cover, not the area of the box.
        var level = 0;
        while (level < _levels - 1 && ((_x1 >> level) - (_x0 >> level) > 3 || (_r1 >> level) - (_r0 >> level) > 3)) level++;
        for (var r = _r0 >> level; r <= _r1 >> level; r++)
        for (var x = _x0 >> level; x <= _x1 >> level; x++)
        {
            if (ShowsBehind(level, x, r)) return DepthCover.ShowsBehind;
        }

        return DepthCover.Covered;
    }

    // True when something at or behind the box shows in this texel's part of the box.
    private bool ShowsBehind(int level, int x, int r)
    {
        if (level == 0)
        {
            var d = _depth[r * Width + x];
            if (d <= _boxNearest) return true;
            if (d > NearestCover) NearestCover = d;
            return false;
        }

        var w = _widths[level - 1];
        var t = r * w + x;
        // Everything below is nearer than the box: covered.
        if (_far[level - 1][t] > _boxNearest)
        {
            var near = _near[level - 1][t];
            if (near > NearestCover) NearestCover = near;
            return false;
        }

        // Lying wholly inside the box with something behind it somewhere below: it shows.
        if (x << level >= _x0 && Math.Min(((x + 1) << level) - 1, Width - 1) <= _x1
            && r << level >= _r0 && Math.Min(((r + 1) << level) - 1, Height - 1) <= _r1) return true;

        var shift = level - 1;
        for (var cr = Math.Max(r * 2, _r0 >> shift); cr <= Math.Min(r * 2 + 1, _r1 >> shift); cr++)
        for (var cx = Math.Max(x * 2, _x0 >> shift); cx <= Math.Min(x * 2 + 1, _x1 >> shift); cx++)
        {
            if (ShowsBehind(shift, cx, cr)) return true;
        }

        return false;
    }
}
