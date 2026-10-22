#!/bin/bash
# Checks EnginePatch.LodLoop: its Original bytes are the DLL's at RVA 0xA49160, its Routine bytes
# are what lod_loop.s assembles to, and the routine, run natively on a fake LOD manager pass after
# pass (fresh garbage in the outputs each time, as Unity's caller hands it new vectors), gives every
# group the result Unity's own loop would: groups Smoothie forced (their flag byte set) mask 0x80
# and fade 0 without a call, every other group CalculateLODMask with Unity's arguments (the bool
# pointer at [rsp+0xb0] included), and a flag byte left set on a slot no longer forced is caught,
# corrected and cleared within 16 passes. Linux x86-64, needs as and gcc.
# usage: lod_loop_check.sh [path to UnityPlayer.dll, default $SPT_PATH/UnityPlayer.dll]
set -e
here=$(cd "$(dirname "$0")" && pwd)
dll=${1:-$SPT_PATH/UnityPlayer.dll}
t=$(mktemp -d); trap 'rm -rf "$t"' EXIT

as --64 -o "$t/r.o" "$here/lod_loop.s"
objcopy -O binary -j .text "$t/r.o" "$t/asm.bin"
slots=$(nm "$t/r.o" | awk '/_slot/ { printf "#define %s 0x%s\n", toupper($3), $1 }')

python3 - "$here/../../src/Smoothie/EnginePatch.cs" "$dll" "$t/asm.bin" "$t/bytes.h" <<'EOF'
import re, struct, sys
cs, dll, asm, out = sys.argv[1:]
src = open(cs).read()
block = src[src.index('LodLoop = new('):]
block = block[:block.index(');') + 2]
original, routine = (bytes(int(x, 16) for x in re.findall(r'0x([0-9A-F]{2})\b', a))
                     for a in re.findall(r'new byte\[\]\s*\{(.*?)\}', block, re.S)[:2])
d = open(dll, 'rb').read()
e = struct.unpack_from('<I', d, 0x3c)[0]; n = struct.unpack_from('<H', d, e + 6)[0]
osz = struct.unpack_from('<H', d, e + 20)[0]; opt = e + 24
for s in range(n):
    vs, va, rs, rp = struct.unpack_from('<IIII', d, opt + osz + s * 40 + 8)
    if va <= 0xA49160 < va + vs: at = 0xA49160 - va + rp
assert d[at:at + len(original)] == original, 'Original bytes differ from the DLL'
assert open(asm, 'rb').read() == routine, 'Routine bytes differ from lod_loop.s'
with open(out, 'w') as f:
    f.write('static const unsigned char routine[] = {%s};\n' % ','.join(map(str, routine)))
print(f'bytes: Original matches the DLL ({len(original)}), Routine matches lod_loop.s ({len(routine)})')
EOF
echo "$slots" >> "$t/bytes.h"

cat > "$t/enter.s" <<'EOF'
.intel_syntax noprefix
.text
# run_lod(routine, masks, fades, count, manager): Unity's loop state as at RVA 0xA49160 (rsi =
# manager, r12 = parameters, rbx = 1, r14 = count, rdi = 0x68, rbp = fades + 4, [rsp+0xb8] = masks,
# r15b = maximum-LOD mask, xmm6 = distance scale, xmm7 = fade parameter), then jmp into the
# routine; its back slot points at lod_back, which returns from here. bool_at gets rsp+0xb0.
.globl run_lod, lod_back, bool_at
run_lod:
    push rbx
    push rbp
    push rsi
    push rdi
    push r12
    push r13
    push r14
    push r15
    # run_lod(rdi = routine, rsi = masks, rdx = fades, rcx = count, r8 = manager)
    sub rsp, 0xc8
    mov [rsp+0xb8], rsi
    lea rax, [rsp+0xb0]
    mov [rip+bool_at], rax
    mov rax, rdi
    mov r14, rcx
    mov rsi, r8
    mov ebx, 1
    mov edi, 0x68
    lea rbp, [rdx+4]
    lea r12, [rip+params]
    mov r15d, 0xff
    mov r9d, 0x3f800000
    movd xmm6, r9d
    xorps xmm7, xmm7
    jmp rax
lod_back:
    add rsp, 0xc8
    pop r15
    pop r14
    pop r13
    pop r12
    pop rdi
    pop rsi
    pop rbp
    pop rbx
    ret
.data
params: .long 0, 0, 0, 0
bool_at: .quad 0
EOF

