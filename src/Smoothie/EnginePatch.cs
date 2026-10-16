using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Smoothie;

// Faster versions of loops in Unity's own (native, C++) engine code, put in place at runtime:
// memory only, nothing on disk changes. Each replaces the first bytes of one loop (or of one step
// in it) with a jump to a routine doing the same work faster, or skipping work whose result
// nothing reads, which jumps back into Unity's code.
//
// WHY. On big maps (Streets: ~220k renderers, ~134k LOD groups) Unity spends main-thread time every
// frame in loops over every object on the map, drawn or not. Switching objects off
// (renderer.enabled = false) shortens those loops, which is why an old experiment that switched
// out-of-view objects off ran ~1 ms faster, but switching back on costs ~1 µs per object and
// hitched on every turn. forceRenderingOff (what Smoothie hides with) doesn't shorten them at all.
// So the loops themselves are made cheaper instead. Measured on Streets: VisibilityScan alone
// ~0.15-0.2 ms a frame; LodLoop and "Skip LOD work" together ~0.3 ms where many groups are set
// aside, nothing where few are.
//
// HOW THE LOOPS WERE FOUND (to redo after a game update):
// 1. Identify the build. UnityPlayer.dll carries its PDB name + GUID + age in its PE debug
//    directory (RSDS record). tools/engine-patches/pdb_id.py prints it and the URL of Unity's
//    public symbols for exactly that build:
//      https://symbolserver.unity3d.com/UnityPlayer_Win64_player_mono_x64.pdb/<GUID+age>/UnityPlayer_Win64_player_mono_x64.pd_
//    Download it, `cabextract` it. EFT 0.16.9 / SPT 4.1 ships Unity 2022.3.43f1 with id
//    BD9605D7A8BC4F21AF766D4707334ADC1 (SceneSkip.BuildId).
// 2. Name the code. tools/engine-patches/publics.py lists the PDB's public symbols as RVAs. Only
//    public functions are named; static ones appear as the public symbol before them.
// 3. Find what costs. Profile the running game (Linux/Proton here): sudo sysctl
//    kernel.perf_event_paranoid=1, then `perf record -p <EscapeFromTarkov.exe pid> -F 1999 -k mono`
//    while standing still in a raid. Wine maps UnityPlayer.dll at its preferred base
//    (0x6ffffb800000 here, see /proc/<pid>/maps; the code is anonymous mappings after the header),
//    so sample address - base = RVA. Split samples by thread: the busiest is the main thread, the
//    only one that sets the frame time (worker-thread work, e.g. the shadow-caster culling
//    kernels, runs in parallel: an experiment that switched most far casters off didn't move
//    the frame time). Bucket the RVAs
//    by function (and by 64-byte block to see the hot instructions).
// 4. Read the hot code. tools/engine-patches/dis.sh disassembles at an RVA with calls named.
//    Work out the loop's registers on entry, where it starts and ends, and pick a patch site
//    of >= 14 bytes of whole instructions at the loop head that nothing else jumps into the middle
//    of (the loop's own back-edge only targets its first instruction, which becomes the jump).
// 5. Write the replacement in GNU as Intel syntax (tools/engine-patches/*.s) doing exactly the
//    same work, same calls, same writes, same order, and assemble it with
//    tools/engine-patches/assemble.sh into the byte lists below. Calls back into Unity and the way
//    back go through 8-byte address slots at the routine's end, filled with module base + RVA here.
//    Check the embedded bytes against the assembled ones and the original bytes against the DLL
//    before installing: one wrong byte crashes the game.
//
// AFTER A GAME UPDATE: if UnityPlayer.dll changes, SceneSkip.Check sees a different build id and
// every patch here (and SceneSkip's LOD forcing) stays off by itself. To bring them back, redo 1-5
// for the new build: new symbols, new RVAs, new original bytes, and check the loops still do the
// same thing (register use and struct offsets change between builds). Never just move the RVAs.
//
// SAFETY. Only for that exact build, only over the exact original bytes (anything else: left
// alone, logged), off puts the original bytes back. Written only from Plugin.Update (main
// thread, script phase): the main thread is the only one that runs these loops, and between
// scripts it isn't inside either, so neither can be mid-way through one during a write. Instructions:
// only the x86-64 baseline (SSE/SSE2, which every 64-bit x86 CPU has), so no CPU check is needed.
// A routine using anything newer (SSSE3, SSE4.x, AVX...) must check CPUID first and leave the
// original code in place when the CPU lacks it: an unsupported instruction crashes the game.
// Background, Unity's culling data layout and measurements: tools/engine-patches/unity-internals.md.
internal sealed unsafe class EnginePatch
{
    // RendererScene::SceneAfterCullingOutputReady walks every scene entry (~220k on Streets) one
    // byte at a time each frame to find those that just became visible (~6% of the main thread).
    // The routine compares 16 at a time and goes entry by entry only through a block with a match:
    // the same renderer call (virtual 0xd8) and the byte or'ed with 4, in the same order.
    internal static readonly EnginePatch VisibilityScan = new(
        "visibility scan", 0x3EE942,
        new byte[] { 0x4C, 0x8B, 0xB3, 0xC8, 0x00, 0x00, 0x00, 0x41, 0x80, 0x3C, 0x3E, 0x01, 0x75, 0x24 },
        new byte[]
        {
            0x4C, 0x89, 0xF8, 0x48, 0x29, 0xF8, 0x48, 0x83, 0xF8, 0x10, 0x72, 0x33, 0x4C, 0x8B, 0xB3, 0xC8,
            0x00, 0x00, 0x00, 0xF3, 0x41, 0x0F, 0x6F, 0x04, 0x3E, 0xB8, 0x01, 0x01, 0x01, 0x01, 0x66, 0x0F,
            0x6E, 0xC8, 0x66, 0x0F, 0x70, 0xC9, 0x00, 0x66, 0x0F, 0x74, 0xC1, 0x66, 0x0F, 0xD7, 0xC0, 0x85,
            0xC0, 0x75, 0x06, 0x48, 0x83, 0xC7, 0x10, 0xEB, 0xC7, 0x48, 0x8D, 0x77, 0x10, 0xEB, 0x08, 0x4C,
            0x39, 0xFF, 0x73, 0x46, 0x4C, 0x89, 0xFE, 0x4C, 0x8B, 0xB3, 0xC8, 0x00, 0x00, 0x00, 0x41, 0x80,
            0x3C, 0x3E, 0x01, 0x75, 0x28, 0x48, 0x8B, 0x83, 0x88, 0x00, 0x00, 0x00, 0x48, 0x8D, 0x14, 0x7F,
            0x48, 0x8B, 0x14, 0xD0, 0x48, 0x85, 0xD2, 0x48, 0x8D, 0x4A, 0xC8, 0x49, 0x0F, 0x44, 0xCD, 0x48,
            0x8B, 0x01, 0xFF, 0x90, 0xD8, 0x00, 0x00, 0x00, 0x41, 0x80, 0x0C, 0x3E, 0x04, 0x48, 0xFF, 0xC7,
            0x48, 0x39, 0xF7, 0x72, 0xC2, 0xE9, 0x76, 0xFF, 0xFF, 0xFF, 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        },
        (0x90, 0x3EE981));

