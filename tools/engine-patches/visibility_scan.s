.intel_syntax noprefix
.text
# Replacement for the per-entry loop in RendererScene::SceneAfterCullingOutputReady (RVA 0xAC4890).
# Entered by jmp with: rbx = scene, rdi = 0 (index), rsi = 0, r15 = entry count, r13 = 0;
# the caller already saved rsi, r14, r15 (Unity's loop counts r15 down; here it's the end index). Same work as Unity's loop: every entry whose
# visibility byte (scene+0xc8) is exactly 1 gets its renderer's virtual 0xd8 called and the
# byte or'ed with 4. Bytes are compared 16 at a time; a block with a match goes entry by entry.
# Patch site (EnginePatch.VisibilityScan): the 14 bytes at RVA 0xAC4890
#   4C 8B B3 C8 00 00 00   mov r14,[rbx+0xc8]
#   41 80 3C 3E 01         cmp byte [r14+rdi],1
#   75 24                  jne +0x24
# become jmp [rip+0] + this routine's address. The routine ends by jumping to RVA 0xAC48CF
# (Unity's code right after the loop, restoring r15, r14, rsi) through the 8-byte slot at offset 0x90.
# The scene entry holds the renderer's BaseRenderer, 0x60 into BSG's Renderer (0x38 in public f1).
# Unity 2022.3.43f2 (BSG's build, SPT 5.0 / EFT 1.2), UnityPlayer_Win64_player_il2cpp_x64.pdb id
# 3C9C828B7504489DB8FD348F0F90ADE01 (no public symbols: see unity-internals.md).
# Assemble with assemble.sh; how this was found and how to redo it: EnginePatch.cs header.
cave:
loop16:
    mov rax, r15
    sub rax, rdi
    cmp rax, 16
    jb tail
    mov r14, [rbx+0xc8]
    movdqu xmm0, [r14+rdi]
    mov eax, 0x01010101
    movd xmm1, eax
    pshufd xmm1, xmm1, 0
    pcmpeqb xmm0, xmm1
    pmovmskb eax, xmm0
    test eax, eax
    jnz block
    add rdi, 16
    jmp loop16
block:
    lea rsi, [rdi+16]
    jmp one
tail:
    cmp rdi, r15
    jae done
    mov rsi, r15
one:
    mov r14, [rbx+0xc8]
    cmp byte ptr [r14+rdi], 1
    jne next
    mov rax, [rbx+0x88]
    lea rdx, [rdi+rdi*2]
    mov rdx, [rax+rdx*8]
    test rdx, rdx
    lea rcx, [rdx-0x60]
    cmove rcx, r13
    mov rax, [rcx]
    call qword ptr [rax+0xd8]
    or byte ptr [r14+rdi], 4
next:
    inc rdi
    cmp rdi, rsi
    jb one
    jmp loop16
done:
    jmp qword ptr [rip+0]
back:
    .quad 0
