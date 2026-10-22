using System;
using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace Smoothie;

// Takes LOD groups out of Unity's per-frame LOD work without switching anything off, and holds
// the build check every native trick in Smoothie relies on (EnginePatch too).
// Unity's LOD pass (LODGroupManager::CalculateLODMasks, RVA 0xA49020) works out every LOD group's
// level (~134k on Streets) for every camera each frame; its per-group step
// (CalculateLODMask, 0x6A6070) first reads the group's forced-level byte and returns at once when
// it's set (LODGroup.ForceLOD writes 1 << min(level, 7) there: its internal call, 0x19EF70).
// Forcing level 7, which no group here has, draws nothing and skips that step's math. Done
// natively, a byte through the group's slot in Unity's LOD manager: ForceLOD through the scripting
// layer costs ~0.4 µs a call. The game never calls ForceLOD; a byte someone else set is left alone.
// Measured: a one-shot managed ForceLOD(7) test gained ~0.7-1 ms (95k groups), but forced groups
// are still visited by the loop (slot read, distance, square root, call), so the per-frame feature
// alone gained little; EnginePatch.LodLoop makes the loop skip that work for forced groups.
// Where the offsets come from: the global LOD manager pointer, slot layout and LODGroup+0x98 from
// ForceLOD's and AddLODGroup's (0x90F120) code; the slot's back-pointer at +0x58 from the slot
// set-up AddLODGroup calls (0x92CB20). Found and checked the way EnginePatch describes (this build
// has no public symbols: Unity's own 2022.3.43f1 IL2CPP player is the named reference);
// background in tools/engine-patches/unity-internals.md. They hold for this exact UnityPlayer.dll
// only: any other build (Check), or a slot that doesn't point back at its group, leaves
// everything alone.
// (The scene entries' own skip bit, Unity's "being removed" mark, works too but saved only
// ~0.1 ms for 84k meshes and costs a scattered write per mesh to undo: not used.)
internal static unsafe class SceneSkip
{
    // UnityPlayer_Win64_player_il2cpp_x64.pdb of Unity 2022.3.43f2, the game's (BSG's) build for
    // SPT 5.0 / EFT 1.2. Its Object base is 0x28 bytes bigger than public Unity's: every native
    // object field below is 0x28 further in than in 2022.3.43f1.
    private const string BuildId = "3C9C828B7504489DB8FD348F0F90ADE01";
    private const long LodManagerRva = 0x1A88FF8; // LODGroupManager* (global)
    private const int LodSlots = 0x8, LodSlotCount = 0x18, LodSlotSize = 0x68; // its group slots
    private const int SlotGroup = 0x58, SlotForced = 0x60; // slot: LODGroup*, forced-level mask
    private const int GroupSlot = 0x98; // LODGroup: its slot's index, 0 when not registered
    private const byte NothingMask = 0x80; // level 7
    // Native Renderer fields read by Unity's own property getters (Renderer.isVisible's internal
    // call 0xBC4A30: visible = flags & 0x1000 and scene handle != -1; forceRenderingOff's setter
    // 0x259960 only writes the byte at +0x168).
    private const int RendererFlags = 0x150, RendererVisible = 0x1000, RendererHandle = 0x190, RendererForceOff = 0x168;

    private static bool _checked;
    private static byte* _lodManagerPointer;
    // UnityPlayer.dll in memory, once the build matched (EnginePatch).
    internal static byte* Module;
    internal static bool Available { get; private set; }
    // UnityEngine.Object.m_CachedPtr: the native object, zero once destroyed.
    internal static IntPtr NativeOf(UnityEngine.Object o) => o.m_CachedPtr;
    internal static string Note = "not checked";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    // SmoothieNative's byte per LOD slot (lodflags.c): 1 where Smoothie forced the group in that
    // slot. EnginePatch.LodLoop reads them instead of the slots; null without SmoothieNative.dll.
    private static byte* _flags;
    private static int _flagCount;
    private static bool _flagsLooked;

    internal static byte* Flags
    {
        get
        {
            if (_flagsLooked) return _flags;
            _flagsLooked = true;
            var native = GetModuleHandleW("SmoothieNative.dll");
            var flags = GetProcAddress(native, "SmoothieLodFlags");
            var count = GetProcAddress(native, "SmoothieLodFlagCount");
            if (native == IntPtr.Zero || flags == IntPtr.Zero || count == IntPtr.Zero) return null;
            _flagCount = ((delegate* unmanaged[Cdecl]<int>)count)();
            _flags = ((delegate* unmanaged[Cdecl]<byte*>)flags)();
            return _flags;
        }
    }

    private static void SetFlag(uint index, bool forced)
    {
        var flags = Flags;
        if (flags != null && index < (uint)_flagCount) flags[index] = forced ? (byte)1 : (byte)0;
    }

    // Every byte from scratch, for the groups Smoothie has forced (the hook that keeps them in step
    // with Unity's slot moves was off until now, so the old ones may point at moved slots).
    internal static bool RebuildFlags(System.Collections.Generic.IEnumerable<LODGroup> forced)
    {
        var flags = Flags;
        if (flags == null || !Available) return false;
        new Span<byte>(flags, _flagCount).Clear();
        foreach (var group in forced)
        {
            var n = group is null ? null : (byte*)group.m_CachedPtr;
            if (n == null) continue;
            var index = *(uint*)(n + GroupSlot);
            var slot = Slot(index, n);
            if (slot != null && slot[SlotForced] == NothingMask) SetFlag(index, true);
        }

        return true;
    }