    // LODGroupManager::CalculateLODMasks (perspective) works out every LOD group's level for every
    // camera each frame (~134k groups on Streets, ~5% of the main thread): distance, square root and
    // a call to CalculateLODMask, whose first act for a forced group is to hand back the forced byte
    // with no fade. The routine does that itself for forced groups (LodCulling forces the ones
    // nothing can be seen of) and sends every other group Unity's own way. A group still forced
    // from last time isn't even read: its result in the camera's own array says so (most of the
    // loop's remaining cost was reading ~135k slots of 104 bytes; tools/engine-patches/lod_loop.s).
    internal static readonly EnginePatch LodLoop = new(
        "LOD loop", 0x4FC7C0,
        new byte[] { 0x4C, 0x8B, 0x6E, 0x08, 0x4A, 0x8D, 0x04, 0x03, 0x4C, 0x03, 0xEF, 0x48, 0x89, 0x84, 0x24, 0xB0, 0x00, 0x00, 0x00 },
        new byte[]
        {
            0x48, 0x8B, 0x05, 0x89, 0x01, 0x00, 0x00, 0xFF, 0x00, 0x4C, 0x39, 0xF3, 0x0F, 0x83, 0x5E, 0x01,
            0x00, 0x00, 0x4C, 0x8B, 0x84, 0x24, 0xA8, 0x00, 0x00, 0x00, 0x4A, 0x8D, 0x04, 0x03, 0x48, 0x8D,
            0x53, 0x10, 0x4C, 0x39, 0xF2, 0x77, 0x72, 0x48, 0x8B, 0x0D, 0x62, 0x01, 0x00, 0x00, 0x8B, 0x09,
            0x89, 0xDA, 0xC1, 0xEA, 0x0D, 0x31, 0xCA, 0xF6, 0xC2, 0x0F, 0x74, 0x5D, 0x8D, 0x53, 0x0F, 0xC1,
            0xEA, 0x0D, 0x31, 0xCA, 0xF6, 0xC2, 0x0F, 0x74, 0x50, 0xF3, 0x0F, 0x6F, 0x00, 0xBA, 0x80, 0x80,
            0x80, 0x80, 0x66, 0x0F, 0x6E, 0xCA, 0x66, 0x0F, 0x70, 0xC9, 0x00, 0x66, 0x0F, 0x74, 0xC1, 0x66,
            0x0F, 0xD7, 0xD0, 0xF7, 0xD2, 0x81, 0xE2, 0xFF, 0xFF, 0x00, 0x00, 0x74, 0x18, 0x0F, 0xBC, 0xCA,
            0x85, 0xC9, 0x74, 0x25, 0x48, 0x01, 0xCB, 0x48, 0x8D, 0x6C, 0x8D, 0x00, 0x48, 0x6B, 0xD1, 0x68,
            0x48, 0x01, 0xD7, 0xEB, 0x84, 0x48, 0x83, 0xC3, 0x10, 0x48, 0x83, 0xC5, 0x40, 0x48, 0x81, 0xC7,
            0x80, 0x06, 0x00, 0x00, 0xE9, 0x70, 0xFF, 0xFF, 0xFF, 0x80, 0x38, 0x80, 0x75, 0x17, 0x89, 0xD9,
            0xC1, 0xE9, 0x0D, 0x48, 0x8B, 0x15, 0xE6, 0x00, 0x00, 0x00, 0x33, 0x0A, 0xF6, 0xC1, 0x0F, 0x0F,
            0x85, 0xAB, 0x00, 0x00, 0x00, 0x4C, 0x8B, 0x6E, 0x08, 0x49, 0x01, 0xFD, 0x41, 0x0F, 0xB6, 0x4D,
            0x60, 0x84, 0xC9, 0x74, 0x0E, 0x88, 0x08, 0xC7, 0x45, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE9, 0x8D,
            0x00, 0x00, 0x00, 0x48, 0x89, 0x84, 0x24, 0xB0, 0x00, 0x00, 0x00, 0xF3, 0x41, 0x0F, 0x10, 0x45,
            0x04, 0xF3, 0x41, 0x0F, 0x5C, 0x44, 0x24, 0x08, 0xF3, 0x41, 0x0F, 0x10, 0x55, 0x00, 0xF3, 0x41,
            0x0F, 0x5C, 0x54, 0x24, 0x04, 0xF3, 0x41, 0x0F, 0x10, 0x4D, 0x08, 0xF3, 0x41, 0x0F, 0x5C, 0x4C,
            0x24, 0x0C, 0xF3, 0x0F, 0x59, 0xC0, 0xF3, 0x0F, 0x59, 0xD2, 0xF3, 0x0F, 0x59, 0xC9, 0xF3, 0x0F,
            0x58, 0xD0, 0x0F, 0x57, 0xC0, 0xF3, 0x0F, 0x58, 0xD1, 0xF3, 0x0F, 0x59, 0xD6, 0x0F, 0x2E, 0xC2,
            0x77, 0x09, 0x0F, 0x57, 0xC0, 0xF3, 0x0F, 0x51, 0xC2, 0xEB, 0x11, 0x0F, 0x28, 0xC2, 0xFF, 0x15,
            0x54, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x84, 0x24, 0xB0, 0x00, 0x00, 0x00, 0x44, 0x8B, 0x46, 0x2C,
            0x45, 0x0F, 0xB6, 0xCF, 0x48, 0x89, 0x6C, 0x24, 0x30, 0x0F, 0x28, 0xC8, 0x48, 0x89, 0x44, 0x24,
            0x28, 0x4C, 0x89, 0xE9, 0xF3, 0x0F, 0x11, 0x7C, 0x24, 0x20, 0xFF, 0x15, 0x20, 0x00, 0x00, 0x00,
            0x48, 0xFF, 0xC3, 0x48, 0x83, 0xC5, 0x04, 0x48, 0x83, 0xC7, 0x68, 0xE9, 0x99, 0xFE, 0xFF, 0xFF,
            0xFF, 0x25, 0x02, 0x00, 0x00, 0x00, 0x66, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        },
        // back after the loop, CalculateLODMask, the square-root helper Unity calls for a negative value
        (0x178, 0x4FC872), (0x180, 0x4FC400), (0x188, 0x18343FC)) { _cellSlot = 0x190 };

