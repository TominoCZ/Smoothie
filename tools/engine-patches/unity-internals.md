# Unity internals Smoothie relies on (UnityPlayer.dll, this exact build)

Unity 2022.3.43**f2**, the game's own Win64 IL2CPP player for SPT 5.0 / EFT 1.2:
`UnityPlayer_Win64_player_il2cpp_x64.pdb`, GUID+age `3C9C828B7504489DB8FD348F0F90ADE01`
(`SceneSkip.BuildId`). Everything below was read from its machine code and holds for this build
only. How to redo it after a game update: the header of `src/Smoothie/EnginePatch.cs`.

RVAs are relative to the image base `0x180000000`. The f1 columns are Unity's public 2022.3.43f1
builds (Mono: SPT 4.1.6's, which Smoothie 1.0.x patched; IL2CPP: the named reference below).

## No symbols: the reference build

Unity never published symbols for 43f2 (a build it made for BSG; the symbol server answers 404).
Unity's own 43f1 IL2CPP player is the closest named code:

1. Download `https://download.unity3d.com/download_unity/85497d293fa1/TargetSupportInstaller/UnitySetup-Windows-IL2CPP-Support-for-Editor-2022.3.43f1.exe`.
2. 7-Zip can't open it, so take its overlay apart by hand. `7z x` the PE's `[0]` blob, which is an
   NSIS archive: after its 0x24-byte header comes a list of
   `[u32 length, top bit = compressed][data]` blocks, compressed ones raw LZMA with a 5-byte
   properties header. Block 744 is the Win64 non-development IL2CPP `UnityPlayer.dll`.
3. `pdb_id.py` on that block gives `2BE90635567F48A696DAB209A5224F9B1`, whose PDB is on Unity's
   symbol server. `publics.py` lists its symbols.

The two builds differ widely. `bmatch` pairs only a third of their functions, even with fuzzy and
call-graph rounds. BSG's `Object` base class is **0x28 bytes bigger**: every field of a native
`Renderer`, `LODGroup`, `Animator` or `NavMeshObstacle` sits 0x28 further in than in f1. Engine
structures that aren't Objects (LOD manager slots, crowd agents, playable outputs) kept their
layout. Inlining differs too: functions inlined in f1 are calls in f2, and the other way round.

So every name below was found function by function:
- **Internal calls:** `icall.py` reads each module's name/function tables, e.g.
  `UnityEngine.LODGroup::ForceLOD` leads to its native function in either build.
- **Unique byte patterns** with wildcards (`bsearch.py`).
- **Callers and callees** (`bmatch xref`), which the matcher's partial names help navigate.
- **Side-by-side disassembly** of both builds (`fnall.py`, `dis.sh`).

## Tools here

- `pdb_id.py <UnityPlayer.dll>`: the build's PDB id and the symbol server URL.
- `publics.py <.pdb> <dll>`: public symbols as `RVA name` lines.
- `dis.sh <dll> <publics> <RVA> [bytes]`: disassembly with calls named.
- `fnall.py <dll> <publics> <RVA>`: every `.pdata` chunk of the function holding an RVA.
- `icall.py <dll> [-t] [name...]`: IL2CPP internal calls, name to native function.
- `bsearch.py <dll> "<hex, ?? = any>"`: byte pattern search in the image.
- `name.py <publics> <RVA>...`: nearest public symbol.
- `bmatch/` (C#, needs `SPT_PATH` for Iced):
  - `bmatch <A.dll> <A publics> <B.dll>`: function matching, as hints.
  - `bmatch xref <dll> <RVA>...`: instructions referencing those addresses.
  - `bmatch xstr <dll> <text>`: instructions referencing a string.
- `assemble.sh <file.s>`: a routine as the byte list `EnginePatch.cs` embeds, with its slots.
- `check_bytes.py <dll>`: everything Smoothie writes, checked against the DLL and the `.s` files.
- `lod_loop_check.sh`, `light_count_check.sh`: those routines run natively on fake Unity data.
- `../il2cpp-xref/`: the same kind of call graph for the game's C# (`GameAssembly.dll`), for
  checking that a Harmony patch target is still called on its own (not inlined, not folded).
  Its `publics` mode writes a publics file for `GameAssembly.dll` (`dis.sh`, `fnall.py`, `stacks.py`).

Reading the game's own C# logic in `GameAssembly.dll` also needs field offsets and IL2CPP
metadata globals. Both come from Cpp2IL (`../il2cpp-dump/`), run offline the way BepInEx runs it. SPT keeps
`global-metadata.dat` decrypted on disk.
- Its dummy assemblies give every field offset and method RVA.
- LibCpp2IL's `GetAnyGlobalByAddress` names the type, method or string literal behind each
  `lea rcx,[rip+…]`.

The deferred ambient recording, the bot profile budget and the keep-alive sender were rebuilt this
way (see their comments in `HotPaths.cs`, `ProfileBudget.cs`, `BackendSender.cs`).

## Renderer fields (SceneSkip)

| What | f2 | f1 | Source in f2 |
|---|---|---|---|
| `forceRenderingOff`, one byte | `+0x168` | `+0x140` | its setter `0x259960` writes only this |
| flags; `& 0x1000` = drawn last frame | `+0x150` | `+0x128` | `Renderer.isVisible`'s internal call `0xBC4A30` |
| scene handle, `-1` when not in the scene | `+0x190` | `+0x168` | same getter: visible needs the bit and a handle |

Setting `forceRenderingOff` tells neither the scene nor the culling data anything. Culling still
visits the object; it just isn't drawn.

## The scene's per-object entry (RendererScene)

One entry per switched-on renderer, 24 bytes each. The scene is the global read by
`SceneAfterCullingOutputReady` (`0xAC47A0`). The entry array is at `scene+0x88`, the count at
`scene+0x98`, and `scene+0xc8` points to one visibility byte per entry. An entry starts with the
renderer's `BaseRenderer*`, which is the `Renderer` plus 0x60 (f1: plus 0x38).

## LOD manager (SceneSkip, LodCulling)

| Where | What | Source in f2 |
|---|---|---|
| RVA `0x1A88FF8` | `LODGroupManager*` (global) | `LODGroup.ForceLOD`'s internal call, `0x19EF70` |
| `manager+0x8` | slot array, `0x68` bytes per slot | same; `AddLODGroup` `0x90F120` |
| `manager+0x18` | slot count | same |
| `slot+0x0/+0x4/+0x8` | position | slot set-up `0x92CB20` |
| `slot+0x58` | the slot's `LODGroup*` (back-pointer) | slot set-up `0x92CB20` |
| `slot+0x60` | forced-level mask; `ForceLOD(n)` writes `1 << min(n, 7)` | `0x19EF70` |
| `LODGroup+0x98` | its slot index, 0 when not registered (f1: `+0x70`) | `0x19EF70` |

`CalculateLODMask` (`0x6A6070`) returns at once for a forced group: mask = the forced byte, fade
0. Its seventh argument, a `bool&`, isn't touched on that path. Level 7 exists in no group here,
so forcing it draws nothing.

**What changed from f1.** In f1, each camera's LOD results were kept on the manager from frame to
frame (`[manager+0x38]`), and Smoothie's loop skipped groups whose last mask was its own. In f2,
`CalculateLODMasks` (`0xA49020`) is called only through `0x73960`, from `0xA9C450`, from
`0xAF8370`. That chain hands it mask and fade vectors that are fresh locals each time. The
per-camera entries still exist (`AddLODGroup` and `RemoveLODGroup` keep them in step), but
nothing feeds them to the loop.

So Smoothie keeps its own byte per slot (SmoothieNative `lodflags.c`, 1 = forced by Smoothie),
and keeps it in step with Unity's slot moves:
- `SceneSkip` sets a slot's byte when it forces the group and clears it when it un-forces it.
- `RemoveLODGroup` (`0x40D020`, called from LODGroup deactivation with `group+0x98`) copies the
  last slot (`count - 1`) over the removed one and decrements the count. A prologue hook
  (`EnginePatch.LodRemoveHook`) moves the bytes the same way before Unity's original runs.
- The hook is on only while the LOD loop patch is. Each time the patch goes on, the bytes are
  rebuilt from the groups Smoothie forces (`LodCulling.RebuildFlags`).
- The loop reads the bytes 16 at a time and writes mask `0x80` / fade 0 for Smoothie's groups
  without touching their slots. Every pass it re-reads one block of 8192 slots and clears any
  byte whose slot isn't forced any more.

## The patched code (EnginePatch, AnimatorPause)

| Patch | Site (f2) | Back to | f1 Mono site |
|---|---|---|---|
| Visibility scan, `SceneAfterCullingOutputReady` | `0xAC4890` | `0xAC48CF` | `0x3EE942` |
| LOD loop, `CalculateLODMasks` perspective loop | `0xA49160` | `0xA4921F` | `0x4FC7C0` |
| Obstacle crowd update, `UpdateNavMeshObstacles` | `0x702114` | `0x702124` / skip `0x70220F` | `0xA4791A` |
| Obstacle velocity read, per-obstacle step `0xD5180` | `0xD527B` | `0xD528D` / skip `0xD52BA` | `0xA47831` |
| Light count, CullScene's light culling `0xD43780` | `0xD437A0` | `0xD437BC` | `0x3FC771` |
| LOD flag bytes: `LODGroupManager::RemoveLODGroup` | `0x40D020` (hook) | | |
| Animator pause: `Animator::UpdateAvatars` | `0xF06E10` (hook) | | `0xB16510` |
| Animator pause: `PlayableGraph::PrepareFrame` | `0x123EF0` (hook) | | `0x76C950` |

Each `.s` file lists its site's original bytes, registers on entry and address slots. Notes per
patch:

- **Visibility scan.** Same loop as f1, except the entry→Renderer offset (−0x60) and the loop
  counting `r15` down; the routine treats `r15` as the end index.
- **LOD loop.** f2 passes `CalculateLODMask` a seventh argument (`&[rsp+0xb0]`). The mask array
  base is at `[rsp+0xb8]`, the scratch mask pointer at `[rsp+0xc0]`, and the sqrt helper is
  `0x162EC48`. Smoothie's groups come from the flag bytes above. The orthographic loop
  (`0xA490F0`) is left alone.
- **Navigation.** In f2 the per-obstacle work before the crowd part moved into its own function
  (`0xD5180`), which doesn't get the manager. The velocity routine reads Unity's NavMeshManager
  global (`0x1AB97E8`, what `NavMeshManager::Update` `0xECA200` passes in) and the physics module
  through its getter (`0x15B1F0`). Crowd agent slots are unchanged from f1 (f2's `RemoveAgent`,
  `0xA82570`): count `crowd+0x80`, array `crowd+0xa0`, `0x2c0` each, active = bit 0 of `+0x2ba`.
- **Light count.** `LightManager` comes from a getter (`0x519EC0`, global `0x1A475E8`). The set
  layout is unchanged (`+0x10` buckets, `+0x18` size, live count `+0x1c`, read first by
  `FindActiveLights` `0xC10FE0`). The count is in `rdi` in f2 (`r14` in f1).
- **Animator pause.** Both functions are found through the `Animator.Update` internal call
  (`0x6B2DA0`), which calls `Animator::Update` (`0xF08300`), which calls both. Their callers pass
  the same frame types as in f1: 0 for a normal frame from `ExecutePrepareFrames`, 1/2/4 for
  play-state changes, 3 for `Evaluate` and `Animator.Update`. Playable output → Animator stays at
  `+0xD0` (getter `0x5195F0`). The Animator's graph handle is node `+0x640`, version `+0x648`
  (f1: `+0x618`/`+0x620`); the node's version is at `+0x20`, its graph at `+0x28`.

Nothing else jumps into any patched range (checked with `bmatch xref` over each range's bytes).

## Where the main thread's time goes

Measured on 0.16.9 / f1 Mono (Streets, Oct 2026). With everything on, the main thread was
~6.5 ms a frame: ~17% game C#, ~8% Mono runtime, ~8% the Wine/Proton system-call path, ~6.5%
world transforms computed for C# callers, then many items of 1-3%. Before the patches the
visibility scan was ~6.3% and the LOD loops ~5.3% of it. Not yet re-measured on f2 / IL2CPP.

## Tried and not used (f1)

- **Bit 29 on hidden objects' scene entries** (skip them in culling for one write): ~0.1 ms for
  84k meshes, and every undo is a scattered write.
- **`renderer.enabled = false` out of view**: ~1 ms saved standing still, 30-69 ms hitches on turns.
- **Far objects stop casting shadows**: no frame-time change (worker threads).
- **One queued frame**: GPU wait up, frame rate down.
- **Polling the depth readback more often**: frame rate down.
