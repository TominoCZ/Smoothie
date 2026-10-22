#!/usr/bin/env python3
"""fnall.py <dll> <publics.txt> <RVA hex>: disassemble every .pdata chunk of the function containing
the RVA (chained unwind info points each chunk at its root), chunks in address order."""
import sys, struct, subprocess, pefile
dll, pub, rva = sys.argv[1], sys.argv[2], int(sys.argv[3], 16)
pe = pefile.PE(dll, fast_load=True)
pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_EXCEPTION']])
ex = [(e.struct.BeginAddress, e.struct.EndAddress, e.struct.UnwindData) for e in pe.DIRECTORY_ENTRY_EXCEPTION]
def root(begin, unwind):
    while True:
        b = pe.get_data(unwind, 4)
        if not (b[0] >> 3) & 4: return begin
        off = unwind + 4 + ((b[2] + 1) & ~1) * 2
        begin, _, unwind = struct.unpack('<III', pe.get_data(off, 12))
def find(r):
    for b, e, u in ex:
        if b <= r < e: return b, e, u
start = find(rva)
dis = str(__import__('pathlib').Path(__file__).resolve().parent / 'dis.sh')
if start is None:
    # A leaf function has no unwind entry: show the next 0x100 bytes instead.
    print(f'--- no .pdata entry at {rva:X} (leaf function): 0x100 bytes from it')
    sys.stdout.flush()
    subprocess.run(['bash', dis, dll, pub, f'{rva:X}', '256'])
    sys.exit(0)
r0 = root(start[0], start[2])
chunks = sorted((b, e) for b, e, u in ex if root(b, u) == r0)
for b, e in chunks:
    print(f'--- chunk {b:X}-{e:X}')
    sys.stdout.flush()
    subprocess.run(['bash', dis, dll, pub, f'{b:X}', str(e - b)])
