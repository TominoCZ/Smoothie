// Native cell walk for Smoothie's "Hide objects covered by nearer ones": the same as CellWalk's C#
// in ThrottleMath.cs (which stays as the fallback and as the reference the self-check compares
// against), compiled ahead of time with SSE2. Every round the worker walks every object of every
// cell (tens of thousands on Streets) to find the few due for a depth test; under the game's Mono
// JIT that walk was most of the round.
//
// SSE2 only (x86-64 baseline, no CPUID check). Arrays are the worker's (or read racily from the
// main thread's Hidden, as the C# does): nothing here allocates or keeps a pointer.

#include <emmintrin.h>
#include <math.h>
#include <stdint.h>
#include <string.h>

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

// CellWalk.Units: per object, indexed alike. checkedFrom, min and max hold 3 floats each.
typedef struct
{
    const float *retryAt;
    float *nextCheck;
    const float *slack, *checkedFrom, *min, *max;
    uint8_t *streak;
    const uint8_t *hidden;
    int32_t *wantedIn;
} Units;

// The box against the view's left, right, bottom and top planes (4 floats each), in the same
// order of operations as the C# Sides.Out, so both give the same answer.
static int out_of_view(const float *planes, const float *mn, const float *mx)
{
    const float cx = (mn[0] + mx[0]) * 0.5f, cy = (mn[1] + mx[1]) * 0.5f, cz = (mn[2] + mx[2]) * 0.5f;
    const float ex = (mx[0] - mn[0]) * 0.5f, ey = (mx[1] - mn[1]) * 0.5f, ez = (mx[2] - mn[2]) * 0.5f;
    for (int k = 0; k < 4; k++)
    {
        const float *p = planes + k * 4;
        if (p[0] * cx + p[1] * cy + p[2] * cz + p[3] + fabsf(p[0]) * ex + fabsf(p[1]) * ey + fabsf(p[2]) * ez < 0.0f) return 1;
    }

    return 0;
}

static float moved2(const float *eye, const float *at)
{
    const float dx = eye[0] - at[0], dy = eye[1] - at[1], dz = eye[2] - at[2];
    return dx * dx + dy * dy + dz * dz;
}

// Not due but wanted hidden: listed, and a candidate for Apply's try list when not hidden yet,
// not waiting to retry and in view (the C# then asks GameSkips, which reads managed objects).
static void wanted(const Units *u, int i, float gameTime, int round, const float *planes, int32_t *want, int *nw,
                   int32_t *maybeTry, int *nt)
{
    u->wantedIn[i] = round;
    want[(*nw)++] = i;
    if (!u->hidden[i] && u->retryAt[i] <= gameTime && !out_of_view(planes, u->min + i * 3, u->max + i * 3)) maybeTry[(*nt)++] = i;
}