    internal static bool Check()
    {
        if (_checked) return Available;
        _checked = true;
        try
        {
            var id = PdbId(Path.Combine(Path.GetDirectoryName(Application.dataPath)!, "UnityPlayer.dll"));
            if (id != BuildId)
            {
                Note = $"UnityPlayer.dll is build {id}, not {BuildId}: off";
                return false;
            }

            var module = GetModuleHandleW("UnityPlayer.dll");
            if (module == IntPtr.Zero)
            {
                Note = "UnityPlayer.dll not found: off";
                return false;
            }

            _lodManagerPointer = (byte*)module + LodManagerRva;
            Module = (byte*)module;
            Available = true;
            Note = "on (build matches)";
        }
        catch (Exception ex)
        {
            Note = $"check failed: {ex.Message}";
        }

        return Available;
    }

    // The slot at `index`, if it still belongs to the native group (slots move when groups are
    // removed; the manager's array moves when it grows, so it's always read afresh).
    private static byte* Slot(uint index, byte* native)
    {
        var manager = *(byte**)_lodManagerPointer;
        if (manager == null || native == null || index == 0 || index >= *(ulong*)(manager + LodSlotCount)) return null;
        var slot = *(byte**)(manager + LodSlots) + (long)index * LodSlotSize;
        return *(byte**)(slot + SlotGroup) == native ? slot : null;
    }

    // Forces the group to draw nothing. Returns its slot index (0 when it wasn't forced: not
    // registered, or someone else forced it) and its native object, to find the slot again fast.
    internal static uint ForceNothing(LODGroup group, out IntPtr native)
    {
        native = IntPtr.Zero;
        if (!Available || group is null) return 0;
        var n = (byte*)group.m_CachedPtr;
        if (n == null) return 0;
        var index = *(uint*)(n + GroupSlot);
        var slot = Slot(index, n);
        if (slot == null || slot[SlotForced] != 0) return 0;
        slot[SlotForced] = NothingMask;
        SetFlag(index, true);
        native = (IntPtr)n;
        return index;
    }

    // Whether the slot still holds our forced level (A/B log: the write took and stayed).
    internal static bool IsForced(uint index, IntPtr native)
    {
        if (!Available) return false;
        var slot = Slot(index, (byte*)native);
        return slot != null && slot[SlotForced] == NothingMask;
    }

    // Back to normal: through the remembered slot, or looked up again if the group moved.
    internal static void Unforce(LODGroup group, uint index, IntPtr native)
    {
        if (!Available) return;
        var slot = Slot(index, (byte*)native);
        if (slot == null)
        {
            if (group is null) return;
            var n = (byte*)group.m_CachedPtr;
            if (n == null) return;
            index = *(uint*)(n + GroupSlot);
            slot = Slot(index, n);
        }

        if (slot == null || slot[SlotForced] != NothingMask) return;
        slot[SlotForced] = 0;
        SetFlag(index, false);
    }

    // renderer.isVisible and forceRenderingOff without a call into Unity per renderer: the same
    // fields its getters and setter read and write, straight from the native object. Hiding asks
    // these thousands of times a check; each scripting call costs tens of ns (a native transition
    // plus Unity's argument marshalling), a read a few. Only once Check passed (Available); a
    // destroyed renderer (m_CachedPtr zero) reads as not visible and not switched off.
    internal static bool IsVisible(Renderer renderer)
    {
        var native = (byte*)renderer.m_CachedPtr;
        return native != null && (*(int*)(native + RendererFlags) & RendererVisible) != 0 && *(int*)(native + RendererHandle) != -1;
    }

    internal static bool ForceRenderingOff(Renderer renderer)
    {
        var native = (byte*)renderer.m_CachedPtr;
        return native != null && native[RendererForceOff] != 0;
    }

    internal static void SetForceRenderingOff(Renderer renderer, bool off)
    {
        var native = (byte*)renderer.m_CachedPtr;
        if (native != null) native[RendererForceOff] = off ? (byte)1 : (byte)0;
    }

    // GUID + age of the PDB a PE file was built with (its RSDS debug record).
    private static string PdbId(string path)
    {
        var d = File.ReadAllBytes(path);
        var pe = BitConverter.ToInt32(d, 0x3c);
        int sections = BitConverter.ToUInt16(d, pe + 6), optional = BitConverter.ToUInt16(d, pe + 20);
        var opt = pe + 24;
        var dirs = opt + (BitConverter.ToUInt16(d, opt) == 0x20b ? 112 : 96);
        int debugRva = BitConverter.ToInt32(d, dirs + 6 * 8), debugSize = BitConverter.ToInt32(d, dirs + 6 * 8 + 4);
        var debug = -1;
        for (var s = 0; s < sections; s++)
        {
            var h = opt + optional + s * 40;
            int va = BitConverter.ToInt32(d, h + 12), size = Math.Max(BitConverter.ToInt32(d, h + 8), 1), raw = BitConverter.ToInt32(d, h + 20);
            if (debugRva >= va && debugRva < va + size) debug = debugRva - va + raw;
        }

        for (var e = 0; debug >= 0 && e < debugSize / 28; e++)
        {
            var entry = debug + e * 28;
            if (BitConverter.ToInt32(d, entry + 12) != 2) continue;
            var p = BitConverter.ToInt32(d, entry + 24);
            if (BitConverter.ToInt32(d, p) != 0x53445352) continue; // "RSDS"
            var g = new Guid(new ArraySegment<byte>(d, p + 4, 16).ToArray()).ToString("N").ToUpperInvariant();
            return g + BitConverter.ToInt32(d, p + 20).ToString("X");
        }

        return "unknown";
    }
}
