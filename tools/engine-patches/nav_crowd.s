.intel_syntax noprefix
.text
# Replacement for the head of the crowd part of NavMeshManager::UpdateNavMeshObstacles (RVA 0xA4791A).
# For every NavMeshObstacle, every frame, that part recomputes the obstacle's world box (several
# walks up its transform's parents) and writes it into the CrowdManager's obstacle list, moved or
# not. Only NavMeshAgents' local avoidance reads that list, and EFT has none: bots path with
# NavMesh.CalculatePath and carving, which the carving part above (left as it is) takes care of.
# ~5% of the main thread on Streets.
# Entered by jmp inside the per-obstacle loop: r13 = NavMeshManager, crowd at [r13+0x90].
# Does Unity's own null test, then also skips the crowd part while no agent slot is active.
# Agent slots: count [crowd+0x80] (4 reserved at start, only grows), array [crowd+0xa0], 0x2c0
# each, active = bit 0 of slot+0x2ba (set by AddAgent, cleared by RemoveAgent, counted by
# CrowdManager::Update). As soon as one is active Unity's code runs for every obstacle again and
# writes each one's full shape, so nothing an agent reads is left stale.
# rax, rcx, rdx are free here: both ways out load them before use. Flags aren't read after.
# Patch site (EnginePatch.NavCrowd): the 16 bytes at RVA 0xA4791A
#   49 8B 9D 90 00 00 00         mov rbx,[r13+0x90]
#   48 85 DB                     test rbx,rbx
#   0F 84 F3 04 00 00            je 0xA47E1D
# become jmp [rip+0] + this routine's address + 2 nops. Address slots, filled at runtime:
# back_slot = RVA 0xA4792A (the crowd part), skip_slot = RVA 0xA47E1D (next obstacle).
# Unity 2022.3.43f1, UnityPlayer_Win64_player_mono_x64.pdb id BD9605D7A8BC4F21AF766D4707334ADC1.
# Assemble with assemble.sh; how this was found and how to redo it: EnginePatch.cs header.
cave:
    mov rbx, [r13+0x90]
    test rbx, rbx
    jz skip
    mov eax, [rbx+0x80]
    mov rcx, [rbx+0xa0]
slot:
    dec eax
    js skip
    imul rdx, rax, 0x2c0
    test byte ptr [rcx+rdx+0x2ba], 1
    jz slot
    jmp qword ptr [rip+back_slot]
skip:
    jmp qword ptr [rip+skip_slot]
.balign 8
back_slot:
    .quad 0
skip_slot:
    .quad 0
