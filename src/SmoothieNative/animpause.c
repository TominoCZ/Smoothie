// Pausing animators inside Unity without switching them off (AnimatorPause.cs).
//
// Switching an Animator off stops its PlayableGraph and switching it on plays it again; each
// play makes Unity walk the whole graph (every state's clip playable) to hand down the new play
// state, over memory gone cold while the bot was held: ~3.7% of the main thread on Streets with
// 15 throttled bots. Instead the throttle leaves held animators on and lists them here; two
// entry hooks keep Unity from updating them:
// - PlayableGraph::PrepareFrame, the per-frame advance of a graph (state machine, times): a
//   normal frame (type 0) of a listed graph returns at once. Play-state changes (type 1) pass.
// - Animator::UpdateAvatars, the pose evaluation of every animator output (the FK and IK passes
//   both end up here): listed animators are left out of the output list it gets.
// Both run on Unity's worker threads too, so the lists are double-buffered: the main thread
// fills the idle copy and flips. A reader racing two flips reads a stale entry for one call at
// worst (one animator one frame early or late), never memory it doesn't own.

#include <stdint.h>
#include <string.h>

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

#define MAX_PAUSED 1024
#define MAX_OUTPUTS 2048
// AnimationPlayableOutput: its target Animator (read the same way by UpdateAvatars itself).
#define OUTPUT_ANIMATOR 0xD0

typedef void (*UpdateAvatarsFn)(void **outputs, char a, char b, char c);
typedef void (*PrepareFrameFn)(void *graph, int type, double time, float delta, uint64_t frame, uint32_t index, int context);

static UpdateAvatarsFn g_updateAvatars;
static PrepareFrameFn g_prepareFrame;
static void *g_animators[2][MAX_PAUSED], *g_graphs[2][MAX_PAUSED];
static volatile long g_count[2];
static volatile long g_active;

// The original functions' entries (trampolines: their first instructions, then a jump back).
EXPORT void SmoothieAnimInit(void *updateAvatars, void *prepareFrame)
{
    g_updateAvatars = (UpdateAvatarsFn)updateAvatars;
    g_prepareFrame = (PrepareFrameFn)prepareFrame;
}

// Main thread only. n is clamped to MAX_PAUSED.
EXPORT void SmoothieAnimSet(void *const *animators, void *const *graphs, int n)
{
    if (n < 0) n = 0;
    if (n > MAX_PAUSED) n = MAX_PAUSED;
    long next = 1 - g_active;
    memcpy(g_animators[next], animators, (size_t)n * sizeof(void *));
    memcpy(g_graphs[next], graphs, (size_t)n * sizeof(void *));
    g_count[next] = n;
    __atomic_store_n(&g_active, next, __ATOMIC_RELEASE);
}

static int Listed(void *const *list, long n, const void *p)
{
    for (long i = 0; i < n; i++)
        if (list[i] == p) return 1;
    return 0;
}

EXPORT void SmoothieUpdateAvatars(void **outputs, char a, char b, char c)
{
    long active = __atomic_load_n(&g_active, __ATOMIC_ACQUIRE);
    long n = g_count[active];
    void **data = (void **)outputs[0];
    size_t size = (size_t)outputs[2];
    if (n == 0 || size == 0 || size > MAX_OUTPUTS)
    {
        g_updateAvatars(outputs, a, b, c);
        return;
    }

    void *kept[MAX_OUTPUTS];
    size_t count = 0;
    for (size_t i = 0; i < size; i++)
    {
        void *output = data[i];
        void *animator = output ? *(void **)((char *)output + OUTPUT_ANIMATOR) : 0;
        if (!animator || !Listed(g_animators[active], n, animator)) kept[count++] = output;
    }

    if (count == size)
    {
        g_updateAvatars(outputs, a, b, c);
        return;
    }

    if (count == 0) return;
    // core::vector: data, memory label, size, capacity. Read-only for the callee.
    void *filtered[4] = { kept, outputs[1], (void *)count, (void *)count };
    g_updateAvatars(filtered, a, b, c);
}

EXPORT void SmoothiePrepareFrame(void *graph, int type, double time, float delta, uint64_t frame, uint32_t index, int context)
{
    if (type == 0)
    {
        long active = __atomic_load_n(&g_active, __ATOMIC_ACQUIRE);
        long n = g_count[active];
        if (n != 0 && Listed(g_graphs[active], n, graph)) return;
    }

    g_prepareFrame(graph, type, time, delta, frame, index, context);
}