    // NavMeshManager::UpdateNavMeshObstacles recomputes every NavMeshObstacle's world box each frame
    // (walks up its transform's parents, moved or not) for the CrowdManager's obstacle list (~5%
    // of the main thread on Streets). Only NavMeshAgents read that list and EFT has none. The
    // routine skips that part while no agent is active; with one, Unity's code runs as before.
    internal static readonly EnginePatch NavCrowd = new(
        "obstacle crowd update", 0xA4791A,
        new byte[] { 0x49, 0x8B, 0x9D, 0x90, 0x00, 0x00, 0x00, 0x48, 0x85, 0xDB, 0x0F, 0x84, 0xF3, 0x04, 0x00, 0x00 },
        new byte[]
        {
            0x49, 0x8B, 0x9D, 0x90, 0x00, 0x00, 0x00, 0x48, 0x85, 0xDB, 0x74, 0x28, 0x8B, 0x83, 0x80, 0x00,
            0x00, 0x00, 0x48, 0x8B, 0x8B, 0xA0, 0x00, 0x00, 0x00, 0xFF, 0xC8, 0x78, 0x17, 0x48, 0x69, 0xD0,
            0xC0, 0x02, 0x00, 0x00, 0xF6, 0x84, 0x11, 0xBA, 0x02, 0x00, 0x00, 0x01, 0x74, 0xEB, 0xFF, 0x25,
            0x0C, 0x00, 0x00, 0x00, 0xFF, 0x25, 0x0E, 0x00, 0x00, 0x00, 0x66, 0x0F, 0x1F, 0x44, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        },
        // the crowd part, the next obstacle
        (0x40, 0xA4792A), (0x48, 0xA47E1D));

