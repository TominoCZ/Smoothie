// Native depth pyramid for Smoothie's "Hide objects covered by nearer ones": the same build and
// box test as DepthPyramid in ThrottleMath.cs (the C# one stays as the fallback and as the
// reference the self-check compares against), compiled ahead of time with SSE2. The game's Mono
// JIT does neither SIMD nor much optimising, and the worker runs the test for thousands of
// boxes a round, more while moving; on CPUs with fewer cores the round's length is how stale
// the result is when it lands.
//
// SSE2 only: part of x86-64 itself, so every CPU that runs the game has it (no CPUID check).
// Built by build.sh: SmoothieNative.dll (mingw-w64, for the game) and libSmoothieNative.so (for
// the self-check on Linux). Thread-safe after sm_build: tests only read.
//
// Layout (as DepthPyramid): level 0 is the fine image, rows upside down; levels 1 and up live
// in shared arrays, level L's texel (x, r) at offset[L] + r * wid[L] + x. far: the farthest
// depth below a texel (min, larger is nearer); nearWorld: the nearest that isn't the player's
// gear (max); gearAny: gear below. The level arrays have 4 floats / bytes to spare at the end,
// so a row can be read 4 at a time.

#include <emmintrin.h>
#include <math.h>
#include <stdint.h>

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

enum { COVERED = 0, SHOWS_BEHIND = 1, OFF_SCREEN = 2 };

// Filled by DepthPyramid after each build; pointers into its pinned arrays.
typedef struct
{
    const float *depth, *far, *nearWorld;
    const uint8_t *gearMask, *gearAny;
    const int32_t *offset, *wid;
    int32_t levels, width, height;
} Pyramid;

// One test: the box's fine texel range and nearest depth, and what covers it.
typedef struct
{
    const Pyramid *p;
    int x0, x1, r0, r1;
    float boxNearest, nearestCover;
    int gearCover;
} Probe;

EXPORT int32_t sm_version(void) { return 2; }

// Level `level` from level - 1: each texel the min of far, the max of nearWorld and the OR of
// gear over the 2x2 below it; an odd last row or column pairs with itself.
static void build_level(const float *sf, const float *sw, const uint8_t *sg, int cw, int ch,
                        float *df, float *dw, uint8_t *dg, int pw, int ph)
{
    for (int r = 0; r < ph; r++)
    {
        const int down = r * 2 + 1 < ch ? cw : 0;
        const float *f0 = sf + r * 2 * cw, *f1 = f0 + down;
        const float *w0 = sw + r * 2 * cw, *w1 = w0 + down;
        const uint8_t *g0 = sg + r * 2 * cw, *g1 = g0 + down;
        float *rf = df + r * pw, *rw = dw + r * pw;
        uint8_t *rg = dg + r * pw;
        // Four output texels (eight input columns) at a time while both columns of each pair exist.
        int x = 0;
        const int pairs = cw / 2;
        for (; x + 4 <= pairs; x += 4)
        {
            __m128 a = _mm_min_ps(_mm_loadu_ps(f0 + 2 * x), _mm_loadu_ps(f1 + 2 * x));
            __m128 b = _mm_min_ps(_mm_loadu_ps(f0 + 2 * x + 4), _mm_loadu_ps(f1 + 2 * x + 4));
            _mm_storeu_ps(rf + x, _mm_min_ps(_mm_shuffle_ps(a, b, _MM_SHUFFLE(2, 0, 2, 0)), _mm_shuffle_ps(a, b, _MM_SHUFFLE(3, 1, 3, 1))));
            a = _mm_max_ps(_mm_loadu_ps(w0 + 2 * x), _mm_loadu_ps(w1 + 2 * x));
            b = _mm_max_ps(_mm_loadu_ps(w0 + 2 * x + 4), _mm_loadu_ps(w1 + 2 * x + 4));
            _mm_storeu_ps(rw + x, _mm_max_ps(_mm_shuffle_ps(a, b, _MM_SHUFFLE(2, 0, 2, 0)), _mm_shuffle_ps(a, b, _MM_SHUFFLE(3, 1, 3, 1))));
        }

        for (; x < pw; x++)
        {
            const int i = 2 * x, i1 = i + (2 * x + 1 < cw ? 1 : 0);
            rf[x] = fminf(fminf(f0[i], f0[i1]), fminf(f1[i], f1[i1]));
            rw[x] = fmaxf(fmaxf(w0[i], w0[i1]), fmaxf(w1[i], w1[i1]));
        }

        for (x = 0; x < pw; x++)
        {
            const int i = 2 * x, i1 = i + (2 * x + 1 < cw ? 1 : 0);
            rg[x] = g0[i] | g0[i1] | g1[i] | g1[i1];
        }
    }
}

EXPORT void sm_build(const float *fine, const float *fineWorld, const uint8_t *fineGear, int32_t levels,
                     const int32_t *offset, const int32_t *wid, const int32_t *hei,
                     float *far, float *nearWorld, uint8_t *gearAny)
{
    for (int level = 1; level < levels; level++)
    {
        const float *sf = level == 1 ? fine : far + offset[level - 1];
        const float *sw = level == 1 ? fineWorld : nearWorld + offset[level - 1];
        const uint8_t *sg = level == 1 ? fineGear : gearAny + offset[level - 1];
        build_level(sf, sw, sg, wid[level - 1], hei[level - 1], far + offset[level], nearWorld + offset[level],
                    gearAny + offset[level], wid[level], hei[level]);
    }
}

