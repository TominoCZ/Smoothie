.intel_syntax noprefix
.text
# Replacement for the light count at the start of CullScene's light culling (the function at RVA
# 0x3FC750, named after ~BRGCullResults in the symbols). Every culling pass, several a frame,
# Unity counts LightManager's lights by walking its linked list of every enabled light (Light+0x40
# links; the list has no size field), one cache miss per light, only to size that pass's light
# buffers. ~2% of the main thread on Streets, 0.37% on Factory (598 lights).
# LightManager also keeps a hash set of the same lights minus fully baked ones (AddToManager skips
# lightmap mode 2), and that set counts itself: [manager+0x1c], raised by its insert, lowered by
# RemoveFromManager, the first thing FindActiveLights reads. The buffers sized from this count are
# filled only by FindActiveLights, one entry per light it takes from that set, and everything
# after it is sized from the lights found (r14 is zeroed at 0x3FCC46). So the set's count is
# enough, and with no fully baked lights it is the same number. A count of 0 walks the list as
# before, so "no lights at all" still takes Unity's early exit exactly as it did.
# Entered by jmp from RVA 0x3FC771: rcx = LightManager, r8 = 0. Leaves r14 = count and goes on at
# 0x3FC78C (test r14,r14 / jne), as Unity's loop did. rax is set by Unity before its next use on
# the found path; on the walk it ends equal to rcx, as Unity's did. Flags are set again there.
# Patch site (EnginePatch.LightCount): the 27 bytes at RVA 0x3FC771
#   45 8B F0                     mov r14d,r8d
#   48 8B 41 08                  mov rax,[rcx+8]
#   48 3B C1                     cmp rax,rcx
#   74 14                        je 0x3FC791
#   0F 1F 00                     nop
#   48 8B 40 08                  mov rax,[rax+8]        <- loop (its own back-edge is the only jump in)
#   49 FF C6                     inc r14
#   48 3B C1                     cmp rax,rcx
#   75 F4                        jne 0x3FC780
# become jmp [rip+0] + this routine's address + 13 nops. Address slot, filled at runtime:
# back_slot = RVA 0x3FC78C.
# Unity 2022.3.43f1, UnityPlayer_Win64_player_mono_x64.pdb id BD9605D7A8BC4F21AF766D4707334ADC1.
# Assemble with assemble.sh; how this was found and how to redo it: EnginePatch.cs header.
cave:
    mov r14d, [rcx+0x1c]
    test r14d, r14d
    jnz back
    mov rax, rcx
walk:
    mov rax, [rax+8]
    cmp rax, rcx
    je back
    inc r14
    jmp walk
back:
    jmp qword ptr [rip+back_slot]
.balign 8
back_slot:
    .quad 0
