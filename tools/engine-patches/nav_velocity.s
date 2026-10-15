.intel_syntax noprefix
.text
# Replacement for the velocity read in NavMeshManager::UpdateNavMeshObstacles (RVA 0xA47831).
# For every NavMeshObstacle, every frame, Unity asks the physics module for the velocity of the
# obstacle's object (virtual call +0xd8, PhysicsModule::GetObjectVelocity) and stores it at
# obstacle+0x6c. Only the crowd part reads it back (agent avoidance; see nav_crowd.s), and the
# C# NavMeshObstacle.velocity getter, which neither EFT nor the usual mods call. ~0.8% of the
# main thread on Streets.
# Entered by jmp inside the per-obstacle loop: r13 = NavMeshManager, crowd at [r13+0x90],
# rbx = obstacle. Skips the read while no agent slot is active (same test as nav_crowd.s);
# otherwise does Unity's instructions and goes on to the call.
# rax, rcx, rdx are free at both exits: 0xA47840 sets rdx and gets rcx/rax as Unity's code
# leaves them; Unity's own skip path reaches 0xA47862 with them in any state, and the code there
# loads them before use. Flags aren't read after either exit.
# Patch site (EnginePatch.NavVelocity): the 15 bytes at RVA 0xA47831
#   48 8B 0D B0 1F 2B 01         mov rcx,[rip+0x12b1fb0]   (physics module pointer, RVA 0x1CF97E8)
#   48 85 C9                     test rcx,rcx
#   74 25                        je 0xA47862
#   48 8B 01                     mov rax,[rcx]
# become jmp [rip+0] + this routine's address + 1 nop. Address slots, filled at runtime:
# physics_slot = RVA 0x1CF97E8, back_slot = RVA 0xA47840 (the call), skip_slot = RVA 0xA47862.
# Unity 2022.3.43f1, UnityPlayer_Win64_player_mono_x64.pdb id BD9605D7A8BC4F21AF766D4707334ADC1.
# Assemble with assemble.sh; how this was found and how to redo it: EnginePatch.cs header.
cave:
    mov rcx, [r13+0x90]
    test rcx, rcx
    jz skip
    mov eax, [rcx+0x80]
    mov rdx, [rcx+0xa0]
slot:
    dec eax
    js skip
    imul rcx, rax, 0x2c0
    test byte ptr [rdx+rcx+0x2ba], 1
    jz slot
    mov rcx, [rip+physics_slot]
    mov rcx, [rcx]
    test rcx, rcx
    jz skip
    mov rax, [rcx]
    jmp qword ptr [rip+back_slot]
skip:
    jmp qword ptr [rip+skip_slot]
.balign 8
physics_slot:
    .quad 0
back_slot:
    .quad 0
skip_slot:
    .quad 0