    // The same loop also reads each obstacle's physics velocity every frame (~0.8% of the main
    // thread on Streets), which only that crowd part uses. Skipped under the same test.
    internal static readonly EnginePatch NavVelocity = new(
        "obstacle velocity read", 0xA47831,
        new byte[] { 0x48, 0x8B, 0x0D, 0xB0, 0x1F, 0x2B, 0x01, 0x48, 0x85, 0xC9, 0x74, 0x25, 0x48, 0x8B, 0x01 },
        new byte[]
        {
            0x49, 0x8B, 0x8D, 0x90, 0x00, 0x00, 0x00, 0x48, 0x85, 0xC9, 0x74, 0x3A, 0x8B, 0x81, 0x80, 0x00,
            0x00, 0x00, 0x48, 0x8B, 0x91, 0xA0, 0x00, 0x00, 0x00, 0xFF, 0xC8, 0x78, 0x29, 0x48, 0x69, 0xC8,
            0xC0, 0x02, 0x00, 0x00, 0xF6, 0x84, 0x0A, 0xBA, 0x02, 0x00, 0x00, 0x01, 0x74, 0xEB, 0x48, 0x8B,
            0x0D, 0x1B, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x09, 0x48, 0x85, 0xC9, 0x74, 0x09, 0x48, 0x8B, 0x01,
            0xFF, 0x25, 0x12, 0x00, 0x00, 0x00, 0xFF, 0x25, 0x14, 0x00, 0x00, 0x00, 0x0F, 0x1F, 0x40, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        },
        // the physics module pointer, the velocity call, the code after it
        (0x50, 0x1CF97E8), (0x58, 0xA47840), (0x60, 0xA47862));

