#!/bin/bash
# Checks EnginePatch.LightCount: its Original bytes are the DLL's at RVA 0xD437A0, its Routine bytes
# are what light_count.s assembles to, and the routine, run natively on fake LightManagers, leaves
# the count Unity's code would size its buffers with. Linux x86-64, needs as and gcc.
# usage: light_count_check.sh [path to UnityPlayer.dll, default $SPT_PATH/UnityPlayer.dll]
set -e
here=$(cd "$(dirname "$0")" && pwd)
dll=${1:-$SPT_PATH/UnityPlayer.dll}
t=$(mktemp -d); trap 'rm -rf "$t"' EXIT

as --64 -o "$t/r.o" "$here/light_count.s"
objcopy -O binary -j .text "$t/r.o" "$t/asm.bin"

python3 - "$here/../../src/Smoothie/EnginePatch.cs" "$dll" "$t/asm.bin" "$t/bytes.h" <<'EOF'
import re, struct, sys
cs, dll, asm, out = sys.argv[1:]
src = open(cs).read()
block = src[src.index('LightCount = new('):]
block = block[:block.index(');') + 2]
original, routine = (bytes(int(x, 16) for x in re.findall(r'0x([0-9A-F]{2})\b', a))
                     for a in re.findall(r'new byte\[\]\s*\{(.*?)\}', block, re.S))
d = open(dll, 'rb').read()
e = struct.unpack_from('<I', d, 0x3c)[0]; n = struct.unpack_from('<H', d, e + 6)[0]
osz = struct.unpack_from('<H', d, e + 20)[0]; opt = e + 24
for s in range(n):
    vs, va, rs, rp = struct.unpack_from('<IIII', d, opt + osz + s * 40 + 8)
    if va <= 0xD437A0 < va + vs: at = 0xD437A0 - va + rp
assert d[at:at + len(original)] == original, 'Original bytes differ from the DLL'
assert open(asm, 'rb').read() == routine, 'Routine bytes differ from light_count.s'
with open(out, 'w') as f:
    f.write('static const unsigned char routine[] = {%s};\n' % ','.join(map(str, routine)))
print(f'bytes: Original matches the DLL ({len(original)}), Routine matches light_count.s ({len(routine)})')
EOF

cat > "$t/run.c" <<'EOF'
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include "bytes.h"

// A fake LightManager: the list head is the manager itself (next at +8), the set's count at +0x1c.
struct node { struct node *prev, *next; };
static struct { struct node head; uint32_t pad[3], count; } manager;
static struct node lights[50];

static uint64_t run(uint8_t *code, uint64_t *rax)
{
    uint64_t count, a;
    // Entered as Unity enters it (rax = manager, r12 = 0, rdi garbage); the back slot points at a
    // ret, so the jmp back returns here. Leaves the count in rdi.
    __asm__ volatile("push %%rdi\n\tpush %%r12\n\txor %%r12d, %%r12d\n\tmov $0xdeadbeefdeadbeef, %%rdi\n\t"
                     "call *%%rsi\n\tmov %%rdi, %%rcx\n\tpop %%r12\n\tpop %%rdi"
                     : "=c"(count), "=a"(a) : "S"(code), "a"(&manager) : "rdx", "memory", "cc");
    *rax = a;
    return count;
}

int main(void)
{
    uint8_t *code = mmap(0, 4096, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    memcpy(code, routine, sizeof routine);
    code[0x40] = 0xC3;
    *(uint64_t *)(code + 0x1e) = (uint64_t)(code + 0x40);
    int failed = 0;
    // (lights in the list, the set's count, what rdi must be)
    int cases[][3] = { {0, 0, 0}, {5, 0, 5}, {1, 0, 1}, {50, 50, 50}, {598 % 50, 598 % 50, 598 % 50}, {50, 7, 7} };
    for (unsigned c = 0; c < sizeof cases / sizeof cases[0]; c++)
    {
        int n = cases[c][0];
        struct node *at = &manager.head;
        for (int i = 0; i < n; i++) { at->next = &lights[i]; at = &lights[i]; }
        at->next = &manager.head;
        manager.count = cases[c][1];
        uint64_t rax, count = run(code, &rax);
        if (count != (uint64_t)cases[c][2] || rax != (uint64_t)&manager.head)
        {
            printf("FAIL: %d lights, count %d: rdi %llu, want %d%s\n", n, cases[c][1], (unsigned long long)count,
                   cases[c][2], rax != (uint64_t)&manager.head ? ", rax not the manager" : "");
            failed = 1;
        }
    }
    if (!failed) printf("OK: the routine leaves Unity's count in every case\n");
    return failed;
}
EOF
gcc -O1 -mno-red-zone -I"$t" -o "$t/run" "$t/run.c"
"$t/run"
