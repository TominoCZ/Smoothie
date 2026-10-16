# Unity internals Smoothie relies on (UnityPlayer.dll, this exact build)

Unity 2022.3.43f1, EFT's Win64 Mono player: `UnityPlayer_Win64_player_mono_x64.pdb`, GUID+age
`BD9605D7A8BC4F21AF766D4707334ADC1` (`SceneSkip.BuildId`). Everything below was read from the
machine code with Unity's public symbols for this build and holds for this build only. How to get
the symbols and redo it after a game update: the header of `src/Smoothie/EnginePatch.cs`.

Tools here:

- `pdb_id.py <UnityPlayer.dll>`: the build's PDB id and the symbol server URL.
- `publics.py <.pdb>`: public symbols as `RVA name` lines.
- `dis.sh <UnityPlayer.dll> <publics.txt> <RVA> [bytes]`: disassembly with calls named.
- `assemble.sh <file.s>`: a routine as the byte list `EnginePatch.cs` embeds, with its slots.
- `visibility_scan.s`, `lod_loop.s`: the two replacement routines.

RVAs are relative to the image base `0x180000000`.

## Renderer fields (SceneSkip)

| Offset | What | Source |
|---|---|---|
| `Renderer+0x140` | `forceRenderingOff`, one byte | `Renderer_Set_Custom_PropForceRenderingOff` (`0xA31D0`) writes only this |
| `Renderer+0x128` | flags; `& 0x1000` = drawn last frame | `Renderer_Get_Custom_PropIsVisible` (`0xA2A50`) |
| `Renderer+0x168` | scene handle, `-1` when not in the scene | same getter: visible needs the bit and a handle |

Setting `forceRenderingOff` tells neither the scene nor the culling data anything: culling still
visits the object (see below), it only isn't drawn.

## The scene's per-object entry (`SceneNode`, 24 bytes)

`RendererScene` keeps one entry per switched-on renderer: the array at `scene+0x88`, the count at
`scene+0x98`, and at `scene+0xc8` a pointer to one visibility byte per entry.

| Offset | Size | What |
|---|---|---|
| 0x00 | 8 | `BaseRenderer*` (the `Renderer` plus 0x38) |
| 0x08 | 4 | layer |
| 0x10 | 2 | LOD data index |
| 0x12 | 1 | LOD level mask |
| 0x14 | 4 | flags: bits 0-27 LOD group, bit 29 being removed, bits 30-31 shadow casting mode |

The main camera's culling pre-pass checks, per entry and in this order: shadows-only, layer in the
camera's mask, null renderer, bit 29, `forceRenderingOff` (read through the renderer pointer: a
cache miss per object), LOD level; only then the box against the view. The shadow-caster kernels
do the same. `renderer.enabled = false` removes the entry instead, so nothing is read at all, but
putting it back costs ~0.6-1.7 µs per object (a turn brought tens of thousands back at once).

## LOD manager (SceneSkip, LodCulling)

| Where | What | Source |
|---|---|---|
| RVA `0x1CF96A0` | `LODGroupManager*` (global) | `LODGroup_CUSTOM_ForceLOD` (`0xCC620`), `AddLODGroup` (`0x4FCF50`) |
| `manager+0x8` | slot array, `0x68` bytes per slot | same |
| `manager+0x18` | slot count | same |
| `slot+0x58` | the slot's `LODGroup*` (back-pointer) | `UpdateLODGroupParameters` (`0x4FD1D0`) |
| `slot+0x60` | forced-level mask; `ForceLOD(n)` writes `1 << min(n, 7)` | `LODGroup_CUSTOM_ForceLOD` |
| `LODGroup+0x70` | its slot index, 0 when not registered | `AddLODGroup` |

`CalculateLODMask` (`0x4FC400`) returns at once for a forced group (mask = the forced byte, fade
0). Level 7 exists in no group here, so forcing it draws nothing.

