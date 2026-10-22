.intel_syntax noprefix
.text
# Replacement for the velocity read in NavMeshManager::UpdateNavMeshObstacles' per-obstacle step
# (function 0xD5180, its only caller the obstacle loop at 0x702022; site RVA 0xD527B).
# For every NavMeshObstacle, every frame, Unity asks the physics module (getter 0x15B1F0) for the
# velocity of the obstacle's object (virtual call +0xd8) and stores it at obstacle+0x94. Only the
# crowd part reads it back (agent avoidance; see nav_crowd.s), and the C# NavMeshObstacle.velocity
# getter, which neither the game nor the usual mods call. ~0.8% of the main thread on Streets in 0.16.
# This step doesn't get the manager, so it's read from Unity's global (RVA 0x1AB97E8, what
# NavMeshManager::Update 0xECA200 passes to the loop). Skips the read while the crowd is missing
# or no agent slot is active (same test as nav_crowd.s); otherwise does Unity's instructions and
# goes on to the virtual call. No manager at all: Unity's way.
# rax, r10, r11 are free at both exits: 0xD528D (the call's setup) and 0xD52BA (the function's
# end) don't read them before writing. Flags aren't read after either exit. The routine's own
# call to the getter is made with rsp exactly as Unity's was (entered by jmp).
# Patch site (EnginePatch.NavVelocity): the 18 bytes at RVA 0xD527B
#   E8 70 5F 08 00               call 0x15B1F0             (physics module getter)
#   48 85 C0                     test rax,rax
#   74 35                        je 0xD52BA
#   48 8B 08                     mov rcx,[rax]
#   4C 8D 44 24 20               lea r8,[rsp+0x20]
# become jmp [rip+0] + this routine's address + 4 nops. Address slots, filled at runtime:
# manager_slot = RVA 0x1AB97E8, module_slot = RVA 0x15B1F0, back_slot = RVA 0xD528D (mov rdx,
# [rbx+0x58], the call's setup), skip_slot = RVA 0xD52BA.
# Unity 2022.3.43f2 (BSG's build, SPT 5.0 / EFT 1.2), UnityPlayer_Win64_player_il2cpp_x64.pdb id
# 3C9C828B7504489DB8FD348F0F90ADE01. Assemble with assemble.sh; how this was found and how to
# redo it: EnginePatch.cs header and unity-internals.md.
cave:
    mov rax, [rip+manager_slot]
    mov rax, [rax]
    test rax, rax
    jz unity
    mov rax, [rax+0x90]
    test rax, rax
    jz skip
    mov r10d, [rax+0x80]
    mov r11, [rax+0xa0]
slot:
    dec r10d
    js skip
    imul rax, r10, 0x2c0
    test byte ptr [r11+rax+0x2ba], 1
    jz slot
unity:
    call qword ptr [rip+module_slot]
    test rax, rax
    jz skip
    mov rcx, [rax]
    lea r8, [rsp+0x20]
    jmp qword ptr [rip+back_slot]
skip:
    jmp qword ptr [rip+skip_slot]
.balign 8
manager_slot:
    .quad 0
module_slot:
    .quad 0
back_slot:
    .quad 0
skip_slot:
    .quad 0
