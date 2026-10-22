#!/usr/bin/env python3
"""bsearch.py <dll> <hex pattern, ?? = any byte>: RVAs (in .text) of every match."""
import sys, re, pefile
pe = pefile.PE(sys.argv[1], fast_load=True); d = bytes(pe.__data__)
pat = b''.join(b'.' if t == '??' else re.escape(bytes([int(t, 16)])) for t in sys.argv[2].split())
for m in re.finditer(pat, d, re.S):
    r = pe.get_rva_from_offset(m.start()); print(f'{r:X}')
