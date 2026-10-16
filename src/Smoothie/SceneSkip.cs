using System;
using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace Smoothie;

// Takes LOD groups out of Unity's per-frame LOD work without switching anything off, and holds
// the build check every native trick in Smoothie relies on (EnginePatch too).
// Unity's LOD pass (LODGroupManager::CalculateLODMasks, RVA 0x4FC670) works out every LOD group's
// level (~134k on Streets) for every camera each frame; its per-group step
// (CalculateLODMask, 0x4FC400) first reads the group's forced-level byte and returns at once when
// it's set (LODGroup.ForceLOD writes 1 << min(level, 7) there: LODGroup_CUSTOM_ForceLOD, 0xCC620).
// Forcing level 7, which no group here has, draws nothing and skips that step's math. Done
// natively, a byte through the group's slot in Unity's LOD manager: ForceLOD through the scripting
// layer costs ~0.4 µs a call. The game never calls ForceLOD; a byte someone else set is left alone.
// Measured: a one-shot managed ForceLOD(7) test gained ~0.7-1 ms (95k groups), but forced groups
// are still visited by the loop (slot read, distance, square root, call), so the per-frame feature
// alone gained little; EnginePatch.LodLoop makes the loop skip that work for forced groups.
// Where the offsets come from: the global LOD manager pointer, slot layout and LODGroup+0x70 from
// ForceLOD's and AddLODGroup's (0x4FCF50) code; the slot's back-pointer at +0x58 from
// UpdateLODGroupParameters (0x4FD1D0). Found and checked the way EnginePatch describes (Unity's
// public symbols for this exact build, disassembly); background in
// tools/engine-patches/unity-internals.md. They hold for this exact UnityPlayer.dll only: any
// other build (Check), or a slot that doesn't point back at its group, leaves everything alone.
// (The scene entries' own skip bit, Unity's "being removed" mark, works too but saved only
// ~0.1 ms for 84k meshes and costs a scattered write per mesh to undo: not used.)
internal static unsafe class SceneSkip
{
    // UnityPlayer_Win64_player_mono_x64.pdb of Unity 2022.3.43f1, EFT's build.
    private const string BuildId = "BD9605D7A8BC4F21AF766D4707334ADC1";
    private const long LodManagerRva = 0x1CF96A0; // LODGroupManager* (global)
    private const int LodSlots = 0x8, LodSlotCount = 0x18, LodSlotSize = 0x68; // its group slots
    private const int SlotGroup = 0x58, SlotForced = 0x60; // slot: LODGroup*, forced-level mask
    private const int GroupSlot = 0x70; // LODGroup: its slot's index, 0 when not registered
    private const byte NothingMask = 0x80; // level 7
    // Native Renderer fields read by Unity's own property getters (Renderer_Get_Custom_PropIsVisible
    // 0xA2A50: visible = flags & 0x1000 and scene handle != -1; the forceRenderingOff setter
    // 0xA31D0 only writes the byte at +0x140).
    private const int RendererFlags = 0x128, RendererVisible = 0x1000, RendererHandle = 0x168, RendererForceOff = 0x140;

    private static bool _checked;
    private static byte* _lodManagerPointer;
    // UnityPlayer.dll in memory, once the build matched (EnginePatch).
    internal static byte* Module;
    // UnityEngine.Object.m_CachedPtr: the native object, zero once destroyed.
    private static readonly AccessTools.FieldRef<UnityEngine.Object, IntPtr> _cachedPtr =
        AccessTools.FieldRefAccess<UnityEngine.Object, IntPtr>("m_CachedPtr");
    internal static bool Available { get; private set; }
    internal static IntPtr NativeOf(UnityEngine.Object o) => _cachedPtr(o);
    internal static string Note = "not checked";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

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
        var n = (byte*)_cachedPtr(group);
        if (n == null) return 0;
        var index = *(uint*)(n + GroupSlot);
        var slot = Slot(index, n);
        if (slot == null || slot[SlotForced] != 0) return 0;
        slot[SlotForced] = NothingMask;
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
            var n = (byte*)_cachedPtr(group);
            if (n == null) return;
            index = *(uint*)(n + GroupSlot);
            slot = Slot(index, n);
        }

        if (slot == null || slot[SlotForced] != NothingMask) return;
        slot[SlotForced] = 0;
        ClearResults(index);
    }

    // Every camera's LOD results, kept from frame to frame on the manager (one entry per camera,
    // its masks a byte per slot; RemoveLODGroup moves them along with the slots). The LOD loop
    // patch passes over a group whose last mask is ours without reading its slot
    // (EnginePatch.LodLoop), so un-forcing one clears that mask everywhere: the next pass works
    // the group out again, before the frame it could show in culls.
    private const int LodResults = 0x38, LodResultCount = 0x48, ResultMasks = 0x10, ResultMaskCount = 0x20;

    private static void ClearResults(uint index)
    {
        var manager = *(byte**)_lodManagerPointer;
        var entries = *(byte***)(manager + LodResults);
        var count = *(ulong*)(manager + LodResultCount);
        for (ulong e = 0; e < count; e++)
        {
            var entry = entries[e];
            if (entry == null || index >= *(ulong*)(entry + ResultMaskCount)) continue;
            var masks = *(byte**)(entry + ResultMasks);
            if (masks[index] == NothingMask) masks[index] = 0;
        }
    }

    // renderer.isVisible and forceRenderingOff without a call into Unity per renderer: the same
    // fields its getters and setter read and write, straight from the native object. Hiding asks
    // these thousands of times a check; each scripting call costs tens of ns (a native transition
    // plus Unity's argument marshalling), a read a few. Only once Check passed (Available); a
    // destroyed renderer (m_CachedPtr zero) reads as not visible and not switched off.
    internal static bool IsVisible(Renderer renderer)
    {
        var native = (byte*)_cachedPtr(renderer);
        return native != null && (*(int*)(native + RendererFlags) & RendererVisible) != 0 && *(int*)(native + RendererHandle) != -1;
    }

    internal static bool ForceRenderingOff(Renderer renderer)
    {
        var native = (byte*)_cachedPtr(renderer);
        return native != null && native[RendererForceOff] != 0;
    }

    internal static void SetForceRenderingOff(Renderer renderer, bool off)
    {
        var native = (byte*)_cachedPtr(renderer);
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
