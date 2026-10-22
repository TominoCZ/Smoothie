// Which LOD group slots Smoothie has forced to draw nothing, one byte per slot index, for the LOD
// loop patch (tools/engine-patches/lod_loop.s): it reads these 16 at a time instead of each forced
// group's 104-byte slot in Unity's LOD manager. SceneSkip sets and clears a group's byte when it
// forces or un-forces it. Unity's LODGroupManager::RemoveLODGroup moves its last slot into the
// removed one; the hook here moves the bytes the same way first, so a byte always belongs to the
// group in its slot. Installed (EnginePatch.LodRemove) only while the loop patch is, which rebuilds
// the bytes from Smoothie's forced groups each time it goes on.

#include <stdint.h>

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

// More slots than any map has (Streets ~135k); Unity's count beyond it leaves the bytes alone, and
// SceneSkip never sets one past it.
#define LOD_FLAGS (1 << 20)
// LODGroupManager: slot count (slots are indexed from 1, so the last is count - 1).
#define MANAGER_COUNT 0x18

typedef void (*RemoveFn)(void *manager, uint32_t index);

static uint8_t g_flags[LOD_FLAGS];
static RemoveFn g_remove;
static void **g_manager;

EXPORT uint8_t *SmoothieLodFlags(void) { return g_flags; }

EXPORT int SmoothieLodFlagCount(void) { return LOD_FLAGS; }

// The original function's entry (a trampoline: its first instructions, then a jump back) and
// Unity's global LODGroupManager pointer: another manager's removals leave the bytes alone.
EXPORT void SmoothieLodInit(void *removeTrampoline, void **managerGlobal)
{
    g_remove = (RemoveFn)removeTrampoline;
    g_manager = managerGlobal;
}

EXPORT void SmoothieRemoveLODGroup(void *manager, uint32_t index)
{
    if (g_manager && manager == *g_manager)
    {
        uint64_t count = *(uint64_t *)((char *)manager + MANAGER_COUNT);
        if (count > 0 && index < count && count <= LOD_FLAGS)
        {
            uint64_t last = count - 1;
            g_flags[index] = g_flags[last];
            g_flags[last] = 0;
        }
    }

    g_remove(manager, index);
}
