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
# The mask and fade outputs are the camera's own arrays, kept from frame to frame on the manager
# (CalculateLODDataArray 0x4FCCD0: one entry per camera at [manager+0x38], count +0x48; masks
# at entry+0x10, fades at entry+0x30), and RemoveLODGroup (0x4FD550) moves their entries along
# with the slots. So a group whose mask from last time is 0x80 (forced to level 7, which only
# Smoothie writes: no group here has 8 levels) was forced to nothing then and still holds mask
# 0x80 / fade 0: it's passed over without reading its slot (104 bytes, the loop's whole cost
# across ~135k groups). Smoothie clears that mask in every camera's array when it un-forces a
# group (SceneSkip.Unforce), so the next pass works it out. Anything else changing a forced
# byte is still caught: each pass reads the slots of one block of 8192 groups anyway, the next
# block every pass ((index >> 13) & 15 == a counter the routine advances every pass, in a
# writable cell: cell_slot), in order. What a pass costs is mostly going round the loop ~135k
# times, so the masks are compared 16 at a time (SSE2, as the visibility scan): 16 of ours in a
# row outside the re-read block are passed over in one step, and up to the first that isn't.
# Patch site (EnginePatch.LodLoop): the 19 bytes at RVA 0x4FC7C0
#   4C 8B 6E 08                  mov r13,[rsi+0x8]
#   4A 8D 04 03                  lea rax,[rbx+r8]
#   4C 03 EF                     add r13,rdi
#   48 89 84 24 B0 00 00 00      mov [rsp+0xb0],rax
# become jmp [rip+0] + this routine's address + 5 nops. Address slots, filled at runtime:
# back_slot = RVA 0x4FC872 (after the loop), mask_slot = CalculateLODMask (0x4FC400), sqrt_slot =
# the square-root helper Unity calls for a negative value (0x18343FC), cell_slot = a writable
# 4-byte counter (EnginePatch allocates it).
# Only the perspective loop is replaced; the orthographic one (0x4FC730) is left as it is.
# Unity 2022.3.43f1, UnityPlayer_Win64_player_mono_x64.pdb id BD9605D7A8BC4F21AF766D4707334ADC1.
# Assemble with assemble.sh; how this was found and how to redo it: EnginePatch.cs header.
cave:
    mov rax, [rip+cell_slot]
    inc dword ptr [rax]
loop:
    cmp rbx, r14
    jae done
    mov r8, [rsp+0xa8]
    lea rax, [rbx+r8]
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
    movdqu xmm0, [rax]
    mov edx, 0x80808080
    movd xmm1, edx
    pshufd xmm1, xmm1, 0
    pcmpeqb xmm0, xmm1
    pmovmskb edx, xmm0
    not edx
    and edx, 0xffff
    jz skip16
    bsf ecx, edx
    test ecx, ecx
    jz single
    add rbx, rcx
    lea rbp, [rbp+rcx*4]
    imul rdx, rcx, 0x68
    add rdi, rdx
    jmp loop
skip16:
    add rbx, 16
    add rbp, 64
    add rdi, 16*0x68
    jmp loop
single:
    cmp byte ptr [rax], 0x80
    jne full
    mov ecx, ebx
    shr ecx, 13
    mov rdx, [rip+cell_slot]
    xor ecx, [rdx]
    test cl, 15
    jnz next
full:
    mov r13, [rsi+8]
    add r13, rdi
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
cell_slot:
    .quad 0