    // Every culling pass (several a frame) starts by counting the scene's lights to size its light
    // buffers, walking LightManager's linked list of every enabled light, one cache miss per light
    // (~2% of the main thread on Streets). The manager's set of the same lights, which is what the
    // pass then works through, keeps its own count: the routine reads that, and walks only when it
    // reads 0. tools/engine-patches/light_count.s says why that count is enough.
    internal static readonly EnginePatch LightCount = new(
        "light count", 0x3FC771,
        new byte[]
        {
            0x45, 0x8B, 0xF0, 0x48, 0x8B, 0x41, 0x08, 0x48, 0x3B, 0xC1, 0x74, 0x14, 0x0F, 0x1F, 0x00, 0x48,
            0x8B, 0x40, 0x08, 0x49, 0xFF, 0xC6, 0x48, 0x3B, 0xC1, 0x75, 0xF4,
        },
        new byte[]
        {
            0x44, 0x8B, 0x71, 0x1C, 0x45, 0x85, 0xF6, 0x75, 0x11, 0x48, 0x89, 0xC8, 0x48, 0x8B, 0x40, 0x08,
            0x48, 0x39, 0xC8, 0x74, 0x05, 0x49, 0xFF, 0xC6, 0xEB, 0xF2, 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        },
        // back to Unity's test of the count
        (0x20, 0x3FC78C));

    private readonly string _name;
    private readonly long _targetRva;
    private readonly byte[] _original, _routine;
    // Routine offsets that hold an absolute address, and the RVA in UnityPlayer.dll to put there.
    private readonly (int Offset, long Rva)[] _addresses;
    // Routine offset that gets the address of a writable 8-byte cell of its own, or -1.
    private int _cellSlot = -1;
    private byte* _memory;
    private bool _applied;
    internal bool Applied => _applied;
    internal string Note = "off";

    private EnginePatch(string name, long targetRva, byte[] original, byte[] routine, params (int, long)[] addresses)
    {
        _name = name;
        _targetRva = targetRva;
        _original = original;
        _routine = routine;
        _addresses = addresses;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);
    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const uint MemCommitReserve = 0x3000, ExecuteRead = 0x20, ExecuteReadWrite = 0x40;

