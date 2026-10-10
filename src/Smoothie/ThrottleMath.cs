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
