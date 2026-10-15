.intel_syntax noprefix
.text
# Replacement for the perspective loop of LODGroupManager::CalculateLODMasks (RVA 0x4FC7C0).
# Entered by jmp with Unity's loop state: rsi = manager (slots at [rsi+8], 0x68 each),
# r12 = LOD parameters (camera position at +4..+0xc), rbx = group index (from 1), r14 = count,
# rdi = rbx * 0x68, rbp = fade output for this group, [rsp+0xa8] = mask output array,
# r15b = maximum-LOD mask, xmm6 = distance scale, xmm7 = fade parameter; r13 already saved.
# Per group: a forced group (slot+0x60 non-zero) gets exactly what CalculateLODMask gives it
# (mask = that byte, fade = 0) without the distance, square root and call; every other group
# goes Unity's own way, instruction for instruction. Back to Unity's code after the loop.
# Patch site (EnginePatch.LodLoop): the 19 bytes at RVA 0x4FC7C0
#   4C 8B 6E 08                  mov r13,[rsi+0x8]
#   4A 8D 04 03                  lea rax,[rbx+r8]
#   4C 03 EF                     add r13,rdi
#   48 89 84 24 B0 00 00 00      mov [rsp+0xb0],rax
# become jmp [rip+0] + this routine's address + 5 nops. Address slots, filled at runtime:
# back_slot (0xD8) = RVA 0x4FC872 (after the loop), mask_slot (0xE0) = CalculateLODMask
# (0x4FC400), sqrt_slot (0xE8) = the square-root helper Unity calls for a negative value (0x18343FC).
# Only the perspective loop is replaced; the orthographic one (0x4FC730) is left as it is.
# Unity 2022.3.43f1, UnityPlayer_Win64_player_mono_x64.pdb id BD9605D7A8BC4F21AF766D4707334ADC1.
# Assemble with assemble.sh; how this was found and how to redo it: EnginePatch.cs header.
cave:
loop:
    cmp rbx, r14
    jae done
    mov r13, [rsi+8]
    add r13, rdi
    mov r8, [rsp+0xa8]
    lea rax, [rbx+r8]
    movzx ecx, byte ptr [r13+0x60]
    test cl, cl
    jz slow
    mov [rax], cl
    mov dword ptr [rbp], 0
    jmp next
slow:
    mov [rsp+0xb0], rax
    movss xmm0, [r13+4]
    subss xmm0, [r12+8]
    movss xmm2, [r13+0]
    subss xmm2, [r12+4]
    movss xmm1, [r13+8]
    subss xmm1, [r12+0xc]
    mulss xmm0, xmm0
    mulss xmm2, xmm2
    mulss xmm1, xmm1
    addss xmm2, xmm0
    xorps xmm0, xmm0
    addss xmm2, xmm1
    mulss xmm2, xmm6
    ucomiss xmm0, xmm2
    ja negative
    xorps xmm0, xmm0
    sqrtss xmm0, xmm2
    jmp mask
negative:
    movaps xmm0, xmm2
    call qword ptr [rip+sqrt_slot]
    mov rax, [rsp+0xb0]
mask:
    mov r8d, [rsi+0x2c]
    movzx r9d, r15b
    mov [rsp+0x30], rbp
    movaps xmm1, xmm0
    mov [rsp+0x28], rax
    mov rcx, r13
    movss [rsp+0x20], xmm7
    call qword ptr [rip+mask_slot]
next:
    inc rbx
    add rbp, 4
    add rdi, 0x68
    jmp loop
done:
    jmp qword ptr [rip+back_slot]
.balign 8
back_slot:
    .quad 0
mask_slot:
    .quad 0
sqrt_slot:
    .quad 0
