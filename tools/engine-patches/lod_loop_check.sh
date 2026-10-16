#!/bin/bash
# Checks EnginePatch.LodLoop: its Original bytes are the DLL's at RVA 0x4FC7C0, its Routine bytes
# are what lod_loop.s assembles to, and the routine, run natively on a fake LOD manager pass after
# pass, gives every group the result Unity's own loop would: still-forced groups keep their mask
# without a slot read, new and un-forced ones are worked out, and a forced byte changed behind
# Smoothie's back is caught within 16 passes. Linux x86-64, needs as and gcc.
# usage: lod_loop_check.sh [path to UnityPlayer.dll, default $SPT_PATH/UnityPlayer.dll]
set -e
here=$(cd "$(dirname "$0")" && pwd)
dll=${1:-$SPT_PATH/UnityPlayer.dll}
t=$(mktemp -d); trap 'rm -rf "$t"' EXIT

as --64 -o "$t/r.o" "$here/lod_loop.s"
objcopy -O binary -j .text "$t/r.o" "$t/asm.bin"

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
    if va <= 0x4FC7C0 < va + vs: at = 0x4FC7C0 - va + rp
assert d[at:at + len(original)] == original, 'Original bytes differ from the DLL'
assert open(asm, 'rb').read() == routine, 'Routine bytes differ from lod_loop.s'
with open(out, 'w') as f:
    f.write('static const unsigned char routine[] = {%s};\n' % ','.join(map(str, routine)))
print(f'bytes: Original matches the DLL ({len(original)}), Routine matches lod_loop.s ({len(routine)})')
EOF

cat > "$t/enter.s" <<'EOF'
.intel_syntax noprefix
.text
# run_lod(routine, manager, masks, fades, count): Unity's loop state, then jmp into the routine;
# its back slot points at lod_back, which returns from here.
.globl run_lod, lod_back
run_lod:
    push rbx
    push rbp
    push r12
    push r13
    push r14
    push r15
    sub rsp, 0xc8
    mov [rsp+0xa8], rdx
    mov rax, rdi
    mov r14, r8
    mov ebx, 1
    mov edi, 0x68
    lea rbp, [rcx+4]
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
    pop rbp
    pop rbx
    ret
.data
params: .long 0, 0, 0, 0
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
#define KIND(i) ((i) < 70 ? 0 : (i) < 90 ? (i) % 4 : (i) < 140 ? 3 : (i) < 150 ? 1 : (i) < 237 ? 0 : (i) % 3 == 0 ? 2 : (i) % 4)
void run_lod(void *routine, void *manager, uint8_t *masks, float *fades, uint64_t count);
void lod_back(void);

// Unity's CalculateLODMask, as far as the routine can tell: honours a forced byte, else marks
// the group worked out.
__attribute__((ms_abi)) static void mask_fn(uint8_t *slot, float dist, unsigned a, unsigned b, float c, uint8_t *mask, float *fade)
{
    (void)dist; (void)a; (void)b; (void)c;
    if (slot[0x60]) { *mask = slot[0x60]; *fade = 0; return; }
    *mask = WORKED;
    *fade = 0.5f;
}
__attribute__((ms_abi)) static float sqrt_fn(float x) { return sqrtf(x); }

static uint8_t slots[N * 0x68];
static struct { uint8_t pad[8]; uint8_t *slots; uint8_t pad2[0x2c - 0x10]; uint32_t r8; } manager;
static uint8_t masks[N];
static float fades[N];

int main(void)
{
    uint8_t *code = mmap(0, 4096, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    static uint64_t cell;
    memcpy(code, routine, sizeof routine);
    *(void **)(code + 0x178) = (void *)lod_back;
    *(void **)(code + 0x180) = (void *)mask_fn;
    *(void **)(code + 0x188) = (void *)sqrt_fn;
    *(void **)(code + 0x190) = &cell;
    manager.slots = slots;

    // Groups 1..N-1. Kinds: 0 still forced (mask 0x80 from last pass), 1 not forced, 2 newly
    // forced (mask from an earlier normal pass), 3 forced byte cleared behind our back (mask still
    // 0x80: only the block re-read may catch it). Long runs of 0 and 3 (passed over 16 at a time
    // and up to the first that isn't), mixed stretches, and runs that end mid-block.
    for (int i = 1; i < N; i++)
    {
        int kind = KIND(i);
        slots[i * 0x68 + 0x60] = kind == 0 || kind == 2 ? 0x80 : 0;
        masks[i] = kind == 0 || kind == 3 ? 0x80 : WORKED;
        fades[i] = kind == 0 || kind == 3 ? 0 : 0.5f;
    }

    int failed = 0;
    for (int pass = 1; pass <= 17; pass++)
    {
        run_lod(code, &manager, masks, fades, N);
        for (int i = 1; i < N; i++)
        {
            int kind = KIND(i);
            uint8_t want = kind == 0 || kind == 2 ? 0x80 : WORKED;
            if (kind == 3 && pass < 16 && masks[i] == 0x80) continue; // not re-read yet: allowed
            if (masks[i] != want || (want == 0x80 && fades[i] != 0))
            {
                printf("FAIL pass %d group %d kind %d: mask %#x fade %g\n", pass, i, kind, masks[i], fades[i]);
                failed = 1;
            }
        }
    }
    if (cell != 17) { printf("FAIL: pass counter %llu, want 17\n", (unsigned long long)cell); failed = 1; }
    if (!failed) printf("OK: forced groups kept, others worked out, a byte changed behind our back caught within 16 passes\n");
    return failed;
}
EOF
gcc -O1 -I"$t" -o "$t/run" "$t/run.c" "$t/enter.s" -lm
"$t/run"