// A cell in view, objects from..to-1. Due: not hidden and waiting to retry, or past its next
// check, or covered and the eye moved past its slack. Counts out: due, want, maybeTry.
EXPORT void sm_walk_in(const Units *u, int32_t from, int32_t to, const float *eye, float now, float gameTime,
                       int32_t round, const float *planes, int32_t hideAfter, int32_t *due, int32_t *want,
                       int32_t *maybeTry, int32_t *counts)
{
    int nd = 0, nw = 0, nt = 0, i = from;
    const __m128 now4 = _mm_set1_ps(now), game4 = _mm_set1_ps(gameTime);
    const __m128 ex = _mm_set1_ps(eye[0]), ey = _mm_set1_ps(eye[1]), ez = _mm_set1_ps(eye[2]);
    const __m128i zero = _mm_setzero_si128(), below = _mm_set1_epi32(hideAfter - 1);
    for (; i + 4 <= to; i += 4)
    {
        int32_t s4, h4;
        memcpy(&s4, u->streak + i, 4);
        memcpy(&h4, u->hidden + i, 4);
        const __m128i st = _mm_unpacklo_epi16(_mm_unpacklo_epi8(_mm_cvtsi32_si128(s4), zero), zero);
        const __m128i hd = _mm_unpacklo_epi16(_mm_unpacklo_epi8(_mm_cvtsi32_si128(h4), zero), zero);
        const float *cf = u->checkedFrom + i * 3;
        const __m128 dx = _mm_sub_ps(ex, _mm_setr_ps(cf[0], cf[3], cf[6], cf[9]));
        const __m128 dy = _mm_sub_ps(ey, _mm_setr_ps(cf[1], cf[4], cf[7], cf[10]));
        const __m128 dz = _mm_sub_ps(ez, _mm_setr_ps(cf[2], cf[5], cf[8], cf[11]));
        const __m128 d2 = _mm_add_ps(_mm_add_ps(_mm_mul_ps(dx, dx), _mm_mul_ps(dy, dy)), _mm_mul_ps(dz, dz));
        // Waiting: not hidden and its retry time still ahead.
        const __m128i waiting = _mm_and_si128(_mm_cmpeq_epi32(hd, zero), _mm_castps_si128(_mm_cmpgt_ps(_mm_loadu_ps(u->retryAt + i), game4)));
        const __m128i pastCheck = _mm_castps_si128(_mm_cmpge_ps(now4, _mm_loadu_ps(u->nextCheck + i)));
        const __m128i movedPast = _mm_andnot_si128(_mm_cmpeq_epi32(st, zero), _mm_castps_si128(_mm_cmpge_ps(d2, _mm_loadu_ps(u->slack + i))));
        const __m128i isDue = _mm_andnot_si128(waiting, _mm_or_si128(pastCheck, movedPast));
        const __m128i isWanted = _mm_andnot_si128(isDue, _mm_cmpgt_epi32(st, below));
        int dueBits = _mm_movemask_ps(_mm_castsi128_ps(isDue)), wantBits = _mm_movemask_ps(_mm_castsi128_ps(isWanted));
        if ((dueBits | wantBits) == 0) continue;
        for (int k = 0; k < 4; k++)
        {
            if (dueBits >> k & 1) due[nd++] = i + k;
            else if (wantBits >> k & 1) wanted(u, i + k, gameTime, round, planes, want, &nw, maybeTry, &nt);
        }
    }

    for (; i < to; i++)
    {
        int isDue;
        if (!u->hidden[i] && u->retryAt[i] > gameTime) isDue = 0;
        else if (now >= u->nextCheck[i]) isDue = 1;
        else if (u->streak[i] == 0) isDue = 0;
        else isDue = moved2(eye, u->checkedFrom + i * 3) >= u->slack[i];
        if (isDue) due[nd++] = i;
        else if (u->streak[i] >= hideAfter) wanted(u, i, gameTime, round, planes, want, &nw, maybeTry, &nt);
    }

    counts[0] = nd;
    counts[1] = nw;
    counts[2] = nt;
}

// A cell out of view, objects from..to-1: a covered one whose cover may have shifted (the eye
// moved past its slack) is forgotten and checked again once back in view; covered ones stay
// wanted, hidden ones too and are listed stale. Returns 1 when nothing in it is covered or hidden
// (the cell isn't walked again until it's back in view). Counts out: want, stale.
EXPORT int32_t sm_walk_out(const Units *u, int32_t from, int32_t to, const float *eye, int32_t round, int32_t hideAfter,
                           int32_t *want, int32_t *stale, int32_t *counts)
{
    int nw = 0, ns = 0, quiet = 1, i = from;
    const __m128i zero = _mm_setzero_si128();
    for (; i < to; i++)
    {
        // Mostly nothing covered or hidden: 16 at a time while none is.
        while (i + 16 <= to)
        {
            const __m128i any = _mm_or_si128(_mm_loadu_si128((const __m128i *)(u->streak + i)), _mm_loadu_si128((const __m128i *)(u->hidden + i)));
            if (_mm_movemask_epi8(_mm_cmpeq_epi8(any, zero)) != 0xFFFF) break;
            i += 16;
        }

        if (i >= to) break;
        if (u->streak[i] == 0 && !u->hidden[i]) continue;
        if (u->streak[i] != 0 && moved2(eye, u->checkedFrom + i * 3) >= u->slack[i])
        {
            u->streak[i] = 0;
            u->nextCheck[i] = 0.0f;
        }

        if (u->streak[i] >= hideAfter)
        {
            u->wantedIn[i] = round;
            want[nw++] = i;
        }
        else if (u->hidden[i])
        {
            u->wantedIn[i] = round;
            want[nw++] = i;
            stale[ns++] = i;
        }

        quiet &= u->streak[i] == 0 && !u->hidden[i];
    }

    counts[0] = nw;
    counts[1] = ns;
    return quiet;
}