static int shows_behind(Probe *q, int level, int x, int r);

// Texels x0..x1 (at most 4) of row r: does any show something at or behind the box? Above
// level 0, the texels whose farthest depth is nearer than the box are cover, decided four at
// a time; the rest are looked at closer, left to right.
static int row_shows_behind(Probe *q, int level, int r, int x0, int x1)
{
    const Pyramid *p = q->p;
    if (level == 0)
    {
        for (int x = x0; x <= x1; x++)
        {
            const int i = r * p->width + x;
            const float d = p->depth[i];
            if (d <= q->boxNearest) return 1;
            if (p->gearMask[i]) q->gearCover = 1;
            else if (d > q->nearestCover) q->nearestCover = d;
        }

        return 0;
    }

    const int t = p->offset[level] + r * p->wid[level] + x0, n = x1 - x0 + 1;
    const int lanes = (1 << n) - 1;
    const int cover = _mm_movemask_ps(_mm_cmpgt_ps(_mm_loadu_ps(p->far + t), _mm_set1_ps(q->boxNearest))) & lanes;
    if (cover)
    {
        float near[4];
        _mm_storeu_ps(near, _mm_loadu_ps(p->nearWorld + t));
        for (int k = 0; k < n; k++)
        {
            if (!(cover >> k & 1)) continue;
            if (near[k] > q->nearestCover) q->nearestCover = near[k];
            if (p->gearAny[t + k]) q->gearCover = 1;
        }
    }

    for (int k = 0; k < n; k++)
    {
        if (!(cover >> k & 1) && shows_behind(q, level, x0 + k, r)) return 1;
    }

    return 0;
}

// A texel whose farthest depth isn't nearer than the box: lying wholly inside the box it shows
// something behind it; otherwise its part of the box is looked at one level finer.
static int shows_behind(Probe *q, int level, int x, int r)
{
    const Pyramid *p = q->p;
    int bx0 = x << level, by0 = r << level;
    int bx1 = ((x + 1) << level) - 1, by1 = ((r + 1) << level) - 1;
    if (bx1 > p->width - 1) bx1 = p->width - 1;
    if (by1 > p->height - 1) by1 = p->height - 1;
    if (bx0 >= q->x0 && bx1 <= q->x1 && by0 >= q->r0 && by1 <= q->r1) return 1;

    const int shift = level - 1;
    int rFrom = r * 2, rTo = r * 2 + 1, xFrom = x * 2, xTo = x * 2 + 1;
    if (q->r0 >> shift > rFrom) rFrom = q->r0 >> shift;
    if (q->r1 >> shift < rTo) rTo = q->r1 >> shift;
    if (q->x0 >> shift > xFrom) xFrom = q->x0 >> shift;
    if (q->x1 >> shift < xTo) xTo = q->x1 >> shift;
    for (int cr = rFrom; cr <= rTo; cr++)
    {
        if (row_shows_behind(q, shift, cr, xFrom, xTo)) return 1;
    }

    return 0;
}

EXPORT int32_t sm_test(const Pyramid *p, float xMin, float yMin, float xMax, float yMax, float boxNearest,
                       float *nearestCover, int32_t *gearCover)
{
    *nearestCover = 0.0f;
    *gearCover = 0;
    const int width = p->width, height = p->height;
    if (xMax <= 0.0f || yMax <= 0.0f || xMin >= 1.0f || yMin >= 1.0f) return OFF_SCREEN;
    int x0 = (int)floorf(xMin * width);
    if (x0 < 0) x0 = 0;
    int x1 = (int)ceilf(xMax * width) - 1;
    if (x1 < x0) x1 = x0;
    if (x1 > width - 1) x1 = width - 1;
    int y0 = (int)floorf(yMin * height);
    if (y0 < 0) y0 = 0;
    int y1 = (int)ceilf(yMax * height) - 1;
    if (y1 < y0) y1 = y0;
    if (y1 > height - 1) y1 = height - 1;
    // Rows as stored: upside down.
    Probe q = { p, x0, x1, height - 1 - y1, height - 1 - y0, boxNearest, 0.0f, 0 };

    // Start where the box spans at most 4x4 texels.
    int level = 0;
    while (level < p->levels - 1 && ((q.x1 >> level) - (q.x0 >> level) > 3 || (q.r1 >> level) - (q.r0 >> level) > 3)) level++;
    const int rEnd = q.r1 >> level, xBegin = q.x0 >> level, xEnd = q.x1 >> level;
    for (int r = q.r0 >> level; r <= rEnd; r++)
    {
        if (row_shows_behind(&q, level, r, xBegin, xEnd)) return SHOWS_BEHIND;
    }

    *nearestCover = q.nearestCover;
    *gearCover = q.gearCover;
    return COVERED;
}
