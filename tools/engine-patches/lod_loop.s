.intel_syntax noprefix
.text
# Replacement for the perspective loop of LODGroupManager::CalculateLODMasks (function 0xA49020,
# loop at RVA 0xA49160). Entered by jmp with Unity's loop state: rsi = manager (slots at [rsi+8],
# 0x68 each), r12 = LOD parameters (camera position at +4..+0xc), rbx = group index (from 1),
# r14 = count, rdi = rbx * 0x68, rbp = fade output for this group, [rsp+0xb8] = mask output array,
# r15b = maximum-LOD mask, xmm6 = distance scale, xmm7 = fade parameter; r13 already saved.
# A group Smoothie forced to draw nothing (SceneSkip: slot+0x60 = 0x80) gets exactly what
# CalculateLODMask (0x6A6070) gives it, mask 0x80 and fade 0 (its early return doesn't touch the
# bool it's handed), without reading its slot: which groups those are comes from Smoothie's own
# byte per slot (flags_slot, src/SmoothieNative/lodflags.c), kept in step with Unity's slot moves
# by the RemoveLODGroup hook. 16 bytes are compared at a time (SSE2): 16 of ours in a row get their
# 16 masks and fades in a few stores. Every other group goes Unity's own way, instruction for
# instruction (slot read, distance, square root, CalculateLODMask).
# The masks and fades go into vectors the caller makes fresh each pass, so they are always written.
# A byte that ever disagrees with its slot is caught: each pass reads the slots of one block of
# 8192 groups anyway, the next block every pass ((index >> 13) & 15 == a counter the routine
# advances every pass, in a writable cell: cell_slot), and clears our byte where the slot isn't
# forced to 0x80 any more.
# Patch site (EnginePatch.LodLoop): the 19 bytes at RVA 0xA49160
#   4C 8B 6E 08                  mov r13,[rsi+0x8]
#   4A 8D 04 03                  lea rax,[rbx+r8]
#   4C 03 EF                     add r13,rdi
#   48 89 84 24 C0 00 00 00      mov [rsp+0xc0],rax
# become jmp [rip+0] + this routine's address + 5 nops. Unity's back-edge targets only the
# first of them. Address slots, filled at runtime: back_slot = RVA 0xA4921F (after the loop:
# restores r13), mask_slot = CalculateLODMask (0x6A6070), sqrt_slot = the square-root helper
# Unity calls for a negative value (0x162EC48), flags_slot = SmoothieLodFlags(), cell_slot = a
# writable 4-byte counter (EnginePatch allocates it).
# Only the perspective loop is replaced; the orthographic one (0xA490F0) is left as it is.
# Unity 2022.3.43f2 (BSG's build, SPT 5.0 / EFT 1.2), UnityPlayer_Win64_player_il2cpp_x64.pdb id
# 3C9C828B7504489DB8FD348F0F90ADE01. Assemble with assemble.sh; how this was found and how to
# redo it: EnginePatch.cs header and unity-internals.md.
cave:
    mov rax, [rip+cell_slot]
    inc dword ptr [rax]
loop:
    cmp rbx, r14
    jae done
    mov r8, [rsp+0xb8]
    lea rax, [rbx+r8]
    mov r9, [rip+flags_slot]
    lea rdx, [rbx+16]
    cmp rdx, r14
    ja single
    mov rcx, [rip+cell_slot]
    mov ecx, [rcx]
    mov edx, ebx
    shr edx, 13
    xor edx, ecx
    test dl, 15
    jz single
    lea edx, [rbx+15]
    shr edx, 13
    xor edx, ecx
    test dl, 15
    jz single
    movdqu xmm0, [r9+rbx]
    pxor xmm1, xmm1
    pcmpeqb xmm0, xmm1
    pmovmskb edx, xmm0
    test edx, edx
    jnz single
    mov edx, 0x80808080
    movd xmm1, edx
    pshufd xmm1, xmm1, 0
    movdqu [rax], xmm1
    pxor xmm0, xmm0
    movdqu [rbp], xmm0
    movdqu [rbp+16], xmm0
    movdqu [rbp+32], xmm0
    movdqu [rbp+48], xmm0
    add rbx, 16
    add rbp, 64
    add rdi, 16*0x68
    jmp loop
single:
    cmp byte ptr [r9+rbx], 0
    je full
    mov ecx, ebx
    shr ecx, 13
    mov rdx, [rip+cell_slot]
    xor ecx, [rdx]
    test cl, 15
    jz full
    mov byte ptr [rax], 0x80
    mov dword ptr [rbp], 0
    jmp next
full:
    mov r13, [rsi+8]
    add r13, rdi
    movzx ecx, byte ptr [r13+0x60]
    cmp cl, 0x80
    je forced
    cmp byte ptr [r9+rbx], 0
    je forced
    mov byte ptr [r9+rbx], 0
forced:
    test cl, cl
    jz slow
    mov [rax], cl
    mov dword ptr [rbp], 0
    jmp next
slow:
    mov [rsp+0xc0], rax
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
    mov rax, [rsp+0xc0]
mask:
    mov r8d, [rsi+0x2c]
    lea rcx, [rsp+0xb0]
    mov [rsp+0x38], rcx
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
    mov r8, [rsp+0xb8]
    jmp qword ptr [rip+back_slot]
.balign 8
back_slot:
    .quad 0
mask_slot:
    .quad 0
sqrt_slot:
    .quad 0
flags_slot:
    .quad 0
cell_slot:
    .quad 0
