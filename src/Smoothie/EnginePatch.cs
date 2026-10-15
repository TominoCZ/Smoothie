using System;
using System.Runtime.InteropServices;

namespace Smoothie;

// Faster versions of loops in Unity's own (native, C++) engine code, put in place at runtime:
// memory only, nothing on disk changes. Each replaces the first bytes of one loop with a jump to a
// routine doing the same work faster, which jumps back into Unity's code right after the loop.
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
    // nothing can be seen of) and sends every other group Unity's own way.
    internal static readonly EnginePatch LodLoop = new(
        "LOD loop", 0x4FC7C0,
        new byte[] { 0x4C, 0x8B, 0x6E, 0x08, 0x4A, 0x8D, 0x04, 0x03, 0x4C, 0x03, 0xEF, 0x48, 0x89, 0x84, 0x24, 0xB0, 0x00, 0x00, 0x00 },
        new byte[]
        {
            0x4C, 0x39, 0xF3, 0x0F, 0x83, 0xC7, 0x00, 0x00, 0x00, 0x4C, 0x8B, 0x6E, 0x08, 0x49, 0x01, 0xFD,
            0x4C, 0x8B, 0x84, 0x24, 0xA8, 0x00, 0x00, 0x00, 0x4A, 0x8D, 0x04, 0x03, 0x41, 0x0F, 0xB6, 0x4D,
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
            0x48, 0xFF, 0xC3, 0x48, 0x83, 0xC5, 0x04, 0x48, 0x83, 0xC7, 0x68, 0xE9, 0x30, 0xFF, 0xFF, 0xFF,
            0xFF, 0x25, 0x02, 0x00, 0x00, 0x00, 0x66, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        },
        // back after the loop, CalculateLODMask, the square-root helper Unity calls for a negative value
        (0xD8, 0x4FC872), (0xE0, 0x4FC400), (0xE8, 0x18343FC));

    private readonly string _name;
    private readonly long _targetRva;
    private readonly byte[] _original, _routine;
    // Routine offsets that hold an absolute address, and the RVA in UnityPlayer.dll to put there.
    private readonly (int Offset, long Rva)[] _addresses;
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

        for (var i = 0; i < _original.Length; i++)
        {
            if (target[i] == _original[i]) continue;
            Note = $"{_name}: Unity's code isn't what this patch expects, left alone";
            return;
        }

        if (_memory == null)
        {
            var memory = (byte*)VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, MemCommitReserve, ExecuteReadWrite);
            if (memory == null)
            {
                Note = $"{_name}: no executable memory, left alone";
                return;
            }

            Marshal.Copy(_routine, 0, (IntPtr)memory, _routine.Length);
            foreach (var (offset, rva) in _addresses) *(long*)(memory + offset) = (long)(SceneSkip.Module + rva);
            VirtualProtect((IntPtr)memory, (UIntPtr)4096, ExecuteRead, out _);
            FlushInstructionCache(GetCurrentProcess(), (IntPtr)memory, (UIntPtr)4096);
            _memory = memory;
        }

        // jmp [rip+0]; dq routine; the rest of the replaced bytes nop (never run)
        var jump = new byte[_original.Length];
        for (var i = 14; i < jump.Length; i++) jump[i] = 0x90;
        jump[0] = 0xFF;
        jump[1] = 0x25;
        BitConverter.GetBytes((long)_memory).CopyTo(jump, 6);
        Write(target, jump);
        _applied = true;
        Note = $"{_name}: on";
    }

    private static void Write(byte* target, byte[] bytes)
    {
        VirtualProtect((IntPtr)target, (UIntPtr)bytes.Length, ExecuteReadWrite, out var old);
        Marshal.Copy(bytes, 0, (IntPtr)target, bytes.Length);
        VirtualProtect((IntPtr)target, (UIntPtr)bytes.Length, old, out _);
        FlushInstructionCache(GetCurrentProcess(), (IntPtr)target, (UIntPtr)bytes.Length);
    }
}