    // A page of executable memory holding `code`, or null.
    internal static byte* Executable(byte[] code)
    {
        var memory = (byte*)VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, MemCommitReserve, ExecuteReadWrite);
        if (memory == null) return null;
        Marshal.Copy(code, 0, (IntPtr)memory, code.Length);
        VirtualProtect((IntPtr)memory, (UIntPtr)4096, ExecuteRead, out _);
        FlushInstructionCache(GetCurrentProcess(), (IntPtr)memory, (UIntPtr)4096);
        return memory;
    }

    internal void Set(bool on)
    {
        if (on == _applied) return;
        if (!SceneSkip.Check())
        {
            Note = SceneSkip.Note;
            return;
        }

        var target = SceneSkip.Module + _targetRva;
        if (!on)
        {
            Write(target, _original);
            _applied = false;
            Note = $"{_name}: off";
            return;
        }

        if (!Matches(target, _original))
        {
            Note = $"{_name}: Unity's code isn't what this patch expects, left alone";
            return;
        }

        if (_memory == null)
        {
            var routine = (byte[])_routine.Clone();
            foreach (var (offset, rva) in _addresses) BitConverter.GetBytes((long)(SceneSkip.Module + rva)).CopyTo(routine, offset);
            if (_cellSlot >= 0)
            {
                var cell = Marshal.AllocHGlobal(8);
                Marshal.WriteInt64(cell, 0);
                BitConverter.GetBytes((long)cell).CopyTo(routine, _cellSlot);
            }
            _memory = Executable(routine);
            if (_memory == null)
            {
                Note = $"{_name}: no executable memory, left alone";
                return;
            }
        }

        Write(target, Jump((IntPtr)_memory, _original.Length));
        _applied = true;
        Note = $"{_name}: on";
    }

    internal static void Write(byte* target, byte[] bytes)
    {
        VirtualProtect((IntPtr)target, (UIntPtr)bytes.Length, ExecuteReadWrite, out var old);
        Marshal.Copy(bytes, 0, (IntPtr)target, bytes.Length);
        VirtualProtect((IntPtr)target, (UIntPtr)bytes.Length, old, out _);
        FlushInstructionCache(GetCurrentProcess(), (IntPtr)target, (UIntPtr)bytes.Length);
    }

    internal static bool Matches(byte* code, byte[] expected)
    {
        for (var i = 0; i < expected.Length; i++)
            if (code[i] != expected[i]) return false;
        return true;
    }

    // jmp [rip+0] to `to`, padded with nops (never run) to `length` bytes.
    internal static byte[] Jump(IntPtr to, int length)
    {
        var code = new byte[length];
        for (var i = 14; i < length; i++) code[i] = 0x90;
        code[0] = 0xFF;
        code[1] = 0x25;
        BitConverter.GetBytes((long)to).CopyTo(code, 6);
        return code;
    }
}

// Bots' animators the throttle holds back, paused inside Unity instead of switched off: switching
// one back on makes Unity walk its whole animation graph (src/SmoothieNative/animpause.c says
// why, and how the two hooks there keep Unity from updating a listed animator). Same build and
// same rules as EnginePatch: exact original bytes, written only from Plugin.Update, off puts
// them back (after letting every paused animator go). Each hook replaces a function's first
// whole instructions with a jump to SmoothieNative.dll; those instructions, followed by a jump
// back, become the trampoline the hook calls to run Unity's original.
internal static unsafe class AnimatorPause
{
    private const long UpdateAvatarsRva = 0xB16510, PrepareFrameRva = 0x76C950;
    // mov rax,rsp; mov [rax+20h],r9b; mov [rax+18h],r8b; mov [rax+8],rcx
    private static readonly byte[] UpdateAvatarsStart = { 0x48, 0x8B, 0xC4, 0x44, 0x88, 0x48, 0x20, 0x44, 0x88, 0x40, 0x18, 0x48, 0x89, 0x48, 0x08 };
    // mov rax,rsp; mov [rax+8],rcx; push rbp; push r14; lea rbp,[rax-68h]
    private static readonly byte[] PrepareFrameStart = { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x48, 0x08, 0x55, 0x41, 0x56, 0x48, 0x8D, 0x68, 0x98 };
    // Animator: its graph handle (node, version; the graph is the node's +0x28 when the versions
    // match), read the way Animator::AddToManager reads it.
    private const int GraphNode = 0x618, GraphVersion = 0x620, NodeVersion = 0x20, NodeGraph = 0x28;

    private static readonly HashSet<UnityEngine.Animator> Paused = new(Diagnostics.ReferenceComparer.Instance);
    private static readonly List<UnityEngine.Animator> Gone = new();
    private static bool _applied, _failed;
    private static SetFn? _set;
    internal static string Note = "off";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InitFn(IntPtr updateAvatars, IntPtr prepareFrame);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetFn(IntPtr* animators, IntPtr* graphs, int count);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    // Every frame from Plugin.Update.
    internal static void Sync(bool want)
    {
        if (want && !_applied && !_failed) Apply();
        else if (!want && _applied) Remove();
        // Animators get destroyed and graphs rebuilt: the list is redone every frame.
        if (_applied && Paused.Count > 0) Publish();
    }

    // True when paused this way; false: not available, switch it off instead.
    internal static bool Hold(UnityEngine.Animator animator)
    {
        if (!_applied) return false;
        if (Paused.Add(animator)) Publish();
        return true;
    }

