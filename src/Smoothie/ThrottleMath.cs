using System;

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
