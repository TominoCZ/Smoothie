.intel_syntax noprefix
.text
# Replacement for the light count at the start of CullScene's light culling (function 0xD43780).
# Every culling pass, several a frame, Unity counts LightManager's lights by walking its linked
# list of every enabled light (no size field), one cache miss per light, only to size that
# pass's light buffers.
# LightManager (getter 0x519EC0, global 0x1A475E8) also keeps a hash set of the same lights minus
# fully baked ones, and that set counts itself: [manager+0x1c], the first thing FindActiveLights
# (0xC10FE0) reads. The buffers sized from this count are filled only by FindActiveLights, one
# entry per light it takes from that set, and everything after it is sized from the lights found
# ([rbp+0xa00], read back after the call). So the set's count is enough, and with no fully baked
# lights it is the same number. A count of 0 walks the list as before, so "no lights at all"
# still takes Unity's early exit exactly as it did.
# Entered by jmp from RVA 0xD437A0: rax = LightManager, r12 = 0. Leaves rdi = count and goes on
# at 0xD437BC (a call to a getter that takes no arguments, then test rdi,rdi / jne), as Unity's
# loop did. rax is unchanged, as in Unity's loop; rdx is the call's to clobber.
# Patch site (EnginePatch.LightCount): the 28 bytes at RVA 0xD437A0
#   41 8B FC                     mov edi,r12d
#   48 8B 50 08                  mov rdx,[rax+8]
#   48 3B D0                     cmp rdx,rax
#   74 10                        je 0xD437BC
#   0F 1F 40 00                  nop
#   48 8B 52 08                  mov rdx,[rdx+8]        <- loop (its own back-edge is the only jump in)
#   48 FF C7                     inc rdi
#   48 3B D0                     cmp rdx,rax
#   75 F4                        jne 0xD437B0
# become jmp [rip+0] + this routine's address + 14 nops. Address slot, filled at runtime:
# back_slot = RVA 0xD437BC.
# Unity 2022.3.43f2 (BSG's build, SPT 5.0 / EFT 1.2), UnityPlayer_Win64_player_il2cpp_x64.pdb id
# 3C9C828B7504489DB8FD348F0F90ADE01. Assemble with assemble.sh; how this was found and how to
# redo it: EnginePatch.cs header and unity-internals.md.
cave:
    mov edi, [rax+0x1c]
    test edi, edi
    jnz done
    mov rdx, rax
walk:
    mov rdx, [rdx+8]
    cmp rdx, rax
    je done
    inc rdi
    jmp walk
done:
    jmp qword ptr [rip+0]
back_slot:
    .quad 0