    // True when it was paused this way (and now runs again).
    internal static bool Release(UnityEngine.Animator animator)
    {
        if (!Paused.Remove(animator)) return false;
        Publish();
        return true;
    }

    private static void Publish()
    {
        var animators = stackalloc IntPtr[Math.Max(1, Paused.Count)];
        var graphs = stackalloc IntPtr[Math.Max(1, Paused.Count)];
        var n = 0;
        foreach (var animator in Paused)
        {
            // Destroyed: dropped.
            var native = (byte*)SceneSkip.NativeOf(animator);
            if (native == null)
            {
                Gone.Add(animator);
                continue;
            }

            animators[n] = (IntPtr)native;
            graphs[n] = GraphOf(native);
            n++;
        }

        foreach (var animator in Gone) Paused.Remove(animator);
        Gone.Clear();
        _set?.Invoke(animators, graphs, n);
    }

    private static IntPtr GraphOf(byte* animator)
    {
        var node = *(byte**)(animator + GraphNode);
        if (node == null || *(uint*)(node + NodeVersion) != (*(uint*)(animator + GraphVersion) & ~1u)) return IntPtr.Zero;
        return *(IntPtr*)(node + NodeGraph);
    }

    private static void Apply()
    {
        _failed = true;
        if (!SceneSkip.Check())
        {
            Note = SceneSkip.Note;
            return;
        }

        var native = GetModuleHandleW("SmoothieNative.dll");
        var init = GetProcAddress(native, "SmoothieAnimInit");
        var set = GetProcAddress(native, "SmoothieAnimSet");
        var updateHook = GetProcAddress(native, "SmoothieUpdateAvatars");
        var prepareHook = GetProcAddress(native, "SmoothiePrepareFrame");
        if (native == IntPtr.Zero || init == IntPtr.Zero || set == IntPtr.Zero || updateHook == IntPtr.Zero || prepareHook == IntPtr.Zero)
        {
            Note = "SmoothieNative.dll missing or too old: off";
            return;
        }

        var updateAvatars = SceneSkip.Module + UpdateAvatarsRva;
        var prepareFrame = SceneSkip.Module + PrepareFrameRva;
        if (!EnginePatch.Matches(updateAvatars, UpdateAvatarsStart) || !EnginePatch.Matches(prepareFrame, PrepareFrameStart))
        {
            Note = "Unity's animator code isn't what this expects: off";
            return;
        }

        var updateTrampoline = EnginePatch.Executable(Trampoline(UpdateAvatarsStart, updateAvatars));
        var prepareTrampoline = EnginePatch.Executable(Trampoline(PrepareFrameStart, prepareFrame));
        if (updateTrampoline == null || prepareTrampoline == null)
        {
            Note = "no executable memory: off";
            return;
        }

        Marshal.GetDelegateForFunctionPointer<InitFn>(init)((IntPtr)updateTrampoline, (IntPtr)prepareTrampoline);
        _set = Marshal.GetDelegateForFunctionPointer<SetFn>(set);
        _set(null, null, 0);
        EnginePatch.Write(updateAvatars, EnginePatch.Jump(updateHook, UpdateAvatarsStart.Length));
        EnginePatch.Write(prepareFrame, EnginePatch.Jump(prepareHook, PrepareFrameStart.Length));
        _applied = true;
        _failed = false;
        Note = "on";
    }

    private static void Remove()
    {
        Paused.Clear();
        _set?.Invoke(null, null, 0);
        EnginePatch.Write(SceneSkip.Module + UpdateAvatarsRva, UpdateAvatarsStart);
        EnginePatch.Write(SceneSkip.Module + PrepareFrameRva, PrepareFrameStart);
        _applied = false;
        Note = "off";
    }

    // Unity's first instructions, then a jump back to the one after them.
    private static byte[] Trampoline(byte[] start, byte* function)
    {
        var code = new byte[start.Length + 14];
        start.CopyTo(code, 0);
        EnginePatch.Jump((IntPtr)(function + start.Length), 14).CopyTo(code, start.Length);
        return code;
    }
}
