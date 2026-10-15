#!/usr/bin/env python3
"""List the public symbols of a PDB as "RVA name" lines, sorted, for finding Unity's functions.

usage: publics.py <file.pdb> <UnityPlayer.dll> > publics.txt

Unity's public PDBs carry only public symbols (no types, no static functions): the MSF file's
DBI stream (stream 3) names the symbol-record stream, whose S_PUB32 records (kind 0x110E) give
section + offset + decorated name. RVA = that section's virtual address + offset. A static
(unnamed) function shows up as the public symbol before it, so check hot addresses against the
disassembly before trusting a name.
"""
import struct, sys
import pefile  # pip install pefile

d = open(sys.argv[1], 'rb').read()
assert d.startswith(b'Microsoft C/C++ MSF 7.00\r\n\x1aDS\0\0\0'), 'not an MSF 7 PDB'
bs, _, _, dirbytes, _, dirmap = struct.unpack_from('<IIIIII', d, 32)
blk = lambda i: d[i * bs:(i + 1) * bs]
dirblocks = struct.unpack_from('<%dI' % ((dirbytes + bs - 1) // bs), blk(dirmap))
directory = b''.join(blk(b) for b in dirblocks)[:dirbytes]
count = struct.unpack_from('<I', directory, 0)[0]
sizes = struct.unpack_from('<%dI' % count, directory, 4)
pos, streams = 4 + 4 * count, []
for size in sizes:
    size = 0 if size == 0xffffffff else size
    n = (size + bs - 1) // bs
    streams.append((size, struct.unpack_from('<%dI' % n, directory, pos)))
    pos += 4 * n
stream = lambda i: b''.join(blk(b) for b in streams[i][1])[:streams[i][0]]
symbols = stream(struct.unpack_from('<H', stream(3), 20)[0])
sections = [s.VirtualAddress for s in pefile.PE(sys.argv[2], fast_load=True).sections]
out, p = [], 0
while p + 4 <= len(symbols):
    length, kind = struct.unpack_from('<HH', symbols, p)
    if kind == 0x110E:
        _, offset, segment = struct.unpack_from('<IIH', symbols, p + 4)
        name = symbols[p + 14:symbols.index(b'\0', p + 14)].decode('latin1')
        if 1 <= segment <= len(sections):
            out.append((sections[segment - 1] + offset, name))
    p += length + 2
for rva, name in sorted(out):
    print('%08X %s' % (rva, name))