Each camera's LOD results are kept from frame to frame on the manager: `[manager+0x38]` an array
of entry pointers, count `+0x48`; an entry is the camera's id at `+0`, last time at `+0x8`, the
mask bytes (a `core::vector<uint8>`) at `+0x10` (size `+0x20`) and the fades (`vector<float>`)
at `+0x30` (size `+0x40`), one per slot (`CalculateLODDataArray`, `0x4FCCD0`, finds or makes the
camera's entry and the loop writes into it). `AddLODGroup` appends 0 to every entry;
`RemoveLODGroup` (`0x4FD550`) moves the last slot into the removed one and does the same in
every entry. The LOD loop patch reads a group's last mask before its slot: `0x80` means
Smoothie's forced level, so the slot is skipped (`SceneSkip.Unforce` clears that byte in every
entry; every 16th group is still read each pass). Checked natively by `lod_loop_check.sh`. Slots move when groups are
removed, so a slot is always found again through `LODGroup+0x70` and checked by its back-pointer.

## The two patched loops (EnginePatch)

**Visibility scan**, in `RendererScene::SceneAfterCullingOutputReady`, patch site RVA `0x3EE942`,
back at `0x3EE981`. Unity walks every entry's visibility byte (~220k on Streets) one at a time,
and for each that is exactly 1 calls the renderer's virtual `0xd8` and ORs the byte with 4. The
replacement compares 16 bytes at a time (SSE2) and goes entry by entry only through a block with
a match: same calls, same writes, same order.

**LOD loop**, the perspective loop of `LODGroupManager::CalculateLODMasks` (`0x4FC670`), patch
site RVA `0x4FC7C0`, back at `0x4FC872`. For each group Unity works out the distance (with a
square root) and calls `CalculateLODMask`, even for forced groups, which ignore all of it. The
replacement hands a forced group exactly what `CalculateLODMask` would (its forced byte, fade 0)
and runs every other group through Unity's own instructions. The orthographic loop (`0x4FC730`)
is left alone.

**Light count**, at the start of CullScene's light culling (the function at `0x3FC750`, named
after `~BRGCullResults` in the symbols), patch site RVA `0x3FC771`, back at `0x3FC78C`.
`LightManager*` is the global at RVA `0x1CF9450`; the manager is the head of an intrusive linked
list of every enabled `Light` (links at `Light+0x40`, no size field), and at `+0x10` a hash set
of the same lights minus fully baked ones (lightmap mode 2), live count at `+0x1c`. Unity counts
the list one light at a time on every culling pass, only to size the pass's light buffers, which
only `FindActiveLights` fills, from the set. The replacement reads the set's count and walks only
when it is 0. Checked natively by `light_count_check.sh`.

Patch sites, original bytes and address slots are listed at the top of each `.s` file.

## Where the main thread's time goes (Streets, perf, Oct 2026)

With everything on, the main thread is ~6.5 ms a frame: ~17% game C#, ~8% Mono runtime, ~8% the
Wine/Proton system-call path (job waits, timer reads), ~6.5% world transforms computed for C#
callers, then many items of 1-3% (job waits, instanced-draw cleanup, UI layout, the LOD loop).
Before the patches the visibility scan was ~6.3% and the LOD loops ~5.3% of it.

Worker-thread culling (the shadow-caster kernels, ~12% of all CPU samples) runs in parallel:
cutting most far shadow casters didn't change the frame time.

## Tried and not used

- **Bit 29 on hidden objects' entries** (skip them in culling for one write): ~0.1 ms for 84k
  meshes, and every undo is a scattered write.
- **`renderer.enabled = false` out of view**: ~1 ms saved standing still, 30-69 ms hitches on turns.
- **Far objects stop casting shadows**: no frame-time change (worker threads).
- **One queued frame** (`QualitySettings.maxQueuedFrames = 1`, to get the depth back sooner): GPU
  wait up, frame rate down.
- **Polling the depth readback more often**: frame rate down.
