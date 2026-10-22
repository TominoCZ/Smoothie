#!/usr/bin/env python3
"""IL2CPP internal calls of a UnityPlayer.dll: name -> native function RVA.
Unity registers each module's internal calls from two parallel arrays of the same length: pointers
to the names ("Namespace.Class::method" strings) and pointers to the functions, name[k] belonging to
function[k]. The function array sits right before the name array or right after it, at most two
pointers of padding between. Each table is reported with the placement that is all code pointers.
Checked against Unity's public 2022.3.43f1 IL2CPP symbols (see engine_patches_f2.md).
usage: icall.py <UnityPlayer.dll> [-t] [name substring ...]   (-t: list the tables found)"""
import sys, struct, pefile
pe = pefile.PE(sys.argv[1], fast_load=True); d = bytes(pe.__data__); base = pe.OPTIONAL_HEADER.ImageBase
sec = {s.Name.rstrip(b'\0').decode(): s for s in pe.sections}
def inside(name, v): s = sec[name]; return s.VirtualAddress <= v - base < s.VirtualAddress + s.Misc_VirtualSize
def cstr(v):
    if not inside('.rdata', v): return None
    o = pe.get_offset_from_rva(v - base); e = d.find(b'\0', o, o + 300); t = d[o:e]
    return t.decode() if e > o and b'::' in t and all(0x20 <= c < 0x7f for c in t) else None
tables, entries = [], []
for s in pe.sections:
    if s.Name.rstrip(b'\0') not in (b'.rdata', b'.data'): continue
    n = s.SizeOfRawData // 8; vals = struct.unpack_from('<%dQ' % n, d, s.PointerToRawData)
    names = [cstr(v) for v in vals]; code = [inside('.text', v) for v in vals]
    i = 0
    while i < n:
        if names[i] is None: i += 1; continue
        j = i
        while j < n and names[j] is not None: j += 1
        def pair(ns, length, fs):
            tables.append((s.VirtualAddress + 8 * ns, s.VirtualAddress + 8 * fs, length))
            entries.extend((vals[fs + k] - base, names[ns + k]) for k in range(length))
        length = j - i
        for fs in [i - length - p for p in (0, 1, 2)] + [j + p for p in (0, 1, 2)]:
            if length >= 8 and 0 <= fs and fs + length <= n and all(code[fs:fs + length]):
                pair(i, length, fs)
                break
        else:
            # Two modules' name arrays back to back: the first one's functions end right before
            # it, the second one's start right after it.
            for p in (0, 1, 2):
                e = i - p; b = e
                while b > 0 and code[b - 1]: b -= 1
                first = e - b
                if 8 <= first < length and all(code[j + q] for q in range(length - first) if j + q < n):
                    pair(i, first, b)
                    pair(i + first, length - first, j)
                    break
        i = j
args = sys.argv[2:]
if args[:1] == ['-t']:
    for nm, fn, ln in tables: print(f'names {nm:X} functions {fn:X} count {ln}')
    args = args[1:]
for f, name in entries:
    if not args or any(w in name for w in args): print(f'{f:08X}\t{name}')
