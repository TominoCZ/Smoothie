.intel_syntax noprefix
.text
# Replacement for the head of the crowd part of NavMeshManager::UpdateNavMeshObstacles (function
# 0x701FF0, site RVA 0x702114). For every NavMeshObstacle, every frame, that part recomputes the
# obstacle's world box (walks up its transform's parents) and writes it into the CrowdManager's
# obstacle list, moved or not. Only NavMeshAgents' local avoidance reads that list, and EFT has
# none: bots path with NavMesh.CalculatePath and carving, which the carving part above (left as
# it is) takes care of. ~5% of the main thread on Streets in 0.16.
# Entered by jmp inside the per-obstacle loop: rsi = NavMeshManager, crowd at [rsi+0x90].
# Does Unity's own null test, then also skips the crowd part while no agent slot is active.
# Agent slots (CrowdManager, layout read from RemoveAgent 0xA82570): count [crowd+0x80], array
# [crowd+0xa0], 0x2c0 each, active = bit 0 of slot+0x2ba (cleared by RemoveAgent). As soon as
# one is active Unity's code runs for every obstacle again and writes each one's full shape, so
# nothing an agent reads is left stale.
# Leaves rcx = crowd on the way into Unity's crowd part, as Unity's code does. rax, r10, r11 are
# free: rax is loaded at both exits before use, and nothing in the loop reads r10/r11. Flags
# aren't read after either exit.
# Patch site (EnginePatch.NavCrowd): the 16 bytes at RVA 0x702114
#   48 8B 8E 90 00 00 00         mov rcx,[rsi+0x90]
#   48 85 C9                     test rcx,rcx
#   0F 84 EB 00 00 00            je 0x70220F
# become jmp [rip+0] + this routine's address + 2 nops. Address slots, filled at runtime:
# back_slot = RVA 0x702124 (the crowd part), skip_slot = RVA 0x70220F (next obstacle).
# Unity 2022.3.43f2 (BSG's build, SPT 5.0 / EFT 1.2), UnityPlayer_Win64_player_il2cpp_x64.pdb id
# 3C9C828B7504489DB8FD348F0F90ADE01. Assemble with assemble.sh; how this was found and how to
# redo it: EnginePatch.cs header and unity-internals.md.
cave:
    mov rcx, [rsi+0x90]
    test rcx, rcx
    jz skip
    mov r10d, [rcx+0x80]
    mov r11, [rcx+0xa0]
slot:
    dec r10d
    js skip
    imul rax, r10, 0x2c0
    test byte ptr [r11+rax+0x2ba], 1
    jz slot
    jmp qword ptr [rip+back_slot]
skip:
    jmp qword ptr [rip+skip_slot]
.balign 8
back_slot:
    .quad 0
skip_slot:
    .quad 0