cat > "$t/run.c" <<'EOF'
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include "bytes.h"

#define N 300
#define WORKED 0x11
// 0 ours (flag and slot forced), 1 not forced, 2 stale flag (slot no longer forced). Long runs of
// ours (16 at a time), mixed stretches, runs ending mid-block.
#define KIND(i) ((i) < 70 ? 0 : (i) < 90 ? (i) % 3 : (i) < 140 ? 2 : (i) < 150 ? 1 : (i) < 237 ? 0 : (i) % 3)
void run_lod(void *routine, uint8_t *masks, float *fades, uint64_t count, void *manager);
void lod_back(void);
extern void *bool_at;

static int calls, bad_args;
// Unity's CalculateLODMask, as far as the routine can tell: honours a forced byte, else marks the
// group worked out. Checks the arguments Unity passes that the routine has to rebuild.
__attribute__((ms_abi)) static void mask_fn(uint8_t *slot, float dist, unsigned levels, unsigned max, float fade_param,
                                            uint8_t *mask, float *fade, uint8_t *flag)
{
    (void)dist; (void)fade_param;
    calls++;
    if (levels != 3 || max != 0xff || flag != bool_at) bad_args++;
    if (slot[0x60]) { *mask = slot[0x60]; *fade = 0; return; }
    *mask = WORKED;
    *fade = 0.5f;
}
__attribute__((ms_abi)) static float sqrt_fn(float x) { return sqrtf(x); }

static uint8_t slots[N * 0x68];
static struct { uint8_t pad[8]; uint8_t *slots; uint8_t pad2[0x2c - 0x10]; uint32_t levels; } manager;
static uint8_t masks[N], flags[N + 16];
static float fades[N];

int main(void)
{
    uint8_t *code = mmap(0, 4096, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    static uint64_t cell;
    memcpy(code, routine, sizeof routine);
    *(void **)(code + BACK_SLOT) = (void *)lod_back;
    *(void **)(code + MASK_SLOT) = (void *)mask_fn;
    *(void **)(code + SQRT_SLOT) = (void *)sqrt_fn;
    *(void **)(code + FLAGS_SLOT) = flags;
    *(void **)(code + CELL_SLOT) = &cell;
    manager.slots = slots;
    manager.levels = 3;
    int unforced = 0;
    for (int i = 1; i < N; i++)
    {
        int kind = KIND(i);
        slots[i * 0x68 + 0x60] = kind == 0 ? 0x80 : 0;
        flags[i] = kind == 0 || kind == 2 ? 1 : 0;
        unforced += kind != 0;
    }

    int failed = 0;
    for (int pass = 1; pass <= 17; pass++)
    {
        memset(masks, 0x33, sizeof masks);
        for (int i = 0; i < N; i++) fades[i] = -1;
        calls = bad_args = 0;
        run_lod(code, masks, fades, N, &manager);
        for (int i = 1; i < N; i++)
        {
            int kind = KIND(i);
            uint8_t want = kind == 0 ? 0x80 : WORKED;
            if (kind == 2 && pass < 16 && masks[i] == 0x80 && fades[i] == 0 && flags[i]) continue; // not re-read yet: allowed
            if (masks[i] != want || fades[i] != (want == 0x80 ? 0 : 0.5f))
            {
                printf("FAIL pass %d group %d kind %d: mask %#x fade %g\n", pass, i, kind, masks[i], fades[i]);
                failed = 1;
            }
            if (kind == 2 && masks[i] == WORKED && flags[i]) { printf("FAIL pass %d group %d: stale flag not cleared\n", pass, i); failed = 1; }
        }
        if (bad_args) { printf("FAIL pass %d: %d calls with wrong arguments\n", pass, bad_args); failed = 1; }
        // Once the stale bytes are gone, only the unforced groups call CalculateLODMask.
        if (pass == 17 && calls != unforced) { printf("FAIL: %d calls in the last pass, want %d\n", calls, unforced); failed = 1; }
    }
    if (cell != 17) { printf("FAIL: pass counter %llu, want 17\n", (unsigned long long)cell); failed = 1; }
    if (!failed) printf("OK: forced groups from the flag bytes without a call, the others Unity's call with Unity's arguments, stale bytes caught within 16 passes\n");
    return failed;
}
EOF
gcc -O1 -I"$t" -o "$t/run" "$t/run.c" "$t/enter.s" -lm
"$t/run"
