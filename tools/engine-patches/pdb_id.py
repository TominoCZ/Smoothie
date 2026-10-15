#!/usr/bin/env python3
"""Print UnityPlayer.dll's debug-symbol identity and where Unity publishes its symbols.

usage: pdb_id.py <path to UnityPlayer.dll>

Every Unity player build is identified by the RSDS record in its PE debug directory: the PDB
file name, a GUID and an age. Unity serves the matching (public) symbols from its symbol server
as a CAB-compressed .pd_ file. Download it and unpack it with `cabextract` to get the .pdb.
"""
import struct, sys

d = open(sys.argv[1], 'rb').read()
pe = struct.unpack_from('<I', d, 0x3c)[0]
sections, optional = struct.unpack_from('<H', d, pe + 6)[0], struct.unpack_from('<H', d, pe + 20)[0]
opt = pe + 24
dirs = opt + (112 if struct.unpack_from('<H', d, opt)[0] == 0x20b else 96)
debug_rva, debug_size = struct.unpack_from('<II', d, dirs + 6 * 8)
debug = None
for s in range(sections):
    h = opt + optional + s * 40
    size, va, raw = struct.unpack_from('<III', d, h + 8)[0], struct.unpack_from('<I', d, h + 12)[0], struct.unpack_from('<I', d, h + 20)[0]
    if va <= debug_rva < va + max(size, 1):
        debug = debug_rva - va + raw
for e in range(debug_size // 28):
    _, _, _, _, kind, _, _, ptr = struct.unpack_from('<IIHHIIII', d, debug + e * 28)
    if kind != 2 or d[ptr:ptr + 4] != b'RSDS':
        continue
    g = struct.unpack_from('<IHH8s', d, ptr + 4)
    age = struct.unpack_from('<I', d, ptr + 20)[0]
    name = d[ptr + 24:d.index(b'\0', ptr + 24)].decode().split('\\')[-1]
    ident = '%08X%04X%04X%s%X' % (g[0], g[1], g[2], g[3].hex().upper(), age)
    print('pdb    ', name)
    print('id     ', ident, '(SceneSkip.BuildId must equal this)')
    print('symbols', f'https://symbolserver.unity3d.com/{name}/{ident}/{name[:-1]}_')
    print('then    cabextract the .pd_ to get', name)
